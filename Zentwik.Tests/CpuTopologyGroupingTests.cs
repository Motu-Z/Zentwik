using System.Collections.Generic;
using System.Linq;
using Xunit;
using Zentwik.Core.Services;

namespace Zentwik.Tests
{
    /// <summary>核心分组回退与掩码宽度上限。</summary>
    public sealed class CpuTopologyGroupingTests
    {
        private static CpuSetScheduler.CpuSetEntry Entry(uint id, byte lp, byte llc)
            => new(id, lp, 0, llc, 0);

        /// <summary>每核一个缓存索引是把核心编号塞错字段，按它分等于每核一组，必须回退。</summary>
        [Fact]
        public void Per_core_cache_indices_are_rejected()
        {
            List<CpuSetScheduler.CpuSetEntry> entries = new();
            for (uint i = 0; i < 8; i++)
                entries.Add(Entry(i + 1, (byte)i, (byte)i));

            Assert.Null(CpuSetScheduler.TryGroupByLastLevelCache(entries));
        }

        [Fact]
        public void Single_cache_domain_keeps_every_processor()
        {
            List<CpuSetScheduler.CpuSetEntry> entries = new() { Entry(1, 0, 0), Entry(2, 1, 0), Entry(3, 2, 0) };

            List<uint>[]? groups = CpuSetScheduler.TryGroupByLastLevelCache(entries);

            Assert.NotNull(groups);
            Assert.Single(groups!);
            Assert.Equal(new uint[] { 1, 2, 3 }, groups![0]);
        }

        [Fact]
        public void Domains_are_ordered_by_first_logical_processor()
        {
            // 缓存索引 5 的核排在前面，组 0 应落在它上面，而不是按索引数值排序。
            List<CpuSetScheduler.CpuSetEntry> entries = new()
            {
                Entry(11, 0, 5), Entry(12, 1, 5), Entry(21, 2, 2), Entry(22, 3, 2)
            };

            List<uint>[]? groups = CpuSetScheduler.TryGroupByLastLevelCache(entries);

            Assert.NotNull(groups);
            Assert.Equal(2, groups!.Length);
            Assert.Equal(new uint[] { 11, 12 }, groups[0]);
            Assert.Equal(new uint[] { 21, 22 }, groups[1]);
        }

        [Fact]
        public void Too_few_processors_returns_null()
        {
            Assert.Null(CpuSetScheduler.TryGroupByLastLevelCache(new List<CpuSetScheduler.CpuSetEntry>()));
            Assert.Null(CpuSetScheduler.TryGroupByLastLevelCache(new List<CpuSetScheduler.CpuSetEntry> { Entry(1, 0, 0) }));
        }

        [Fact]
        public void MaskWidth_caps_at_sixty_four_bits()
        {
            Assert.Equal(ulong.MaxValue, CpuSetScheduler.MaskWidth(64));
            Assert.Equal(ulong.MaxValue, CpuSetScheduler.MaskWidth(96));
            Assert.Equal(0xFFFFFFFFUL, CpuSetScheduler.MaskWidth(32));
            Assert.Equal(0UL, CpuSetScheduler.MaskWidth(0));
        }
    }
}
