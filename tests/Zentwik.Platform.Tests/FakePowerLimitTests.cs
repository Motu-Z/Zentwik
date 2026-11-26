using Xunit;
using Zentwik.Domain.Power;
using Zentwik.Platform.Fakes;

namespace Zentwik.Platform.Tests;

public sealed class FakePowerLimitTests
{
    [Fact]
    public void Default_Scale_Is_Applied()
    {
        var provider = new FakePowerLimitProvider();

        var result = provider.ApplyPowerLimits(new PowerLimitRequest(100, 90, null, null));

        Assert.All(result.Items, static item => Assert.Equal(PowerLimitWriteState.Applied, item.State));
        Assert.Equal(100, result.After?.FastWatts);
    }

    [Fact]
    public void Scaled_Firmware_Diverges_And_Keeps_Both_Values()
    {
        var provider = new FakePowerLimitProvider { WriteScale = 0.8 };

        var result = provider.ApplyPowerLimits(new PowerLimitRequest(95, 65, null, null));

        var fast = result.Items.Single(static item => item.Item == "fast");
        Assert.Equal(PowerLimitWriteState.Diverged, fast.State);
        Assert.Equal(95, fast.Requested);
        Assert.Equal(76, fast.Actual);
        Assert.Contains("80%", PowerLimitReadBack.DescribeFirmwareScaling(result.Items)!, StringComparison.Ordinal);
    }
}
