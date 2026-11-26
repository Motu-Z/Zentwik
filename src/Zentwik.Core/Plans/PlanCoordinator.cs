using System.Globalization;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.State;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Platform;

namespace Zentwik.Core.Plans;

public sealed class CoreException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;
}

public sealed record TargetOutcome(
    string Target,
    string? Previous,
    string? Desired,
    string? Actual,
    LedgerStatus Status,
    string? Failure);

public sealed record PlanApplyOutcome(
    Plan Plan,
    PlanApplyState State,
    IReadOnlyList<TargetOutcome> Targets,
    string? Error);

/// <summary>
/// 方案应用的唯一写入口。每次写入前记前值、写入后回读，期望值与实际值不一致即判定未生效。
/// </summary>
public sealed class PlanCoordinator
{
    private readonly StateStore _store;
    private readonly ICpuTuningProvider _tuning;
    private readonly IIntelTuningProvider? _intel;
    private readonly IPowerPolicyService _power;
    private readonly IProcessScheduler _scheduler;
    private readonly IPowerStatus? _powerStatus;
    private readonly DiagnosticLog _log;
    private readonly StateLedger _ledger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _cpuSetsWarned = new(StringComparer.Ordinal);

    public PlanCoordinator(
        StateStore store,
        ICpuTuningProvider tuning,
        IPowerPolicyService power,
        IProcessScheduler scheduler,
        DiagnosticLog log,
        IIntelTuningProvider? intel = null,
        IPowerStatus? powerStatus = null)
    {
        _store = store;
        _tuning = tuning;
        _intel = intel;
        _power = power;
        _scheduler = scheduler;
        _powerStatus = powerStatus;
        _log = log;
        _ledger = StateLedger.Restore(store.Current.Ledger);
    }

    public StateLedger Ledger => _ledger;

    public IReadOnlyList<LedgerEntry> Entries => [.. _ledger.Entries.OrderBy(static entry => entry.Target, StringComparer.Ordinal)];

    public async Task<PlanApplyOutcome> ApplyAsync(
        string planId,
        string source,
        int priority,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plan = FindPlan(planId);
            var (targets, error) = plan switch
            {
                CpuTuningPlan cpu => ApplyCpu(cpu, source, priority),
                PowerPlan power => ApplyPower(power, source, priority),
                SchedulingPlan scheduling => ApplyScheduling(scheduling, source, priority),
                _ => (Array.Empty<TargetOutcome>(), "未知方案类型。"),
            };

            var state = Summarize(targets, error);
            var updated = plan with
            {
                LastApply = new PlanApplyRecord
                {
                    State = state,
                    AppliedAt = state == PlanApplyState.NotApplied ? plan.LastApply?.AppliedAt : DateTimeOffset.Now,
                    PersistsAcrossReboot = PersistsAcrossReboot(plan),
                    Outcome = error ?? Describe(state, targets),
                },
            };

            await PersistAsync(updated, ct).ConfigureAwait(false);
            _log.Info($"方案 {plan.Id} 应用：{state}，目标 {targets.Count}，{updated.LastApply?.Outcome}");
            return new PlanApplyOutcome(updated, state, targets, error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TargetOutcome>> RevertPlanAsync(string planId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = _ledger.Entries
                .Where(entry => string.Equals(entry.PlanId, planId, StringComparison.Ordinal))
                .Where(static entry => StateLedger.IsHeld(entry) && entry.Restorable)
                .OrderBy(static entry => entry.Target, StringComparer.Ordinal)
                .ToArray();

            var outcomes = Restore(entries);
            await PersistLedgerAsync(ct).ConfigureAwait(false);

            var plan = _store.Current.Plans.FirstOrDefault(p => p.Id == planId);
            if (plan is not null)
            {
                var remaining = _ledger.Entries.Any(entry =>
                    string.Equals(entry.PlanId, planId, StringComparison.Ordinal)
                    && StateLedger.IsHeld(entry));

                var updated = plan with
                {
                    LastApply = remaining
                        ? plan.LastApply
                        : new PlanApplyRecord
                        {
                            State = PlanApplyState.NotApplied,
                            AppliedAt = plan.LastApply?.AppliedAt,
                            PersistsAcrossReboot = PersistsAcrossReboot(plan),
                            Outcome = "已回退",
                        },
                };
                await PersistPlanAsync(updated, ct).ConfigureAwait(false);
            }

            _log.Info($"方案 {planId} 回退：{outcomes.Count(static o => o.Status == LedgerStatus.Reverted)}/{outcomes.Count}");
            return outcomes;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TargetOutcome>> RevertSourceAsync(string source, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = _ledger.PlanRevert(source);
            var outcomes = Restore(entries);
            var affected = entries
                .Select(static entry => entry.PlanId)
                .Where(static id => id is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            await _store.UpdateAsync(
                document => document with
                {
                    Ledger = [.. _ledger.Entries],
                    Plans = document.Plans
                        .Select(plan => affected.Contains(plan.Id, StringComparer.Ordinal)
                                        && !HasAppliedEntries(plan.Id)
                            ? plan with
                            {
                                LastApply = new PlanApplyRecord
                                {
                                    State = PlanApplyState.NotApplied,
                                    AppliedAt = plan.LastApply?.AppliedAt,
                                    PersistsAcrossReboot = PersistsAcrossReboot(plan),
                                    Outcome = "已随场景退出回退",
                                },
                            }
                            : plan)
                        .ToArray(),
                },
                ct).ConfigureAwait(false);

            return outcomes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 系统恢复后核对 CPU 相关目标：实际值等于期望值记为确认，退回前值视为失效并按期望重写，
    /// 既不是期望值也不是前值视为用户接管（不覆盖），读不到则记为未知。
    /// </summary>
    public async Task<ResumeReconcileResult> ReconcileAfterResumeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var candidates = _ledger.Entries
                .Where(static entry => StateLedger.IsHeld(entry) && IsResumeTarget(entry.Target))
                .ToArray();

            if (candidates.Length == 0)
            {
                return new ResumeReconcileResult(0, 0, 0, 0, []);
            }

            IReadOnlyList<CoreOffset>? offsets = null;
            if (candidates.Any(static entry => entry.Target.StartsWith("cpu.co.", StringComparison.Ordinal)))
            {
                _tuning.TryReadOffsets(out var read, out _);
                offsets = read;
            }

            var verified = 0;
            var reapplied = 0;
            var takenOver = 0;
            var unknown = 0;
            var details = new List<string>();

            foreach (var entry in candidates)
            {
                var equivalent = EquivalentFor(entry.Target);
                var actual = ReadTarget(entry.Target, offsets);
                if (actual is null)
                {
                    _ledger.MarkUnknown(entry.Target, "系统恢复后无法读取实际值。");
                    unknown++;
                    details.Add($"{entry.Target}: 无法读取");
                    continue;
                }

                if (entry.DesiredValue is { } desired && Matches(actual, desired, equivalent))
                {
                    _ledger.Commit(entry.Target, actual, equivalent);
                    verified++;
                    continue;
                }

                if (entry.Restorable
                    && entry.PreviousValue is { } previous
                    && string.Equals(actual, previous, StringComparison.Ordinal))
                {
                    var (restored, error) = ReapplyTarget(entry.Target, entry.DesiredValue!);
                    if (error is null && restored is not null)
                    {
                        _ledger.Commit(entry.Target, restored, equivalent);
                        reapplied++;
                        details.Add($"{entry.Target}: 已重写为 {restored}");
                    }
                    else
                    {
                        _ledger.Fail(entry.Target, error ?? "重写后无法回读。");
                        details.Add($"{entry.Target}: 重写失败 {error}");
                    }

                    continue;
                }

                _ledger.MarkUserTakeover(entry.Target, actual);
                takenOver++;
                details.Add($"{entry.Target}: 实际 {actual}，标记为用户接管");
            }

            await PersistLedgerAsync(ct).ConfigureAwait(false);
            _log.Info($"恢复核对：确认 {verified}，重写 {reapplied}，用户接管 {takenOver}，未知 {unknown}。");
            return new ResumeReconcileResult(verified, reapplied, takenOver, unknown, details);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsResumeTarget(string target) =>
        target.StartsWith("cpu.co.", StringComparison.Ordinal)
        || string.Equals(target, "cpu.fmax", StringComparison.Ordinal)
        || string.Equals(target, "cpu.intel.voltage", StringComparison.Ordinal);

    private static Func<string, string, bool>? EquivalentFor(string target) =>
        string.Equals(target, "cpu.fmax", StringComparison.Ordinal) ? FMaxTolerance.Within : null;

    private (string? Actual, string? Error) ReapplyTarget(string target, string desired)
    {
        if (!int.TryParse(desired, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return (null, "期望值不是整数。");
        }

        if (target.StartsWith("cpu.co.", StringComparison.Ordinal))
        {
            if (!int.TryParse(target["cpu.co.".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var core))
            {
                return (null, "目标格式非法。");
            }

            return _tuning.TryWriteOffset(core, value, out var error)
                ? (ReadCoreOffset(core), null)
                : (null, error);
        }

        if (string.Equals(target, "cpu.fmax", StringComparison.Ordinal))
        {
            if (_tuning is not IFMaxTuningProvider fmax)
            {
                return (null, "当前调校通道不支持 FMax 写入。");
            }

            return fmax.TryWriteFMax(value, out var error) == FMaxWriteResult.Applied
                   && fmax.TryReadFMax(out var actual, out _)
                ? (actual.ToString(CultureInfo.InvariantCulture), null)
                : (null, error ?? "FMax 写入未被接受。");
        }

        if (string.Equals(target, "cpu.intel.voltage", StringComparison.Ordinal))
        {
            if (_intel is null)
            {
                return (null, "未装配 Intel MSR 调校通道。");
            }

            return _intel.TryWriteVoltageOffset(value, out var error) && _intel.TryRead(out var actual, out _)
                ? (actual.VoltageOffsetMillivolts.ToString(CultureInfo.InvariantCulture), null)
                : (null, error ?? "Intel 电压偏移写入未被接受。");
        }

        return (null, "未知目标类型。");
    }

    public async Task<IReadOnlyList<string>> DetectDriftAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var candidates = _ledger.Entries
                .Where(static entry => entry.Owner is OwnerKind.Scene or OwnerKind.Plan
                                       && entry.Status == LedgerStatus.Applied
                                       && entry.PlanId is not null)
                .ToArray();

            if (candidates.Length == 0)
            {
                return [];
            }

            IReadOnlyList<CoreOffset>? offsets = null;
            if (candidates.Any(static entry => entry.Target.StartsWith("cpu.co.", StringComparison.Ordinal)))
            {
                if (!_tuning.TryReadOffsets(out var read, out _))
                {
                    return [];
                }

                offsets = read;
            }

            var taken = new List<string>();
            foreach (var entry in candidates)
            {
                var actual = ReadTarget(entry.Target, offsets);
                if (actual is null || string.Equals(actual, entry.ActualValue, StringComparison.Ordinal))
                {
                    continue;
                }

                _ledger.MarkUserTakeover(entry.Target, actual);
                taken.Add(entry.Target);
                _log.Warn($"检测到外部改动：{entry.Target} 期望 {entry.ActualValue}，实际 {actual}，标记为用户接管。");
            }

            if (taken.Count > 0)
            {
                await PersistLedgerAsync(ct).ConfigureAwait(false);
            }

            return taken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnforceSchedulingAsync(string? activeSceneId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = false;

            // 1. 清理已退出（或被同 PID 新进程顶替）的 sched.{pid}.* 账本记录，避免误判所有权。
            var aliveByPid = new Dictionary<int, bool>();
            foreach (var entry in _ledger.Entries.ToArray())
            {
                if (!TryParseSchedulingTarget(entry.Target, out var pid, out _))
                {
                    continue;
                }

                if (!aliveByPid.TryGetValue(pid, out var alive))
                {
                    alive = ProcessStillAlive(pid, entry.ProcessStartTicks);
                    aliveByPid[pid] = alive;
                }

                if (!alive && _ledger.Remove(entry.Target))
                {
                    changed = true;
                }
            }

            // 2. 收集当前活跃的调度方案（活动场景引用的方案 + 手动应用的方案）。
            var activePlans = new List<(SchedulingPlan Plan, string Source, int Priority)>();
            var coveredPlanIds = new HashSet<string>(StringComparer.Ordinal);

            if (activeSceneId is not null)
            {
                var activeScene = _store.Current.Scenes.FirstOrDefault(scene =>
                    string.Equals(scene.Id, activeSceneId, StringComparison.Ordinal));
                if (activeScene is not null)
                {
                    foreach (var action in activeScene.Actions)
                    {
                        var plan = _store.Current.Plans.FirstOrDefault(candidate =>
                            string.Equals(candidate.Id, action.PlanId, StringComparison.Ordinal));
                        if (plan is null)
                        {
                            continue;
                        }

                        if (plan is SchedulingPlan schedPlan)
                        {
                            activePlans.Add((schedPlan, $"scene:{activeSceneId}", activeScene.Priority));
                            coveredPlanIds.Add(schedPlan.Id);
                        }
                    }
                }
            }

            foreach (var plan in _store.Current.Plans)
            {
                if (plan is not SchedulingPlan manualPlan || coveredPlanIds.Contains(manualPlan.Id))
                {
                    continue;
                }

                if (manualPlan.LastApply?.State is not (PlanApplyState.Applied or PlanApplyState.Partial))
                {
                    continue;
                }

                var ownedByScene = _ledger.Entries.Any(entry =>
                    string.Equals(entry.PlanId, manualPlan.Id, StringComparison.Ordinal)
                    && entry.Owner == OwnerKind.Scene
                    && entry.Status is LedgerStatus.Applied or LedgerStatus.Unknown);
                if (!ownedByScene)
                {
                    activePlans.Add((manualPlan, $"plan:{manualPlan.Id}", int.MaxValue - 1));
                }
            }

            // 2.5 配置变更对账：若某活跃来源的规则已被删改或清空字段，回退并清理其遗留接管项。
            var activeSources = activePlans.Select(static p => p.Source).ToHashSet(StringComparer.Ordinal);
            var desiredTargets = new HashSet<(string Source, string Target)>();
            foreach (var (schedPlan, source, _) in activePlans)
            {
                foreach (var rule in schedPlan.Rules)
                {
                    foreach (var process in _scheduler.Find(rule.ProcessMatch))
                    {
                        if (rule.CpuMask is not null)
                        {
                            desiredTargets.Add((source, $"sched.{process.ProcessId}.affinity"));
                        }

                        if (rule.Priority is not null)
                        {
                            desiredTargets.Add((source, $"sched.{process.ProcessId}.priority"));
                        }

                        if (rule.CpuSets is not null)
                        {
                            desiredTargets.Add((source, $"sched.{process.ProcessId}.cpusets"));
                        }
                    }
                }
            }

            foreach (var entry in _ledger.Entries.ToArray())
            {
                if (!TryParseSchedulingTarget(entry.Target, out _, out _)
                    || entry.Owner is not (OwnerKind.Scene or OwnerKind.Plan)
                    || entry.Status != LedgerStatus.Applied
                    || !activeSources.Contains(entry.Source)
                    || desiredTargets.Contains((entry.Source, entry.Target)))
                {
                    continue;
                }

                if (entry.Restorable && entry.PreviousValue is not null)
                {
                    var (actual, error) = RestoreTarget(entry);
                    var reverted = error is null && string.Equals(actual, entry.PreviousValue, StringComparison.Ordinal);
                    _ledger.MarkReverted(entry.Target, actual ?? string.Empty, reverted, error);
                }

                if (_ledger.Remove(entry.Target))
                {
                    changed = true;
                }
            }

            // 3. 仅对 Enforce == true 的规则执行新进程绑定与漂移重施加。
            var cpuSetScheduler = _scheduler as IProcessCpuSetScheduler;
            var cpuSetsSupported = cpuSetScheduler is not null && cpuSetScheduler.IsCpuSetsSupported(out _);

            foreach (var (schedPlan, source, priority) in activePlans)
            {
                foreach (var rule in schedPlan.Rules)
                {
                    if (!rule.Enforce)
                    {
                        continue;
                    }

                    string? normalizedCpuSets = null;
                    if (rule.CpuSets is not null && cpuSetsSupported && CpuSetSpec.TryNormalize(rule.CpuSets, out var norm, out _))
                    {
                        normalizedCpuSets = norm;
                    }

                    if (rule.CpuSets is not null && normalizedCpuSets is null)
                    {
                        WarnCpuSetsUnavailable(schedPlan.Id, cpuSetsSupported);
                    }
                    else if (normalizedCpuSets is not null)
                    {
                        _cpuSetsWarned.Remove(schedPlan.Id);
                    }

                    var processes = _scheduler.Find(rule.ProcessMatch);
                    foreach (var process in processes)
                    {
                        var startTicks = StartTicksOf(process);
                        DropStaleSchedulingEntries(process.ProcessId, startTicks);

                        if (!_scheduler.TryRead(process.ProcessId, out var currentMask, out var currentPriority, out _))
                        {
                            continue;
                        }

                        var affinityTarget = $"sched.{process.ProcessId}.affinity";
                        var priorityTarget = $"sched.{process.ProcessId}.priority";
                        var cpuSetsTarget = $"sched.{process.ProcessId}.cpusets";

                        var needMask = PrepareEnforceField(
                            affinityTarget,
                            source,
                            priority,
                            schedPlan.Id,
                            currentMask,
                            rule.CpuMask,
                            startTicks,
                            ref changed);

                        var needPriority = PrepareEnforceField(
                            priorityTarget,
                            source,
                            priority,
                            schedPlan.Id,
                            currentPriority,
                            rule.Priority,
                            startTicks,
                            ref changed);

                        if (needMask)
                        {
                            if (_scheduler.TryApply(process.ProcessId, rule.CpuMask, null, out var applyError))
                            {
                                if (!_scheduler.TryRead(process.ProcessId, out var actualMask, out _, out _))
                                {
                                    _ledger.MarkUnknown(affinityTarget, "写入后读不到掩码，无法确认是否生效。");
                                }
                                else if (SameMask(actualMask, rule.CpuMask))
                                {
                                    _ledger.Commit(affinityTarget, actualMask!);
                                }
                                else
                                {
                                    _ledger.Fail(affinityTarget, $"回读掩码 {actualMask} 与期望 {rule.CpuMask} 不一致。");
                                }
                            }
                            else
                            {
                                _ledger.Fail(affinityTarget, applyError ?? "应用失败。");
                            }

                            changed = true;
                        }

                        if (needPriority)
                        {
                            if (_scheduler.TryApply(process.ProcessId, null, rule.Priority, out var applyError))
                            {
                                if (!_scheduler.TryRead(process.ProcessId, out _, out var actualPriority, out _))
                                {
                                    _ledger.MarkUnknown(priorityTarget, "写入后读不到优先级，无法确认是否生效。");
                                }
                                else if (string.Equals(actualPriority, rule.Priority, StringComparison.Ordinal))
                                {
                                    _ledger.Commit(priorityTarget, actualPriority!);
                                }
                                else
                                {
                                    _ledger.Fail(priorityTarget, $"回读优先级 {actualPriority} 与期望 {rule.Priority} 不一致。");
                                }
                            }
                            else
                            {
                                _ledger.Fail(priorityTarget, applyError ?? "应用失败。");
                            }

                            changed = true;
                        }

                        if (normalizedCpuSets is not null
                            && cpuSetScheduler is not null
                            && cpuSetScheduler.TryReadCpuSets(process.ProcessId, out var currentCpuSets, out _))
                        {
                            var needCpuSets = PrepareEnforceField(
                                cpuSetsTarget,
                                source,
                                priority,
                                schedPlan.Id,
                                currentCpuSets ?? string.Empty,
                                normalizedCpuSets,
                                startTicks,
                                ref changed);

                            if (needCpuSets)
                            {
                                if (cpuSetScheduler.TryApplyCpuSets(process.ProcessId, normalizedCpuSets, out var csError))
                                {
                                    if (!cpuSetScheduler.TryReadCpuSets(process.ProcessId, out var actualCpuSets, out _))
                                    {
                                        _ledger.MarkUnknown(cpuSetsTarget, "写入后读不到 CPU Sets，无法确认是否生效。");
                                    }
                                    else if (SameCpuSets(actualCpuSets, normalizedCpuSets))
                                    {
                                        _ledger.Commit(cpuSetsTarget, actualCpuSets ?? string.Empty);
                                    }
                                    else
                                    {
                                        _ledger.Fail(cpuSetsTarget, $"回读 CPU Sets {actualCpuSets} 与期望 {normalizedCpuSets} 不一致。");
                                    }

                                    WarnCoreParking(schedPlan.Id);
                                }
                                else
                                {
                                    _ledger.Fail(cpuSetsTarget, csError ?? "应用 CPU Sets 失败。");
                                }

                                changed = true;
                            }
                        }
                    }
                }
            }

            if (changed)
            {
                await PersistLedgerAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool SameCpuSets(string? actual, string? desired)
    {
        var left = actual ?? string.Empty;
        var right = desired ?? string.Empty;

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        }

        return CpuSetSpec.TryNormalize(left, out var normalizedLeft, out _)
               && CpuSetSpec.TryNormalize(right, out var normalizedRight, out _)
               && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    /// <summary>CPU Set 是软约束：核心停泊开启时实际运行范围可能超出设定集合，按方案提示一次。</summary>
    private void WarnCoreParking(string planId)
    {
        var parked = CoreParkingPercent();
        if (parked is not int percent || percent >= 100)
        {
            return;
        }

        lock (_cpuSetsWarned)
        {
            if (!_cpuSetsWarned.Add($"parking:{planId}"))
            {
                return;
            }
        }

        _log.Warn($"方案 {planId} 的 CPU Sets 已写入，但当前核心停泊 {percent}%，实际运行范围可能超出设定集合。");
    }

    private int? CoreParkingPercent()
    {
        var now = Environment.TickCount64;
        if (now - _parkingTick < 10_000)
        {
            return _parkingValue;
        }

        _parkingTick = now;
        _parkingValue = null;

        var scheme = _power.GetActiveScheme();
        if (scheme is null)
        {
            return null;
        }

        // 按当前供电侧取值：AC 与 DC 的停泊策略本就不同，混用会让插电用户天天看到电池策略的提示。
        var ac = _powerStatus?.OnAcPower ?? true;
        _parkingValue = _power.TryReadValue(scheme.Guid, SubProcessorGroup, CoreParkingSetting, ac, out var value, out _)
                        && value is >= 0 and <= 100
            ? (int)value
            : null;

        return _parkingValue;
    }

    private static readonly Guid SubProcessorGroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid CoreParkingSetting = new("0cc5b647-c1df-4637-891a-dec35c318583");

    private long _parkingTick = long.MinValue / 2;
    private int? _parkingValue;

    private void WarnCpuSetsUnavailable(string planId, bool supported)
    {
        lock (_cpuSetsWarned)
        {
            if (!_cpuSetsWarned.Add(planId))
            {
                return;
            }
        }

        _log.Warn(supported
            ? $"方案 {planId} 的 CPU Sets 规则无法解析，持续执行时跳过该字段。"
            : $"方案 {planId} 的 CPU Sets 规则在当前系统不可用，持续执行时跳过该字段（亲和性与优先级不受影响）。");
    }

    private bool PrepareEnforceField(
        string target,
        string source,
        int priority,
        string planId,
        string? currentValue,
        string? desiredValue,
        long processStartTicks,
        ref bool changed)
    {
        if (desiredValue is null)
        {
            return false;
        }

        var existing = _ledger.Get(target);
        if (existing is not null)
        {
            if (existing.Owner == OwnerKind.User)
            {
                return false;
            }

            if (existing.Owner is OwnerKind.Scene or OwnerKind.Plan
                && existing.Status == LedgerStatus.Applied
                && existing.Priority > priority
                && !string.Equals(existing.Source, source, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(existing.Source, source, StringComparison.Ordinal)
                && existing.Status == LedgerStatus.Applied)
            {
                if (string.Equals(currentValue, desiredValue, StringComparison.Ordinal))
                {
                    return false;
                }

                _ledger.Acquire(target, source, priority, currentValue, desiredValue, planId, OwnerFor(source), processStartTicks);
                return true;
            }
        }

        var outcome = _ledger.Acquire(target, source, priority, currentValue, desiredValue, planId, OwnerFor(source), processStartTicks);
        if (outcome is not (AcquireOutcome.Acquired or AcquireOutcome.AlreadyOwned))
        {
            return false;
        }

        if (string.Equals(currentValue, desiredValue, StringComparison.Ordinal))
        {
            _ledger.Commit(target, currentValue ?? string.Empty);
            changed = true;
            return false;
        }

        return true;
    }

    public async Task<bool> ReleaseTakeoverAsync(string target, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ledger.ReleaseUserTakeover(target))
            {
                return false;
            }

            await PersistLedgerAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private (IReadOnlyList<TargetOutcome> Targets, string? Error) ApplyCpu(
        CpuTuningPlan plan,
        string source,
        int priority)
    {
        var outcomes = new List<TargetOutcome>();
        var hasCo = CurveOptimizerConfig.HasAnyCo(plan);
        var needsAmd = hasCo || plan.FMaxLimitMhz is not null;
        if (needsAmd)
        {
            var status = _tuning.Query();
            if (status.Availability != HardwareAvailability.Available)
            {
                if (plan.IntelVoltageOffsetMillivolts is null)
                {
                    return (Array.Empty<TargetOutcome>(), status.Detail ?? status.Availability.ToString());
                }

                outcomes.Add(new TargetOutcome(
                    "cpu.amd",
                    null,
                    null,
                    null,
                    LedgerStatus.Skipped,
                    status.Detail ?? status.Availability.ToString()));
                needsAmd = false;
            }
            else if (!string.IsNullOrWhiteSpace(plan.PlatformId)
                     && !string.IsNullOrWhiteSpace(status.Platform)
                     && !string.Equals(plan.PlatformId, status.Platform, StringComparison.OrdinalIgnoreCase))
            {
                var mismatch = $"方案绑定平台 {plan.PlatformId} 与当前平台 {status.Platform} 不匹配，已安全跳过 AMD 调校。";
                if (plan.IntelVoltageOffsetMillivolts is null)
                {
                    return (new[] { new TargetOutcome("cpu.amd", null, plan.PlatformId, status.Platform, LedgerStatus.Skipped, mismatch) }, mismatch);
                }

                outcomes.Add(new TargetOutcome(
                    "cpu.amd",
                    null,
                    plan.PlatformId,
                    status.Platform,
                    LedgerStatus.Skipped,
                    mismatch));
                needsAmd = false;
            }
        }

        if (needsAmd && hasCo)
        {
            if (!_tuning.TryReadOffsets(out var offsets, out var readError))
            {
                if (plan.FMaxLimitMhz is null && plan.IntelVoltageOffsetMillivolts is null)
                {
                    return (Array.Empty<TargetOutcome>(), readError ?? "读取当前偏移失败。");
                }

                outcomes.Add(new TargetOutcome(
                    "cpu.co",
                    null,
                    null,
                    null,
                    LedgerStatus.Skipped,
                    readError ?? "读取当前偏移失败，已跳过 CO 写入。"));
            }
            else
            {
                var byCore = offsets.ToDictionary(static offset => offset.Core, static offset => offset.Offset);
                var hasLayerDefaults = plan.DefaultOffset is not null || plan.CcdOffsets is { Count: > 0 };
                var candidateCores = hasLayerDefaults
                    ? byCore.Keys.Concat(plan.Cores.Select(static c => c.CoreIndex)).Distinct().Order().ToArray()
                    : plan.Cores.Select(static c => c.CoreIndex).Distinct().Order().ToArray();

                var topology = _tuning as ICpuCcdTopologyProvider;
                var resolved = CurveOptimizerConfig.Resolve(
                    plan,
                    candidateCores,
                    core => topology is not null && topology.TryGetCoreCcd(core, out var ccd) ? ccd : null);

                if (plan.CcdOffsets is { Count: > 0 })
                {
                    foreach (var ccdPair in plan.CcdOffsets.OrderBy(static p => p.Key))
                    {
                        if (!resolved.Any(r => r.CcdIndex == ccdPair.Key))
                        {
                            outcomes.Add(new TargetOutcome(
                                $"cpu.co.ccd.{ccdPair.Key}",
                                null,
                                ccdPair.Value.ToString(CultureInfo.InvariantCulture),
                                null,
                                LedgerStatus.Skipped,
                                topology is null
                                    ? $"当前调校通道不支持 CCD 拓扑映射，已跳过 CCD {ccdPair.Key} 配置。"
                                    : $"当前平台未映射 CCD {ccdPair.Key} 核心，已跳过该 CCD 配置。"));
                        }
                    }
                }

                foreach (var item in resolved)
                {
                    var target = $"cpu.co.{item.CoreIndex}";
                    if (item.Source == CoValueSource.AutoOverride)
                    {
                        outcomes.Add(new TargetOutcome(
                            target,
                            null,
                            "auto",
                            null,
                            LedgerStatus.Skipped,
                            "该核已显式设为 auto，跳过写入以保留默认/当前行为。"));
                        continue;
                    }

                    if (item.EffectiveOffset is not { } desired)
                    {
                        continue;
                    }

                    if (!CurveOptimizerBounds.IsWithinL2(desired))
                    {
                        outcomes.Add(new TargetOutcome(
                            target,
                            null,
                            desired.ToString(CultureInfo.InvariantCulture),
                            null,
                            LedgerStatus.Skipped,
                            CurveOptimizerBounds.FormatL2Rejection(desired)));
                        continue;
                    }

                    if (!byCore.TryGetValue(item.CoreIndex, out var current))
                    {
                        outcomes.Add(new TargetOutcome(
                            target,
                            null,
                            desired.ToString(CultureInfo.InvariantCulture),
                            null,
                            LedgerStatus.Skipped,
                            "读取该核当前偏移失败，未写入。"));
                        continue;
                    }

                    outcomes.Add(ApplyTarget(
                        target,
                        plan.Id,
                        source,
                        priority,
                        current.ToString(CultureInfo.InvariantCulture),
                        desired.ToString(CultureInfo.InvariantCulture),
                        () => _tuning.TryWriteOffset(item.CoreIndex, desired, out var writeError) ? null : writeError,
                        () => ReadCoreOffset(item.CoreIndex)));
                }
            }
        }

        if (needsAmd && plan.FMaxLimitMhz is { } desiredFMax)
        {
            if (_tuning is not IFMaxTuningProvider fmax)
            {
                outcomes.Add(new TargetOutcome(
                    "cpu.fmax",
                    null,
                    desiredFMax.ToString(CultureInfo.InvariantCulture),
                    null,
                    LedgerStatus.Skipped,
                    "当前调校通道不支持 FMax 读写。"));
            }
            else if (!fmax.TryReadFMax(out var currentFMax, out var fmaxReadError))
            {
                outcomes.Add(new TargetOutcome(
                    "cpu.fmax",
                    null,
                    desiredFMax.ToString(CultureInfo.InvariantCulture),
                    null,
                    LedgerStatus.Skipped,
                    fmaxReadError ?? "读取当前 FMax 失败，未写入。"));
            }
            else
            {
                var writeResult = FMaxWriteResult.Failed;
                var outcome = ApplyTarget(
                    "cpu.fmax",
                    plan.Id,
                    source,
                    priority,
                    currentFMax.ToString(CultureInfo.InvariantCulture),
                    desiredFMax.ToString(CultureInfo.InvariantCulture),
                    () =>
                    {
                        writeResult = fmax.TryWriteFMax(desiredFMax, out var writeError);
                        return writeResult == FMaxWriteResult.Applied ? null : writeError;
                    },
                    () => fmax.TryReadFMax(out var actualFMax, out _)
                        ? actualFMax.ToString(CultureInfo.InvariantCulture)
                        : null,
                    FMaxTolerance.Within);

                // 平台未实现该消息（未配置写命令或基线为 0）属于能力缺失，按 Skipped 安全降级，不阻断同方案内其他核配置。
                if (writeResult == FMaxWriteResult.Unsupported)
                {
                    _ledger.Skip("cpu.fmax", outcome.Failure ?? "平台未实现 Boost Limit 消息。");
                    outcome = outcome with { Status = LedgerStatus.Skipped };
                }

                outcomes.Add(outcome);
            }
        }

        if (plan.IntelVoltageOffsetMillivolts is { } desiredMv)
        {
            if (!IntelVoltageOffsetBounds.IsWithinRange(desiredMv))
            {
                outcomes.Add(new TargetOutcome(
                    "cpu.intel.voltage",
                    null,
                    desiredMv.ToString(CultureInfo.InvariantCulture),
                    null,
                    LedgerStatus.Skipped,
                    IntelVoltageOffsetBounds.FormatRejection(desiredMv)));
            }
            else if (_intel is null)
            {
                outcomes.Add(new TargetOutcome(
                    "cpu.intel.voltage",
                    null,
                    desiredMv.ToString(CultureInfo.InvariantCulture),
                    null,
                    LedgerStatus.Skipped,
                    "未装配 Intel MSR 调校通道。"));
            }
            else
            {
                var intelStatus = _intel.Query();
                if (intelStatus.Availability != HardwareAvailability.Available)
                {
                    if (outcomes.Count == 0)
                    {
                        return (Array.Empty<TargetOutcome>(), intelStatus.Detail ?? intelStatus.Availability.ToString());
                    }

                    outcomes.Add(new TargetOutcome(
                        "cpu.intel.voltage",
                        null,
                        desiredMv.ToString(CultureInfo.InvariantCulture),
                        null,
                        LedgerStatus.Failed,
                        intelStatus.Detail ?? intelStatus.Availability.ToString()));
                }
                else if (!_intel.TryRead(out var intelState, out var intelReadError))
                {
                    outcomes.Add(new TargetOutcome(
                        "cpu.intel.voltage",
                        null,
                        desiredMv.ToString(CultureInfo.InvariantCulture),
                        null,
                        LedgerStatus.Skipped,
                        intelReadError ?? "读取 Intel MSR 失败，未写入。"));
                }
                else
                {
                    outcomes.Add(ApplyTarget(
                        "cpu.intel.voltage",
                        plan.Id,
                        source,
                        priority,
                        intelState.VoltageOffsetMillivolts.ToString(CultureInfo.InvariantCulture),
                        desiredMv.ToString(CultureInfo.InvariantCulture),
                        () => _intel.TryWriteVoltageOffset(desiredMv, out var writeError) ? null : writeError,
                        () => _intel.TryRead(out var actual, out _)
                            ? actual.VoltageOffsetMillivolts.ToString(CultureInfo.InvariantCulture)
                            : null));
                }
            }
        }

        return (outcomes, outcomes.Count == 0 ? "方案没有核、FMax 或 Intel 电压偏移配置。" : null);
    }

    private (IReadOnlyList<TargetOutcome> Targets, string? Error) ApplyPower(
        PowerPlan plan,
        string source,
        int priority)
    {
        var outcomes = new List<TargetOutcome>();
        var active = _power.GetActiveScheme();
        var scheme = plan.BaseSchemeGuid ?? active?.Guid;

        if (scheme is null)
        {
            return (Array.Empty<TargetOutcome>(), "无法确定目标电源方案。");
        }

        var schemeId = scheme.Value;

        // 指定 BaseSchemeGuid 只表示「改哪个方案」，不隐含「切过去」。
        // 自动切换会把用户当前的电源方案换掉——笔记本上就是风扇、亮度、待机策略当场变，
        // 而且切到隐藏方案时会把它顶进枚举，用户会误以为它变成永久可见。
        foreach (var setting in plan.Settings)
        {
            if (!Guid.TryParse(setting.SettingId, out var settingId))
            {
                outcomes.Add(InvalidTarget($"power.setting.{setting.SettingId}", "设置项 ID 不是 GUID。"));
                continue;
            }

            var subGroup = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(setting.SubGroupId))
            {
                if (!Guid.TryParse(setting.SubGroupId, out subGroup))
                {
                    outcomes.Add(InvalidTarget($"power.setting.{setting.SettingId}", "子组 ID 不是 GUID。"));
                    continue;
                }
            }

            foreach (var ac in new[] { true, false })
            {
                var text = ac ? setting.AcValue : setting.DcValue;
                if (text is null)
                {
                    continue;
                }

                if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var desired))
                {
                    outcomes.Add(InvalidTarget(
                        TargetName(schemeId, subGroup, settingId, ac),
                        $"电源值不是整数：{text}。"));
                    continue;
                }

                var targetName = TargetName(schemeId, subGroup, settingId, ac);
                if (!_power.TryReadValue(schemeId, subGroup, settingId, ac, out var current, out var readError))
                {
                    outcomes.Add(new TargetOutcome(
                        targetName,
                        null,
                        desired.ToString(CultureInfo.InvariantCulture),
                        null,
                        LedgerStatus.Skipped,
                        readError ?? "读取当前电源值失败，未写入。"));
                    continue;
                }

                outcomes.Add(ApplyTarget(
                    targetName,
                    plan.Id,
                    source,
                    priority,
                    current.ToString(CultureInfo.InvariantCulture),
                    desired.ToString(CultureInfo.InvariantCulture),
                    () => _power.TryWriteValue(schemeId, subGroup, settingId, ac, desired, out var error) ? null : error,
                    () => _power.TryReadValue(schemeId, subGroup, settingId, ac, out var value, out _)
                        ? value.ToString(CultureInfo.InvariantCulture)
                        : null));
            }
        }

        // 写值后要重新激活一次才刷新生效；但只在目标本来就是活动方案时做。
        // 目标不是活动方案时激活等于替用户切方案，改动留在那个方案里，等它被（用户）激活时生效。
        string? activateError = null;
        if (schemeId == active?.Guid
            && outcomes.Any(static outcome => outcome.Status == LedgerStatus.Applied)
            && !_power.TrySetActiveScheme(schemeId, out var setError))
        {
            activateError = setError ?? "重新激活电源方案失败，部分设置可能未立即生效。";
        }

        return (outcomes, activateError);
    }

    private (IReadOnlyList<TargetOutcome> Targets, string? Error) ApplyScheduling(
        SchedulingPlan plan,
        string source,
        int priority)
    {
        var outcomes = new List<TargetOutcome>();

        foreach (var rule in plan.Rules)
        {
            var processes = _scheduler.Find(rule.ProcessMatch);
            if (processes.Count == 0)
            {
                outcomes.Add(new TargetOutcome(
                    $"sched.{rule.ProcessMatch}",
                    null,
                    rule.CpuMask ?? rule.Priority ?? rule.CpuSets,
                    null,
                    LedgerStatus.Skipped,
                    "未找到匹配进程。"));
                continue;
            }

            var cpuSetScheduler = _scheduler as IProcessCpuSetScheduler;
            string? cpuSetsDetail = null;
            var cpuSetsSupported = cpuSetScheduler is not null && cpuSetScheduler.IsCpuSetsSupported(out cpuSetsDetail);

            foreach (var process in processes)
            {
                var startTicks = StartTicksOf(process);
                DropStaleSchedulingEntries(process.ProcessId, startTicks);

                if (!_scheduler.TryRead(process.ProcessId, out var currentMask, out var currentPriority, out _))
                {
                    outcomes.Add(InvalidTarget($"sched.{process.ProcessId}", "无法读取当前调度设置。"));
                    continue;
                }

                var affinityTarget = $"sched.{process.ProcessId}.affinity";
                var priorityTarget = $"sched.{process.ProcessId}.priority";
                var cpuSetsTarget = $"sched.{process.ProcessId}.cpusets";

                string? maskToApply = null;
                if (rule.CpuMask is not null)
                {
                    var outcome = _ledger.Acquire(affinityTarget, source, priority, currentMask, rule.CpuMask, plan.Id, OwnerFor(source), startTicks);
                    if (outcome is AcquireOutcome.Acquired or AcquireOutcome.AlreadyOwned)
                    {
                        maskToApply = rule.CpuMask;
                    }
                    else
                    {
                        outcomes.Add(new TargetOutcome(
                            affinityTarget,
                            currentMask,
                            rule.CpuMask,
                            currentMask,
                            LedgerStatus.Skipped,
                            DenialReason(outcome)));
                    }
                }

                string? priorityToApply = null;
                if (rule.Priority is not null)
                {
                    var outcome = _ledger.Acquire(priorityTarget, source, priority, currentPriority, rule.Priority, plan.Id, OwnerFor(source), startTicks);
                    if (outcome is AcquireOutcome.Acquired or AcquireOutcome.AlreadyOwned)
                    {
                        priorityToApply = rule.Priority;
                    }
                    else
                    {
                        outcomes.Add(new TargetOutcome(
                            priorityTarget,
                            currentPriority,
                            rule.Priority,
                            currentPriority,
                            LedgerStatus.Skipped,
                            DenialReason(outcome)));
                    }
                }

                // 亲和性与优先级分开写入：一项失败时另一项的结果必须保留，不能合并成一个失败。
                if (maskToApply is not null)
                {
                    if (_scheduler.TryApply(process.ProcessId, maskToApply, null, out var applyError))
                    {
                        if (!_scheduler.TryRead(process.ProcessId, out var actualMask, out _, out _))
                        {
                            _ledger.MarkUnknown(affinityTarget, "写入后读不到掩码，无法确认是否生效。");
                        }
                        else if (SameMask(actualMask, maskToApply))
                        {
                            _ledger.Commit(affinityTarget, actualMask!);
                        }
                        else
                        {
                            _ledger.Fail(affinityTarget, $"回读掩码 {actualMask} 与期望 {maskToApply} 不一致。");
                        }
                    }
                    else
                    {
                        _ledger.Fail(affinityTarget, applyError ?? "应用失败。");
                    }

                    outcomes.Add(OutcomeFromLedger(affinityTarget));
                }

                if (priorityToApply is not null)
                {
                    if (_scheduler.TryApply(process.ProcessId, null, priorityToApply, out var applyError))
                    {
                        if (!_scheduler.TryRead(process.ProcessId, out _, out var actualPriority, out _))
                        {
                            _ledger.MarkUnknown(priorityTarget, "写入后读不到优先级，无法确认是否生效。");
                        }
                        else if (string.Equals(actualPriority, priorityToApply, StringComparison.Ordinal))
                        {
                            _ledger.Commit(priorityTarget, actualPriority!);
                        }
                        else
                        {
                            _ledger.Fail(priorityTarget, $"回读优先级 {actualPriority} 与期望 {priorityToApply} 不一致。");
                        }
                    }
                    else
                    {
                        _ledger.Fail(priorityTarget, applyError ?? "应用失败。");
                    }

                    outcomes.Add(OutcomeFromLedger(priorityTarget));
                }

                if (rule.CpuSets is not null)
                {
                    if (!CpuSetSpec.TryNormalize(rule.CpuSets, out var normalizedCpuSets, out var parseError))
                    {
                        outcomes.Add(new TargetOutcome(
                            cpuSetsTarget,
                            null,
                            rule.CpuSets,
                            null,
                            LedgerStatus.Skipped,
                            parseError ?? "CPU Sets 格式非法。"));
                    }
                    else if (!cpuSetsSupported || cpuSetScheduler is null)
                    {
                        outcomes.Add(new TargetOutcome(
                            cpuSetsTarget,
                            null,
                            normalizedCpuSets,
                            null,
                            LedgerStatus.Skipped,
                            cpuSetsDetail ?? "当前调度器不支持 CPU Sets。"));
                    }
                    else if (!cpuSetScheduler.TryReadCpuSets(process.ProcessId, out var currentCpuSets, out var readCsError))
                    {
                        outcomes.Add(new TargetOutcome(
                            cpuSetsTarget,
                            null,
                            normalizedCpuSets,
                            null,
                            LedgerStatus.Skipped,
                            readCsError ?? "读取当前 CPU Sets 失败。"));
                    }
                    else
                    {
                        var cpuSetsOutcome = ApplyTarget(
                            cpuSetsTarget,
                            plan.Id,
                            source,
                            priority,
                            currentCpuSets ?? string.Empty,
                            normalizedCpuSets,
                            () => cpuSetScheduler.TryApplyCpuSets(process.ProcessId, normalizedCpuSets, out var csErr) ? null : csErr,
                            () => cpuSetScheduler.TryReadCpuSets(process.ProcessId, out var actualCs, out _) ? actualCs ?? string.Empty : null,
                            SameCpuSets,
                            startTicks);

                        outcomes.Add(cpuSetsOutcome);

                        if (cpuSetsOutcome.Status == LedgerStatus.Applied)
                        {
                            WarnCoreParking(plan.Id);
                        }
                    }
                }
            }
        }

        return (outcomes, outcomes.Count == 0 ? "方案没有调度规则。" : null);
    }

    private static string DenialReason(AcquireOutcome outcome) => outcome switch
    {
        AcquireOutcome.DeniedUserOwned => "用户已接管。",
        AcquireOutcome.DeniedLowerPriority => "更高优先级场景持有。",
        _ => "调度目标不可用。",
    };

    private TargetOutcome ApplyTarget(
        string target,
        string planId,
        string source,
        int priority,
        string? current,
        string desired,
        Func<string?> write,
        Func<string?> readBack,
        Func<string, string, bool>? equivalent = null,
        long processStartTicks = 0)
    {
        var acquired = _ledger.Acquire(target, source, priority, current, desired, planId, OwnerFor(source), processStartTicks);
        if (acquired == AcquireOutcome.DeniedUserOwned)
        {
            return new TargetOutcome(target, current, desired, current, LedgerStatus.Skipped, "用户已接管。");
        }

        if (acquired == AcquireOutcome.DeniedLowerPriority)
        {
            return new TargetOutcome(target, current, desired, current, LedgerStatus.Skipped, "更高优先级场景持有。");
        }

        if (string.Equals(current, desired, StringComparison.Ordinal))
        {
            _ledger.Commit(target, desired);
            return new TargetOutcome(target, current, desired, desired, LedgerStatus.Applied, null);
        }

        var writeError = write();
        if (writeError is not null)
        {
            _ledger.Fail(target, writeError);
            return new TargetOutcome(target, current, desired, null, LedgerStatus.Failed, writeError);
        }

        var actual = readBack();
        if (actual is null)
        {
            _ledger.Fail(target, "回读失败。");
            return new TargetOutcome(target, current, desired, null, LedgerStatus.Failed, "回读失败。");
        }

        _ledger.Commit(target, actual, equivalent);
        return Matches(actual, desired, equivalent)
            ? new TargetOutcome(target, current, desired, actual, LedgerStatus.Applied, null)
            : new TargetOutcome(target, current, desired, actual, LedgerStatus.Failed,
                $"回读值 {actual} 与期望 {desired} 不一致。");
    }

    private static bool Matches(string actual, string desired, Func<string, string, bool>? equivalent) =>
        equivalent is null
            ? string.Equals(actual, desired, StringComparison.Ordinal)
            : equivalent(actual, desired);

    private IReadOnlyList<TargetOutcome> Restore(IReadOnlyList<LedgerEntry> entries)
    {
        var outcomes = new List<TargetOutcome>(entries.Count);
        foreach (var entry in entries)
        {
            var (actual, error) = RestoreTarget(entry);
            var success = error is null && string.Equals(actual, entry.PreviousValue, StringComparison.Ordinal);
            _ledger.MarkReverted(entry.Target, actual ?? string.Empty, success, error);

            if (!success)
            {
                _log.Warn($"回退 {entry.Target} 失败：{error ?? $"回读 {actual}，期望 {entry.PreviousValue}"}");
            }

            outcomes.Add(new TargetOutcome(
                entry.Target,
                entry.PreviousValue,
                entry.DesiredValue,
                actual,
                success ? LedgerStatus.Reverted : LedgerStatus.Applied,
                success ? null : error ?? "回读与前值不一致。"));
        }

        return outcomes;
    }

    private (string? Actual, string? Error) RestoreTarget(LedgerEntry entry)
    {
        if (entry.PreviousValue is null)
        {
            return (null, "缺少前值，无法回退。");
        }

        if (string.Equals(entry.Target, "cpu.intel.voltage", StringComparison.Ordinal))
        {
            if (_intel is null)
            {
                return (null, "未装配 Intel MSR 调校通道。");
            }

            if (!int.TryParse(entry.PreviousValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prevMv))
            {
                return (null, "前值不是整数电压偏移。");
            }

            if (!_intel.TryWriteVoltageOffset(prevMv, out var error))
            {
                return (null, error);
            }

            return _intel.TryRead(out var actual, out _)
                ? (actual.VoltageOffsetMillivolts.ToString(CultureInfo.InvariantCulture), null)
                : (null, "回读失败。");
        }

        if (string.Equals(entry.Target, "cpu.fmax", StringComparison.Ordinal))
        {
            if (_tuning is not IFMaxTuningProvider fmax)
            {
                return (null, "当前调校通道不支持 FMax 回退。");
            }

            if (!int.TryParse(entry.PreviousValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prevMhz))
            {
                return (null, "前值不是整数频率。");
            }

            if (fmax.TryWriteFMax(prevMhz, out var error) != FMaxWriteResult.Applied)
            {
                return (null, error);
            }

            return fmax.TryReadFMax(out var actual, out _)
                ? (actual.ToString(CultureInfo.InvariantCulture), null)
                : (null, "回读失败。");
        }

        if (entry.Target.StartsWith("cpu.co.", StringComparison.Ordinal))
        {
            if (!int.TryParse(entry.Target["cpu.co.".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var core)
                || !int.TryParse(entry.PreviousValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli))
            {
                return (null, "目标格式非法。");
            }

            return _tuning.TryWriteOffset(core, milli, out var error)
                ? (ReadCoreOffset(core), null)
                : (null, error);
        }

        if (string.Equals(entry.Target, "power.active", StringComparison.Ordinal))
        {
            if (!Guid.TryParse(entry.PreviousValue, out var scheme))
            {
                return (null, "前值不是方案 GUID。");
            }

            return _power.TrySetActiveScheme(scheme, out var error)
                ? (_power.GetActiveScheme()?.Guid.ToString("D"), null)
                : (null, error);
        }

        if (TryParsePowerTarget(entry.Target, out var schemeId, out var subGroup, out var setting, out var ac))
        {
            if (!uint.TryParse(entry.PreviousValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return (null, "前值不是整数。");
            }

            if (!_power.TryWriteValue(schemeId, subGroup, setting, ac, value, out var error))
            {
                return (null, error);
            }

            return _power.TryReadValue(schemeId, subGroup, setting, ac, out var actual, out _)
                ? (actual.ToString(CultureInfo.InvariantCulture), null)
                : (null, "回读失败。");
        }

        if (TryParseSchedulingTarget(entry.Target, out var pid, out var field))
        {
            // 账本记的是某个具体进程；PID 被复用时回退会写到无关的新进程上。
            if (!ProcessStillAlive(pid, entry.ProcessStartTicks))
            {
                return (null, $"进程 {pid} 已退出或已被复用，跳过回退。");
            }

            if (field == "cpusets")
            {
                if (_scheduler is not IProcessCpuSetScheduler cpuSetScheduler)
                {
                    return (null, "当前调度器不支持 CPU Sets 回退。");
                }

                if (!cpuSetScheduler.TryApplyCpuSets(pid, entry.PreviousValue, out var csError))
                {
                    return (null, csError);
                }

                return cpuSetScheduler.TryReadCpuSets(pid, out var actualCs, out _)
                    ? (actualCs ?? string.Empty, null)
                    : (null, "回读失败。");
            }

            var mask = field == "affinity" ? entry.PreviousValue : null;
            var priority = field == "priority" ? entry.PreviousValue : null;
            if (!_scheduler.TryApply(pid, mask, priority, out var error))
            {
                return (null, error);
            }

            if (!_scheduler.TryRead(pid, out var actualMask, out var actualPriority, out _))
            {
                return (null, "回读失败。");
            }

            return (field == "affinity" ? actualMask : actualPriority, null);
        }

        return (null, "未知目标类型。");
    }

    private string? ReadTarget(string target, IReadOnlyList<CoreOffset>? offsets)
    {
        if (string.Equals(target, "cpu.intel.voltage", StringComparison.Ordinal))
        {
            return _intel is not null && _intel.TryRead(out var actual, out _)
                ? actual.VoltageOffsetMillivolts.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        if (string.Equals(target, "cpu.fmax", StringComparison.Ordinal))
        {
            return _tuning is IFMaxTuningProvider fmax && fmax.TryReadFMax(out var actual, out _)
                ? actual.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        if (target.StartsWith("cpu.co.", StringComparison.Ordinal))
        {
            if (offsets is null
                || !int.TryParse(target["cpu.co.".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var core))
            {
                return null;
            }

            var match = offsets.FirstOrDefault(offset => offset.Core == core);
            return match is null ? null : match.Offset.ToString(CultureInfo.InvariantCulture);
        }

        if (string.Equals(target, "power.active", StringComparison.Ordinal))
        {
            return _power.GetActiveScheme()?.Guid.ToString("D");
        }

        if (TryParsePowerTarget(target, out var scheme, out var subGroup, out var setting, out var ac))
        {
            return _power.TryReadValue(scheme, subGroup, setting, ac, out var value, out _)
                ? value.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        return null;
    }

    private string? ReadCoreOffset(int core)
    {
        if (!_tuning.TryReadOffsets(out var offsets, out _))
        {
            return null;
        }

        var match = offsets.FirstOrDefault(offset => offset.Core == core);
        return match is null ? null : match.Offset.ToString(CultureInfo.InvariantCulture);
    }

    private Plan FindPlan(string planId) =>
        _store.Current.Plans.FirstOrDefault(plan => string.Equals(plan.Id, planId, StringComparison.Ordinal))
        ?? throw new CoreException(ErrorCodes.PlanNotFound, $"方案 {planId} 不存在。");

    private bool PersistsAcrossReboot(Plan plan) => plan switch
    {
        CpuTuningPlan cpu when cpu.IntelVoltageOffsetMillivolts is not null
                               && !CurveOptimizerConfig.HasAnyCo(cpu)
                               && cpu.FMaxLimitMhz is null
            => _intel?.Query().PersistsAcrossReboot ?? false,
        CpuTuningPlan cpu when cpu.IntelVoltageOffsetMillivolts is not null
            => _tuning.Query().PersistsAcrossReboot && (_intel?.Query().PersistsAcrossReboot ?? false),
        CpuTuningPlan => _tuning.Query().PersistsAcrossReboot,
        _ => true,
    };

    private static OwnerKind OwnerFor(string source) =>
        source.StartsWith("scene:", StringComparison.Ordinal) ? OwnerKind.Scene : OwnerKind.Plan;

    private bool HasAppliedEntries(string planId) =>
        _ledger.Entries.Any(entry =>
            string.Equals(entry.PlanId, planId, StringComparison.Ordinal)
            && StateLedger.IsHeld(entry));

    private static PlanApplyState Summarize(IReadOnlyList<TargetOutcome> targets, string? error)
    {
        if (targets.Count == 0)
        {
            return error is null ? PlanApplyState.NotApplied : PlanApplyState.Failed;
        }

        if (targets.All(static target => target.Status == LedgerStatus.Skipped))
        {
            return PlanApplyState.NotApplied;
        }

        var applied = targets.Count(static target => target.Status == LedgerStatus.Applied);
        if (applied == 0)
        {
            return PlanApplyState.Failed;
        }

        return applied == targets.Count && error is null ? PlanApplyState.Applied : PlanApplyState.Partial;
    }

    private static string Describe(PlanApplyState state, IReadOnlyList<TargetOutcome> targets)
    {
        var applied = targets.Count(static target => target.Status == LedgerStatus.Applied);
        var failed = targets.Count(static target => target.Status == LedgerStatus.Failed);
        var skipped = targets.Count(static target => target.Status == LedgerStatus.Skipped);
        return state switch
        {
            PlanApplyState.Applied => $"已应用 {applied} 项。",
            PlanApplyState.Partial => $"部分生效：成功 {applied}，失败 {failed}，跳过 {skipped}。",
            PlanApplyState.Failed => $"未生效：失败 {failed}。",
            _ => $"未应用：跳过 {skipped}。",
        };
    }

    private static TargetOutcome InvalidTarget(string target, string reason) =>
        new(target, null, null, null, LedgerStatus.Skipped, reason);

    private TargetOutcome OutcomeFromLedger(string target)
    {
        var entry = _ledger.Get(target);
        return entry is null
            ? InvalidTarget(target, "账本缺少记录。")
            : new TargetOutcome(target, entry.PreviousValue, entry.DesiredValue, entry.ActualValue, entry.Status, entry.Failure);
    }

    private static string TargetName(Guid scheme, Guid subGroup, Guid setting, bool ac) =>
        $"power.setting.{scheme:D}.{subGroup:D}.{setting:D}.{(ac ? "ac" : "dc")}";

    private static bool TryParsePowerTarget(
        string target,
        out Guid scheme,
        out Guid subGroup,
        out Guid setting,
        out bool ac)
    {
        scheme = Guid.Empty;
        subGroup = Guid.Empty;
        setting = Guid.Empty;
        ac = true;

        if (!target.StartsWith("power.setting.", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = target["power.setting.".Length..].Split('.');
        if (parts.Length != 4
            || parts[3] is not ("ac" or "dc")
            || !Guid.TryParse(parts[0], out scheme)
            || !Guid.TryParse(parts[1], out subGroup)
            || !Guid.TryParse(parts[2], out setting))
        {
            return false;
        }

        ac = parts[3] == "ac";
        return true;
    }

    /// <summary>回读值与期望值按数值比较：写入方可能返回不同位宽的十六进制文本。</summary>
    private static bool SameMask(string? actual, string? desired) =>
        TryParseHexMask(actual, out var a) && TryParseHexMask(desired, out var d) && a == d;

    private static bool TryParseHexMask(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseSchedulingTarget(string target, out int processId, out string field)
    {
        processId = 0;
        field = string.Empty;

        if (!target.StartsWith("sched.", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = target["sched.".Length..].Split('.');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId)
               && (field = parts[1]) is "affinity" or "priority" or "cpusets";
    }

    /// <summary>同 PID 的新进程不是账本记录的那个：旧条目直接丢弃，既不复用前值也不回退到新进程。</summary>
    private void DropStaleSchedulingEntries(int processId, long liveTicks)
    {
        if (liveTicks == 0)
        {
            return;
        }

        foreach (var entry in _ledger.Entries.ToArray())
        {
            if (TryParseSchedulingTarget(entry.Target, out var pid, out _)
                && pid == processId
                && entry.ProcessStartTicks != 0
                && entry.ProcessStartTicks != liveTicks)
            {
                _ledger.Remove(entry.Target);
            }
        }
    }

    /// <summary>进程创建时间只在需要记账时单查，不对整表逐个开句柄。</summary>
    private long StartTicksOf(ProcessTarget process) =>
        process.StartedAtTicks != 0
            ? process.StartedAtTicks
            : (_scheduler.TryGetStartTicks(process.ProcessId, out var ticks) ? ticks : 0);

    /// <summary>账本记录的是某个具体进程：PID 被复用后不能再算作同一目标。</summary>
    private bool ProcessStillAlive(int processId, long recordedTicks) =>
        _scheduler.TryGetStartTicks(processId, out var liveTicks)
        && (recordedTicks == 0 || liveTicks == recordedTicks);

    private Task PersistAsync(Plan updated, CancellationToken ct) =>
        _store.UpdateAsync(
            document => document with
            {
                Plans = document.Plans.Select(plan => plan.Id == updated.Id ? updated : plan).ToArray(),
                Ledger = [.. _ledger.Entries],
            },
            ct);

    private Task PersistPlanAsync(Plan updated, CancellationToken ct) =>
        _store.UpdateAsync(
            document => document with
            {
                Plans = document.Plans.Select(plan => plan.Id == updated.Id ? updated : plan).ToArray(),
            },
            ct);

    private Task PersistLedgerAsync(CancellationToken ct) =>
        _store.UpdateAsync(document => document with { Ledger = [.. _ledger.Entries] }, ct);
}
/// <summary>系统恢复后的核对结果：确认、重写、用户接管与无法确认分别计数。</summary>
public sealed record ResumeReconcileResult(
    int Verified,
    int Reapplied,
    int TakenOver,
    int Unknown,
    IReadOnlyList<string> Details);
