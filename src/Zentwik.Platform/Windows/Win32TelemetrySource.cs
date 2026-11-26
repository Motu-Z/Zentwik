namespace Zentwik.Platform.Windows;

/// <summary>用户态可读，未提权时监控仍可用。</summary>
public sealed class Win32TelemetrySource : ITelemetrySource
{
    private readonly Lock _gate = new();
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private TelemetrySample? _last;

    // 进程数变化慢，且统计它要遍历整张进程表；与 CPU 同频刷新属白烧。
    private int _processCount;
    private DateTime _processCountAt = DateTime.MinValue;
    private static readonly TimeSpan ProcessCountTtl = TimeSpan.FromSeconds(2);

    public TelemetrySample Sample()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var cpu = ReadCpu();

            var memory = new Native.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryStatusEx>() };
            var memoryOk = Native.GlobalMemoryStatusEx(ref memory);

            var sample = new TelemetrySample(
                now,
                cpu,
                memoryOk ? memory.TotalPhys : 0,
                memoryOk ? memory.AvailPhys : 0,
                CountProcesses(),
                Environment.ProcessorCount);
            _last = sample;
            return sample;
        }
    }

    private double ReadCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return _last?.CpuTotalPercent ?? 0d;
        }

        var idleTicks = idle.ToUInt64();
        var kernelTicks = kernel.ToUInt64();
        var userTicks = user.ToUInt64();

        if (_previousKernel == 0 && _previousUser == 0)
        {
            _previousIdle = idleTicks;
            _previousKernel = kernelTicks;
            _previousUser = userTicks;
            return 0d;
        }

        var idleDelta = idleTicks - _previousIdle;
        var totalDelta = (kernelTicks - _previousKernel) + (userTicks - _previousUser);
        _previousIdle = idleTicks;
        _previousKernel = kernelTicks;
        _previousUser = userTicks;

        if (totalDelta == 0)
        {
            return _last?.CpuTotalPercent ?? 0d;
        }

        var busy = totalDelta > idleDelta ? totalDelta - idleDelta : 0d;
        var percent = busy / totalDelta * 100d;
        return Math.Clamp(percent, 0d, 100d);
    }

    /// <summary>整表计数按 2 秒节流：桌面端 500ms 订阅也不需要每拍都数一遍。</summary>
    private int CountProcesses()
    {
        var now = DateTime.UtcNow;
        if (now - _processCountAt < ProcessCountTtl)
        {
            return _processCount;
        }

        _processCountAt = now;
        _processCount = NativeProcessList.Count();
        return _processCount;
    }
}
