using System.Collections.Generic;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    public class ProcessInstanceLedgerTests
    {
        [Fact]
        public void TakeReused_reports_and_drops_the_pid_whose_creation_time_changed()
        {
            ProcessInstanceLedger ledger = new();
            ledger.Note(100, 1111);
            ledger.Note(200, 2222);

            int[] reused = ledger.TakeReused(pid => pid == 100 ? 9999 : 2222);

            Assert.Equal(new[] { 100 }, reused);

            // 已移除：下一轮不会再被当成复用重复报。
            Assert.Empty(ledger.TakeReused(pid => pid == 100 ? 9999 : 2222));
        }

        [Fact]
        public void TakeReused_adopts_a_late_reading_instead_of_reporting_a_change()
        {
            ProcessInstanceLedger ledger = new();
            ledger.Note(100, 0);

            Assert.Empty(ledger.TakeReused(_ => 1111));

            // 补记之后按真值比对：同一实例不再报。
            Assert.Empty(ledger.TakeReused(_ => 1111));
            Assert.Equal(new[] { 100 }, ledger.TakeReused(_ => 2222));
        }

        [Fact]
        public void TakeReused_never_judges_when_the_reading_fails()
        {
            ProcessInstanceLedger ledger = new();
            ledger.Note(100, 1111);

            Assert.Empty(ledger.TakeReused(_ => 0));

            // 读不到不作数，记录仍在：下一次读到了才比对。
            Assert.Equal(new[] { 100 }, ledger.TakeReused(_ => 2222));
        }

        [Fact]
        public void PruneTo_drops_pids_that_are_gone()
        {
            ProcessInstanceLedger ledger = new();
            ledger.Note(100, 1111);
            ledger.Note(200, 2222);

            ledger.PruneTo(new HashSet<int> { 200 });

            // 只剩 200：一个都会报到，但报的不是已清掉的 100。
            Assert.Equal(new[] { 200 }, ledger.TakeReused(_ => 9999));
        }

        [Fact]
        public void Forget_and_Clear_empty_the_ledger()
        {
            ProcessInstanceLedger ledger = new();
            ledger.Note(100, 1111);
            ledger.Note(200, 2222);

            ledger.Forget(100);
            Assert.Equal(new[] { 200 }, ledger.TakeReused(pid => pid == 200 ? 9999 : 1111));

            ledger.Clear();
            Assert.Empty(ledger.TakeReused(_ => 8888));
        }
    }
}
