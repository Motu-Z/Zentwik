using Xunit;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.Scenes;
using Zentwik.Core.State;
using Zentwik.Domain.Ownership;
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
    public async Task Scene_Skips_Plan_Whose_Revision_Changed()
    {
        var plan = await SeedPlanAsync();
        await _store.UpdateAsync(document => document with
        {
            Scenes =
            [
                new Scene
                {
                    Id = "broken",
                    Name = "Broken",
                    Priority = 1,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.Manual }],
                    Actions = [new SceneAction { PlanId = plan.Id, PlanRevision = plan.Revision + 1 }],
                },
            ],
        });

        var activation = await _scenes.ActivateAsync("broken");

        Assert.True(activation.Active);
        Assert.Empty(activation.PlanIds);
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

        var reapplied = Assert.Single(scheduler.Applied);
        Assert.Equal(1001, reapplied.ProcessId);
        Assert.Equal("0xF", reapplied.CpuMask);
        Assert.Equal("High", reapplied.Priority);

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

        await _plans.ApplyAsync(plan.Id, "scene:test", 10);
        _tuning.TryWriteFMax(5600, out _);

        var taken = await _plans.DetectDriftAsync();

        Assert.Single(taken, "cpu.fmax");
        var entry = _plans.Entries.Single(e => e.Target == "cpu.fmax");
        Assert.Equal(OwnerKind.User, entry.Owner);
        Assert.False(entry.Restorable);
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
            Actions = [new SceneAction { PlanId = plan.Id, PlanRevision = 1 }],
        };

        await _store.UpdateAsync(document => document with
        {
            Plans = [.. document.Plans, plan],
            Scenes = [.. document.Scenes, scene],
        });
    }

    private int ReadOffset(int core)
    {
        Assert.True(_tuning.TryReadOffsets(out var offsets, out _));
        return offsets.Single(offset => offset.Core == core).Offset;
    }

    private async Task<Plan> SeedPlanAsync()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu",
            Name = "CPU",
            Revision = 1,
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -25 }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        await _store.UpdateAsync(document => document with { Plans = [plan] });
        return plan;
    }

    private async Task SeedBatteryPlanAsync()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu-battery",
            Name = "CPU battery",
            Revision = 1,
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -10 }],
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        await _store.UpdateAsync(document => document with { Plans = [.. document.Plans, plan] });
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
                    Priority = 5,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.PowerSource, Argument = "ac" }],
                    Actions = [new SceneAction { PlanId = "cpu", PlanRevision = 1 }],
                },
                new Scene
                {
                    Id = "battery",
                    Name = "Battery",
                    Priority = 10,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.PowerSource, Argument = "dc" }],
                    Actions = [new SceneAction { PlanId = "cpu-battery", PlanRevision = 1 }],
                },
            ],
        });
    }

    private async Task<Zentwik.Domain.Persistence.StateDocument> ReloadAsync()
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
    }
}
