using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Zentwik.Domain.Plans;
using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

public sealed class AffinityScheduler : IProcessScheduler, IProcessCpuSetScheduler
{
    // 一轮持续执行会对多条规则逐个查找进程；共用一份短时快照，避免每条规则各枚举一遍系统进程。
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(1);

    private readonly Lock _snapshotGate = new();
    private Dictionary<string, ProcessTarget[]>? _snapshot;
    private DateTime _snapshotAt;

    public IReadOnlyList<ProcessTarget> Find(string name)
    {
        var normalized = Normalize(name);
        var now = DateTime.UtcNow;

        lock (_snapshotGate)
        {
            if (_snapshot is null || now - _snapshotAt > SnapshotTtl)
            {
                _snapshot = TakeSnapshot();
                _snapshotAt = now;
            }

            return _snapshot.GetValueOrDefault(normalized) ?? [];
        }
    }

    private static string Normalize(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static Dictionary<string, ProcessTarget[]> TakeSnapshot()
    {
        var byName = new Dictionary<string, List<ProcessTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (processId, exeName) in NativeProcessList.Capture())
        {
            var name = Normalize(exeName);
            if (!byName.TryGetValue(name, out var list))
            {
                list = [];
                byName[name] = list;
            }

            // 创建时间不在这里读：对全表逐个开句柄才是这项开销的来源，调用方按需单查。
            list.Add(new ProcessTarget(processId, name));
        }

        return byName.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetStartTicks(int processId, out long ticks)
    {
        ticks = 0;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            ticks = ReadStartTicks(process);
            return ticks != 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static long ReadStartTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 读不到创建时间时不伪造：返回 0，调用方按"无标识"处理。
            return 0;
        }
    }

    public bool TryRead(int processId, out string? cpuMask, out string? priority, out string? error)
    {
        cpuMask = null;
        priority = null;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                // 已退出进程的句柄仍可读到退出前的值，直接采用会造成"假成功"。
                error = $"进程 {processId} 已退出。";
                return false;
            }

            cpuMask = $"0x{process.ProcessorAffinity.ToInt64():X}";
            priority = process.PriorityClass.ToString();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryApply(int processId, string? cpuMask, string? priority, out string? error)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                error = $"进程 {processId} 已退出。";
                return false;
            }

            if (cpuMask is not null)
            {
                if (!TryParseMask(cpuMask, out var mask))
                {
                    error = $"无法解析 CPU 掩码：{cpuMask}。";
                    return false;
                }

                // 亲和性掩码只能表达单个处理器组内的位；多组时写了也不会覆盖全部处理器。
                if (Native.GetActiveProcessorGroupCount() > 1)
                {
                    error = "系统存在多个处理器组，亲和性掩码只覆盖单个组，已拒绝。";
                    return false;
                }

                var bits = unchecked((ulong)(long)mask);
                var processors = Environment.ProcessorCount;
                if (processors is > 0 and < 64 && (bits >> processors) != 0)
                {
                    error = $"掩码 0x{bits:X} 超出系统 {processors} 个逻辑处理器。";
                    return false;
                }

                process.ProcessorAffinity = mask;
            }

            if (priority is not null)
            {
                if (!Enum.TryParse<ProcessPriorityClass>(priority, ignoreCase: true, out var priorityClass))
                {
                    error = $"未知进程优先级：{priority}。";
                    return false;
                }

                process.PriorityClass = priorityClass;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool IsCpuSetsSupported(out string? detail)
    {
        if (!CpuSetsNative.TryGetSystemCpuSetMap(out var logicalToId, out _, out var error))
        {
            detail = error;
            return false;
        }

        detail = $"Windows CPU Sets 可用，共 {logicalToId.Count} 个逻辑处理器集合。";
        return true;
    }

    public bool TryReadCpuSets(int processId, out string? cpuSets, out string? error)
    {
        cpuSets = null;
        if (!CpuSetsNative.IsAlive(processId))
        {
            error = $"进程 {processId} 已退出。";
            return false;
        }

        if (!CpuSetsNative.TryGetSystemCpuSetMap(out _, out var idToLogical, out error))
        {
            return false;
        }

        var handle = CpuSetsNative.OpenProcess(CpuSetsNative.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            error = $"打开进程 {processId} 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
            return false;
        }

        try
        {
            if (!CpuSetsNative.GetProcessDefaultCpuSets(handle, null, 0, out var required))
            {
                var code = Marshal.GetLastWin32Error();
                if (code != 122) // ERROR_INSUFFICIENT_BUFFER
                {
                    error = $"读取进程 {processId} CPU Sets 失败，Win32 错误码 {code}。";
                    return false;
                }
            }

            if (required == 0)
            {
                cpuSets = string.Empty;
                error = null;
                return true;
            }

            if (required > 4096)
            {
                error = $"进程 {processId} 报告的 CPU Set 数量 {required} 超出合理范围。";
                return false;
            }

            var ids = new uint[required];
            if (!CpuSetsNative.GetProcessDefaultCpuSets(handle, ids, required, out _))
            {
                error = $"读取进程 {processId} CPU Sets 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            var logical = ids
                .Where(idToLogical.ContainsKey)
                .Select(id => idToLogical[id])
                .ToArray();
            cpuSets = CpuSetSpec.Format(logical);
            error = null;
            return true;
        }
        finally
        {
            CpuSetsNative.CloseHandle(handle);
        }
    }

    public bool TryApplyCpuSets(int processId, string? cpuSets, out string? error)
    {
        // 已退出进程能开句柄且 SetProcessDefaultCpuSets 返回成功，实际什么都没设上。
        if (!CpuSetsNative.IsAlive(processId))
        {
            error = $"进程 {processId} 已退出。";
            return false;
        }

        if (!CpuSetsNative.TryGetSystemCpuSetMap(out var logicalToId, out _, out error))
        {
            return false;
        }

        uint[]? ids = null;
        if (!string.IsNullOrWhiteSpace(cpuSets))
        {
            if (!CpuSetSpec.TryParse(cpuSets, out var requested, out error)
                || !CpuSetSpec.ValidateAndResolve(requested, logicalToId, out ids, out error))
            {
                return false;
            }
        }

        // 先只申请写权限：权限要得越多，越容易在受保护进程上被整体拒绝。
        var handle = CpuSetsNative.OpenProcess(CpuSetsNative.ProcessSetLimitedInformation, false, processId);
        if (handle == 0)
        {
            handle = CpuSetsNative.OpenProcess(CpuSetsNative.ProcessSetInformation, false, processId);
        }

        if (handle == 0)
        {
            error = $"打开进程 {processId} 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
            return false;
        }

        try
        {
            // 外部硬亲和性优先于 CPU Sets，两者叠加只会取交集，表现为"绑了没变化"。
            var probe = CpuSetsNative.OpenProcess(CpuSetsNative.ProcessQueryLimitedInformation, false, processId);
            if (probe != 0)
            {
                try
                {
                    if (CpuSetsNative.HasNarrowAffinity(probe, out var processMask, out var systemMask))
                    {
                        error = $"进程 {processId} 的硬亲和性 0x{processMask:X} 窄于系统掩码 0x{systemMask:X}，CPU Sets 不会生效，已跳过。";
                        return false;
                    }
                }
                finally
                {
                    CpuSetsNative.CloseHandle(probe);
                }
            }

            var count = (uint)(ids?.Length ?? 0);
            if (!CpuSetsNative.SetProcessDefaultCpuSets(handle, ids, count))
            {
                error = $"设置进程 {processId} CPU Sets 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            error = null;
            return true;
        }
        finally
        {
            CpuSetsNative.CloseHandle(handle);
        }
    }

    private static bool TryParseMask(string text, out nint mask)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                mask = unchecked((nint)(long)hex);
                return mask != 0;
            }
        }
        else if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
        {
            mask = unchecked((nint)(long)dec);
            return mask != 0;
        }

        mask = 0;
        return false;
    }
}