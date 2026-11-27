using Zentwik.Core.Telemetry;
using Zentwik.Platform;
using Xunit;

namespace Zentwik.Core.Tests;

public sealed class TelemetryRingBufferTests
{
    private static TelemetrySample Sample(int cpu) =>
        new(DateTimeOffset.UtcNow, cpu, 0, 0, 0, 1);

    [Fact]
    public void Snapshot_Returns_Oldest_First()
    {
        var buffer = new TelemetryRingBuffer(4);
        buffer.Add(Sample(1));
        buffer.Add(Sample(2));
        buffer.Add(Sample(3));

        var snapshot = buffer.Snapshot(10);

        Assert.Equal(3, snapshot.Length);
        Assert.Equal(1d, snapshot[0].CpuTotalPercent);
        Assert.Equal(3d, snapshot[2].CpuTotalPercent);
    }

    [Fact]
    public void Full_Buffer_Drops_Oldest()
    {
        var buffer = new TelemetryRingBuffer(3);
        buffer.Add(Sample(1));
        buffer.Add(Sample(2));
        buffer.Add(Sample(3));
        buffer.Add(Sample(4));

        var snapshot = buffer.Snapshot(10);

        Assert.Equal(3, snapshot.Length);
        Assert.Equal(2d, snapshot[0].CpuTotalPercent);
        Assert.Equal(4d, snapshot[2].CpuTotalPercent);
    }

    [Fact]
    public void Count_Never_Exceeds_Capacity()
    {
        var buffer = new TelemetryRingBuffer(5);
        for (var index = 0; index < 12; index++)
        {
            buffer.Add(Sample(index));
        }

        Assert.Equal(5, buffer.Count);
        Assert.Equal(5, buffer.Capacity);
    }

    [Fact]
    public void Snapshot_Limit_Returns_Newest_Frames()
    {
        var buffer = new TelemetryRingBuffer(10);
        for (var index = 1; index <= 10; index++)
        {
            buffer.Add(Sample(index));
        }

        var snapshot = buffer.Snapshot(3);

        Assert.Equal(3, snapshot.Length);
        Assert.Equal(8d, snapshot[0].CpuTotalPercent);
        Assert.Equal(10d, snapshot[2].CpuTotalPercent);
    }

    [Fact]
    public void Empty_Buffer_Returns_Empty()
    {
        var buffer = new TelemetryRingBuffer(4);
        Assert.Empty(buffer.Snapshot(10));
        Assert.Equal(0, buffer.Count);
    }
}
