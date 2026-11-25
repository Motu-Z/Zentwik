using Xunit;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Tests;

public sealed class AmdCoreMaskTests
{
    [Theory]
    [InlineData(0, 0, 0x0000_0000u)]
    [InlineData(0, 3, 0x0030_0000u)]
    [InlineData(0, 7, 0x0070_0000u)]
    [InlineData(1, 0, 0x1000_0000u)]
    [InlineData(1, 7, 0x1070_0000u)]
    public void Modern_families_encode_core_index_as_number(int ccd, int core, uint expected)
    {
        var address = new AmdCoreAddress(0, ccd, 0, core, 2);

        Assert.Equal(expected, AmdCoreMask.Encode(address, 0x1A));
        Assert.Equal(expected, AmdCoreMask.Encode(address, 0x19));
    }

    [Theory]
    [InlineData(0, 0, 0, 0x0000_0000u)]
    [InlineData(0, 1, 2, 0x0120_0000u)]
    [InlineData(1, 0, 3, 0x1030_0000u)]
    [InlineData(1, 1, 0, 0x1100_0000u)]
    public void Family17h_includes_the_ccx_field(int ccd, int ccx, int core, uint expected)
    {
        var address = new AmdCoreAddress(0, ccd, ccx, core, 2);

        Assert.Equal(expected, AmdCoreMask.Encode(address, 0x17));
    }

    [Fact]
    public void Core_three_is_not_a_one_hot_bit()
    {
        var mask = AmdCoreMask.Encode(new AmdCoreAddress(0, 0, 0, 3, 2), 0x1A);

        Assert.Equal(0x0030_0000u, mask);
        Assert.NotEqual(0x0080_0000u, mask);
    }

    [Fact]
    public void Mask_keeps_the_low_20_bits_free_for_the_margin()
    {
        var mask = AmdCoreMask.Encode(new AmdCoreAddress(0, 1, 0, 6, 2), 0x1A);

        Assert.Equal(0u, mask & 0x000F_FFFFu);
    }
}
