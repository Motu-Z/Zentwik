using Zentwik.Platform.Windows;
using Xunit;

namespace Zentwik.Platform.Tests;

public sealed class ProcessorUsageTests
{
    [Fact]
    public void Idle_Core_Is_Zero_Percent()
    {
        // KernelTime 含 IdleTime：全空闲时 kernel == idle，busy = 0。
        Assert.Equal(0d, ProcessorUsage.Percent(idleDelta: 1000, kernelDelta: 1000, userDelta: 0), 3);
    }

    [Fact]
    public void Busy_Core_Is_Hundred_Percent()
    {
        // idle 不走，kernel + user 全是忙。
        Assert.Equal(100d, ProcessorUsage.Percent(idleDelta: 0, kernelDelta: 1000, userDelta: 1000), 3);
    }

    [Fact]
    public void Half_Idle_Is_Scaled_Correctly()
    {
        // kernel 含 500 idle，另有 500 用户时间：total = 1500，busy = 1000。
        Assert.Equal(66.667, ProcessorUsage.Percent(idleDelta: 500, kernelDelta: 1000, userDelta: 500), 3);
    }

    [Fact]
    public void No_Elapsed_Time_Is_Zero()
    {
        // 两个采样点相同（时钟没走或读回同一快照），分母为 0 时不得除零。
        Assert.Equal(0d, ProcessorUsage.Percent(idleDelta: 0, kernelDelta: 0, userDelta: 0), 3);
    }

    [Fact]
    public void Idle_Greater_Than_Total_Clamps_To_Zero()
    {
        // 计数器口径错乱时 busy 会变负，夹到 0 而不是报负数占用。
        Assert.Equal(0d, ProcessorUsage.Percent(idleDelta: 2000, kernelDelta: 1000, userDelta: 0), 3);
    }
}
