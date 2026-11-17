using System.Collections.Generic;
using System.Linq;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>按 EfficiencyClass 分性能核与能效核。</summary>
    public sealed class CpuSetEfficiencyTests
    {
        private static List<CpuSetScheduler.CpuSetEntry> Build(params (uint Id, byte Class)[] items)
        {
            return items
                .Select((item, index) => new CpuSetScheduler.CpuSetEntry(
                    item.Id, (byte)index, 0, 0, item.Class))
                .ToList();
        }

        [Fact]
        public void Homogeneous_machine_has_no_pools()
        {
            List<CpuSetScheduler.CpuSetEntry> entries = Build((1, 0), (2, 0), (3, 0), (4, 0));

            (ulong efficient, ulong performance) = CpuSetScheduler.SplitByEfficiency(
                entries, entries.Select(e => e.Id).ToArray());

            Assert.Equal(0UL, efficient);
            Assert.Equal(0UL, performance);
        }

        [Fact]
        public void Top_class_becomes_performance_pool()
        {
            List<CpuSetScheduler.CpuSetEntry> entries =
                Build((1, 0), (2, 0), (3, 0), (4, 0), (5, 1), (6, 1), (7, 1), (8, 1));

            (ulong efficient, ulong performance) = CpuSetScheduler.SplitByEfficiency(
                entries, entries.Select(e => e.Id).ToArray());

            Assert.Equal(0x0FUL, efficient);
            Assert.Equal(0xF0UL, performance);
        }

        [Fact]
        public void Middle_tier_stays_in_efficiency_pool()
        {
            List<CpuSetScheduler.CpuSetEntry> entries =
                Build((1, 0), (2, 1), (3, 2));

            (ulong efficient, ulong performance) = CpuSetScheduler.SplitByEfficiency(
                entries, entries.Select(e => e.Id).ToArray());

            Assert.Equal(0x03UL, efficient);
            Assert.Equal(0x04UL, performance);
        }

        [Fact]
        public void Bits_stop_at_sixty_four_processors()
        {
            List<CpuSetScheduler.CpuSetEntry> entries = new();
            for (uint id = 1; id <= 70; id++)
                entries.Add(new CpuSetScheduler.CpuSetEntry(id, (byte)(id - 1), 0, 0, (byte)(id > 64 ? 1 : 0)));

            (ulong efficient, ulong performance) = CpuSetScheduler.SplitByEfficiency(
                entries, entries.Select(e => e.Id).ToArray());

            Assert.Equal(ulong.MaxValue, efficient);
            Assert.Equal(0UL, performance);
        }

        [Fact]
        public void Ids_missing_from_the_table_are_skipped()
        {
            List<CpuSetScheduler.CpuSetEntry> entries = Build((1, 0), (2, 1));

            (ulong efficient, ulong performance) = CpuSetScheduler.SplitByEfficiency(
                entries, new[] { 1u, 99u, 2u });

            Assert.Equal(0x01UL, efficient);
            Assert.Equal(0x04UL, performance);
        }
    }
}
