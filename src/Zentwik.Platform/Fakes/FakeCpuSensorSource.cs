namespace Zentwik.Platform.Fakes;

/// <summary>确定性传感器源，用于测试与无硬件环境下的 UI 联调。</summary>
public sealed class FakeCpuSensorSource(int physicalCores = 8) : ICpuSensorSource
{
    private readonly Lock _gate = new();
    private int _tick;

    public (HardwareAvailability Availability, string? Detail) ProbeCpuSensors() =>
        (HardwareAvailability.Available, "模拟传感器。");

    public bool TrySampleCpuSensors(out CpuSensorReading reading, out string? error)
    {
        lock (_gate)
        {
            _tick++;
            var wave = Math.Sin(_tick / 6d) * 0.5 + 0.5;

            var clocks = new List<CpuCoreClock>(physicalCores);
            for (var core = 0; core < physicalCores; core++)
            {
                var phase = Math.Sin((_tick + core * 3) / 6d) * 0.5 + 0.5;
                clocks.Add(new CpuCoreClock(core / 8, core % 8, Math.Round(3200 + phase * 1450)));
            }

            reading = new CpuSensorReading(
                Math.Round(45 + wave * 35, 1),
                Math.Round(20 + wave * 60, 1),
                clocks);
            error = null;
            return true;
        }
    }
}
