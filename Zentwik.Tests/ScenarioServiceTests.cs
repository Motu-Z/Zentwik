using System.IO;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>场景仲裁：三条规则同时命中时，Priority 数字最小的那条赢。</summary>
    public sealed class ScenarioServiceTests : IDisposable
    {
        private readonly FakeTuningStore _tuning = new();
        private readonly FakeAppPaths _paths = new();
        private readonly ScenarioService _scenarios;

        public ScenarioServiceTests()
        {
            FakeLogService log = new();
            TuningApplyService apply = new(log, new FakeUiSettingsStore(), _tuning, new FakeSmuReader());
            PlanService plans = new(
                log, _tuning, new FakeSchedulingStore(), apply, new FakeProcessWatcher(), _paths, new FakePowerPolicyApi());
            _scenarios = new ScenarioService(log, plans, _paths);

            foreach ((string id, int[] co) in new[] { ("p10", new[] { 1, 1 }), ("p5", new[] { 5, 5 }), ("p20", new[] { 2, 2 }) })
                plans.Save(new PlanEntry { Kind = PlanKind.Tuning, Id = id, Name = id, CurveOptimizer = co, FMaxMHz = 4500 });

            foreach ((string id, int priority) in new[] { ("p10", 10), ("p5", 5), ("p20", 20) })
                _scenarios.Save(new ScenarioRule
                {
                    Trigger = ScenarioTrigger.ProcessRunning, Argument = id + ".exe", ProfileId = id, Priority = priority
                });

            _scenarios.SetEnabled(true);
        }

        /// <summary>Save 会把 profiles.json / scenarios.json 写进临时目录，用例结束后整个目录删掉。</summary>
        public void Dispose()
        {
            if (Directory.Exists(_paths.DataDirectory)) Directory.Delete(_paths.DataDirectory, recursive: true);
        }

        [Fact]
        public void Notify_three_hits_picks_smallest_priority()
        {
            _scenarios.Notify([
                (ScenarioTrigger.ProcessRunning, "p10.exe"),
                (ScenarioTrigger.ProcessRunning, "p5.exe"),
                (ScenarioTrigger.ProcessRunning, "p20.exe")]);

            Assert.Equal("p5", _scenarios.LastAppliedProfileId);
            Assert.Equal(new[] { 5, 5 }, _tuning.LastSaved!.CurveOptimizer);
            Assert.Equal(4500u, _tuning.LastSaved!.FMaxMHz);
        }

        [Fact]
        public void Notify_without_smallest_falls_back_to_next()
        {
            _scenarios.Notify([
                (ScenarioTrigger.ProcessRunning, "p10.exe"),
                (ScenarioTrigger.ProcessRunning, "p20.exe")]);

            Assert.Equal("p10", _scenarios.LastAppliedProfileId);
            Assert.Equal(new[] { 1, 1 }, _tuning.LastSaved!.CurveOptimizer);
        }

        [Fact]
        public void Resolve_outcome_carries_all_three_actions()
        {
            _scenarios.Save(new ScenarioRule
            {
                Trigger = ScenarioTrigger.ProcessRunning,
                Argument = "game.exe",
                ProfileId = "p5",
                MaskId = "mask-1",
                PowerSchemeId = "381b4222-f694-41f0-9685-ff5bb260df2e",
                Priority = 1
            });

            ScenarioOutcome? outcome = _scenarios.ResolveOutcome([
                (ScenarioTrigger.ProcessRunning, "game.exe")]);

            Assert.NotNull(outcome);
            Assert.Equal("p5", outcome!.ProfileId);
            Assert.Equal("mask-1", outcome.MaskId);
            Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", outcome.PowerSchemeId);
            Assert.Equal("game.exe", outcome.TriggerArgument);
        }

        [Fact]
        public void Mask_only_rule_still_resolves()
        {
            _scenarios.Save(new ScenarioRule
            {
                Trigger = ScenarioTrigger.ProcessRunning,
                Argument = "sched.exe",
                MaskId = "m1",
                Priority = 1
            });

            ScenarioOutcome? outcome = _scenarios.ResolveOutcome([
                (ScenarioTrigger.ProcessRunning, "sched.exe")]);

            Assert.NotNull(outcome);
            Assert.Equal("m1", outcome!.MaskId);
            Assert.Equal(string.Empty, outcome.ProfileId);
            Assert.Equal(string.Empty, outcome.PowerSchemeId);
        }

        [Fact]
        public void Rule_without_any_action_is_skipped()
        {
            // 三个动作全空的规则命中了也不知道该干什么，必须让位给后面的规则。
            _scenarios.Save(new ScenarioRule
            {
                Trigger = ScenarioTrigger.ProcessRunning,
                Argument = "idle.exe",
                Priority = 0
            });

            Assert.Null(_scenarios.ResolveOutcome([
                (ScenarioTrigger.ProcessRunning, "idle.exe")]));
        }

        [Fact]
        public void Exit_rule_resolves_only_on_exit_trigger()
        {
            _scenarios.Save(new ScenarioRule
            {
                Trigger = ScenarioTrigger.ProcessExited,
                Argument = "game.exe",
                ProfileId = "p20",
                Priority = 1
            });

            // 进程还在跑：退出规则不该命中，否则「游戏退出时切回平衡」
            // 会在游戏启动那一刻就生效。
            Assert.Null(_scenarios.ResolveOutcome([
                (ScenarioTrigger.ProcessRunning, "game.exe")]));

            ScenarioOutcome? outcome = _scenarios.ResolveOutcome([
                (ScenarioTrigger.ProcessExited, "game.exe")]);

            Assert.NotNull(outcome);
            Assert.Equal("p20", outcome!.ProfileId);
        }

        [Fact]
        public void Exit_argument_is_watched_for_diffing()
        {
            // 监视方靠这份名单做「上一拍在、这一拍不在」的差集，
            // 停用或删掉的规则不能继续留在名单里。
            _scenarios.Save(new ScenarioRule
            {
                Trigger = ScenarioTrigger.ProcessExited,
                Argument = "watched.exe",
                ProfileId = "p5",
                Priority = 1
            });

            Assert.Contains("watched.exe", _scenarios.ProcessExitArguments);
            Assert.True(_scenarios.HasProcessRules);

            ScenarioRule stored = _scenarios.Rules.First(r => r.Argument == "watched.exe");
            stored.Enabled = false;
            _scenarios.Save(stored);

            Assert.DoesNotContain("watched.exe", _scenarios.ProcessExitArguments);
        }
    }
}
