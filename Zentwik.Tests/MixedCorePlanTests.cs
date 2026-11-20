using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Tests
{
    /// <summary>混合架构的 P / E 划分：效率类最高的一档算 P，其余归 E。</summary>
    public sealed class MixedCorePlanTests
    {
        private static CoreMask[] Masks(params ulong[] values)
        {
            CoreMask[] masks = new CoreMask[values.Length];

            for (int i = 0; i < values.Length; i++)
                masks[i] = values[i];

            return masks;
        }

        [Fact]
        public void Single_efficiency_class_is_not_hybrid()
        {
            CoreMask[] masks = Masks(0x3UL, 0xCUL, 0x30UL, 0xC0UL);
            int[] classes = { 0, 0, 0, 0 };

            Assert.False(MixedCorePlan.TrySplit(masks, classes, out _, out _));
        }

        [Fact]
        public void Highest_class_becomes_performance_cores()
        {
            // 4 个 P 核各 2 线程，4 个 E 核各 1 线程。
            CoreMask[] masks = Masks(0x3UL, 0xCUL, 0x30UL, 0xC0UL, 0x100UL, 0x200UL, 0x400UL, 0x800UL);
            int[] classes = { 10, 10, 10, 10, 1, 1, 1, 1 };

            Assert.True(MixedCorePlan.TrySplit(masks, classes, out CoreMask perf, out CoreMask eff));
            Assert.Equal(0xFFUL, perf.Low);
            Assert.Equal(0xF00UL, eff.Low);
        }

        [Fact]
        public void Third_tier_folds_into_efficiency()
        {
            // P / E / LP-E 三档：只有最高档算 P，中间档不能留成空档。
            CoreMask[] masks = Masks(0x3UL, 0x4UL, 0x8UL);
            int[] classes = { 10, 5, 1 };

            Assert.True(MixedCorePlan.TrySplit(masks, classes, out CoreMask perf, out CoreMask eff));
            Assert.Equal(0x3UL, perf.Low);
            Assert.Equal(0xCUL, eff.Low);
        }

        [Fact]
        public void Split_survives_cores_above_63()
        {
            // 第 65 个逻辑处理器落在高位字，切分不能把它丢掉。
            CoreMask[] masks = { new(0x3UL), new(0UL, 0x1UL) };
            int[] classes = { 10, 1 };

            Assert.True(MixedCorePlan.TrySplit(masks, classes, out CoreMask perf, out CoreMask eff));
            Assert.Equal(0x3UL, perf.Low);
            Assert.Equal(0x1UL, eff.High);
        }

        [Fact]
        public void Missing_relations_falls_back()
        {
            Assert.False(MixedCorePlan.TrySplit(System.Array.Empty<CoreMask>(), new[] { 1, 0 }, out _, out _));
            Assert.False(MixedCorePlan.TrySplit(Masks(0x3UL, 0xCUL), new[] { 10 }, out _, out _));
        }
    }
}
