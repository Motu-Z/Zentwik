namespace Zentwik.Domain.Scenes;

public static class SceneTriggerKinds
{
    public const string Manual = "manual";
    public const string Process = "process";
    public const string PowerSource = "powerSource";
}

public sealed record SceneAction
{
    // 场景始终应用方案的最新内容：用户改方案就是要让引用它的场景跟着变。
    public required string PlanId { get; init; }
}

public sealed record SceneTrigger
{
    public required string Kind { get; init; }

    // process：进程名；powerSource：ac 或 dc；manual：忽略。
    public string? Argument { get; init; }
}

/// <summary>场景是跨方案协调机制，不是第四种方案。</summary>
public sealed record Scene
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    // 数值越大越优先；同优先级由进入时间决定，后来者不抢占。
    public int Priority { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<SceneTrigger> Triggers { get; init; } = [];

    public IReadOnlyList<SceneAction> Actions { get; init; } = [];

    // 仅回退本场景写入且仍归本场景所有的对象。
    public bool RestoreOnExit { get; init; } = true;

    public DateTimeOffset? LastActivatedAt { get; init; }

    public string? LastOutcome { get; init; }

    public bool IsManualOnly => Triggers.Count == 0
                                || Triggers.All(static t => string.Equals(t.Kind, SceneTriggerKinds.Manual, StringComparison.Ordinal));
}

public sealed record SceneCandidate(string SceneId, int Priority, DateTimeOffset ActivatedAt);

public enum ArbitrationReason
{
    Selected,
    LowerPriority,
    LaterActivation,
}

public sealed record ArbitrationDecision(string SceneId, bool Selected, ArbitrationReason Reason);

/// <summary>同一对象同一时刻只允许一个场景持有写权，不做部分应用的折中。</summary>
public static class SceneArbitration
{
    public static IReadOnlyList<ArbitrationDecision> Resolve(IEnumerable<SceneCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(static c => c.Priority)
            .ThenBy(static c => c.ActivatedAt)
            .ThenBy(static c => c.SceneId, StringComparer.Ordinal)
            .ToList();

        var decisions = new List<ArbitrationDecision>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var candidate = ordered[i];
            decisions.Add(i == 0
                ? new ArbitrationDecision(candidate.SceneId, true, ArbitrationReason.Selected)
                : new ArbitrationDecision(
                    candidate.SceneId,
                    false,
                    candidate.Priority < ordered[0].Priority
                        ? ArbitrationReason.LowerPriority
                        : ArbitrationReason.LaterActivation));
        }

        return decisions;
    }

    public static SceneCandidate? Winner(IEnumerable<SceneCandidate> candidates) =>
        candidates
            .OrderByDescending(static c => c.Priority)
            .ThenBy(static c => c.ActivatedAt)
            .ThenBy(static c => c.SceneId, StringComparer.Ordinal)
            .FirstOrDefault();
}
