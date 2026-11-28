using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Zentwik.Platform.Windows;

/// <summary>
/// 全表进程快照：一次 NtQuerySystemInformation 拿 PID/名字/创建时间/累计 CPU 时间/基础优先级，零 OpenProcess。
/// 路径只对调用方点名的 PID 单查——全表逐进程开句柄会随进程数线性放大系统调用。
/// </summary>
/// <remarks>
/// CPU% 与旧版同口径：以 (PID, 创建时间) 为键维护 30 秒 / 64 点滑窗，用窗口最老点与当前点做差，
/// 再除以逻辑处理器数。键里带创建时间是为了让 PID 复用后的新进程不会继承旧进程的时间片。
/// 进程页是手动刷新，所以窗口靠每次请求推进；首见或基线超过 30 秒时补一次 200 ms 短间隔采样，
/// 否则第一次刷新会是一屏「—」。
/// </remarks>
public sealed partial class WindowsProcessInventory : IProcessInventory
{
    private const int MaxWindowPoints = 64;

    private static readonly long WindowTicks = (long)(30.0 * Stopwatch.Frequency);
    private static readonly TimeSpan BootstrapGap = TimeSpan.FromMilliseconds(200);
    private static readonly long CacheTicks = (long)(2.0 * Stopwatch.Frequency);

    private readonly Lock _gate = new();
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
    private readonly Dictionary<(int ProcessId, long CreateTime), Queue<(long Stamp, long Ticks)>> _windows = [];

    private List<ProcessInventoryEntry> _cached = [];
    private long _cachedAt = long.MinValue / 2;

    public IReadOnlyList<ProcessInventoryEntry> Snapshot(IReadOnlyCollection<int> detailProcessIds)
    {
        lock (_gate)
        {
            // 2 秒内的重复请求（连点刷新、多客户端）复用上一帧：全表采集本身没有新信息可给。
            var rows = _cached.Count > 0 && Stopwatch.GetTimestamp() - _cachedAt <= CacheTicks
                ? _cached
                : Compute();

            if (detailProcessIds.Count == 0 || rows.Count == 0)
            {
                return rows;
            }

            var byPid = new Dictionary<int, int>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                byPid[rows[i].ProcessId] = i;
            }

            var withPaths = rows.ToArray();
            foreach (var pid in detailProcessIds)
            {
                if (byPid.TryGetValue(pid, out var position) && TryReadImagePath(pid, out var path) && path is not null)
                {
                    withPaths[position] = withPaths[position] with { ImagePath = path };
                }
            }

            return withPaths;
        }
    }

    private List<ProcessInventoryEntry> Compute()
    {
        var frame = NativeProcessSnapshot.Capture();
        if (frame.Count == 0)
        {
            return [];
        }

        var stamp = Stopwatch.GetTimestamp();
        var needsBaseline = frame.Any(entry => !HasFreshPoint((entry.ProcessId, entry.CreateTime), stamp));
        var baseline = needsBaseline ? frame : null;
        var baselineStamp = stamp;

        if (needsBaseline)
        {
            Thread.Sleep(BootstrapGap);
            frame = NativeProcessSnapshot.Capture();
            stamp = Stopwatch.GetTimestamp();
            if (frame.Count == 0)
            {
                return [];
            }
        }

        Dictionary<(int, long), long>? baselineTicks = null;
        if (baseline is not null)
        {
            baselineTicks = new Dictionary<(int, long), long>(baseline.Count);
            foreach (var entry in baseline)
            {
                baselineTicks[(entry.ProcessId, entry.CreateTime)] = entry.CpuTime100Ns;
            }
        }

        var rows = new List<ProcessInventoryEntry>(frame.Count);
        foreach (var item in frame)
        {
            var key = (item.ProcessId, item.CreateTime);
            if (!_windows.TryGetValue(key, out var window))
            {
                window = new Queue<(long Stamp, long Ticks)>();
                _windows[key] = window;
            }

            if (!HasFreshPoint(key, stamp)
                && baselineTicks is not null
                && baselineTicks.TryGetValue(key, out var startTicks))
            {
                window.Enqueue((baselineStamp, startTicks));
            }

            window.Enqueue((stamp, item.CpuTime100Ns));
            Trim(window, stamp);

            double? percent = null;
            if (window.Count >= 2)
            {
                var (oldestStamp, oldestTicks) = window.Peek();
                var elapsedSeconds = (stamp - oldestStamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds > 0 && item.CpuTime100Ns >= oldestTicks)
                {
                    var cpuSeconds = (item.CpuTime100Ns - oldestTicks) / 10_000_000d;
                    percent = Math.Clamp(cpuSeconds / elapsedSeconds * 100d / _processorCount, 0d, 100d);
                }
            }

            rows.Add(new ProcessInventoryEntry(item.ProcessId, item.Name, item.BasePriority, null, percent));
        }

        PruneWindows(frame);
        _cached = rows;
        _cachedAt = Stopwatch.GetTimestamp();
        return rows;
    }

    private bool HasFreshPoint((int ProcessId, long CreateTime) key, long stamp) =>
        _windows.TryGetValue(key, out var window)
        && window.Count > 0
        && stamp - window.Peek().Stamp <= WindowTicks;

    private static void Trim(Queue<(long Stamp, long Ticks)> window, long stamp)
    {
        // 至少留一个点：窗口清空会让下一次请求又走一遍补基线。
        while (window.Count > 1 && stamp - window.Peek().Stamp > WindowTicks)
        {
            window.Dequeue();
        }

        while (window.Count > MaxWindowPoints)
        {
            window.Dequeue();
        }
    }

    private void PruneWindows(IReadOnlyList<RawProcessEntry> frame)
    {
        var alive = new HashSet<(int, long)>(frame.Count);
        foreach (var entry in frame)
        {
            alive.Add((entry.ProcessId, entry.CreateTime));
        }

        foreach (var key in _windows.Keys.Where(key => !alive.Contains(key)).ToArray())
        {
            _windows.Remove(key);
        }
    }

    private static unsafe bool TryReadImagePath(int processId, out string? path)
    {
        path = null;

        var handle = CpuSetsNative.OpenProcess(CpuSetsNative.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return false;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            fixed (char* pointer = buffer)
            {
                if (!QueryFullProcessImageNameW(handle, 0, pointer, ref size) || size == 0)
                {
                    return false;
                }
            }

            path = new string(buffer, 0, (int)size);
            return true;
        }
        finally
        {
            CpuSetsNative.CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref uint size);
}
