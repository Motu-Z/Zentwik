using System.Collections.Generic;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>调度链路的三块纯逻辑：失败重试排期、回读比较、场景去重键。</summary>
    public sealed class SchedulingRobustnessTests
    {
        [Fact]
        public void Retry_bumps_and_schedules_after_delay()
        {
            ScheduleRetryLedger ledger = new(maxAttempts: 3, baseDelayMs: 1000);
            HashSet<int> alive = new() { 42 };

            Assert.Equal(1, ledger.Bump(42, "a.exe", 0));
            Assert.Empty(ledger.Due(alive, 999));
            Assert.Single(ledger.Due(alive, 1000));
        }

        [Fact]
        public void Retry_stops_after_max_attempts()
        {
            ScheduleRetryLedger ledger = new(maxAttempts: 2, baseDelayMs: 1000);
            HashSet<int> alive = new() { 7 };

            ledger.Bump(7, "a.exe", 0);
            Assert.Single(ledger.Due(alive, 1000));

            ledger.Bump(7, "a.exe", 2000);
            Assert.Single(ledger.Due(alive, 4000));

            ledger.Bump(7, "a.exe", 5000);
            Assert.Empty(ledger.Due(alive, 60_000));
        }

        [Fact]
        public void Retry_skips_pid_that_is_gone()
        {
            ScheduleRetryLedger ledger = new(baseDelayMs: 1000);
            HashSet<int> alive = new();

            ledger.Bump(9, "a.exe", 0);
            Assert.Empty(ledger.Due(alive, 5000));

            ledger.Remove(9);
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void Cpu_sets_match_ignores_order()
        {
            Assert.True(CpuSetScheduler.CpuSetsMatch(new uint[] { 3, 1, 2 }, new[] { 2, 3, 1 }));
            Assert.False(CpuSetScheduler.CpuSetsMatch(new uint[] { 1, 2 }, new[] { 1, 2, 3 }));
            Assert.False(CpuSetScheduler.CpuSetsMatch(new uint[] { 1, 2 }, new[] { 1, 4 }));
        }

        [Fact]
        public void Scheduling_key_distinguishes_process_instances()
        {
            string first = ScenarioService.SchedulingKey("mask", "game.exe", new[] { 100, 200 }, _ => 0);
            string same = ScenarioService.SchedulingKey("mask", "game.exe", new[] { 200, 100 }, _ => 0);
            string restarted = ScenarioService.SchedulingKey("mask", "game.exe", new[] { 300 }, _ => 0);

            Assert.Equal(first, same);
            Assert.NotEqual(first, restarted);
        }

        [Fact]
        public void Scenario_ledger_takes_only_the_given_rule()
        {
            ScenarioBindingLedger ledger = new();
            ledger.Note(11, "rule-a");
            ledger.Note(12, "rule-b");
            ledger.Note(13, "rule-a");

            int[] taken = ledger.Take("rule-a");

            Assert.Equal(new[] { 11, 13 }, taken.OrderBy(v => v).ToArray());
            Assert.Equal(1, ledger.Count);
            Assert.True(ledger.Contains(12));
        }

        [Fact]
        public void Scenario_ledger_takes_everything_when_owner_is_null()
        {
            ScenarioBindingLedger ledger = new();
            ledger.Note(11, "rule-a");
            ledger.Note(12, "rule-b");

            Assert.Equal(2, ledger.Take(null).Length);
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void Scenario_ledger_drops_pid_that_is_gone()
        {
            ScenarioBindingLedger ledger = new();
            ledger.Note(11, "rule-a");
            ledger.Note(12, "rule-b");

            ledger.PruneTo(new HashSet<int> { 12 });

            Assert.Equal(new[] { 12 }, ledger.ToArray());
        }
    }
}
