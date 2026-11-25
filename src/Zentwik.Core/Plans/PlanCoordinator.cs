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
    private readonly IPowerPolicyService _power;
    private readonly IProcessScheduler _scheduler;
    private readonly DiagnosticLog _log;
    private readonly StateLedger _ledger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlanCoordinator(
        StateStore store,
        ICpuTuningProvider tuning,
        IPowerPolicyService power,
        IProcessScheduler scheduler,
        DiagnosticLog log)
    {
        _store = store;
        _tuning = tuning;
        _power = power;
        _scheduler = scheduler;
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
                .Where(static entry => entry.Owner is OwnerKind.Scene or OwnerKind.Plan && entry.Restorable && entry.Status == LedgerStatus.Applied)
                .OrderBy(static entry => entry.Target, StringComparer.Ordinal)
                .ToArray();

            var outcomes = Restore(entries);
            await PersistLedgerAsync(ct).ConfigureAwait(false);

            var plan = _store.Current.Plans.FirstOrDefault(p => p.Id == planId);
            if (plan is not null)
            {
                var remaining = _ledger.Entries.Any(entry =>
                    string.Equals(entry.PlanId, planId, StringComparison.Ordinal)
                    && entry.Owner == OwnerKind.Scene
                    && entry.Status == LedgerStatus.Applied);

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
        var status = _tuning.Query();
        if (status.Availability != HardwareAvailability.Available)
        {
            return (Array.Empty<TargetOutcome>(), status.Detail ?? status.Availability.ToString());
        }

        if (!_tuning.TryReadOffsets(out var offsets, out var readError))
        {
            return (Array.Empty<TargetOutcome>(), readError ?? "读取当前偏移失败。");
        }

        var byCore = offsets.ToDictionary(static offset => offset.Core, static offset => offset.Offset);
        var outcomes = new List<TargetOutcome>();

        foreach (var core in plan.Cores)
        {
            if (core.Offset is not { } desired)
            {
                continue;
            }

            var target = $"cpu.co.{core.CoreIndex}";
            if (!byCore.TryGetValue(core.CoreIndex, out var current))
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
                () => _tuning.TryWriteOffset(core.CoreIndex, desired, out var writeError) ? null : writeError,
                () => ReadCoreOffset(core.CoreIndex)));
        }

        if (plan.FMaxLimitMhz is not null)
        {
            outcomes.Add(new TargetOutcome(
                "cpu.fmax",
                null,
                plan.FMaxLimitMhz.Value.ToString(CultureInfo.InvariantCulture),
                null,
                LedgerStatus.Skipped,
                "FMax 写入尚未实现，未做任何修改。"));
        }

        return (outcomes, outcomes.Count == 0 ? "方案没有核配置。" : null);
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
        if (plan.BaseSchemeGuid is { } target && active?.Guid != target)
        {
            outcomes.Add(ApplyTarget(
                "power.active",
                plan.Id,
                source,
                priority,
                active?.Guid.ToString("D"),
                target.ToString("D"),
                () => _power.TrySetActiveScheme(target, out var error) ? null : error,
                () => _power.GetActiveScheme()?.Guid.ToString("D")));
        }

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

                _power.TryReadValue(schemeId, subGroup, settingId, ac, out var current, out _);
                outcomes.Add(ApplyTarget(
                    TargetName(schemeId, subGroup, settingId, ac),
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

        if (outcomes.Any(static outcome => outcome.Status == LedgerStatus.Applied))
        {
            _power.TrySetActiveScheme(schemeId, out _);
        }

        return (outcomes, null);
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
                    rule.CpuMask ?? rule.Priority,
                    null,
                    LedgerStatus.Skipped,
                    "未找到匹配进程。"));
                continue;
            }

            foreach (var process in processes)
            {
                if (!_scheduler.TryRead(process.ProcessId, out var currentMask, out var currentPriority, out _))
                {
                    outcomes.Add(InvalidTarget($"sched.{process.ProcessId}", "无法读取当前调度设置。"));
                    continue;
                }

                var affinityTarget = $"sched.{process.ProcessId}.affinity";
                var priorityTarget = $"sched.{process.ProcessId}.priority";
                var acquired = false;

                if (rule.CpuMask is not null)
                {
                    acquired |= AcquireForApply(affinityTarget, plan.Id, source, priority, currentMask, rule.CpuMask);
                }

                if (rule.Priority is not null)
                {
                    acquired |= AcquireForApply(priorityTarget, plan.Id, source, priority, currentPriority, rule.Priority);
                }

                if (!acquired)
                {
                    outcomes.Add(new TargetOutcome(
                        $"sched.{process.ProcessId}",
                        null,
                        null,
                        null,
                        LedgerStatus.Skipped,
                        "调度目标被用户接管或被更高优先级场景持有。"));
                    continue;
                }

                if (_scheduler.TryApply(process.ProcessId, rule.CpuMask, rule.Priority, out var applyError))
                {
                    _scheduler.TryRead(process.ProcessId, out var actualMask, out var actualPriority, out _);

                    if (rule.CpuMask is not null)
                    {
                        _ledger.Commit(affinityTarget, actualMask ?? string.Empty);
                        outcomes.Add(OutcomeFromLedger(affinityTarget));
                    }

                    if (rule.Priority is not null)
                    {
                        _ledger.Commit(priorityTarget, actualPriority ?? string.Empty);
                        outcomes.Add(OutcomeFromLedger(priorityTarget));
                    }
                }
                else
                {
                    if (rule.CpuMask is not null)
                    {
                        _ledger.Fail(affinityTarget, applyError ?? "应用失败。");
                    }

                    if (rule.Priority is not null)
                    {
                        _ledger.Fail(priorityTarget, applyError ?? "应用失败。");
                    }

                    outcomes.Add(new TargetOutcome(
                        $"sched.{process.ProcessId}",
                        null,
                        null,
                        null,
                        LedgerStatus.Failed,
                        applyError ?? "应用失败。"));
                }
            }
        }

        return (outcomes, outcomes.Count == 0 ? "方案没有调度规则。" : null);
    }

    private bool AcquireForApply(
        string target,
        string planId,
        string source,
        int priority,
        string? current,
        string desired)
    {
        var outcome = _ledger.Acquire(target, source, priority, current, desired, planId, OwnerFor(source));
        return outcome is AcquireOutcome.Acquired or AcquireOutcome.AlreadyOwned;
    }

    private TargetOutcome ApplyTarget(
        string target,
        string planId,
        string source,
        int priority,
        string? current,
        string desired,
        Func<string?> write,
        Func<string?> readBack)
    {
        var acquired = _ledger.Acquire(target, source, priority, current, desired, planId, OwnerFor(source));
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

        _ledger.Commit(target, actual);
        return string.Equals(actual, desired, StringComparison.Ordinal)
            ? new TargetOutcome(target, current, desired, actual, LedgerStatus.Applied, null)
            : new TargetOutcome(target, current, desired, actual, LedgerStatus.Failed,
                $"回读值 {actual} 与期望 {desired} 不一致。");
    }

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

    private bool PersistsAcrossReboot(Plan plan) => plan.Kind switch
    {
        PlanKind.CpuTuning => _tuning.Query().PersistsAcrossReboot,
        _ => true,
    };

    private static OwnerKind OwnerFor(string source) =>
        source.StartsWith("scene:", StringComparison.Ordinal) ? OwnerKind.Scene : OwnerKind.Plan;

    private bool HasAppliedEntries(string planId) =>
        _ledger.Entries.Any(entry =>
            string.Equals(entry.PlanId, planId, StringComparison.Ordinal)
            && entry.Owner is OwnerKind.Scene or OwnerKind.Plan
            && entry.Status == LedgerStatus.Applied);

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
               && (field = parts[1]) is "affinity" or "priority";
    }

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
