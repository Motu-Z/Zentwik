using System.Text.Json;
using Xunit;
using Zentwik.Core.Helpers;

namespace Zentwik.Tests
{
    /// <summary>128 位掩码的位运算、文本形式与线格式。</summary>
    public sealed class CoreMaskTests
    {
        [Fact]
        public void Bits_enumerate_ascending_across_words()
        {
            CoreMask mask = new(0b1011UL, 0b11UL);

            Assert.Equal(new[] { 0, 1, 3, 64, 65 }, mask.Bits());
        }

        [Fact]
        public void Pop_count_and_first_bit_span_both_words()
        {
            CoreMask mask = new(0UL, 1UL << 5);

            Assert.Equal(1, mask.PopCount);
            Assert.Equal(69, mask.FirstBit);
            Assert.True(mask.HasHighBits);
        }

        [Fact]
        public void Empty_mask_has_no_first_bit()
        {
            Assert.Equal(-1, CoreMask.Zero.FirstBit);
            Assert.True(CoreMask.Zero.IsZero);
            Assert.False(CoreMask.Zero.HasHighBits);
        }

        [Fact]
        public void Set_operations_keep_high_word()
        {
            CoreMask left = new(0xF0UL, 0x1UL);
            CoreMask right = new(0x0FUL, 0x2UL);

            Assert.Equal(new CoreMask(0xFFUL, 0x3UL), left | right);
            Assert.Equal(new CoreMask(0x0UL, 0x0UL), left & right);
            Assert.Equal(new CoreMask(0xFFUL, 0x3UL), left ^ right);
            Assert.Equal(new CoreMask(~0xF0UL, ~0x1UL), ~left);
        }

        [Fact]
        public void Group_masks_shift_by_group_number()
        {
            // 组 0 与组 1 的位都从 0 起，换算后不能相撞。
            CoreMask first = CoreMask.FromGroupMask(0, 0b11UL);
            CoreMask second = CoreMask.FromGroupMask(1, 0b11UL);

            Assert.Equal(0b11UL, first.Low);
            Assert.Equal(0UL, first.High);
            Assert.Equal(0UL, second.Low);
            Assert.Equal(0b11UL, second.High);
            Assert.Equal(new[] { 0, 1, 64, 65 }, (first | second).Bits());

            // 第三组及以后超出 128 位表达上限，返回空掩码而不是错位。
            Assert.True(CoreMask.FromGroupMask(2, 0b1UL).IsZero);
        }

        [Fact]
        public void Json_round_trips_as_hex_string()
        {
            CoreMask mask = new(0xFUL, 0x3UL);

            string json = JsonSerializer.Serialize(mask);

            Assert.Equal("\"0x0000000000000003000000000000000F\"", json);
            Assert.Equal(mask, JsonSerializer.Deserialize<CoreMask>(json));
        }

        /// <summary>老配置存的是 ulong 数字，读入必须照样成立。</summary>
        [Fact]
        public void Json_reads_legacy_number()
        {
            CoreMask mask = JsonSerializer.Deserialize<CoreMask>("65280");

            Assert.Equal(0xFF00UL, mask.Low);
            Assert.Equal(0UL, mask.High);
        }

        [Fact]
        public void Json_zero_reads_from_empty_string()
        {
            Assert.True(JsonSerializer.Deserialize<CoreMask>("\"\"").IsZero);
        }
    }
}
