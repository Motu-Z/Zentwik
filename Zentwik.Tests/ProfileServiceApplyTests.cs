using System.IO;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>
    /// PlanService.Apply：三类方案各写各的那一块 store，互不覆盖。
    /// </summary>
    /// <remarks>
    /// 【这组测试守的是「一改全改」】
    /// 旧实现里一份方案同时含调校与调度，切一次就写两份 store —— 用户只想换负压，
    /// 进程页的绑核被一起换掉。这里逐条断言「应用 A 类方案时 B 类 store 没被动过」。
    /// </remarks>
    public sealed class PlanServiceApplyTests : IDisposable
    {
        private readonly FakeAppPaths _paths = new();
        private readonly FakeTuningStore _tuning = new();
        private readonly FakeSchedulingStore _scheduling = new();
        private readonly FakeProcessWatcher _watcher = new();
        private readonly FakePowerPolicyApi _powerApi = new();
        private readonly PlanService _plans;

        public PlanServiceApplyTests()
        {
            _plans = CreateService();
            _plans.Load();

            _plans.Save(new PlanEntry
            {
                Id = "t1",
                Kind = PlanKind.Tuning,
                Name = "性能",
                CurveOptimizer = [5, 5, 5, 5],
                FMaxMHz = 5000
            });

            _plans.Save(new PlanEntry
            {
                Id = "t2",
                Kind = PlanKind.Tuning,
                Name = "静音",
                CurveOptimizer = [-10, -10, -10, -10],
                FMaxMHz = 4800
            });

            _plans.Save(new PlanEntry
            {
                Id = "s1",
                Kind = PlanKind.Scheduling,
                Name = "游戏绑核",
                ScheduleRules = [new ScheduleRule { Name = "game.exe", Target = SchedulingTarget.CCD0 }]
            });

            _plans.Save(new PlanEntry
            {
                Id = "w1",
                Kind = PlanKind.Power,
                Name = "平衡",
                PowerSchemeGuid = FakePowerPolicyApi.BalancedScheme.ToString("D")
            });
        }

        public void Dispose()
        {
            if (Directory.Exists(_paths.DataDirectory))
                Directory.Delete(_paths.DataDirectory, recursive: true);
        }

        private PlanService CreateService() => new(
            new FakeLogService(),
            _tuning,
            _scheduling,
            new TuningApplyService(new FakeLogService(), new FakeUiSettingsStore(), _tuning,
                                   new FakeSmuReader { Available = true }),
            _watcher,
            _paths,
            _powerApi);

        [Fact]
        public void Apply_tuning_writes_only_tuning_store()
        {
            Assert.True(_plans.Apply(PlanKind.Tuning, "t1").Success);

            Assert.Equal(new[] { 5, 5, 5, 5 }, _tuning.LastSaved!.CurveOptimizer);
            Assert.Equal(5000u, _tuning.LastSaved!.FMaxMHz);

            // 关键：调度 store 一次都没被写过。
            Assert.Null(_scheduling.LastSaved);
            Assert.Equal(0, _watcher.ReapplyCount);
        }

        [Fact]
        public void Apply_scheduling_writes_only_scheduling_store()
        {
            Assert.True(_plans.Apply(PlanKind.Scheduling, "s1").Success);

            Assert.Equal("game.exe", Assert.Single(_scheduling.LastSaved!).Name);

            // 调校 store 没被碰过：切调度方案不该改负压。
            Assert.Null(_tuning.LastSaved);
        }

        /// <summary>
        /// 切换调度方案必须触发一次重扫。
        ///
        /// 【为什么这条单独测】
        /// WMI 事件只覆盖「之后启动的进程」。不补这一次重扫，用户看到的会是
        /// 「规则写了，但游戏还绑在旧核心上」—— 要等下次重启才生效。
        /// 写盘正确 ≠ 规则生效，这两件事必须分别断言。
        /// </summary>
        [Fact]
        public void Apply_scheduling_triggers_reapply_so_running_processes_follow()
        {
            int before = _watcher.ReapplyCount;

            Assert.True(_plans.Apply(PlanKind.Scheduling, "s1").Success);

            Assert.Equal(before + 1, _watcher.ReapplyCount);
        }

        [Fact]
        public void Apply_tuning_switches_between_two_tuning_plans()
        {
            Assert.True(_plans.Apply(PlanKind.Tuning, "t1").Success);
            Assert.True(_plans.Apply(PlanKind.Tuning, "t2").Success);

            Assert.Equal(new[] { -10, -10, -10, -10 }, _tuning.LastSaved!.CurveOptimizer);
            Assert.Equal(4800u, _tuning.LastSaved!.FMaxMHz);
        }

        [Fact]
        public void Apply_power_switches_system_scheme()
        {
            Assert.True(_plans.Apply(PlanKind.Power, "w1").Success);
            Assert.Contains(FakePowerPolicyApi.BalancedScheme, _powerApi.CalledGuids);
        }

        [Fact]
        public void Active_id_is_tracked_per_kind()
        {
            Assert.True(_plans.Apply(PlanKind.Tuning, "t1").Success);
            Assert.True(_plans.Apply(PlanKind.Scheduling, "s1").Success);

            Assert.Equal("t1", _plans.ActiveId(PlanKind.Tuning));
            Assert.Equal("s1", _plans.ActiveId(PlanKind.Scheduling));

            // 切调度方案不能把调校的生效标记带跑。
            Assert.Single(_plans.Items(PlanKind.Tuning), p => p.IsActive);
            Assert.Single(_plans.Items(PlanKind.Scheduling), p => p.IsActive);
        }

        /// <summary>三类 Id 各自独立：拿调校方案的 Id 去删调度方案必须删不到。</summary>
        [Fact]
        public void Ids_are_scoped_per_kind()
        {
            Assert.False(_plans.Delete(PlanKind.Scheduling, "t1"));
            Assert.NotNull(_plans.Find(PlanKind.Tuning, "t1"));
        }

        /// <summary>方案写入后必须落盘；重新 Load 仍能读到（否则重启即丢）。</summary>
        [Fact]
        public void Saved_plans_survive_reload()
        {
            _plans.Save(new PlanEntry
            {
                Id = "t3",
                Kind = PlanKind.Tuning,
                Name = "临时",
                CurveOptimizer = [1, 2],
                FMaxMHz = 4000
            });

            PlanService reloaded = CreateService();
            reloaded.Load();

            PlanEntry? entry = reloaded.Find(PlanKind.Tuning, "t3");
            Assert.NotNull(entry);
            Assert.Equal("临时", entry!.Name);
            Assert.Equal(new[] { 1, 2 }, entry.CurveOptimizer);
        }

        /// <summary>
        /// 用户把方案全删了、或旧迁移写错了 Id，plans.json 里会留下悬空的生效标记。
        /// 它不会自己消失：删同类方案时按 Id 相等才清，悬空值永远匹配不上，
        /// 界面上就一直显示「没有生效方案」。读盘必须自愈，并把脏值从盘上抹掉。
        /// </summary>
        [Fact]
        public void Dangling_active_id_is_pruned_on_load()
        {
            string file = Path.Combine(_paths.DataDirectory, "plans.json");
            Directory.CreateDirectory(_paths.DataDirectory);

            JsonFile.Write(file, new PlanFile
            {
                Plans = [new PlanEntry { Id = "t1", Kind = PlanKind.Tuning, Name = "性能" }],
                ActiveTuningId = "t1",
                ActiveSchedulingId = "ghost-id",
            }, AppJsonContext.Default.PlanFile);

            PlanService reloaded = CreateService();
            reloaded.Load();

            Assert.Equal("t1", reloaded.ActiveId(PlanKind.Tuning));
            Assert.Null(reloaded.ActiveId(PlanKind.Scheduling));
            Assert.Single(reloaded.Items(PlanKind.Tuning), p => p.IsActive);
            Assert.DoesNotContain("ghost-id", File.ReadAllText(file));
        }

        /// <summary>删掉生效中的方案，标记必须一起清掉，且不能带跑别的类别。</summary>
        [Fact]
        public void Deleting_active_plan_clears_only_its_own_marker()
        {
            Assert.True(_plans.Apply(PlanKind.Tuning, "t1").Success);
            Assert.True(_plans.Apply(PlanKind.Scheduling, "s1").Success);

            Assert.True(_plans.Delete(PlanKind.Scheduling, "s1"));

            Assert.Null(_plans.ActiveId(PlanKind.Scheduling));
            Assert.Equal("t1", _plans.ActiveId(PlanKind.Tuning));
        }
    }
}
