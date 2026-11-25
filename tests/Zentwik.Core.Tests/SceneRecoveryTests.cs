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

public sealed class SceneRecoveryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"zentwik-recovery-{Guid.NewGuid():N}");
    private readonly FakeBootInfo _boot = new(DateTimeOffset.UtcNow.AddHours(-3));

    private DiagnosticLog _log = null!;
    private StateStore _store = null!;
    private FakeCpuTuningProvider _tuning = null!;
    private PlanCoordinator _plans = null!;
    private SceneEngine _scenes = null!;

    public async Task InitializeAsync()
    {
        (_log, _store, _tuning, _plans, _scenes) = await BuildAsync();
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
    public async Task Shutdown_Restores_Active_Scene_Writes()
    {
        await SeedAsync();
        await _scenes.EvaluateAsync();
        Assert.Equal(-25, ReadOffset());

        var activation = await _scenes.ShutdownAsync();

        Assert.NotNull(activation);
        Assert.Equal(0, ReadOffset());
        Assert.Equal(LedgerStatus.Reverted, _plans.Entries.Single().Status);
    }

    [Fact]
    public async Task Restart_Within_Same_Boot_Rolls_Back_Orphaned_Writes()
    {
        await SeedAsync();
        await _scenes.RecoverAsync();
        await _scenes.EvaluateAsync();
        Assert.Equal(-25, ReadOffset());

        var scenes = await RestartAsync();
        var recovered = await scenes.RecoverAsync();

        Assert.Single(recovered);
        Assert.Equal(LedgerStatus.Reverted, recovered[0].Status);
        Assert.Equal(0, ReadOffset());
    }

    [Fact]
    public async Task Restart_After_Reboot_Leaves_Writes_Untouched()
    {
        await SeedAsync();
        await _scenes.RecoverAsync();
        await _scenes.EvaluateAsync();

        _boot.BootTimeUtc = _boot.BootTimeUtc.AddHours(4);

        var scenes = await RestartAsync();
        var recovered = await scenes.RecoverAsync();

        Assert.Empty(recovered);
        Assert.Equal(-25, ReadOffset());
    }

    [Fact]
    public async Task Scene_Without_RestoreOnExit_Keeps_Writes_Past_Shutdown()
    {
        await SeedAsync(restoreOnExit: false);
        await _scenes.EvaluateAsync();

        await _scenes.ShutdownAsync();

        Assert.Equal(-25, ReadOffset());
        Assert.Null(_scenes.ActiveSceneId);
    }

    [Fact]
    public async Task Failed_Revert_Stays_Restorable_And_Succeeds_On_Retry()
    {
        await SeedAsync();
        await _scenes.RecoverAsync();
        await _scenes.EvaluateAsync();

        _tuning.FailWrites = true;
        await _scenes.ShutdownAsync();

        var pending = _plans.Entries.Single();
        Assert.Equal(LedgerStatus.Applied, pending.Status);
        Assert.True(pending.Restorable);
        Assert.Equal(OwnerKind.Scene, pending.Owner);

        _tuning.FailWrites = false;
        var recovered = await _scenes.RecoverAsync();

        Assert.Single(recovered);
        Assert.Equal(0, ReadOffset());
    }

    private int ReadOffset() =>
        _tuning.TryReadOffsets(out var offsets, out _) ? offsets[0].Offset : int.MinValue;

    /// <summary>模拟进程重启：重新读盘、重建协调器与引擎，硬件状态由同一个提供者实例延续。</summary>
    private async Task<SceneEngine> RestartAsync()
    {
        var store = new StateStore(_directory, _log);
        await store.LoadAsync();
        _store = store;
        _plans = new PlanCoordinator(store, _tuning, new FakePowerPolicyService(), new FakeProcessScheduler(), _log);
        _scenes = new SceneEngine(store, _plans, new FakeProcessScheduler(), new FakePowerStatus(), _log, _boot);
        return _scenes;
    }

    private async Task<(DiagnosticLog Log, StateStore Store, FakeCpuTuningProvider Tuning, PlanCoordinator Plans, SceneEngine Scenes)>
        BuildAsync()
    {
        var log = new DiagnosticLog(_directory, echoToConsole: false);
        var store = new StateStore(_directory, log);
        await store.LoadAsync();
        var tuning = new FakeCpuTuningProvider(4);
        var plans = new PlanCoordinator(store, tuning, new FakePowerPolicyService(), new FakeProcessScheduler(), log);
        var scenes = new SceneEngine(store, plans, new FakeProcessScheduler(), new FakePowerStatus(), log, _boot);
        return (log, store, tuning, plans, scenes);
    }

    private async Task SeedAsync(bool restoreOnExit = true)
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

        await _store.UpdateAsync(document => document with
        {
            Plans = [plan],
            Scenes =
            [
                new Scene
                {
                    Id = "ac",
                    Name = "AC",
                    Priority = 5,
                    RestoreOnExit = restoreOnExit,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.PowerSource, Argument = "ac" }],
                    Actions = [new SceneAction { PlanId = plan.Id, PlanRevision = 1 }],
                },
            ],
        });

        _plans = new PlanCoordinator(_store, _tuning, new FakePowerPolicyService(), new FakeProcessScheduler(), _log);
        _scenes = new SceneEngine(_store, _plans, new FakeProcessScheduler(), new FakePowerStatus(), _log, _boot);
    }

    private sealed class FakeBootInfo(DateTimeOffset bootTime) : IBootInfo
    {
        public DateTimeOffset BootTimeUtc { get; set; } = bootTime;
    }
}
