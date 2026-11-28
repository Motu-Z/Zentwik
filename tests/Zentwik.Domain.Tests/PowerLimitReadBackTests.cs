using Xunit;
using Zentwik.Domain.Power;

namespace Zentwik.Domain.Tests;

/// <summary>
/// 写入回读的判定。核心命题：「回读没变」本身不构成本次写入失败的证据——
/// 重复应用、折算后恰好等于原值都会让回读不动，把它们报成 rejected 会误导使用者。
/// </summary>
public sealed class PowerLimitReadBackTests
{
    [Fact]
    public void Within_Tolerance_Is_Applied()
    {
        var (state, reason) = PowerLimitReadBack.ClassifyDetailed(100, 101, 68);

        Assert.Equal(PowerLimitWriteState.Applied, state);
        Assert.Equal(PowerLimitReadBackReason.Applied, reason);
    }

    [Fact]
    public void Changed_But_Off_Request_Is_Diverged()
    {
        var (state, reason) = PowerLimitReadBack.ClassifyDetailed(95, 76, 100);

        Assert.Equal(PowerLimitWriteState.Diverged, state);
        Assert.Equal(PowerLimitReadBackReason.Changed, reason);
    }

    [Fact]
    public void Changed_Without_Previous_Value_Is_Diverged()
    {
        var (state, reason) = PowerLimitReadBack.ClassifyDetailed(95, 76, null);

        Assert.Equal(PowerLimitWriteState.Diverged, state);
        Assert.Equal(PowerLimitReadBackReason.Changed, reason);
    }

    [Fact]
    public void Unchanged_Without_Any_Explanation_Is_Rejected()
    {
        var (state, reason) = PowerLimitReadBack.ClassifyDetailed(95, 100, 100);

        Assert.Equal(PowerLimitWriteState.Rejected, state);
        Assert.Equal(PowerLimitReadBackReason.Unexplained, reason);
    }

    [Fact]
    public void No_ReadBack_Is_Accepted()
    {
        var (state, _) = PowerLimitReadBack.ClassifyDetailed(180, null, null);

        Assert.Equal(PowerLimitWriteState.Accepted, state);
    }

    [Fact]
    public void Repeating_The_Same_Request_Is_Not_A_Failure()
    {
        // 先写过 125 得到 100，再写一次 125：回读仍是 100，与写前一致。
        var (state, reason) = PowerLimitReadBack.ClassifyDetailed(125, 100, 100, lastRequested: 125);

        Assert.Equal(PowerLimitWriteState.Diverged, state);
        Assert.Equal(PowerLimitReadBackReason.Repeated, reason);
    }

    [Fact]
    public void Known_Scale_Predicting_The_Same_Value_Is_Not_A_Failure()
    {
        // 已知本机折算 0.8，写 125 预测落在 100，而现值正是 100——换了个请求值但结果相同。
        var (state, reason) = PowerLimitReadBack.ClassifyDetailed(125, 100, 100, knownScale: 0.8);

        Assert.Equal(PowerLimitWriteState.Diverged, state);
        Assert.Equal(PowerLimitReadBackReason.ScaledToSame, reason);
    }

    [Fact]
    public void Known_Scale_That_Does_Not_Predict_The_Value_Is_Rejected()
    {
        // 折算 0.8 下写 125 应落在 100，但现值是 90：解释不了，不能谎报成功。
        var (state, reason) = PowerLimitReadBack.ClassifyDetailed(125, 90, 90, knownScale: 0.8);

        Assert.Equal(PowerLimitWriteState.Rejected, state);
        Assert.Equal(PowerLimitReadBackReason.Unexplained, reason);
    }

    [Fact]
    public void Repeated_Request_Wins_Over_Scale()
    {
        // 两条判据同时成立时按「重复应用」解释，说明更贴近使用者的实际操作。
        var (_, reason) = PowerLimitReadBack.ClassifyDetailed(125, 100, 100, knownScale: 0.8, lastRequested: 125);

        Assert.Equal(PowerLimitReadBackReason.Repeated, reason);
    }

    [Fact]
    public void Classify_Still_Returns_State_Only()
    {
        Assert.Equal(PowerLimitWriteState.Diverged, PowerLimitReadBack.Classify(125, 100, 100, knownScale: 0.8));
        Assert.Equal(PowerLimitWriteState.Rejected, PowerLimitReadBack.Classify(50, 100, 100));
    }
}

public sealed class PowerLimitScalingTests
{
    [Fact]
    public void Uniform_Ratio_Is_Reported_As_Percentage()
    {
        var note = PowerLimitReadBack.DescribeFirmwareScaling(
        [
            new PowerLimitWrite("fast", 95, 76, PowerLimitWriteState.Diverged, null),
            new PowerLimitWrite("slow", 65, 52, PowerLimitWriteState.Diverged, null),
        ]);

        Assert.NotNull(note);
        Assert.Contains("80%", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_Ratio_Is_Not_Collapsed_Into_One_Number()
    {
        var note = PowerLimitReadBack.DescribeFirmwareScaling(
        [
            new PowerLimitWrite("fast", 100, 90, PowerLimitWriteState.Diverged, null),
            new PowerLimitWrite("slow", 100, 50, PowerLimitWriteState.Diverged, null),
        ]);

        Assert.NotNull(note);
        Assert.Contains("不同比例", note, StringComparison.Ordinal);
    }

    [Fact]
    public void No_Diverged_Item_Produces_No_Note()
    {
        Assert.Null(PowerLimitReadBack.DescribeFirmwareScaling(
        [
            new PowerLimitWrite("fast", 100, 100, PowerLimitWriteState.Applied, null),
            new PowerLimitWrite("tdc", 180, null, PowerLimitWriteState.Accepted, null),
        ]));
    }

    [Fact]
    public void Diverged_Without_Readback_Produces_No_Note()
    {
        Assert.Null(PowerLimitReadBack.DescribeFirmwareScaling(
            [new PowerLimitWrite("fast", 100, null, PowerLimitWriteState.Diverged, null)]));
    }
}
