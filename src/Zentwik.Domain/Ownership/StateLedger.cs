namespace Zentwik.Domain.Ownership;

public enum OwnerKind
{
    None,
    Scene,
    User,
    Plan,
    System,
}

public enum LedgerStatus
{
    Applied,
    Reverted,
    Failed,
    Skipped,

    /// <summary>写入了但无法确认实际值（如休眠恢复后读取失败），不得当作成功或失败。</summary>
    Unknown,
}

public enum AcquireOutcome
{
    Acquired,
    AlreadyOwned,
    DeniedUserOwned,
    DeniedLowerPriority,
}

public sealed record LedgerEntry
{
    public required string Target { get; init; }

    public string? PlanId { get; init; }

    public string? PreviousValue { get; init; }

    public string? DesiredValue { get; init; }

    public string? ActualValue { get; init; }

    public required string Source { get; init; }

    public OwnerKind Owner { get; init; }

    public int Priority { get; init; }

    public LedgerStatus Status { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Restorable { get; init; }

    public string? Failure { get; init; }

    /// <summary>写入时目标进程的创建时间（UTC ticks）。0 表示未知或非进程目标，用于识别 PID 复用。</summary>
    public long ProcessStartTicks { get; init; }
}

/// <summary>
/// 不变量：用户接管后不得被自动回退；低优先级场景不得抢占已生效的高优先级场景；
/// 回退只回退自己写入且仍归自己所有的对象；回退失败必须保留记录供重试。
/// </summary>
public sealed class StateLedger
{
    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public StateLedger(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public IReadOnlyCollection<LedgerEntry> Entries => _entries.Values;

    public static StateLedger Restore(IEnumerable<LedgerEntry> entries, TimeProvider? clock = null)
    {
        var ledger = new StateLedger(clock);
        foreach (var entry in entries)
        {
            ledger._entries[entry.Target] = entry;
        }

        return ledger;
    }

    public LedgerEntry? Get(string target) => _entries.GetValueOrDefault(target);

    /// <summary>仍然由方案/场景持有（含无法确认实际值的条目），可用于回退与冲突判定。</summary>
    public static bool IsHeld(LedgerEntry entry) =>
        entry.Owner is OwnerKind.Scene or OwnerKind.Plan
        && entry.Status is LedgerStatus.Applied or LedgerStatus.Unknown;

    public bool Remove(string target) => _entries.Remove(target);

    public AcquireOutcome Acquire(
        string target,
        string source,
        int priority,
        string? currentValue,
        string desiredValue,
        string? planId = null,
        OwnerKind owner = OwnerKind.Scene,
        long processStartTicks = 0)
    {
        var now = _clock.GetUtcNow();

        if (_entries.TryGetValue(target, out var existing))
        {
            if (existing.Owner == OwnerKind.User)
            {
                return AcquireOutcome.DeniedUserOwned;
            }

            if (existing.Owner is OwnerKind.Scene or OwnerKind.Plan
                && existing.Status is LedgerStatus.Applied or LedgerStatus.Unknown
                && existing.Priority > priority
                && !string.Equals(existing.Source, source, StringComparison.Ordinal))
            {
                return AcquireOutcome.DeniedLowerPriority;
            }

            if (string.Equals(existing.Source, source, StringComparison.Ordinal)
                && existing.Status is LedgerStatus.Applied or LedgerStatus.Unknown)
            {
                _entries[target] = existing with
                {
                    DesiredValue = desiredValue,
                    PlanId = planId ?? existing.PlanId,
                    ProcessStartTicks = processStartTicks > 0 ? processStartTicks : existing.ProcessStartTicks,
                    UpdatedAt = now,
                };
                return AcquireOutcome.AlreadyOwned;
            }

            // 本方仍持有时沿用最初前值，避免链式恢复丢失基线；
            // 所有权已释放（回退/失败）则以本次读到的实际值为新基线，不能用陈旧快照覆盖用户此后的改动。
            var stillHeld = existing.Status is LedgerStatus.Applied or LedgerStatus.Unknown;
            _entries[target] = existing with
            {
                PreviousValue = stillHeld || currentValue is null ? existing.PreviousValue : currentValue,
                DesiredValue = desiredValue,
                PlanId = planId ?? existing.PlanId,
                Source = source,
                Owner = owner,
                Priority = priority,
                Status = LedgerStatus.Skipped,
                Restorable = true,
                Failure = null,
                ProcessStartTicks = processStartTicks > 0 ? processStartTicks : existing.ProcessStartTicks,
                UpdatedAt = now,
            };
            return AcquireOutcome.Acquired;
        }

        _entries[target] = new LedgerEntry
        {
            Target = target,
            PlanId = planId,
            PreviousValue = currentValue,
            DesiredValue = desiredValue,
            Source = source,
            Owner = owner,
            Priority = priority,
            Status = LedgerStatus.Skipped,
            Restorable = true,
            ProcessStartTicks = processStartTicks,
            UpdatedAt = now,
        };
        return AcquireOutcome.Acquired;
    }

    public void Commit(string target, string actualValue, Func<string, string, bool>? equivalent = null)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            return;
        }

        var matched = entry.DesiredValue is { } desired
                      && (equivalent is null
                          ? string.Equals(actualValue, desired, StringComparison.Ordinal)
                          : equivalent(actualValue, desired));

        _entries[target] = entry with
        {
            ActualValue = actualValue,
            Status = matched ? LedgerStatus.Applied : LedgerStatus.Failed,
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    public void Fail(string target, string reason)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            return;
        }

        _entries[target] = entry with
        {
            Status = LedgerStatus.Failed,
            Failure = reason,
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    public void Skip(string target, string reason)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            return;
        }

        _entries[target] = entry with
        {
            Status = LedgerStatus.Skipped,
            Failure = reason,
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    /// <summary>
    /// 没有可回退基线时标记为不可回退：仍由方案持有写入，但 revert 不再尝试它。
    /// 典型场景是功耗墙的 TDC/EDC——PM 表没有这两项的当前生效值。
    /// </summary>
    public void MarkNotRestorable(string target, string reason)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            return;
        }

        _entries[target] = entry with
        {
            Restorable = false,
            Failure = reason,
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    public void MarkUnknown(string target, string reason)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            return;
        }

        _entries[target] = entry with
        {
            Status = LedgerStatus.Unknown,
            Failure = reason,
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    public bool MarkUserTakeover(string target, string? currentValue)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            _entries[target] = new LedgerEntry
            {
                Target = target,
                ActualValue = currentValue,
                Source = "user",
                Owner = OwnerKind.User,
                Priority = int.MaxValue,
                Status = LedgerStatus.Applied,
                Restorable = false,
                UpdatedAt = _clock.GetUtcNow(),
            };
            return true;
        }

        _entries[target] = entry with
        {
            Owner = OwnerKind.User,
            ActualValue = currentValue ?? entry.ActualValue,
            Restorable = false,
            UpdatedAt = _clock.GetUtcNow(),
        };
        return true;
    }

    public bool ReleaseUserTakeover(string target)
    {
        if (!_entries.TryGetValue(target, out var entry) || entry.Owner != OwnerKind.User)
        {
            return false;
        }

        _entries[target] = entry with
        {
            Owner = OwnerKind.None,
            Restorable = true,
            UpdatedAt = _clock.GetUtcNow(),
        };
        return true;
    }

    public IReadOnlyList<LedgerEntry> PlanRevert(string source) =>
        Entries
            .Where(entry => string.Equals(entry.Source, source, StringComparison.Ordinal))
            .Where(static entry => IsHeld(entry) && entry.Restorable)
            .OrderBy(static entry => entry.Target, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<LedgerEntry> PlanRevertForPlan(string planId, string source) =>
        Entries
            .Where(entry => string.Equals(entry.Source, source, StringComparison.Ordinal)
                            && string.Equals(entry.PlanId, planId, StringComparison.Ordinal))
            .Where(static entry => IsHeld(entry) && entry.Restorable)
            .OrderBy(static entry => entry.Target, StringComparer.Ordinal)
            .ToList();

    public void MarkReverted(string target, string restoredValue, bool success, string? failure = null)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            return;
        }

        // 回退失败仍算 Applied 且保持所有权：对象还在被改写后的状态，必须允许重试。
        _entries[target] = success
            ? entry with
            {
                Owner = OwnerKind.None,
                Status = LedgerStatus.Reverted,
                ActualValue = restoredValue,
                Failure = null,
                Restorable = false,
                UpdatedAt = _clock.GetUtcNow(),
            }
            : entry with
            {
                Status = LedgerStatus.Applied,
                Failure = failure,
                Restorable = true,
                UpdatedAt = _clock.GetUtcNow(),
            };
    }
}
