using Xunit;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Tests;

/// <summary>逐核频率的采样目标：一个物理核一次，标识与 CO 的核编号同源。</summary>
public sealed class AmdClockTargetTests
{
    [Fact]
    public void One_Target_Per_Physical_Core_Not_Per_Thread()
    {
        var map = AmdCoreMap.Create(
            [
                new AmdCoreAddress(0, 0, 0, 0, 2),
                new AmdCoreAddress(1, 0, 0, 0, 2),
                new AmdCoreAddress(2, 0, 0, 1, 2),
                new AmdCoreAddress(3, 0, 0, 1, 2),
                new AmdCoreAddress(4, 1, 0, 0, 2),
                new AmdCoreAddress(5, 1, 0, 0, 2),
            ],
            AmdCoreMapSource.WindowsTopology,
            "test");

        var targets = AmdSensorSource.BuildClockTargets(map);

        // SMT 兄弟线程共享核心时钟，读一个就够；只读第一个线程，避免把成本翻倍。
        Assert.Equal([0, 2, 4], targets.Select(static target => target.Processor));
        Assert.Equal([(0, 0), (0, 1), (1, 0)], targets.Select(static target => (target.Ccd, target.Core)));
        Assert.Equal((UIntPtr)1, targets[0].Mask);
        Assert.Equal((UIntPtr)0b100, targets[1].Mask);
        Assert.Equal((UIntPtr)0b10000, targets[2].Mask);
    }

    [Fact]
    public void Unusable_Map_Yields_No_Targets()
    {
        Assert.Empty(AmdSensorSource.BuildClockTargets(AmdCoreMap.Empty));
    }
}
