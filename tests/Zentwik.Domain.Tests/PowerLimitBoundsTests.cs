using Xunit;
using Zentwik.Domain.Power;

namespace Zentwik.Domain.Tests;

public sealed class PowerLimitBoundsTests
{
    [Fact]
    public void Empty_Request_Is_Rejected()
    {
        Assert.NotNull(PowerLimitBounds.Validate(new PowerLimitRequest(null, null, null, null)));
    }

    [Theory]
    [InlineData(0, 90, null, null)]
    [InlineData(1001, 90, null, null)]
    [InlineData(100, 0, null, null)]
    [InlineData(100, 1001, null, null)]
    public void Out_Of_Range_Watts_Are_Rejected_Without_Clamping(int fast, int slow, int? tdc, int? edc)
    {
        Assert.NotNull(PowerLimitBounds.Validate(new PowerLimitRequest(fast, slow, tdc, edc)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Out_Of_Range_Amps_Are_Rejected(int amps)
    {
        Assert.NotNull(PowerLimitBounds.Validate(new PowerLimitRequest(null, null, amps, amps)));
    }

    [Fact]
    public void Slow_Above_Fast_Is_Rejected()
    {
        var error = PowerLimitBounds.Validate(new PowerLimitRequest(80, 100, null, null));

        Assert.NotNull(error);
        Assert.Contains("SLOW", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_Request_Passes()
    {
        Assert.Null(PowerLimitBounds.Validate(new PowerLimitRequest(120, 100, 180, 220)));
    }

    [Fact]
    public void Slow_Only_Request_Is_Allowed()
    {
        Assert.Null(PowerLimitBounds.Validate(new PowerLimitRequest(null, 95, null, null)));
    }
}

public sealed class PowerLimitReadBackTests
{
    [Fact]
    public void Within_Tolerance_Is_Applied()
    {
        Assert.Equal(PowerLimitWriteState.Applied, PowerLimitReadBack.Classify(100, 101, 68));
    }

    [Fact]
    public void Changed_But_Off_Request_Is_Diverged()
    {
        Assert.Equal(PowerLimitWriteState.Diverged, PowerLimitReadBack.Classify(95, 76, 100));
    }

    [Fact]
    public void Unchanged_After_Write_Is_Rejected()
    {
        Assert.Equal(PowerLimitWriteState.Rejected, PowerLimitReadBack.Classify(95, 100, 100));
    }

    [Fact]
    public void No_ReadBack_Is_Accepted()
    {
        Assert.Equal(PowerLimitWriteState.Accepted, PowerLimitReadBack.Classify(180, null, null));
    }

    [Fact]
    public void Changed_Without_Previous_Value_Is_Diverged()
    {
        Assert.Equal(PowerLimitWriteState.Diverged, PowerLimitReadBack.Classify(95, 76, null));
    }
}
