using Xunit;
using Zentwik.Domain.Power;

namespace Zentwik.Domain.Tests;

public sealed class PowerSchemePresetsTests
{
    [Fact]
    public void BuiltIn_Guids_Are_Recognized()
    {
        Assert.True(PowerSchemePresets.IsBuiltIn(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")));
        Assert.True(PowerSchemePresets.IsBuiltIn(new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")));
        Assert.True(PowerSchemePresets.IsBuiltIn(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")));
        Assert.True(PowerSchemePresets.IsBuiltIn(new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")));
    }

    [Fact]
    public void Random_Guid_Is_Not_BuiltIn() =>
        Assert.False(PowerSchemePresets.IsBuiltIn(Guid.NewGuid()));

    [Fact]
    public void Find_Returns_Preset_Or_Null()
    {
        Assert.Equal("平衡", PowerSchemePresets.Find(PowerSchemePresets.Balanced.Guid)?.Name);
        Assert.Null(PowerSchemePresets.Find(Guid.NewGuid()));
    }
}
