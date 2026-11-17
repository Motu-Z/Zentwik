using Xunit;
using Zentwik.Core.Helpers;

namespace Zentwik.Tests
{
    /// <summary>掩码文本解析的边界：64 位上限、区间、混排。</summary>
    public sealed class MaskFormatterTests
    {
        [Fact]
        public void Single_bit_63_is_accepted()
        {
            Assert.True(MaskFormatter.TryParse("63", out ulong mask, out _));
            Assert.Equal(1UL << 63, mask);
        }

        [Fact]
        public void Single_bit_64_is_rejected()
        {
            Assert.False(MaskFormatter.TryParse("64", out _, out string? error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Range_past_63_is_rejected()
        {
            Assert.False(MaskFormatter.TryParse("62-64", out _, out _));
        }

        [Fact]
        public void Reversed_range_is_rejected()
        {
            Assert.False(MaskFormatter.TryParse("8-3", out _, out _));
        }

        [Fact]
        public void Full_range_sets_every_bit()
        {
            Assert.True(MaskFormatter.TryParse("0-63", out ulong mask, out _));
            Assert.Equal(ulong.MaxValue, mask);
        }

        /// <summary>十六进制与区间混排不被接受：「0xFFFF」走不到列表解析，会按非法单值拒掉。</summary>
        [Fact]
        public void Hex_and_range_mixed_is_rejected()
        {
            Assert.False(MaskFormatter.TryParse("0xFFFF,16-23", out _, out _));
        }

        [Fact]
        public void Hex_and_range_are_each_accepted_alone()
        {
            Assert.True(MaskFormatter.TryParse("0xFFFF", out ulong hex, out _));
            Assert.Equal(0xFFFFUL, hex);

            Assert.True(MaskFormatter.TryParse("16-23", out ulong range, out _));
            Assert.Equal(0xFF0000UL, range);
        }

        [Fact]
        public void Zero_mask_is_rejected()
        {
            Assert.False(MaskFormatter.TryParse("0x0", out _, out _));
            Assert.False(MaskFormatter.TryParse("   ", out _, out _));
        }

        [Fact]
        public void Empty_mask_renders_as_none_word()
        {
            Assert.Equal("无", MaskFormatter.ToRangeText(0));
            Assert.Equal("0-7,16-23", MaskFormatter.ToRangeText(0x00FF00FFUL));
        }
    }
}
