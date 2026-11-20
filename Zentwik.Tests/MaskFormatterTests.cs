using Xunit;
using Zentwik.Core.Helpers;

namespace Zentwik.Tests
{
    /// <summary>掩码文本解析的边界：128 位上限、区间、混排。</summary>
    public sealed class MaskFormatterTests
    {
        [Fact]
        public void Single_bit_63_lands_in_low_word()
        {
            Assert.True(MaskFormatter.TryParse("63", out CoreMask mask, out _));
            Assert.Equal(1UL << 63, mask.Low);
            Assert.Equal(0UL, mask.High);
        }

        [Fact]
        public void Single_bit_64_lands_in_high_word()
        {
            Assert.True(MaskFormatter.TryParse("64", out CoreMask mask, out _));
            Assert.Equal(1UL, mask.High);
            Assert.True(mask.HasHighBits);
        }

        [Fact]
        public void Single_bit_127_is_accepted_and_128_is_rejected()
        {
            Assert.True(MaskFormatter.TryParse("127", out CoreMask mask, out _));
            Assert.Equal(1UL << 63, mask.High);

            Assert.False(MaskFormatter.TryParse("128", out _, out string? error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Range_crossing_63_is_accepted()
        {
            Assert.True(MaskFormatter.TryParse("62-65", out CoreMask mask, out _));
            Assert.Equal(3UL << 62, mask.Low);
            Assert.Equal(0b11UL, mask.High);
        }

        [Fact]
        public void Reversed_range_is_rejected()
        {
            Assert.False(MaskFormatter.TryParse("8-3", out _, out _));
        }

        [Fact]
        public void Full_range_sets_every_bit()
        {
            Assert.True(MaskFormatter.TryParse("0-127", out CoreMask mask, out _));
            Assert.Equal(ulong.MaxValue, mask.Low);
            Assert.Equal(ulong.MaxValue, mask.High);
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
            Assert.True(MaskFormatter.TryParse("0xFFFF", out CoreMask hex, out _));
            Assert.Equal(0xFFFFUL, hex.Low);

            Assert.True(MaskFormatter.TryParse("16-23", out CoreMask range, out _));
            Assert.Equal(0xFF0000UL, range.Low);
        }

        [Fact]
        public void Hex_beyond_64_bits_round_trips()
        {
            const string text = "0x0000000000000003000000000000000F";

            Assert.True(MaskFormatter.TryParseHexToken(text, out CoreMask mask));
            Assert.Equal(0xFUL, mask.Low);
            Assert.Equal(3UL, mask.High);
            Assert.Equal("0-3,64-65", MaskFormatter.ToRangeText(mask));
            Assert.Equal(text, mask.ToString());
        }

        [Fact]
        public void Hex_longer_than_128_bits_is_rejected()
        {
            Assert.False(MaskFormatter.TryParseHexToken("0x1FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", out _));
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
            Assert.Equal("无", MaskFormatter.ToRangeText(CoreMask.Zero));
            Assert.Equal("0-7,16-23", MaskFormatter.ToRangeText(0x00FF00FFUL));
        }
    }
}
