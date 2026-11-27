namespace Zentwik.Platform.Fakes;

/// <summary>确定性采样源，用于测试与无硬件环境下的 UI 联调。</summary>
public sealed class FakeTelemetrySource(int processorCount = 16) : ITelemetrySource
{
    private readonly Lock _gate = new();
    private int _tick;

    public TelemetrySample Sample()
    {
        lock (_gate)
        {
            _tick++;
            var wave = Math.Sin(_tick / 6d) * 0.5 + 0.5;
            var utilization = new double[processorCount];
            for (var index = 0; index < processorCount; index++)
            {
                utilization[index] = Math.Round(Math.Abs(Math.Sin((_tick + index * 5) / 7d)) * 100d, 1);
            }

            return new TelemetrySample(
                DateTimeOffset.UtcNow,
                Math.Round(12 + wave * 60, 1),
                32UL * 1024 * 1024 * 1024,
                (ulong)(16d * 1024 * 1024 * 1024),
                180 + (_tick % 7),
                processorCount,
                ProcessorUtilization: utilization);
        }
    }
}
