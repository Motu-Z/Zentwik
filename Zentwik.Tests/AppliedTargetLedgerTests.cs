using System.Collections.Generic;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>重复下发抑制：只有目标、方式、优先级都没变才跳过。</summary>
    public sealed class AppliedTargetLedgerTests
    {
        private static AppliedTargetLedger.Target Sample(ulong mask = 0xF0, bool byAffinityMask = false, string priority = "High")
            => new(mask, byAffinityMask, priority);

        [Fact]
        public void Same_target_matches()
        {
            AppliedTargetLedger ledger = new();
            ledger.Note(100, Sample());

            Assert.True(ledger.Matches(100, Sample()));
        }

        [Fact]
        public void Any_difference_does_not_match()
        {
            AppliedTargetLedger ledger = new();
            ledger.Note(100, Sample());

            Assert.False(ledger.Matches(100, Sample(mask: 0xF)));
            Assert.False(ledger.Matches(100, Sample(byAffinityMask: true)));
            Assert.False(ledger.Matches(100, Sample(priority: "Normal")));
            Assert.False(ledger.Matches(200, Sample()));
        }

        [Fact]
        public void Forget_makes_target_apply_again()
        {
            AppliedTargetLedger ledger = new();
            ledger.Note(100, Sample());
            ledger.Forget(100);

            Assert.False(ledger.Matches(100, Sample()));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void Prune_drops_pid_that_is_gone()
        {
            AppliedTargetLedger ledger = new();
            ledger.Note(100, Sample());
            ledger.Note(200, Sample());

            ledger.PruneTo(new HashSet<int> { 200 });

            Assert.False(ledger.Matches(100, Sample()));
            Assert.True(ledger.Matches(200, Sample()));
        }

        [Fact]
        public void Clear_resets_everything()
        {
            AppliedTargetLedger ledger = new();
            ledger.Note(100, Sample());
            ledger.Clear();

            Assert.False(ledger.Matches(100, Sample()));
        }
    }
}
