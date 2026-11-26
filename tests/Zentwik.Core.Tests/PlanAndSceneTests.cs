using Xunit;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.Scenes;
using Zentwik.Core.State;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Persistence;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Platform;
using Zentwik.Platform.Fakes;

namespace Zentwik.Core.Tests;

public sealed class PlanAndSceneTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"zentwik-core-{Guid.NewGuid():N}");
    private DiagnosticLog _log = null!;
    private StateStore _store = null!;
    private FakeCpuTuningProvider _tuning = null!;
    private FakePowerStatus _powerStatus = null!;
    private PlanCoordinator _plans = null!;
    private SceneEngine _scenes = null!;

    public async Task InitializeAsync()
    {
        _log = new DiagnosticLog(_directory, echoToConsole: false);
        _store = new StateStore(_directory, _log);
        await _store.LoadAsync();

        _tuning = new FakeCpuTuningProvider(4);
        _powerStatus = new FakePowerStatus(onAc: true);
        _plans = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), new FakeProcessScheduler(), _log);
        _scenes = new SceneEngine(_store, _plans, new FakeProcessScheduler(), _powerStatus, _log);
    }

    public Task DisposeAsync()
    {
        _log.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Apply_Writes_Verifies_And_Persists_State()
    {
        var plan = await SeedPlanAsync();

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        Assert.True(_tuning.TryReadOffsets(out var offsets, out _));
        Assert.Equal(-25, offsets.Single(static offset => offset.Core == 0).Offset);

        var reloaded = await ReloadAsync();
        Assert.Equal(PlanApplyState.Applied, reloaded.Plans[0].LastApply?.State);
        var entry = Assert.Single(reloaded.Ledger);
        Assert.Equal(LedgerStatus.Applied, entry.Status);
        Assert.Equal("-25", entry.ActualValue);
    }

    [Fact]
    public async Task Unavailable_Provider_Fails_Without_Writing_Ledger()
    {
        var plan = await SeedPlanAsync();
        var coordinator = new PlanCoordinator(
            _store,
            new UnavailableTuningProvider(),
            new FakePowerPolicyService(),
            new FakeProcessScheduler(),
            _log);

        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Failed, outcome.State);
        Assert.Empty(outcome.Targets);
        Assert.Contains("驱动", outcome.Error ?? string.Empty, StringComparison.Ordinal);

        var reloaded = await ReloadAsync();
        Assert.Equal(PlanApplyState.Failed, reloaded.Plans[0].LastApply?.State);
        Assert.Empty(reloaded.Ledger);
    }

    [Fact]
    public async Task User_Owned_Target_Is_Not_Overwritten()
    {
        var plan = await SeedPlanAsync();
        await _store.UpdateAsync(document => document with
        {
            Ledger =
            [
                new LedgerEntry
                {
                    Target = "cpu.co.0",
                    Source = "user",
                    Owner = OwnerKind.User,
                    Priority = int.MaxValue,
                    Status = LedgerStatus.Applied,
                    ActualValue = "0",
                    Restorable = false,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });

        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), new FakeProcessScheduler(), _log);
        var outcome = await coordinator.ApplyAsync(plan.Id, "scene:test", 10);

        Assert.Equal(PlanApplyState.NotApplied, outcome.State);
        Assert.True(_tuning.TryReadOffsets(out var offsets, out _));
        Assert.Equal(0, offsets.Single(static offset => offset.Core == 0).Offset);
        Assert.Equal("用户已接管。", outcome.Targets[0].Failure);
    }

    [Fact]
    public async Task Scene_Switches_By_Priority_And_Restores_Previous_Writes()
    {
        await SeedPlanAsync();
        await SeedBatteryPlanAsync();
        await SeedScenesAsync();

        await _scenes.EvaluateAsync();
        Assert.Equal("ac", _scenes.ActiveSceneId);
        Assert.Equal(-25, ReadOffset(0));

        _powerStatus.OnAcPower = false;
        await _scenes.EvaluateAsync();

        Assert.Equal("battery", _scenes.ActiveSceneId);
        Assert.Equal(-10, ReadOffset(0));

        _powerStatus.OnAcPower = true;
        await _scenes.EvaluateAsync();

        Assert.Equal("ac", _scenes.ActiveSceneId);
        Assert.Equal(-25, ReadOffset(0));

        var entry = Assert.Single(_plans.Entries);
        Assert.Equal("scene:ac", entry.Source);
        Assert.Equal(LedgerStatus.Applied, entry.Status);
    }

    [Fact]
    public async Task Reapply_After_Revert_Takes_Fresh_Baseline()
    {
        var plan = await SeedPlanAsync();
        await _plans.ApplyAsync(plan.Id, $"plan:{plan.Id}", int.MaxValue - 1);
        await _plans.RevertPlanAsync(plan.Id);
        Assert.Equal(0, ReadOffset(0));

        // 用户在此期间手动改动，回退后账本的前值已过时。
        _tuning.TryWriteOffset(0, -12, out _);

        await _plans.ApplyAsync(plan.Id, $"plan:{plan.Id}", int.MaxValue - 1);

        var entry = _plans.Entries.Single(static candidate => candidate.Target == "cpu.co.0");
        Assert.Equal("-12", entry.PreviousValue);

        await _plans.RevertPlanAsync(plan.Id);
        Assert.Equal(-12, ReadOffset(0));
    }

    [Fact]
    public async Task Drift_Detection_Marks_User_Takeover()
    {
        var plan = await SeedPlanAsync();
        await SeedScenesAsync();
        await _scenes.EvaluateAsync();

        _tuning.TryWriteOffset(0, -5, out _);

        var taken = await _plans.DetectDriftAsync();

        Assert.Single(taken);
        var entry = _plans.Entries.Single(static candidate => candidate.Target == "cpu.co.0");
        Assert.Equal(OwnerKind.User, entry.Owner);
        Assert.False(entry.Restorable);
    }

    [Fact]
    public async Task Scene_Reapplies_Edited_Plan_While_Active()
    {
        var plan = await SeedPlanAsync();
        await _store.UpdateAsync(document => document with
        {
            Scenes =
            [
                new Scene
                {
                    Id = "edited",
                    Name = "Edited",
                    Priority = 1,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.Manual }],
                    Actions = [new SceneAction { PlanId = plan.Id }],
                },
            ],
        });

        await _scenes.ActivateAsync("edited");
        Assert.Equal(-25, ReadOffset(0));

        var edited = plan with
        {
            Revision = plan.Revision + 1,
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -15 }],
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [edited] });

        var outcome = await _scenes.ReapplyIfActiveAsync(plan.Id);

        Assert.NotNull(outcome);
        Assert.Equal(PlanApplyState.Applied, outcome.State);
        Assert.Equal(-15, ReadOffset(0));
        Assert.Equal(OwnerKind.Scene, _plans.Entries.Single(e => e.Target == "cpu.co.0").Owner);
    }

    [Fact]
    public async Task ReapplyIfActive_Does_Not_Touch_Plans_Outside_The_Active_Scene()
    {
        var plan = await SeedPlanAsync();
        await _store.UpdateAsync(document => document with
        {
            Scenes =
            [
                new Scene
                {
                    Id = "other",
                    Name = "Other",
                    Priority = 1,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.Manual }],
                    Actions = [new SceneAction { PlanId = plan.Id }],
                },
            ],
        });

        var outcome = await _scenes.ReapplyIfActiveAsync(plan.Id);

        Assert.Null(outcome);
        Assert.Equal(0, ReadOffset(0));
    }

    [Fact]
    public async Task Unreadable_Core_Is_Skipped_Without_Writing()
    {
        _tuning.UnreadableCores.Add(1);
        var plan = new CpuTuningPlan
        {
            Id = "cpu",
            Name = "CPU",
            Revision = 1,
            Cores =
            [
                new CoreTuning { CoreIndex = 0, Offset = -25 },
                new CoreTuning { CoreIndex = 1, Offset = -20 },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        Assert.Equal(LedgerStatus.Applied, outcome.Targets.Single(target => target.Target == "cpu.co.0").Status);
        Assert.Equal(LedgerStatus.Skipped, outcome.Targets.Single(target => target.Target == "cpu.co.1").Status);

        _tuning.UnreadableCores.Clear();
        Assert.Equal(-25, ReadOffset(0));
        Assert.Equal(0, ReadOffset(1));

        var reloaded = await ReloadAsync();
        Assert.Equal("cpu.co.0", Assert.Single(reloaded.Ledger).Target);
    }

    [Fact]
    public async Task Power_PreRead_Failure_Skips_Without_Writing()
    {
        var schemeId = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        var settingId = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        var power = new FailingPowerPolicyService(schemeId) { FailReads = true };
        var coordinator = new PlanCoordinator(_store, _tuning, power, new FakeProcessScheduler(), _log);

        var plan = new PowerPlan
        {
            Id = "power-read-fail",
            Name = "Power Read Fail",
            Revision = 1,
            BaseSchemeGuid = schemeId,
            Settings = [new PowerSettingEntry { SettingId = settingId.ToString("D"), AcValue = "50" }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.NotApplied, outcome.State);
        Assert.Single(outcome.Targets);
        Assert.Equal(LedgerStatus.Skipped, outcome.Targets[0].Status);
        Assert.Empty(power.WrittenValues);
        Assert.Empty(coordinator.Entries);
    }

    [Fact]
    public async Task Power_Reactivation_Failure_Reports_Partial_And_Error()
    {
        var schemeId = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        var settingId = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        var power = new FailingPowerPolicyService(schemeId) { FailSetActiveScheme = true };
        var coordinator = new PlanCoordinator(_store, _tuning, power, new FakeProcessScheduler(), _log);

        var plan = new PowerPlan
        {
            Id = "power-activate-fail",
            Name = "Power Activate Fail",
            Revision = 1,
            BaseSchemeGuid = schemeId,
            Settings = [new PowerSettingEntry { SettingId = settingId.ToString("D"), AcValue = "80" }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        Assert.NotNull(outcome.Error);
        Assert.Equal(LedgerStatus.Applied, outcome.Targets[0].Status);
    }

    [Fact]
    public async Task Power_Apply_To_Non_Active_Scheme_Does_Not_Switch_Active()
    {
        // BaseSchemeGuid 只声明「改哪个方案」。它不等于活动方案时不能顺手切过去——
        // 那会把用户当前的电源方案换掉（风扇/亮度/待机策略当场变）。
        var activeScheme = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        var otherScheme = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var settingId = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        var power = new FakePowerPolicyService();
        Assert.True(power.TrySetActiveScheme(activeScheme, out _));
        var coordinator = new PlanCoordinator(_store, _tuning, power, new FakeProcessScheduler(), _log);

        var plan = new PowerPlan
        {
            Id = "power-other-scheme",
            Name = "Power Other Scheme",
            Revision = 1,
            BaseSchemeGuid = otherScheme,
            Settings = [new PowerSettingEntry { SettingId = settingId.ToString("D"), AcValue = "50" }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        Assert.DoesNotContain(outcome.Targets, static target => target.Target == "power.active");
        Assert.Equal(activeScheme, power.GetActiveScheme()?.Guid);
    }

    [Fact]
    public async Task Scheduling_User_Owned_Field_Is_Not_Overwritten_When_Sibling_Field_Acquired()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(4242, "game"));
        await _store.UpdateAsync(document => document with
        {
            Ledger =
            [
                new LedgerEntry
                {
                    Target = "sched.4242.affinity",
                    Source = "user",
                    Owner = OwnerKind.User,
                    Priority = int.MaxValue,
                    Status = LedgerStatus.Applied,
                    ActualValue = "0x3",
                    Restorable = false,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
            Plans =
            [
                new SchedulingPlan
                {
                    Id = "sched",
                    Name = "Sched",
                    Revision = 1,
                    Rules = [new SchedulingRule { ProcessMatch = "game", CpuMask = "0xF", Priority = "High" }],
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now,
                },
            ],
        });

        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var outcome = await coordinator.ApplyAsync("sched", "scene:game", 10);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        var applied = Assert.Single(scheduler.Applied);
        Assert.Equal(4242, applied.ProcessId);
        Assert.Null(applied.CpuMask);
        Assert.Equal("High", applied.Priority);
        Assert.Equal(LedgerStatus.Skipped, outcome.Targets.Single(t => t.Target == "sched.4242.affinity").Status);
        Assert.Equal(LedgerStatus.Applied, outcome.Targets.Single(t => t.Target == "sched.4242.priority").Status);
        Assert.Equal(OwnerKind.User, coordinator.Entries.Single(e => e.Target == "sched.4242.affinity").Owner);
    }

    [Fact]
    public void Co_Asymmetric_L2_Bounds_Accept_Minus50_And_Plus30_Reject_Plus31_And_Minus51()
    {
        Assert.True(CurveOptimizerBounds.IsWithinL2(-50));
        Assert.True(CurveOptimizerBounds.IsWithinL2(0));
        Assert.True(CurveOptimizerBounds.IsWithinL2(30));
        Assert.False(CurveOptimizerBounds.IsWithinL2(31));
        Assert.False(CurveOptimizerBounds.IsWithinL2(-51));

        var message = CurveOptimizerBounds.FormatL2Rejection(31);
        Assert.Contains("L2", message, StringComparison.Ordinal);
        Assert.Contains("+30", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyCpu_Skips_OutOfL2_Core_Without_Blocking_Valid_Cores()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu-l2-mixed",
            Name = "CPU L2 Mixed",
            Revision = 1,
            Cores =
            [
                new CoreTuning { CoreIndex = 0, Offset = -20 },
                new CoreTuning { CoreIndex = 1, Offset = 35 },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        Assert.Equal(LedgerStatus.Applied, outcome.Targets.Single(t => t.Target == "cpu.co.0").Status);
        var skipped = outcome.Targets.Single(t => t.Target == "cpu.co.1");
        Assert.Equal(LedgerStatus.Skipped, skipped.Status);
        Assert.Contains("L2", skipped.Failure ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(-20, ReadOffset(0));
        Assert.Equal(0, ReadOffset(1));
    }

    [Fact]
    public async Task EnforceScheduling_Cleans_Up_Exited_Process_Ledger_Entries()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(1001, "game"));
        await SeedSchedulingSceneAsync("game", "0xF", "High", enforce: true);
        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var scenes = new SceneEngine(_store, coordinator, scheduler, _powerStatus, _log);

        await scenes.EvaluateAsync();
        Assert.NotNull(coordinator.Entries.SingleOrDefault(e => e.Target == "sched.1001.affinity"));

        scheduler.RemoveProcess(1001);
        await coordinator.EnforceSchedulingAsync(scenes.ActiveSceneId);

        Assert.DoesNotContain(coordinator.Entries, e => e.Target.StartsWith("sched.1001.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnforceScheduling_Binds_Newly_Spawned_Process_And_Preserves_PreviousValue()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(1001, "game"));
        await SeedSchedulingSceneAsync("game", "0xF", "High", enforce: true);
        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var scenes = new SceneEngine(_store, coordinator, scheduler, _powerStatus, _log);

        await scenes.EvaluateAsync();

        scheduler.AddProcess(new ProcessTarget(2002, "game"), initialMask: "0x5", initialPriority: "BelowNormal");
        await coordinator.EnforceSchedulingAsync(scenes.ActiveSceneId);

        var affinity = coordinator.Entries.Single(e => e.Target == "sched.2002.affinity");
        var priority = coordinator.Entries.Single(e => e.Target == "sched.2002.priority");
        Assert.Equal("0x5", affinity.PreviousValue);
        Assert.Equal("0xF", affinity.ActualValue);
        Assert.Equal("BelowNormal", priority.PreviousValue);
        Assert.Equal("High", priority.ActualValue);
    }

    [Fact]
    public async Task EnforceScheduling_Reapplies_Drifted_Process_When_Enforce_Is_True()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(1001, "game"));
        await SeedSchedulingSceneAsync("game", "0xF", "High", enforce: true);
        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var scenes = new SceneEngine(_store, coordinator, scheduler, _powerStatus, _log);

        await scenes.EvaluateAsync();
        scheduler.Applied.Clear();

        scheduler.SetProcessState(1001, cpuMask: "0x1", priority: "Normal");
        await coordinator.EnforceSchedulingAsync(scenes.ActiveSceneId);

        var maskApplied = Assert.Single(scheduler.Applied, entry => entry.CpuMask is not null);
        Assert.Equal(1001, maskApplied.ProcessId);
        Assert.Equal("0xF", maskApplied.CpuMask);
        var priorityApplied = Assert.Single(scheduler.Applied, entry => entry.Priority is not null);
        Assert.Equal("High", priorityApplied.Priority);

        var affinity = coordinator.Entries.Single(e => e.Target == "sched.1001.affinity");
        Assert.Equal("0x3", affinity.PreviousValue);
        Assert.Equal("0xF", affinity.ActualValue);
    }

    [Fact]
    public async Task EnforceScheduling_Does_Not_Reapply_Drifted_Process_When_Enforce_Is_False()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(1001, "game"));
        await SeedSchedulingSceneAsync("game", "0xF", "High", enforce: false);
        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var scenes = new SceneEngine(_store, coordinator, scheduler, _powerStatus, _log);

        await scenes.EvaluateAsync();
        scheduler.Applied.Clear();

        scheduler.SetProcessState(1001, cpuMask: "0x1", priority: "Normal");
        await coordinator.EnforceSchedulingAsync(scenes.ActiveSceneId);

        Assert.Empty(scheduler.Applied);
    }

    [Fact]
    public async Task EnforceScheduling_Respects_UserOwned_And_HigherPriority_Scene_Isolation()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(1001, "game"));
        await _store.UpdateAsync(document => document with
        {
            Ledger =
            [
                new LedgerEntry
                {
                    Target = "sched.1001.affinity",
                    Source = "user",
                    Owner = OwnerKind.User,
                    Priority = int.MaxValue,
                    Status = LedgerStatus.Applied,
                    ActualValue = "0x3",
                    Restorable = false,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                new LedgerEntry
                {
                    Target = "sched.1001.priority",
                    PlanId = "higher-plan",
                    Source = "scene:higher",
                    Owner = OwnerKind.Scene,
                    Priority = 100,
                    Status = LedgerStatus.Applied,
                    PreviousValue = "Normal",
                    DesiredValue = "AboveNormal",
                    ActualValue = "AboveNormal",
                    Restorable = true,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });
        await SeedSchedulingSceneAsync("game", "0xF", "High", enforce: true, scenePriority: 10);

        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        await coordinator.EnforceSchedulingAsync("sched-scene");

        Assert.Empty(scheduler.Applied);
        Assert.Equal(OwnerKind.User, coordinator.Entries.Single(e => e.Target == "sched.1001.affinity").Owner);
        Assert.Equal("scene:higher", coordinator.Entries.Single(e => e.Target == "sched.1001.priority").Source);
    }

    [Fact]
    public async Task ApplyCpu_Writes_Verifies_And_Reverts_Intel_Voltage_With_SessionOnly_Persistence()
    {
        var intel = new FakeIntelTuningProvider();
        var coordinator = new PlanCoordinator(_store, new UnavailableTuningProvider(), new FakePowerPolicyService(), new FakeProcessScheduler(), _log, intel);
        var plan = new CpuTuningPlan
        {
            Id = "cpu-intel-uv",
            Name = "CPU Intel UV",
            Revision = 1,
            IntelVoltageOffsetMillivolts = -50,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        Assert.False(outcome.Plan.LastApply?.PersistsAcrossReboot);
        var target = Assert.Single(outcome.Targets);
        Assert.Equal("cpu.intel.voltage", target.Target);
        Assert.Equal("0", target.Previous);
        Assert.Equal("-50", target.Actual);
        Assert.True(intel.TryRead(out var state, out _));
        Assert.Equal(-50, state.VoltageOffsetMillivolts);

        var reverted = await coordinator.RevertPlanAsync(plan.Id);
        Assert.Equal(LedgerStatus.Reverted, Assert.Single(reverted).Status);
        Assert.True(intel.TryRead(out var afterRevert, out _));
        Assert.Equal(0, afterRevert.VoltageOffsetMillivolts);
    }

    [Fact]
    public async Task ApplyCpu_Skips_OutOfRange_Intel_Voltage_And_Detects_Drift()
    {
        var intel = new FakeIntelTuningProvider();
        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), new FakeProcessScheduler(), _log, intel);

        var invalidPlan = new CpuTuningPlan
        {
            Id = "cpu-intel-oob",
            Name = "CPU Intel OOB",
            Revision = 1,
            IntelVoltageOffsetMillivolts = -200,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        var validPlan = new CpuTuningPlan
        {
            Id = "cpu-intel-valid",
            Name = "CPU Intel Valid",
            Revision = 1,
            IntelVoltageOffsetMillivolts = -25,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [invalidPlan, validPlan] });

        var invalidOutcome = await coordinator.ApplyAsync(invalidPlan.Id, "plan:" + invalidPlan.Id, 0);
        Assert.Equal(PlanApplyState.NotApplied, invalidOutcome.State);
        Assert.Equal(LedgerStatus.Skipped, Assert.Single(invalidOutcome.Targets).Status);

        await coordinator.ApplyAsync(validPlan.Id, "scene:intel", 10);
        intel.TryWriteVoltageOffset(-10, out _);
        var taken = await coordinator.DetectDriftAsync();

        Assert.Single(taken, "cpu.intel.voltage");
        Assert.Equal(OwnerKind.User, coordinator.Entries.Single(e => e.Target == "cpu.intel.voltage").Owner);
    }

    [Fact]
    public async Task ApplyCpu_Resolves_Layered_Default_Ccd_PerCore_And_Auto_Overrides()
    {
        var tuning = new FakeCpuTuningProvider(8, coresPerCcd: 4);
        var coordinator = new PlanCoordinator(_store, tuning, new FakePowerPolicyService(), new FakeProcessScheduler(), _log);
        var plan = new CpuTuningPlan
        {
            Id = "cpu-layered-co",
            Name = "Layered CO",
            Revision = 1,
            DefaultOffset = -10,
            CcdOffsets = new Dictionary<int, int>
            {
                [0] = -20,
            },
            Cores =
            [
                new CoreTuning { CoreIndex = 0, Offset = -25 },
                new CoreTuning { CoreIndex = 1, Auto = true },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        Assert.True(tuning.TryReadOffsets(out var offsets, out _));
        var map = offsets.ToDictionary(static o => o.Core, static o => o.Offset);

        // Core 0: PerCore (-25), Core 1: Auto (remains 0), Cores 2..3: CCD0 (-20), Cores 4..7: Default (-10)
        Assert.Equal(-25, map[0]);
        Assert.Equal(0, map[1]);
        Assert.Equal(-20, map[2]);
        Assert.Equal(-20, map[3]);
        Assert.Equal(-10, map[4]);
        Assert.Equal(-10, map[7]);
        Assert.Contains(outcome.Targets, t => t.Target == "cpu.co.1" && t.Status == LedgerStatus.Skipped && t.Desired == "auto");
    }

    [Fact]
    public async Task ApplyCpu_Safely_Degrades_On_PlatformMismatch_And_Missing_Ccd_Topology()
    {
        _tuning.PlatformName = "Family 19h Model 61h";
        var mismatchPlan = new CpuTuningPlan
        {
            Id = "cpu-platform-mismatch",
            Name = "Mismatch",
            Revision = 1,
            PlatformId = "Family 1Ah Model 44h",
            DefaultOffset = -15,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        _tuning.TopologyAvailable = false;
        var noTopoPlan = new CpuTuningPlan
        {
            Id = "cpu-no-topo",
            Name = "No Topo",
            Revision = 1,
            DefaultOffset = -15,
            CcdOffsets = new Dictionary<int, int> { [0] = -20 },
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        await _store.UpdateAsync(document => document with { Plans = [mismatchPlan, noTopoPlan] });

        var mismatchOutcome = await _plans.ApplyAsync(mismatchPlan.Id, "plan:" + mismatchPlan.Id, 0);
        Assert.Equal(PlanApplyState.NotApplied, mismatchOutcome.State);
        Assert.Contains("不匹配", mismatchOutcome.Error ?? string.Empty);

        var noTopoOutcome = await _plans.ApplyAsync(noTopoPlan.Id, "plan:" + noTopoPlan.Id, 0);
        Assert.Equal(PlanApplyState.Partial, noTopoOutcome.State);
        Assert.Contains(noTopoOutcome.Targets, t => t.Target == "cpu.co.ccd.0" && t.Status == LedgerStatus.Skipped);
        Assert.True(_tuning.TryReadOffsets(out var offsets, out _));
        Assert.All(offsets, o => Assert.Equal(-15, o.Offset));
    }

    [Fact]
    public async Task Pid_Reuse_Does_Not_Inherit_Previous_Baseline()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(5001, "game"), new ProcessTarget(5002, "other"));
        var plan = new SchedulingPlan
        {
            Id = "sched-pid",
            Name = "Sched Pid",
            Revision = 1,
            Rules = [new SchedulingRule { ProcessMatch = "game", CpuMask = "0xF" }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, int.MaxValue - 1);
        Assert.Equal("0x3", coordinator.Entries.Single(static e => e.Target == "sched.5001.affinity").PreviousValue);

        // 原进程退出后 PID 被新进程复用，新进程的初始掩码不是旧基线。
        scheduler.RemoveProcess(5001);
        scheduler.AddProcess(new ProcessTarget(5001, "game"), "0x7");

        await coordinator.EnforceSchedulingAsync(null);

        var entry = coordinator.Entries.Single(static e => e.Target == "sched.5001.affinity");
        Assert.Equal("0x7", entry.PreviousValue);
        Assert.Equal("0xF", entry.ActualValue);
    }

    [Fact]
    public async Task ApplyScheduling_And_Revert_With_CpuSets()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(3001, "game"));
        var plan = new SchedulingPlan
        {
            Id = "sched-cpusets",
            Name = "Sched CpuSets",
            Revision = 1,
            Rules =
            [
                new SchedulingRule
                {
                    ProcessMatch = "game",
                    CpuSets = "0-3",
                    Enforce = true,
                },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        var target = Assert.Single(outcome.Targets);
        Assert.Equal("sched.3001.cpusets", target.Target);
        Assert.Equal(string.Empty, target.Previous);
        Assert.Equal("0,1,2,3", target.Actual);

        var reverted = await coordinator.RevertPlanAsync(plan.Id);
        Assert.Equal(LedgerStatus.Reverted, Assert.Single(reverted).Status);
        Assert.True(scheduler.TryReadCpuSets(3001, out var afterRevert, out _));
        Assert.Equal(string.Empty, afterRevert);
    }

    [Fact]
    public async Task ApplyScheduling_CpuSets_Silent_Rejection_Is_Not_Recorded_As_Applied()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(3003, "game"))
        {
            CpuSetsWriteIgnored = true,
        };
        var plan = new SchedulingPlan
        {
            Id = "sched-cpusets-ignored",
            Name = "Sched CpuSets Ignored",
            Revision = 1,
            Rules =
            [
                new SchedulingRule
                {
                    ProcessMatch = "game",
                    CpuSets = "0-3",
                    Enforce = true,
                },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Failed, outcome.State);
        Assert.Equal(LedgerStatus.Failed, Assert.Single(outcome.Targets).Status);
        Assert.Equal(LedgerStatus.Failed, coordinator.Ledger.Get("sched.3003.cpusets")!.Status);
    }

    [Fact]
    public async Task ApplyScheduling_CpuSets_Stays_Applied_When_Core_Parking_Is_On()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(3004, "game"));
        var power = new FakePowerPolicyService { CoreParkingMinPercent = 50 };
        var plan = new SchedulingPlan
        {
            Id = "sched-cpusets-parking",
            Name = "Sched CpuSets Parking",
            Revision = 1,
            Rules =
            [
                new SchedulingRule
                {
                    ProcessMatch = "game",
                    CpuSets = "0-3",
                    Enforce = true,
                },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var coordinator = new PlanCoordinator(_store, _tuning, power, scheduler, _log);
        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        Assert.Equal(LedgerStatus.Applied, Assert.Single(outcome.Targets).Status);
    }

    [Fact]
    public async Task ApplyScheduling_Skips_Unsupported_CpuSets_Without_Blocking_Affinity()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(3002, "game"))
        {
            CpuSetsSupported = false,
        };
        var plan = new SchedulingPlan
        {
            Id = "sched-cpusets-unsupported",
            Name = "Sched CpuSets Unsupported",
            Revision = 1,
            Rules =
            [
                new SchedulingRule
                {
                    ProcessMatch = "game",
                    CpuMask = "0xF",
                    CpuSets = "0-3",
                },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        Assert.Equal(LedgerStatus.Applied, outcome.Targets.Single(t => t.Target == "sched.3002.affinity").Status);
        Assert.Equal(LedgerStatus.Skipped, outcome.Targets.Single(t => t.Target == "sched.3002.cpusets").Status);
    }

    [Fact]
    public async Task EnforceScheduling_Reverts_Orphaned_Target_When_Rule_Config_Changes()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(4001, "game"));
        await SeedSchedulingSceneAsync("game", "0xF", "High", enforce: true);
        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var scenes = new SceneEngine(_store, coordinator, scheduler, _powerStatus, _log);

        await scenes.EvaluateAsync();
        Assert.NotNull(coordinator.Entries.SingleOrDefault(e => e.Target == "sched.4001.affinity"));
        Assert.NotNull(coordinator.Entries.SingleOrDefault(e => e.Target == "sched.4001.priority"));

        // 模拟配置变更：移除 CpuMask，仅保留 Priority
        await _store.UpdateAsync(document => document with
        {
            Plans = document.Plans.Select(p => p is SchedulingPlan sp && sp.Id == "sched-plan"
                ? sp with
                {
                    Rules = [new SchedulingRule { ProcessMatch = "game", CpuMask = null, Priority = "High", Enforce = true }],
                }
                : p).ToArray(),
        });

        await coordinator.EnforceSchedulingAsync(scenes.ActiveSceneId);

        Assert.DoesNotContain(coordinator.Entries, e => e.Target == "sched.4001.affinity");
        Assert.Contains(coordinator.Entries, e => e.Target == "sched.4001.priority" && e.Status == LedgerStatus.Applied);
        Assert.True(scheduler.TryRead(4001, out var maskAfter, out var prioAfter, out _));
        Assert.Equal("0x3", maskAfter);
        Assert.Equal("High", prioAfter);
    }

    [Fact]
    public async Task SchedulingWatcher_Lifecycle_Start_Notify_And_StopAsync()
    {
        var scheduler = new FakeProcessScheduler(new ProcessTarget(5001, "game"));
        await SeedSchedulingSceneAsync("game", "0xF", "High", enforce: true);
        var coordinator = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), scheduler, _log);
        var scenes = new SceneEngine(_store, coordinator, scheduler, _powerStatus, _log);

        await using var watcher = new SchedulingWatcher(scenes, coordinator, _log, TimeSpan.FromMinutes(5));
        watcher.Start();
        Assert.True(watcher.IsRunning);

        watcher.NotifyConfigChanged();
        await Task.Delay(100);
        await watcher.StopAsync();
        Assert.False(watcher.IsRunning);
    }

    private async Task SeedSchedulingSceneAsync(
        string processMatch,
        string? cpuMask,
        string? priority,
        bool enforce,
        int scenePriority = 10)
    {
        var plan = new SchedulingPlan
        {
            Id = "sched-plan",
            Name = "Sched Plan",
            Revision = 1,
            Rules =
            [
                new SchedulingRule
                {
                    ProcessMatch = processMatch,
                    CpuMask = cpuMask,
                    Priority = priority,
                    Enforce = enforce,
                },
            ],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        var scene = new Scene
        {
            Id = "sched-scene",
            Name = "Sched Scene",
            Priority = scenePriority,
            Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.Process, Argument = processMatch }],
            Actions = [new SceneAction { PlanId = plan.Id }],
        };

        await _store.UpdateAsync(document => document with
        {
            Plans = [.. document.Plans, plan],
            Scenes = [.. document.Scenes, scene],
        });
    }

    [Fact]
    public async Task ApplyCpu_Writes_Verifies_And_Reverts_FMax_With_FakeProvider()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu-fmax",
            Name = "CPU FMax",
            Revision = 1,
            FMaxLimitMhz = 5400,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        var target = Assert.Single(outcome.Targets);
        Assert.Equal("cpu.fmax", target.Target);
        Assert.Equal("5700", target.Previous);
        Assert.Equal("5400", target.Actual);
        Assert.Equal(5400, _tuning.FMaxMhz);

        var reverted = await _plans.RevertPlanAsync(plan.Id);
        Assert.Equal(LedgerStatus.Reverted, Assert.Single(reverted).Status);
        Assert.Equal(5700, _tuning.FMaxMhz);
    }

    [Fact]
    public async Task ApplyCpu_Skips_Unconfigured_FMax_Write_Without_Blocking_CO()
    {
        _tuning.FMaxWriteSupported = false;
        var plan = new CpuTuningPlan
        {
            Id = "cpu-fmax-unconfigured",
            Name = "CPU FMax Unconfigured",
            Revision = 1,
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -15 }],
            FMaxLimitMhz = 5400,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        Assert.Equal(LedgerStatus.Applied, outcome.Targets.Single(t => t.Target == "cpu.co.0").Status);
        Assert.Equal(LedgerStatus.Skipped, outcome.Targets.Single(t => t.Target == "cpu.fmax").Status);
        Assert.Equal(-15, ReadOffset(0));
        Assert.Equal(5700, _tuning.FMaxMhz);
    }

    [Fact]
    public async Task ApplyCpu_Accepts_FMax_Readback_Rounded_Within_Tolerance()
    {
        _tuning.FMaxStepMhz = 25;
        var plan = new CpuTuningPlan
        {
            Id = "cpu-fmax-round",
            Name = "CPU FMax Rounded",
            Revision = 1,
            FMaxLimitMhz = 5210,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        Assert.Equal("5200", Assert.Single(outcome.Targets).Actual);
        Assert.Equal(5200, _tuning.FMaxMhz);
        Assert.Equal(LedgerStatus.Applied, _plans.Entries.Single(e => e.Target == "cpu.fmax").Status);
    }

    [Fact]
    public async Task ApplyCpu_Fails_FMax_When_Readback_Exceeds_Tolerance()
    {
        _tuning.FMaxStepMhz = 25;
        var plan = new CpuTuningPlan
        {
            Id = "cpu-fmax-off",
            Name = "CPU FMax Off",
            Revision = 1,
            FMaxLimitMhz = 5240,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Failed, outcome.State);
        Assert.Equal(LedgerStatus.Failed, Assert.Single(outcome.Targets).Status);
    }

    [Fact]
    public async Task ApplyCpu_Treats_Zero_FMax_Baseline_As_Unsupported_Not_Failure()
    {
        _tuning.FMaxBaselineZero = true;
        var plan = new CpuTuningPlan
        {
            Id = "cpu-fmax-zero",
            Name = "CPU FMax Zero",
            Revision = 1,
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -15 }],
            FMaxLimitMhz = 5400,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        var fmax = outcome.Targets.Single(t => t.Target == "cpu.fmax");
        Assert.Equal(LedgerStatus.Skipped, fmax.Status);
        Assert.Contains("未实现", fmax.Failure ?? string.Empty);
        Assert.Equal(LedgerStatus.Skipped, _plans.Entries.Single(e => e.Target == "cpu.fmax").Status);
        Assert.Equal(5700, _tuning.FMaxMhz);
    }

    [Fact]
    public async Task DetectDrift_Detects_External_FMax_Change_And_Marks_User_Takeover()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu-fmax-drift",
            Name = "CPU FMax Drift",
            Revision = 1,
            FMaxLimitMhz = 5500,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });

        await _plans.ApplyAsync(plan.Id, "scene:fmax", 10);
        _tuning.TryWriteFMax(5200, out _);
        var taken = await _plans.DetectDriftAsync();

        Assert.Single(taken, "cpu.fmax");
        var entry = _plans.Entries.Single(e => e.Target == "cpu.fmax");
        Assert.Equal(OwnerKind.User, entry.Owner);
        Assert.False(entry.Restorable);
    }

    [Fact]
    public async Task Resume_Reapplies_Value_That_Fell_Back_To_Baseline()
    {
        var plan = await SeedPlanAsync();
        await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);
        Assert.Equal(-25, ReadOffset(0));

        _tuning.TryWriteOffset(0, 0, out _);
        var result = await _plans.ReconcileAfterResumeAsync();

        Assert.Equal(1, result.Reapplied);
        Assert.Equal(-25, ReadOffset(0));
        Assert.Equal(LedgerStatus.Applied, _plans.Entries.Single(e => e.Target == "cpu.co.0").Status);
    }

    [Fact]
    public async Task Resume_Keeps_Value_That_Matches_Desired_Without_Writing()
    {
        var plan = await SeedPlanAsync();
        await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        var result = await _plans.ReconcileAfterResumeAsync();

        Assert.Equal(1, result.Verified);
        Assert.Equal(0, result.Reapplied);
        Assert.Equal(-25, ReadOffset(0));
    }

    [Fact]
    public async Task Resume_Treats_Third_Party_Value_As_User_Takeover()
    {
        var plan = await SeedPlanAsync();
        await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        _tuning.TryWriteOffset(0, -5, out _);
        var result = await _plans.ReconcileAfterResumeAsync();

        Assert.Equal(1, result.TakenOver);
        Assert.Equal(-5, ReadOffset(0));
        var entry = _plans.Entries.Single(e => e.Target == "cpu.co.0");
        Assert.Equal(OwnerKind.User, entry.Owner);
        Assert.False(entry.Restorable);
    }

    [Fact]
    public async Task Resume_Marks_Unreadable_Target_As_Unknown()
    {
        var plan = await SeedPlanAsync();
        await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        _tuning.UnreadableCores.Add(0);
        var result = await _plans.ReconcileAfterResumeAsync();

        Assert.Equal(1, result.Unknown);
        Assert.Equal(LedgerStatus.Unknown, _plans.Entries.Single(e => e.Target == "cpu.co.0").Status);
    }

    private int ReadOffset(int core)
    {
        Assert.True(_tuning.TryReadOffsets(out var offsets, out _));
        return offsets.Single(offset => offset.Core == core).Offset;
    }

    private async Task<CpuTuningPlan> SeedPlanAsync()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu-ac",
            Name = "CPU AC",
            Revision = 1,
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -25 }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });
        return plan;
    }

    private async Task<CpuTuningPlan> SeedBatteryPlanAsync()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu-bat",
            Name = "CPU Battery",
            Revision = 1,
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -10 }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [.. document.Plans, plan] });
        return plan;
    }

    private async Task SeedScenesAsync()
    {
        await _store.UpdateAsync(document => document with
        {
            Scenes =
            [
                new Scene
                {
                    Id = "ac",
                    Name = "AC",
                    Priority = 10,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.PowerSource, Argument = "ac" }],
                    Actions = [new SceneAction { PlanId = "cpu-ac" }],
                },
                new Scene
                {
                    Id = "battery",
                    Name = "Battery",
                    Priority = 20,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.PowerSource, Argument = "dc" }],
                    Actions = [new SceneAction { PlanId = "cpu-bat" }],
                },
            ],
        });
    }

    private async Task<StateDocument> ReloadAsync()
    {
        var store = new StateStore(_directory, _log);
        await store.LoadAsync();
        return store.Current;
    }

    private sealed class UnavailableTuningProvider : ICpuTuningProvider
    {
        public CpuTuningStatus Query() => new(
            HardwareAvailability.DriverUnavailable,
            "未检测到 PawnIO 驱动。",
            "AuthenticAMD",
            "Family 19h Model 61h",
            true,
            false);

        public bool TryReadOffsets(out IReadOnlyList<CoreOffset> offsets, out string? error)
        {
            offsets = [];
            error = "未检测到 PawnIO 驱动。";
            return false;
        }

        public bool TryWriteOffset(int core, int millivolts, out string? error)
        {
            error = "未检测到 PawnIO 驱动。";
            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FailingPowerPolicyService(Guid activeScheme) : IPowerPolicyService
    {
        private readonly Dictionary<(Guid Scheme, Guid Sub, Guid Setting, bool Ac), uint> _values = new();

        public bool FailReads { get; set; }

        public bool FailSetActiveScheme { get; set; }

        public List<(Guid Scheme, Guid SubGroup, Guid Setting, bool Ac, uint Value)> WrittenValues { get; } = [];

        public PowerSchemeInfo? GetActiveScheme() => new(activeScheme, "Balanced");

        public IReadOnlyList<PowerSchemeInfo> ListSchemes() => [new(activeScheme, "Balanced")];

        public IReadOnlyList<PowerSchemeEntry> ListAllSchemes() => [new(activeScheme, "Balanced", true)];

        public bool TrySetActiveScheme(Guid scheme, out string? error)
        {
            if (FailSetActiveScheme)
            {
                error = "模拟激活电源方案失败。";
                return false;
            }

            error = null;
            return true;
        }

        public bool TryReadValue(Guid scheme, Guid subGroup, Guid setting, bool ac, out uint value, out string? error)
        {
            if (FailReads)
            {
                value = 0;
                error = "模拟读取电源值失败。";
                return false;
            }

            value = _values.GetValueOrDefault((scheme, subGroup, setting, ac));
            error = null;
            return true;
        }

        public bool TryWriteValue(Guid scheme, Guid subGroup, Guid setting, bool ac, uint value, out string? error)
        {
            WrittenValues.Add((scheme, subGroup, setting, ac, value));
            _values[(scheme, subGroup, setting, ac)] = value;
            error = null;
            return true;
        }

        public Guid? DuplicateScheme(Guid template, out string? error)
        {
            error = "模拟不支持派生方案。";
            return null;
        }

        public bool RenameScheme(Guid scheme, string name, out string? error)
        {
            error = "模拟不支持改名。";
            return false;
        }

        public bool DeleteScheme(Guid scheme, out string? error)
        {
            error = "模拟不支持删除。";
            return false;
        }

        public IReadOnlyList<Guid> EnumerateSubgroups(Guid scheme) => [];

        public IReadOnlyList<Guid> EnumerateSettings(Guid scheme, Guid subGroup) => [];

        public string? ReadDescription(Guid scheme) => null;

        public bool TryWriteDescription(Guid scheme, string description, out string? error)
        {
            error = "模拟不支持写说明。";
            return false;
        }
    }
}