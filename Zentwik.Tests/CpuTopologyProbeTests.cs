using System;
using System.Linq;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Tests
{
    /// <summary>核关系探针：读不到就跳过，读到就必须自洽。</summary>
    public sealed class CpuTopologyProbeTests
    {
        [Fact]
        public void Core_relations_match_core_count_and_thread_width()
        {
            if (!CpuTopologyProbe.TryReadCores(out CpuTopologyProbe.CoreRelation[] relations))
                return;

            Assert.NotEmpty(relations);
            Assert.All(relations, r => Assert.False(r.Mask.IsZero));

            // 每个核的线程掩码不能重叠，否则位图会被重复计入。
            CoreMask union = relations.Aggregate(CoreMask.Zero, (acc, r) => acc | r.Mask);
            int bits = relations.Sum(r => r.Mask.PopCount);
            Assert.Equal(bits, union.PopCount);

            if (!CpuTopologyProbe.TryRead(out int cores, out int threads))
                return;

            Assert.Equal(relations.Length, cores);
            Assert.Equal(Math.Max(1, relations[0].Mask.PopCount), threads);
        }
    }
}
