using System.Diagnostics;

namespace Zentwik.Platform.Windows;

/// <summary>用户态可读，未提权时监控仍可用。</summary>
public sealed class Win32TelemetrySource : ITelemetrySource
{
    private readonly Lock _gate = new();
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private TelemetrySample? _last;

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

    private static int CountProcesses()
    {
        try
        {
            return Process.GetProcesses().Length;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}
