using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;
using Zentwik.Worker;

namespace Zentwik.Tests
{
    /// <summary>
    /// apply-tuning 分派的三态映射（applied / skipped / failed）。
    ///
    /// 【为什么不 mock TuningApplyService】
    /// 它是具体类、<c>ApplyFromStore</c> 不是 virtual，没有可替换的接口。
    /// 这里用真实的 TuningApplyService + 四个 Fake 依赖驱动，比 mock 更贴近实际：
    /// 尤其「PawnIO 未就绪」这条路径，它返回的是**失败**而不是 Skipped ——
    /// 这个区别正是 UI 要区分显示的东西，也是最容易被写错的地方。
    /// </summary>
    public sealed class ApplyTuningDispatchTests
    {
        /// <summary>按给定条件装配一个真实的 TuningApplyService。</summary>
        private static TuningApplyService BuildService(
            bool applyOnStartup,
            bool smuAvailable,
            int[]? curveOptimizer = null,
            uint fMax = 0)
        {
            FakeUiSettingsStore settings = new();
            settings.Settings.ApplyTuningOnStartup = applyOnStartup;

            FakeTuningStore tuning = new();
            tuning.Save(new TuningProfile
            {
                CurveOptimizer = curveOptimizer ?? new[] { -10, -20 },
                FMaxMHz = fMax
            });

            return new TuningApplyService(
                new FakeLogService(),
                settings,
                tuning,
                new StubSmuReader(smuAvailable));
        }

        [Fact]
        public void ApplyOnStartupDisabled_MapsToSkipped()
        {
            TuningApplyService service = BuildService(applyOnStartup: false, smuAvailable: true);

            TuningApplyOutcome outcome = service.ApplyFromStore();
            Assert.True(outcome.Skipped);
            Assert.False(outcome.Success);

            PipeResponse response = PipeServerService.BuildApplyTuningResponse(outcome);

            Assert.False(response.Success);
            Assert.Equal("skipped", response.Payload);
            Assert.Contains("启动时自动应用调校", response.Message, StringComparison.Ordinal);
            Assert.Equal(WorkerPipeContract.ProtocolVersion, response.Version);
        }

        [Fact]
        public void PawnIoUnavailable_MapsToFailed_NotSkipped()
        {
            TuningApplyService service = BuildService(applyOnStartup: true, smuAvailable: false);

            TuningApplyOutcome outcome = service.ApplyFromStore();

            // 关键断言：这是「失败」，不是「跳过」。UI 的三态显示依赖这个区别。
            Assert.False(outcome.Skipped);
            Assert.False(outcome.Success);

            PipeResponse response = PipeServerService.BuildApplyTuningResponse(outcome);

            Assert.False(response.Success);
            Assert.Equal("failed", response.Payload);
            Assert.Contains("PawnIO", response.Message, StringComparison.Ordinal);
            Assert.Equal(WorkerPipeContract.ProtocolVersion, response.Version);
        }

        [Fact]
        public void SmuAvailable_MapsToApplied()
        {
            TuningApplyService service = BuildService(
                applyOnStartup: true,
                smuAvailable: true,
                curveOptimizer: new[] { -10, -20 },
                fMax: 4700);

            TuningApplyOutcome outcome = service.ApplyFromStore();
            Assert.True(outcome.Success);

            PipeResponse response = PipeServerService.BuildApplyTuningResponse(outcome);

            Assert.True(response.Success);
            Assert.Equal("applied", response.Payload);
            Assert.Contains("CO", response.Message, StringComparison.Ordinal);
            Assert.Contains("FMax", response.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 显式「应用」必须绕过「启动时自动应用调校」开关：
        /// 用户关掉的是开机自动行为，不是调校页的按钮。
        /// </summary>
        [Fact]
        public void IgnoreStartupGate_WritesEvenWhenSettingDisabled()
        {
            TuningApplyService service = BuildService(applyOnStartup: false, smuAvailable: true);

            TuningApplyOutcome outcome = service.ApplyFromStore(ignoreStartupGate: true);

            Assert.False(outcome.Skipped);
            Assert.True(outcome.Success);
        }

        [Fact]
        public void QuerySmuStatus_Available_ReportsAvailableTrue()
        {
            PipeResponse response = PipeServerService.BuildSmuStatusResponse(new StubSmuReader(true));

            // Success 只表示「查询成功」，可用性在 Payload 里 —— 两者不能合并。
            Assert.True(response.Success);
            Assert.Equal(WorkerPipeContract.ProtocolVersion, response.Version);
            Assert.Contains("\"available\":true", response.Payload!, StringComparison.Ordinal);
        }

        [Fact]
        public void QuerySmuStatus_Unavailable_ReportsFalseWithReason()
        {
            PipeResponse response = PipeServerService.BuildSmuStatusResponse(new StubSmuReader(false));

            Assert.True(response.Success);
            Assert.Contains("\"available\":false", response.Payload!, StringComparison.Ordinal);
            // LastError 原文要透传给 UI，否则界面只能说「不可用」而说不出为什么。
            Assert.Contains("PawnIO", response.Payload!, StringComparison.Ordinal);
        }

        [Fact]
        public void QuerySmuSnapshot_Success_ReturnsPayload()
        {
            SmuSnapshot snapshot = new()
            {
                Success = true,
                CoreMargins = new[] { -10, -20 },
                FMaxMHz = 4700
            };

            PipeResponse response = PipeServerService.BuildSmuSnapshotResponse(snapshot);

            Assert.True(response.Success);
            Assert.Contains("\"fmaxMhz\":4700", response.Payload!, StringComparison.Ordinal);
            Assert.Contains("-10", response.Payload!, StringComparison.Ordinal);
        }

        [Fact]
        public void QuerySmuSnapshot_Failure_ReturnsFalseWithMessage()
        {
            SmuSnapshot snapshot = new() { Success = false, Error = "SMU 探测失败（模拟）" };

            PipeResponse response = PipeServerService.BuildSmuSnapshotResponse(snapshot);

            Assert.False(response.Success);
            Assert.Contains("SMU 探测失败", response.Message, StringComparison.Ordinal);
            Assert.Null(response.Payload);
        }

        /// <summary>
        /// apply-tuning 必须先从盘上重读 tuning.json，再交给 TuningApplyService。
        /// 这里把「分派层持有的 store」与「TuningApplyService 内部的 store」拆成两个实例，
        /// 断言只有前者被 Load —— 否则测不出重读到底发生在哪一层。
        /// </summary>
        [Fact]
        public async Task ApplyTuning_ReloadsStoreBeforeApplying()
        {
            FakeTuningStore dispatchStore = new();

            FakeUiSettingsStore settings = new();
            settings.Settings.ApplyTuningOnStartup = true;

            FakeTuningStore applyStore = new();
            applyStore.Save(new TuningProfile { CurveOptimizer = new[] { -10 }, FMaxMHz = 0 });

            TuningApplyService applyService = new(
                new FakeLogService(),
                settings,
                applyStore,
                new StubSmuReader(true));

            PipeServerService service = BuildServer(applyService, new StubSmuReader(true), dispatchStore, BuildPlans(dispatchStore, applyService));

            PipeResponse response = await service.RunApplyTuningAsync(null, CancellationToken.None);

            Assert.True(response.Success);
            Assert.Equal("applied", response.Payload);
            Assert.Equal(1, dispatchStore.LoadCount);
        }

        /// <summary>装配一个真实的 PlanService（七个依赖里六个是 Fake）。</summary>
        private static PlanService BuildPlans(
            ITuningStore tuning,
            TuningApplyService apply,
            IAppPaths? paths = null,
            ISchedulingStore? scheduling = null)
            => new(
                new FakeLogService(),
                tuning,
                scheduling ?? new FakeSchedulingStore(),
                apply,
                new FakeProcessWatcher(),
                paths ?? new FakeAppPaths(),
                new FakePowerPolicyApi());

        private static PipeServerService BuildServer(
            TuningApplyService apply,
            ISmuReader smu,
            ITuningStore tuning,
            PlanService plans,
            IProcessWatcher? watcher = null,
            ISchedulingStore? bindings = null,
            IMaskStore? masks = null,
            ICpuSetScheduler? cpuSets = null,
            ScenarioService? scenarios = null,
            PowerPolicyService? power = null,
            IUiSettingsStore? settings = null)
            => new(
                new FakeLogService(),
                new FakeHostApplicationLifetime(),
                new AutoStartService(new FakeLogService()),
                apply,
                smu,
                tuning,
                plans,
                watcher ?? new FakeProcessWatcher(),
                new ProcessRefresher(new FakeLogService()),
                bindings ?? new FakeSchedulingStore(),
                masks ?? new FakeMaskStore(),
                cpuSets ?? new FakeCpuSetScheduler(),
                scenarios ?? new ScenarioService(new FakeLogService(), plans, new FakeAppPaths()),
                power ?? new PowerPolicyService(new FakePowerPolicyApi()),
                settings ?? new FakeUiSettingsStore());

        /// <summary>
        /// apply-tuning 的 id 路径：Worker 调 PlanService.Apply，把方案写进 tuning.json 再写 SMU。
        /// 这里把「启动时自动应用调校」关掉，用来同时验证门控被绕开 —— 若没绕开，
        /// 结果会是 skipped 而不是 applied。
        /// </summary>
        [Fact]
        public async Task ApplyTuning_ById_AppliesProfileAndBypassesStartupGate()
        {
            FakeUiSettingsStore settings = new();
            settings.Settings.ApplyTuningOnStartup = false;

            FakeTuningStore tuning = new();
            tuning.Save(new TuningProfile { CurveOptimizer = new[] { -10, -20 }, FMaxMHz = 4700 });

            TuningApplyService apply = new(
                new FakeLogService(),
                settings,
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            // CaptureCurrent 从当前 tuning.Profile 取值，所以上面要先 Save 一份。
            PlanEntry snapshot = plans.CaptureCurrent(PlanKind.Tuning, "测试方案");
            plans.Save(snapshot);

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            PipeResponse response = await service.RunApplyTuningAsync(snapshot.Id, CancellationToken.None);

            Assert.True(response.Success);
            Assert.Equal("applied", response.Payload);
        }

        [Fact]
        public async Task ApplyTuning_UnknownId_Fails()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            PipeResponse response = await service.RunApplyTuningAsync("不存在的Id", CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("failed", response.Payload);
            Assert.Contains("配置不存在", response.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RenameProfile_Success_RenamesAndPersists()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PlanEntry snapshot = plans.CaptureCurrent(PlanKind.Tuning, "旧名字");
            plans.Save(snapshot);

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            string payload = JsonSerializer.Serialize(
                new RenamePlanPayload { Kind = PlanKinds.Tuning, Id = snapshot.Id, NewName = "新名字" },
                IpcJsonContext.Default.RenamePlanPayload);

            PipeResponse response = await service.RunRenamePlanAsync(payload);

            Assert.True(response.Success);
            Assert.Equal("新名字", plans.Find(PlanKind.Tuning, snapshot.Id)!.Name);
        }

        [Fact]
        public async Task RenameProfile_EmptyName_Fails()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            string payload = JsonSerializer.Serialize(
                new RenamePlanPayload { Kind = PlanKinds.Tuning, Id = "任意", NewName = "   " },
                IpcJsonContext.Default.RenamePlanPayload);

            PipeResponse response = await service.RunRenamePlanAsync(payload);

            Assert.False(response.Success);
            Assert.Contains("不能为空", response.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CreateProfile_Success_AddsToList()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            string payload = JsonSerializer.Serialize(
                new SavePlanPayload
                {
                    Kind = PlanKinds.Tuning,
                    Name = "新方案",
                    CoreMargins = new[] { -5, -6 },
                    FMaxMHz = 4800
                },
                IpcJsonContext.Default.SavePlanPayload);

            PipeResponse response = await service.RunSavePlanAsync(payload, CancellationToken.None);

            Assert.True(response.Success);

            PlanEntry? created = plans.Items(PlanKind.Tuning).FirstOrDefault(p => p.Name == "新方案");
            Assert.NotNull(created);
            // Id / 时间戳由 PlanEntry 初始化器给默认值，不由 UI 传。
            Assert.False(string.IsNullOrEmpty(created!.Id));
            Assert.Equal(4800u, created.FMaxMHz);
            Assert.Equal(new[] { -5, -6 }, created.CurveOptimizer);
        }

        [Fact]
        public async Task CreateProfile_EmptyName_Fails()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            string payload = JsonSerializer.Serialize(
                new SavePlanPayload { Kind = PlanKinds.Tuning, Name = "   " },
                IpcJsonContext.Default.SavePlanPayload);

            PipeResponse response = await service.RunSavePlanAsync(payload, CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("不能为空", response.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DeleteProfile_Success_RemovesFromList()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PlanEntry snapshot = plans.CaptureCurrent(PlanKind.Tuning, "待删除");
            plans.Save(snapshot);

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            string payload = JsonSerializer.Serialize(
                new PlanRefPayload { Kind = PlanKinds.Tuning, Id = snapshot.Id },
                IpcJsonContext.Default.PlanRefPayload);

            PipeResponse response = await service.RunDeletePlanAsync(payload, CancellationToken.None);

            Assert.True(response.Success);
            Assert.Null(plans.Find(PlanKind.Tuning, snapshot.Id));
        }

        [Fact]
        public async Task DeleteProfile_UnknownId_Fails()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            string payload = JsonSerializer.Serialize(
                new PlanRefPayload { Kind = PlanKinds.Tuning, Id = "不存在" },
                IpcJsonContext.Default.PlanRefPayload);

            PipeResponse response = await service.RunDeletePlanAsync(payload, CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("方案不存在", response.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 删除生效中的方案：ActiveId 被清空，方案本身从列表消失，
        /// 但**硬件设置不回滚**（这里只断言前两条 —— 回滚与否由 PlanService 的注释定案）。
        /// </summary>
        [Fact]
        public async Task DeleteActiveProfile_ClearsActiveId()
        {
            FakeTuningStore tuning = new();
            tuning.Save(new TuningProfile { CurveOptimizer = new[] { -10 }, FMaxMHz = 0 });

            TuningApplyService apply = new(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();

            PlanEntry snapshot = plans.CaptureCurrent(PlanKind.Tuning, "活动方案");
            plans.Save(snapshot);
            Assert.True(plans.Apply(PlanKind.Tuning, snapshot.Id).Success);
            Assert.Equal(snapshot.Id, plans.ActiveId(PlanKind.Tuning));

            PipeServerService service = BuildServer(apply, new StubSmuReader(true), tuning, plans);

            string payload = JsonSerializer.Serialize(
                new PlanRefPayload { Kind = PlanKinds.Tuning, Id = snapshot.Id },
                IpcJsonContext.Default.PlanRefPayload);

            PipeResponse response = await service.RunDeletePlanAsync(payload, CancellationToken.None);

            Assert.True(response.Success);
            Assert.Null(plans.ActiveId(PlanKind.Tuning));
            Assert.Null(plans.Find(PlanKind.Tuning, snapshot.Id));
        }

        // ==================== 进程列表 / 解绑 ====================

        private static ProcessRefresher.Entry Entry(int pid, string name)
            => new() { Pid = pid, Name = name, Path = $@"C://x//{name}", Cpu = 1.5, PriorityClass = 32 };

        [Fact]
        public void BuildProcessEntries_MatchedRule_FillsName()
        {
            List<ScheduleRule> rules = new()
            {
                new ScheduleRule { Name = "chrome.exe", Target = SchedulingTarget.CCD0, Enabled = true }
            };

            List<ProcessEntryPayload> items = PipeServerService.BuildProcessEntries(
                new[] { Entry(100, "chrome.exe"), Entry(200, "notepad.exe") },
                rules);

            Assert.Equal(2, items.Count);
            Assert.Equal("chrome.exe", items[0].MatchedRuleName);
            Assert.Null(items[1].MatchedRuleName);
        }

        [Fact]
        public void BuildProcessEntries_DisabledRule_NotMatched()
        {
            List<ScheduleRule> rules = new()
            {
                new ScheduleRule { Name = "chrome.exe", Target = SchedulingTarget.CCD0, Enabled = false }
            };

            List<ProcessEntryPayload> items = PipeServerService.BuildProcessEntries(
                new[] { Entry(100, "chrome.exe") },
                rules);

            // Enabled=false 的规则不参与匹配 —— 与真正下发绑定的口径一致。
            Assert.Null(items[0].MatchedRuleName);
        }

        [Fact]
        public async Task ClearAllSchedules_EmptyPayload_ClearSchedulesEveryBoundPid()
        {
            FakeProcessWatcher watcher = new();
            Assert.True(watcher.SimulateBind(100));
            Assert.True(watcher.SimulateBind(200));

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                watcher);

            PipeResponse response = await service.RunClearAllSchedulesAsync(null);

            Assert.True(response.Success);
            Assert.Contains("2", response.Message, StringComparison.Ordinal);
            Assert.Empty(watcher.GetBoundProcessIds());
        }

        [Fact]
        public async Task ClearAllSchedules_WithPid_ClearSchedulesOnlyThatPid()
        {
            FakeProcessWatcher watcher = new();
            watcher.SimulateBind(100);
            watcher.SimulateBind(200);

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                watcher);

            PipeResponse response = await service.RunClearAllSchedulesAsync("100");

            Assert.True(response.Success);
            Assert.Equal(new[] { 200 }, watcher.GetBoundProcessIds());
        }

        [Fact]
        public async Task ClearAllSchedules_InvalidPayload_Fails()
        {
            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()));

            PipeResponse response = await service.RunClearAllSchedulesAsync("abc");

            Assert.False(response.Success);
            Assert.Contains("进程 Id", response.Message, StringComparison.Ordinal);
        }

        // ==================== 调度规则增删 ====================

        private static string SaveRulePayload(string processName, string type, string value, string priority = "")
            => JsonSerializer.Serialize(
                new SaveScheduleRulePayload
                {
                    ProcessName = processName,
                    TargetType = type,
                    TargetValue = value,
                    Priority = priority
                },
                IpcJsonContext.Default.SaveScheduleRulePayload);

        /// <summary>
        /// CCD 目标：写盘 + 重扫必须成对发生。
        /// 只写盘不重扫的话，改一条已开着的游戏的规则要等它下次启动才生效。
        /// </summary>
        [Fact]
        public async Task SaveScheduleRule_Ccd_PersistsAndReapplies()
        {
            FakeSchedulingStore bindings = new();
            FakeProcessWatcher watcher = new();

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                watcher, bindings);

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("game.exe", "ccd", "CCD0", "High"),
                CancellationToken.None);

            Assert.True(response.Success);
            Assert.Equal(1, watcher.ReapplyCount);

            ScheduleRule saved = Assert.Single(bindings.LastSaved!);
            Assert.Equal("game.exe", saved.Name);
            Assert.Equal(SchedulingTarget.CCD0, saved.Target);
            Assert.Equal(ProcessPriority.High, saved.Priority);
            Assert.True(saved.Enabled);
        }

        /// <summary>
        /// 掩码目标：必须把掩码值一起快照进规则，而不是只存一个 Id。
        /// 掩码日后被删掉时，快照是这条规则还能下发下去的唯一依据。
        /// </summary>
        [Fact]
        public async Task SaveScheduleRule_Mask_SnapshotsMaskValue()
        {
            FakeMaskStore masks = new();
            CustomMask mask = masks.Seed("纯大核", 0x00FF);

            FakeSchedulingStore bindings = new();

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                new FakeProcessWatcher(), bindings, masks);

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("cs2.exe", "mask", mask.Id),
                CancellationToken.None);

            Assert.True(response.Success);

            ScheduleRule saved = Assert.Single(bindings.LastSaved!);
            Assert.Equal(SchedulingTarget.TargetMask, saved.Target);
            Assert.Equal(mask.Id, saved.MaskId);
            Assert.Equal(0x00FFUL, saved.TargetMask);
            Assert.Equal("纯大核", saved.MaskName);
        }

        [Fact]
        public async Task SaveScheduleRule_UnknownMask_Fails()
        {
            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()));

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("cs2.exe", "mask", "不存在的Id"),
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("掩码不存在", response.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SaveScheduleRule_UnknownTargetType_Fails()
        {
            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()));

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("cs2.exe", "ccx", "0"),
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("targetType", response.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 优先级必须归一化成 ProcessPriority 的英文常量再落盘：
        /// ClassOf 是大小写敏感的 switch，存 "high" 会让规则在下发时静默失效。
        /// </summary>
        [Fact]
        public async Task SaveScheduleRule_PriorityNormalizedToCanonical()
        {
            FakeSchedulingStore bindings = new();

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                new FakeProcessWatcher(), bindings);

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("cs2.exe", "ccd", "1", "above normal"),
                CancellationToken.None);

            Assert.True(response.Success);

            ScheduleRule saved = Assert.Single(bindings.LastSaved!);
            Assert.Equal(ProcessPriority.AboveNormal, saved.Priority);
            Assert.Equal(SchedulingTarget.CCD1, saved.Target);
        }

        [Fact]
        public async Task SaveScheduleRule_UnknownPriority_Fails()
        {
            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()));

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("cs2.exe", "ccd", "0", "RealTime"),
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("优先级", response.Message, StringComparison.Ordinal);
        }

        /// <summary>同名规则是覆盖而不是追加：进程名就是规则的键。</summary>
        [Fact]
        public async Task SaveScheduleRule_SameProcessName_Overwrites()
        {
            FakeSchedulingStore bindings = new();

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                new FakeProcessWatcher(), bindings);

            await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("cs2.exe", "ccd", "0"), CancellationToken.None);

            await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("CS2.EXE", "ccd", "1"), CancellationToken.None);

            ScheduleRule saved = Assert.Single(bindings.LastSaved!);
            Assert.Equal(SchedulingTarget.CCD1, saved.Target);
        }

        [Fact]
        public async Task DeleteScheduleRule_Existing_RemovesAndReapplies()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[]
            {
                new ScheduleRule { Name = "cs2.exe", Target = SchedulingTarget.CCD0 },
                new ScheduleRule { Name = "chrome.exe", Target = SchedulingTarget.CCD1 }
            });

            FakeProcessWatcher watcher = new();

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                watcher, bindings);

            PipeResponse response = await service.RunDeleteScheduleRuleAsync("CS2.exe", CancellationToken.None);

            Assert.True(response.Success);
            Assert.Equal(1, watcher.ReapplyCount);
            Assert.Equal("chrome.exe", Assert.Single(bindings.LastSaved!).Name);
        }

        [Fact]
        public async Task DeleteScheduleRule_Missing_Fails()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[] { new ScheduleRule { Name = "cs2.exe", Target = SchedulingTarget.CCD0 } });

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                new FakeProcessWatcher(), bindings);

            PipeResponse response = await service.RunDeleteScheduleRuleAsync("notepad.exe", CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("规则不存在", response.Message, StringComparison.Ordinal);
        }

        // ==================== 调度规则查询 ====================

        /// <summary>
        /// 三种目标形态的编码约定：ccd 给索引数字、mask 给掩码值、none 给空串。
        ///
        /// 【为什么这条必须钉住】
        /// UI 侧按 targetType 决定用 int.Parse 还是 ulong.Parse。编码一变，
        /// UI 那边是**运行时异常**而不是编译错误 —— 列表页会直接崩在解析上。
        /// </summary>
        [Fact]
        public async Task QueryScheduleRules_EncodesThreeTargetShapes()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[]
            {
                new ScheduleRule { Name = "a.exe", Target = SchedulingTarget.CCD0, Priority = ProcessPriority.High, Path = @"C:\a.exe" },
                new ScheduleRule { Name = "b.exe", Target = SchedulingTarget.CCD1, Enabled = false },
                new ScheduleRule { Name = "c.exe", Target = SchedulingTarget.TargetMask, TargetMask = 0xFF00, MaskId = "m1", MaskName = "纯大核" },
                new ScheduleRule { Name = "d.exe", Target = SchedulingTarget.None }
            });

            PipeServerService service = BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                new FakeProcessWatcher(), bindings);

            PipeResponse response = await Task.FromResult(service.RunQuerySchedulingRules());

            Assert.True(response.Success);
            Assert.NotNull(response.Payload);

            List<ScheduleRulePayload>? items = JsonSerializer.Deserialize(
                response.Payload!, IpcJsonContext.Default.ListScheduleRulePayload);

            Assert.NotNull(items);
            Assert.Equal(4, items!.Count);

            Assert.Equal("ccd", items[0].TargetType);
            Assert.Equal("0", items[0].TargetValue);
            Assert.Equal(ProcessPriority.High, items[0].Priority);
            Assert.True(items[0].Enabled);
            Assert.Equal(@"C:\a.exe", items[0].Path);

            Assert.Equal("ccd", items[1].TargetType);
            Assert.Equal("1", items[1].TargetValue);
            Assert.False(items[1].Enabled);

            // mask 给的是掩码**值**的十进制字符串，不是掩码 Id。
            Assert.Equal("mask", items[2].TargetType);
            Assert.Equal("65280", items[2].TargetValue);

            Assert.Equal("none", items[3].TargetType);
            Assert.Equal(string.Empty, items[3].TargetValue);
        }

        // ==================== 场景规则查询 / 增删 ====================

        private static (PipeServerService Server, ScenarioService Scenarios, PlanService Plans) BuildScenarioServer()
        {
            FakeTuningStore tuning = new();
            TuningApplyService apply = new(
                new FakeLogService(), new FakeUiSettingsStore(), tuning, new StubSmuReader(true));

            PlanService plans = BuildPlans(tuning, apply);
            plans.Load();
            plans.Save(new PlanEntry
            {
                Kind = PlanKind.Tuning,
                Id = "p1",
                Name = "性能",
                CurveOptimizer = new[] { -10, -10 },
                FMaxMHz = 4700
            });

            ScenarioService scenarios = new(new FakeLogService(), plans, new FakeAppPaths());

            PipeServerService server = BuildServer(
                apply, new StubSmuReader(true), tuning, plans, scenarios: scenarios);

            return (server, scenarios, plans);
        }

        private static string ScenarioPayload(
            string id, ScenarioTrigger trigger, string argument, string profileId,
            int priority = 0, bool enabled = true)
            => JsonSerializer.Serialize(
                new ScenarioRulePayload
                {
                    Id = id,
                    Trigger = trigger,
                    Argument = argument,
                    ProfileId = profileId,
                    Priority = priority,
                    Enabled = enabled
                },
                IpcJsonContext.Default.ScenarioRulePayload);

        /// <summary>
        /// trigger 必须以**名称**上线，不能是 0/1/2。
        /// 数字形态在协议里不可读，而且往枚举中间插一个值就是一次静默的语义漂移。
        /// </summary>
        [Fact]
        public void QueryScenarioRules_SerializesTriggerAsName()
        {
            (PipeServerService server, ScenarioService scenarios, _) = BuildScenarioServer();

            scenarios.Save(new ScenarioRule
            {
                Id = "r1", Trigger = ScenarioTrigger.ProcessRunning, Argument = "game.exe",
                ProfileId = "p1", Priority = 5
            });
            scenarios.Save(new ScenarioRule
            {
                Id = "r2", Trigger = ScenarioTrigger.OnBattery, Argument = string.Empty, ProfileId = "p1"
            });

            PipeResponse response = server.RunQueryScenarioRules();

            Assert.True(response.Success);
            Assert.Contains("\"ProcessRunning\"", response.Payload!, StringComparison.Ordinal);
            Assert.Contains("\"OnBattery\"", response.Payload!, StringComparison.Ordinal);

            List<ScenarioRulePayload>? items = JsonSerializer.Deserialize(
                response.Payload!, IpcJsonContext.Default.ListScenarioRulePayload);

            Assert.NotNull(items);
            Assert.Equal(2, items!.Count);
            Assert.Equal(ScenarioTrigger.ProcessRunning, items[0].Trigger);
            Assert.Equal("game.exe", items[0].Argument);
            Assert.Equal(5, items[0].Priority);
            Assert.True(items[0].Enabled);
        }

        /// <summary>
        /// Id 为空 = 新建，且 Id 由模型初始化器生成 —— 不能被空串覆盖。
        /// 覆盖了就会得到一条 Id 为空的规则，下次 Save 又当它是新的，列表里长出一堆空 Id。
        /// </summary>
        [Fact]
        public async Task SaveScenarioRule_EmptyId_CreatesWithGeneratedId()
        {
            (PipeServerService server, ScenarioService scenarios, _) = BuildScenarioServer();

            PipeResponse response = await server.RunSaveScenarioRuleAsync(
                ScenarioPayload(string.Empty, ScenarioTrigger.ProcessRunning, "game.exe", "p1", 3),
                CancellationToken.None);

            Assert.True(response.Success);

            ScenarioRule created = Assert.Single(scenarios.Rules);
            Assert.False(string.IsNullOrWhiteSpace(created.Id));
            Assert.Equal(32, created.Id.Length);
            Assert.Equal("game.exe", created.Argument);
            Assert.Equal("p1", created.ProfileId);
            Assert.Equal(3, created.Priority);
        }

        [Fact]
        public async Task SaveScenarioRule_ExistingId_UpdatesInPlace()
        {
            (PipeServerService server, ScenarioService scenarios, _) = BuildScenarioServer();

            scenarios.Save(new ScenarioRule
            {
                Id = "r1", Trigger = ScenarioTrigger.ProcessRunning, Argument = "old.exe", ProfileId = "p1"
            });

            PipeResponse response = await server.RunSaveScenarioRuleAsync(
                ScenarioPayload("r1", ScenarioTrigger.ProcessRunning, "new.exe", "p1", 7, enabled: false),
                CancellationToken.None);

            Assert.True(response.Success);

            ScenarioRule updated = Assert.Single(scenarios.Rules);
            Assert.Equal("r1", updated.Id);
            Assert.Equal("new.exe", updated.Argument);
            Assert.Equal(7, updated.Priority);
            Assert.False(updated.Enabled);
        }

        /// <summary>指向不存在方案的规则会让命中时静默什么都不做，必须在写入前拦住。</summary>
        [Fact]
        public async Task SaveScenarioRule_UnknownProfile_Fails()
        {
            (PipeServerService server, ScenarioService scenarios, _) = BuildScenarioServer();

            PipeResponse response = await server.RunSaveScenarioRuleAsync(
                ScenarioPayload(string.Empty, ScenarioTrigger.ProcessRunning, "game.exe", "不存在"),
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("方案不存在", response.Message, StringComparison.Ordinal);
            Assert.Empty(scenarios.Rules);
        }

        /// <summary>进程类触发没有进程名 = 一条永远不可能命中的规则。</summary>
        [Fact]
        public async Task SaveScenarioRule_ProcessTriggerWithoutArgument_Fails()
        {
            (PipeServerService server, _, _) = BuildScenarioServer();

            PipeResponse response = await server.RunSaveScenarioRuleAsync(
                ScenarioPayload(string.Empty, ScenarioTrigger.ProcessRunning, "  ", "p1"),
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("argument", response.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DeleteScenarioRule_Existing_Removes()
        {
            (PipeServerService server, ScenarioService scenarios, _) = BuildScenarioServer();

            scenarios.Save(new ScenarioRule
            {
                Id = "r1", Trigger = ScenarioTrigger.OnAcPower, Argument = string.Empty, ProfileId = "p1"
            });

            PipeResponse response = await server.RunDeleteScenarioRuleAsync("r1", CancellationToken.None);

            Assert.True(response.Success);
            Assert.Empty(scenarios.Rules);
        }

        [Fact]
        public async Task DeleteScenarioRule_Missing_Fails()
        {
            (PipeServerService server, _, _) = BuildScenarioServer();

            PipeResponse response = await server.RunDeleteScenarioRuleAsync("nope", CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("规则不存在", response.Message, StringComparison.Ordinal);
        }

        // ==================== 规则 Id / 按 Id 删除 / 启用开关 ====================

        /// <summary>装配一台只关心规则存储的 Worker（不需要场景、掩码之外的东西）。</summary>
        private static PipeServerService BuildRuleServer(FakeSchedulingStore bindings, FakeProcessWatcher watcher)
            => BuildServer(
                NewApplyService(), new StubSmuReader(true), new FakeTuningStore(),
                BuildPlans(new FakeTuningStore(), NewApplyService()),
                watcher, bindings);

        private static string SaveRulePayloadWithId(string id, string processName, bool? enabled = null)
            => JsonSerializer.Serialize(
                new SaveScheduleRulePayload
                {
                    Id = id,
                    ProcessName = processName,
                    TargetType = "ccd",
                    TargetValue = "CCD0",
                    Enabled = enabled
                },
                IpcJsonContext.Default.SaveScheduleRulePayload);

        /// <summary>
        /// 按进程名覆盖时必须保留原 Id。
        ///
        /// 【为什么这条是关键】
        /// Id 是「这条记录是谁」。保存一次就换一个 Id，界面刚拿到的 Id 立刻失效，
        /// 下一次按 Id 删除会 404 —— 表现是「删了没反应，刷新一下规则还在」。
        /// </summary>
        [Fact]
        public async Task SaveScheduleRule_ByNameOverwrite_KeepsExistingId()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[]
            {
                new ScheduleRule { Id = "keepme", Name = "game.exe", Target = SchedulingTarget.CCD1 }
            });

            PipeServerService service = BuildRuleServer(bindings, new FakeProcessWatcher());

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayload("game.exe", "ccd", "CCD0"), CancellationToken.None);

            Assert.True(response.Success);

            ScheduleRule saved = Assert.Single(bindings.LastSaved!);
            Assert.Equal("keepme", saved.Id);
            Assert.Equal(SchedulingTarget.CCD0, saved.Target);
        }

        /// <summary>
        /// 带 Id 保存 = 改这一条，即使顺带改了进程名也不新增。
        /// </summary>
        [Fact]
        public async Task SaveScheduleRule_ById_RenamesInPlace()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[]
            {
                new ScheduleRule { Id = "abc123", Name = "old.exe", Target = SchedulingTarget.CCD1 }
            });

            PipeServerService service = BuildRuleServer(bindings, new FakeProcessWatcher());

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayloadWithId("abc123", "new.exe"), CancellationToken.None);

            Assert.True(response.Success);

            ScheduleRule saved = Assert.Single(bindings.LastSaved!);
            Assert.Equal("abc123", saved.Id);
            Assert.Equal("new.exe", saved.Name);
        }

        /// <summary>
        /// enabled 必须能从保存侧传进来：查询侧一直返回它，
        /// 保存侧没有入参时，「查出来 → 改一个字段 → 存回去」会把已禁用的规则改回启用。
        /// </summary>
        [Fact]
        public async Task SaveScheduleRule_EnabledFalse_PersistsDisabled()
        {
            FakeSchedulingStore bindings = new();
            PipeServerService service = BuildRuleServer(bindings, new FakeProcessWatcher());

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayloadWithId(string.Empty, "cs2.exe", enabled: false), CancellationToken.None);

            Assert.True(response.Success);
            Assert.False(Assert.Single(bindings.LastSaved!).Enabled);
        }

        /// <summary>enabled 留空 = 不动它；新建时按启用处理（与旧 UI 的行为一致）。</summary>
        [Fact]
        public async Task SaveScheduleRule_EnabledNull_DefaultsToEnabled()
        {
            FakeSchedulingStore bindings = new();
            PipeServerService service = BuildRuleServer(bindings, new FakeProcessWatcher());

            PipeResponse response = await service.RunSaveScheduleRuleAsync(
                SaveRulePayloadWithId(string.Empty, "cs2.exe"), CancellationToken.None);

            Assert.True(response.Success);
            Assert.True(Assert.Single(bindings.LastSaved!).Enabled);
        }

        /// <summary>按 Id 删除：即使这条规则已经被改过名，删的仍然是同一行。</summary>
        [Fact]
        public async Task DeleteScheduleRule_ById_RemovesEvenAfterRename()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[]
            {
                new ScheduleRule { Id = "abc123", Name = "renamed.exe", Target = SchedulingTarget.CCD0 }
            });

            FakeProcessWatcher watcher = new();
            PipeServerService service = BuildRuleServer(bindings, watcher);

            PipeResponse response = await service.RunDeleteScheduleRuleAsync("abc123", CancellationToken.None);

            Assert.True(response.Success);
            Assert.Empty(bindings.LastSaved!);
            Assert.Equal(1, watcher.ReapplyCount);
        }

        /// <summary>旧 UI 只发进程名，这条回退必须继续成立。</summary>
        [Fact]
        public async Task DeleteScheduleRule_FallsBackToName_ForLegacyClients()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[]
            {
                new ScheduleRule { Id = "abc123", Name = "msedge.exe", Target = SchedulingTarget.CCD0 }
            });

            PipeServerService service = BuildRuleServer(bindings, new FakeProcessWatcher());

            PipeResponse response = await service.RunDeleteScheduleRuleAsync("MSEDGE.EXE", CancellationToken.None);

            Assert.True(response.Success);
            Assert.Empty(bindings.LastSaved!);
        }

        [Fact]
        public async Task QueryScheduleRules_ReturnsRuleId()
        {
            FakeSchedulingStore bindings = new();
            bindings.Save(new[]
            {
                new ScheduleRule { Id = "id-1", Name = "a.exe", Target = SchedulingTarget.CCD0 }
            });

            PipeServerService service = BuildRuleServer(bindings, new FakeProcessWatcher());

            PipeResponse response = await Task.FromResult(service.RunQuerySchedulingRules());
            List<ScheduleRulePayload>? items = JsonSerializer.Deserialize(
                response.Payload!, IpcJsonContext.Default.ListScheduleRulePayload);

            Assert.Equal("id-1", Assert.Single(items!).Id);
        }

        private static TuningApplyService NewApplyService()
        {
            FakeTuningStore tuning = new();
            return new TuningApplyService(
                new FakeLogService(),
                new FakeUiSettingsStore(),
                tuning,
                new StubSmuReader(true));
        }

        /// <summary>可切换可用性的 ISmuReader：可用时两个写入都成功，不可用时 WaitForAvailability 返回 false。</summary>
        private sealed class StubSmuReader : ISmuReader
        {
            private readonly bool _available;

            public StubSmuReader(bool available) => _available = available;

            public bool IsAvailable => _available;
            public string? LastError => _available ? null : "StubSmuReader：模拟 PawnIO 未就绪";
            public int CoreCount => 2;
            public uint CoresPerCcd => 2;
            public uint CoresPerCcdRaw => 2;
            public uint PeekCoresPerCcdRaw() => 2;

            public SmuSnapshot ReadSnapshot() => new() { Success = true };

            public SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins) => new() { AllSucceeded = true };

            public SmuFMaxResult WriteFMax(uint mhz) => new() { AllSucceeded = true, Requested = mhz, ReadBack = mhz };

            public bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default) => _available;

            public void Dispose() { }
        }
    }
}
