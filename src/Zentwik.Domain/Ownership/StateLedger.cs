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

    public LedgerEntry? Get(string target) => _entries.GetValueOrDefault(target);

    public AcquireOutcome Acquire(string target, string source, int priority, string? currentValue, string desiredValue)
    {
        var now = _clock.GetUtcNow();

        if (_entries.TryGetValue(target, out var existing))
        {
            if (existing.Owner == OwnerKind.User)
            {
                return AcquireOutcome.DeniedUserOwned;
            }

            if (existing.Owner == OwnerKind.Scene
                && existing.Status == LedgerStatus.Applied
                && existing.Priority > priority
                && !string.Equals(existing.Source, source, StringComparison.Ordinal))
            {
                return AcquireOutcome.DeniedLowerPriority;
            }

            if (string.Equals(existing.Source, source, StringComparison.Ordinal)
                && existing.Status == LedgerStatus.Applied)
            {
                _entries[target] = existing with { DesiredValue = desiredValue, UpdatedAt = now };
                return AcquireOutcome.AlreadyOwned;
            }

            // 接管时沿用最初记录的前值，避免链式恢复丢失真正的前值。
            _entries[target] = existing with
            {
                DesiredValue = desiredValue,
                Source = source,
                Owner = OwnerKind.Scene,
                Priority = priority,
                Status = LedgerStatus.Skipped,
                Restorable = true,
                UpdatedAt = now,
            };
            return AcquireOutcome.Acquired;
        }

        _entries[target] = new LedgerEntry
        {
            Target = target,
            PreviousValue = currentValue,
            DesiredValue = desiredValue,
            Source = source,
            Owner = OwnerKind.Scene,
            Priority = priority,
            Status = LedgerStatus.Skipped,
            Restorable = true,
            UpdatedAt = now,
        };
        return AcquireOutcome.Acquired;
    }

    public void Commit(string target, string actualValue)
    {
        if (!_entries.TryGetValue(target, out var entry))
        {
            return;
        }

        _entries[target] = entry with
        {
            ActualValue = actualValue,
            Status = string.Equals(actualValue, entry.DesiredValue, StringComparison.Ordinal)
                ? LedgerStatus.Applied
                : LedgerStatus.Failed,
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

    public IReadOnlyList<LedgerEntry> PlanRevert(string source)
    {
        var candidates = new List<LedgerEntry>();
        foreach (var entry in _entries.Values)
        {
            if (!string.Equals(entry.Source, source, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Owner != OwnerKind.Scene || !entry.Restorable || entry.Status != LedgerStatus.Applied)
            {
                continue;
            }

            candidates.Add(entry);
        }

        candidates.Sort(static (a, b) => string.CompareOrdinal(a.Target, b.Target));
        return candidates;
    }

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
                Owner = OwnerKind.Scene,
                Status = LedgerStatus.Applied,
                Failure = failure,
                Restorable = true,
                UpdatedAt = _clock.GetUtcNow(),
            };
    }
}
