using Xunit;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.Processes;
using Zentwik.Core.State;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Power;
using Zentwik.Platform;
using Zentwik.Platform.Fakes;

namespace Zentwik.Core.Tests;

/// <summary>
/// 功耗墙并入 CpuTuningPlan 与进程只读枚举。全部基于 fake：真实 SMU DPTC 与
/// NtQuerySystemInformation 结构体解析都必须在 Windows 真机上另验。
/// </summary>
public sealed class ProcessAndPowerLimitTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"zentwik-pl-{Guid.NewGuid():N}");
    private DiagnosticLog _log = null!;
    private StateStore _store = null!;
    private FakeCpuTuningProvider _tuning = null!;
    private FakePowerLimitProvider _limits = null!;
    private PlanCoordinator _plans = null!;
    private bool _moduleEnabled = true;

    public async Task InitializeAsync()
    {
        _log = new DiagnosticLog(_directory, echoToConsole: false);
        _store = new StateStore(_directory, _log);
        await _store.LoadAsync();

        _tuning = new FakeCpuTuningProvider(4);
        _limits = new FakePowerLimitProvider();
        _plans = new PlanCoordinator(
            _store,
            _tuning,
            new FakePowerPolicyService(),
            new FakeProcessScheduler(),
            _log,
            intel: null,
            powerStatus: null,
            powerLimits: _limits,
            powerLimitsEnabled: () => _moduleEnabled);
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
    public async Task Apply_Power_Limits_Records_Readback_And_Is_Revertible()
    {
        var plan = await SeedAsync(new PowerLimitRequest(120, 95, null, null));

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Applied, outcome.State);
        var fast = _plans.Entries.Single(entry => entry.Target == "cpu.power.limit.fast");
        Assert.Equal(LedgerStatus.Applied, fast.Status);
        Assert.Equal("100", fast.PreviousValue);
        Assert.Equal("120", fast.DesiredValue);
        Assert.Equal("120", fast.ActualValue);

        var reverted = await _plans.RevertPlanAsync(plan.Id);

        Assert.All(reverted, static target => Assert.Equal(LedgerStatus.Reverted, target.Status));
        Assert.True(_limits.TryReadCurrentPowerLimits(out var current, out _));
        Assert.Equal(100, current.FastWatts);
        Assert.Equal(90, current.SlowWatts);
    }

    [Fact]
    public async Task Power_Limits_Survive_State_Roundtrip()
    {
        var plan = await SeedAsync(new PowerLimitRequest(120, 95, 60, 40));

        // 走一遍真实落盘与重载：PowerLimits 是嵌套 record，序列化漏注册会在这里炸。
        var store = new StateStore(_directory, _log);
        await store.LoadAsync();

        var stored = Assert.IsType<CpuTuningPlan>(store.Current.Plans.Single(candidate => candidate.Id == plan.Id));
        Assert.Equal(120, stored.PowerLimits?.FastWatts);
        Assert.Equal(95, stored.PowerLimits?.SlowWatts);
        Assert.Equal(60, stored.PowerLimits?.TdcAmps);
        Assert.Equal(40, stored.PowerLimits?.EdcAmps);
    }

    [Fact]
    public async Task Disabled_Module_Skips_Power_Limits_Without_Failing_Cpu_Tuning()
    {
        _moduleEnabled = false;
        var plan = await SeedAsync(new PowerLimitRequest(120, null, null, null), co: -20);

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.Partial, outcome.State);
        Assert.Contains(outcome.Targets, static target => target.Target == "cpu.co.0" && target.Status == LedgerStatus.Applied);
        Assert.Contains(outcome.Targets, static target => target.Target == "cpu.power.limit" && target.Status == LedgerStatus.Skipped);
        Assert.DoesNotContain(_plans.Entries, static entry => entry.Target.StartsWith("cpu.power.limit.", StringComparison.Ordinal));

        // CO 必须真的写下去，不能被功耗墙的跳过连带。
        Assert.True(_tuning.TryReadOffsets(out var offsets, out _));
        Assert.Equal(-20, offsets.Single(static offset => offset.Core == 0).Offset);
    }

    [Fact]
    public async Task Diverged_Readback_Is_Not_Reported_As_Applied()
    {
        _limits.WriteScale = 0.8;
        var plan = await SeedAsync(new PowerLimitRequest(120, null, null, null));

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        var fast = _plans.Entries.Single(entry => entry.Target == "cpu.power.limit.fast");
        Assert.Equal(LedgerStatus.Failed, fast.Status);
        Assert.Equal("96", fast.ActualValue);
        Assert.Equal(PlanApplyState.Failed, outcome.State);
    }

    [Fact]
    public async Task Accepted_Without_Readback_Is_Unknown_And_Not_Restorable()
    {
        var plan = await SeedAsync(new PowerLimitRequest(null, null, 60, null));

        var outcome = await _plans.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        // 无回读通道：账本与方案状态都必须是「未知」，不能报成功也不能报失败。
        Assert.Equal(PlanApplyState.Unknown, outcome.State);
        var tdc = _plans.Entries.Single(entry => entry.Target == "cpu.power.limit.tdc");
        Assert.Equal(LedgerStatus.Unknown, tdc.Status);
        Assert.False(tdc.Restorable);
        Assert.Null(tdc.PreviousValue);
    }

    [Fact]
    public async Task Missing_Provider_Skips_Power_Limits()
    {
        var plan = await SeedAsync(new PowerLimitRequest(120, null, null, null));
        var coordinator = new PlanCoordinator(
            _store,
            _tuning,
            new FakePowerPolicyService(),
            new FakeProcessScheduler(),
            _log);

        var outcome = await coordinator.ApplyAsync(plan.Id, "plan:" + plan.Id, 0);

        Assert.Equal(PlanApplyState.NotApplied, outcome.State);
        Assert.All(outcome.Targets, static target => Assert.Equal(LedgerStatus.Skipped, target.Status));
    }

    [Fact]
    public void Process_List_Only_Queries_Paths_For_Ledger_And_Selected_Pids()
    {
        var inventory = new FakeProcessInventory();
        var ledger = new StateLedger();
        ledger.Acquire("sched.1000.affinity", "plan:p", 0, "0x3", "0xF", "p");
        ledger.Commit("sched.1000.affinity", "0xF");

        var service = new ProcessQueryService(inventory, new FakeProcessScheduler(), ledger);
        var rows = service.List(selectedPid: 2000);

        Assert.Equal([1000, 2000], inventory.LastDetailPids.Order().ToArray());
        var explorer = rows.Single(static row => row.Pid == 1000);
        Assert.NotNull(explorer.Path);
        Assert.Equal("affinity=0xF", explorer.Rule);

        var system = rows.Single(static row => row.Pid == 4);
        Assert.Null(system.Path);

        // CPU% 来自全表快照，不在单查范围内也有值。
        Assert.All(rows, static row => Assert.NotNull(row.CpuPercent));
    }

    private async Task<CpuTuningPlan> SeedAsync(PowerLimitRequest limits, int? co = null)
    {
        var plan = new CpuTuningPlan
        {
            Id = "cpu-power",
            Name = "CPU Power",
            Revision = 1,
            Cores = co is { } offset ? [new CoreTuning { CoreIndex = 0, Offset = offset }] : [],
            PowerLimits = limits,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        await _store.UpdateAsync(document => document with { Plans = [plan] });
        return plan;
    }
}
