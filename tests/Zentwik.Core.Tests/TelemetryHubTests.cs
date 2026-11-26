using Xunit;
using Zentwik.Core.Telemetry;
using Zentwik.Platform;

namespace Zentwik.Core.Tests;

public sealed class TelemetryHubTests
{
    [Fact]
    public async Task Sensors_Are_Read_On_Demand_And_Reused_Between_Reads()
    {
        var sensors = new CountingSensorSource();
        var hub = new TelemetryHub(new StubSystemSource(), sensors);

        var cold = await hub.SampleAsync(CancellationToken.None, readSensors: false);
        Assert.Null(cold.TemperatureCelsius);
        Assert.Equal(0, sensors.Reads);

        var read = await hub.SampleAsync(CancellationToken.None, readSensors: true);
        Assert.Equal(61d, read.TemperatureCelsius!.Value, 3);
        Assert.Equal(41d, read.PackagePowerWatts!.Value, 3);
        Assert.Equal(1, sensors.Reads);

        var reused = await hub.SampleAsync(CancellationToken.None, readSensors: false);
        Assert.Equal(61d, reused.TemperatureCelsius!.Value, 3);
        Assert.Equal(1, sensors.Reads);
    }

    [Fact]
    public async Task Subscription_With_Sensors_Off_Never_Touches_Hardware()
    {
        var sensors = new CountingSensorSource();
        var hub = new TelemetryHub(new StubSystemSource(), sensors);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var samples = 0;

        await hub.RunSubscriptionAsync(20, 0, _ => ++samples < 3, cts.Token);

        Assert.Equal(3, samples);
        Assert.Equal(0, sensors.Reads);
    }

    [Fact]
    public async Task Subscription_Reads_Sensors_At_Most_Once_Per_Interval()
    {
        var sensors = new CountingSensorSource();
        var hub = new TelemetryHub(new StubSystemSource(), sensors);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var samples = 0;

        // 20ms 推送、10 秒传感器间隔：整段订阅只该读一次。
        await hub.RunSubscriptionAsync(20, 10_000, _ => ++samples < 5, cts.Token);

        Assert.Equal(5, samples);
        Assert.Equal(1, sensors.Reads);
    }

    [Fact]
    public async Task Subscription_Reads_Sensors_On_Every_Tick_When_Intervals_Match()
    {
        // 回归：按时间戳判断时，定时器抖动几毫秒会被判成"未到点"，传感器实际变成两拍一读。
        var sensors = new CountingSensorSource();
        var hub = new TelemetryHub(new StubSystemSource(), sensors);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var samples = 0;

        await hub.RunSubscriptionAsync(20, 20, _ => ++samples < 4, cts.Token);

        Assert.Equal(4, samples);
        Assert.Equal(4, sensors.Reads);
    }

    [Fact]
    public async Task Subscription_Reads_Sensors_Every_Nth_Tick()
    {
        var sensors = new CountingSensorSource();
        var hub = new TelemetryHub(new StubSystemSource(), sensors);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var samples = 0;

        // 20ms 推送、40ms 传感器：第 1、3、5 拍读，共 3 次。
        await hub.RunSubscriptionAsync(20, 40, _ => ++samples < 6, cts.Token);

        Assert.Equal(6, samples);
        Assert.Equal(3, sensors.Reads);
    }

    [Fact]
    public async Task Sensor_Failure_Leaves_System_Sample_Intact()    {
        var hub = new TelemetryHub(new StubSystemSource(), new FailingSensorSource());
        var sample = await hub.SampleAsync(CancellationToken.None, readSensors: true);

        Assert.Null(sample.TemperatureCelsius);
        Assert.Null(sample.PackagePowerWatts);
        Assert.Equal("模块缺失", sample.SensorDetail);
        Assert.Equal(12.5d, sample.CpuTotalPercent, 3);
    }

    private sealed class StubSystemSource : ITelemetrySource
    {
        public TelemetrySample Sample() =>
            new(DateTimeOffset.UtcNow, 12.5, 64UL * 1024 * 1024 * 1024, 32UL * 1024 * 1024 * 1024, 10, 8);
    }

    private sealed class CountingSensorSource : ICpuSensorSource
    {
        public int Reads { get; private set; }

        public (HardwareAvailability Availability, string? Detail) ProbeCpuSensors() =>
            (HardwareAvailability.Available, null);

        public bool TrySampleCpuSensors(out CpuSensorReading reading, out string? error)
        {
            Reads++;
            reading = new CpuSensorReading(60 + Reads, 40 + Reads);
            error = null;
            return true;
        }
    }

    private sealed class FailingSensorSource : ICpuSensorSource
    {
        public (HardwareAvailability Availability, string? Detail) ProbeCpuSensors() =>
            (HardwareAvailability.ModuleMissing, "模块缺失");

        public bool TrySampleCpuSensors(out CpuSensorReading reading, out string? error)
        {
            reading = new CpuSensorReading(null, null);
            error = "模块缺失";
            return false;
        }
    }
}
