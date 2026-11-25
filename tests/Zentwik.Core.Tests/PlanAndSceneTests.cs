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
        Assert.Equal(-25, offsets.Single(static offset => offset.Core == 0).Millivolts);

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
        Assert.Equal(0, offsets.Single(static offset => offset.Core == 0).Millivolts);
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
                new CoreTuning { CoreIndex = 0, VoltageOffsetMillivolts = -25 },
                new CoreTuning { CoreIndex = 1, VoltageOffsetMillivolts = -20 },
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

    private int ReadOffset(int core)
    {
        Assert.True(_tuning.TryReadOffsets(out var offsets, out _));
        return offsets.Single(offset => offset.Core == core).Millivolts;
    }

    private async Task<Plan> SeedPlanAsync()
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu",
            Name = "CPU",
            Revision = 1,
            Cores = [new CoreTuning { CoreIndex = 0, VoltageOffsetMillivolts = -25 }],
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
            Cores = [new CoreTuning { CoreIndex = 0, VoltageOffsetMillivolts = -10 }],
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
}
