using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.State;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Platform;
using Zentwik.Platform.Windows;

namespace Zentwik.Core.Scenes;

public sealed record SceneActivation(
    string SceneId,
    bool Active,
    string Outcome,
    IReadOnlyList<string> PlanIds);

/// <summary>
/// 场景协调：同一对象同一时刻只允许一个场景持有写权；切换时只回退仍归本方所有的对象。
/// 自动评估不会覆盖用户接管，也不会套用版本已变更的方案。
/// </summary>
public sealed class SceneEngine
{
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    private readonly StateStore _store;
    private readonly PlanCoordinator _plans;
    private readonly IProcessScheduler _scheduler;
    private readonly IPowerStatus _powerStatus;
    private readonly DiagnosticLog _log;
    private readonly IBootInfo _boot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _firstMatch = new(StringComparer.Ordinal);

    private string? _activeSceneId;

    public SceneEngine(
        StateStore store,
        PlanCoordinator plans,
        IProcessScheduler scheduler,
        IPowerStatus powerStatus,
        DiagnosticLog log,
        IBootInfo? boot = null)
    {
        _store = store;
        _plans = plans;
        _scheduler = scheduler;
        _powerStatus = powerStatus;
        _log = log;
        _boot = boot ?? new SystemBootInfo();
    }

    public string? ActiveSceneId => Volatile.Read(ref _activeSceneId);

    public event Action<SceneActivation>? Changed;

    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var candidates = new List<SceneCandidate>();

            foreach (var scene in _store.Current.Scenes)
            {
                if (!scene.Enabled || scene.IsManualOnly || !TriggersMatch(scene))
                {
                    _firstMatch.Remove(scene.Id);
                    continue;
                }

                if (!_firstMatch.TryGetValue(scene.Id, out var since))
                {
                    since = now;
                    _firstMatch[scene.Id] = since;
                }

                candidates.Add(new SceneCandidate(scene.Id, scene.Priority, since));
            }

            var winner = SceneArbitration.Winner(candidates);
            if (winner is null)
            {
                if (ActiveSceneId is not null)
                {
                    await DeactivateCoreAsync("无匹配场景", ct).ConfigureAwait(false);
                }

                return;
            }

            if (string.Equals(winner.SceneId, ActiveSceneId, StringComparison.Ordinal))
            {
                await _plans.DetectDriftAsync(ct).ConfigureAwait(false);
                return;
            }

            await ActivateCoreAsync(winner.SceneId, "自动触发", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SceneActivation> ActivateAsync(string sceneId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var scene = FindScene(sceneId);
            if (!scene.Enabled)
            {
                return new SceneActivation(sceneId, false, "场景已停用。", []);
            }

            return await ActivateCoreAsync(sceneId, "手动激活", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SceneActivation?> DeactivateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return ActiveSceneId is null
                ? null
                : await DeactivateCoreAsync("手动停用", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 上次退出没有走到停用流程时，账本里会留着属于场景的写入。只在同一启动周期内回退：
    /// 整机重启后固件里的值可能是用户有意保留的，此时按"未知"处理，不擅自改写。
    /// </summary>
    public async Task<IReadOnlyList<TargetOutcome>> RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var boot = _boot.BootTimeUtc;
            var previous = _store.Current.LastBootTimeUtc;
            await _store.UpdateAsync(document => document with { LastBootTimeUtc = boot }, ct).ConfigureAwait(false);

            var orphans = _plans.Entries
                .Where(static entry => entry.Owner == OwnerKind.Scene
                                       && entry.Restorable
                                       && entry.Status == LedgerStatus.Applied)
                .ToArray();

            if (orphans.Length == 0)
            {
                return [];
            }

            if (previous is null || (boot - previous.Value).Duration() > BootTolerance)
            {
                _log.Warn($"检测到整机重启，{orphans.Length} 项遗留写入保持原样，等待场景重新评估。");
                return [];
            }

            var outcomes = new List<TargetOutcome>();
            foreach (var source in orphans.Select(static entry => entry.Source).Distinct(StringComparer.Ordinal))
            {
                var sceneId = source.StartsWith("scene:", StringComparison.Ordinal) ? source["scene:".Length..] : null;
                var scene = sceneId is null
                    ? null
                    : _store.Current.Scenes.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, sceneId, StringComparison.Ordinal));

                if (scene is { RestoreOnExit: false })
                {
                    _log.Info($"场景 {sceneId} 要求保留写入，跳过启动回退。");
                    continue;
                }

                outcomes.AddRange(await _plans.RevertSourceAsync(source, ct).ConfigureAwait(false));
                _log.Info($"启动补回退：{source}（{(scene is null ? "场景已删除" : "上次退出未回退")}）。");
            }

            return outcomes;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SceneActivation?> ShutdownAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return ActiveSceneId is null
                ? null
                : await DeactivateCoreAsync("代理退出", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SceneActivation> ActivateCoreAsync(string sceneId, string reason, CancellationToken ct)
    {
        var scene = FindScene(sceneId);

        if (ActiveSceneId is { } previous && !string.Equals(previous, sceneId, StringComparison.Ordinal))
        {
            await DeactivateCoreAsync($"切换到 {sceneId}", ct).ConfigureAwait(false);
        }

        var planIds = new List<string>();
        var applied = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var action in scene.Actions)
        {
            var plan = _store.Current.Plans.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, action.PlanId, StringComparison.Ordinal));

            if (plan is null)
            {
                failed++;
                _log.Warn($"场景 {sceneId} 引用的方案 {action.PlanId} 不存在。");
                continue;
            }

            if (action.PlanRevision != 0 && action.PlanRevision != plan.Revision)
            {
                skipped++;
                _log.Warn($"场景 {sceneId} 跳过方案 {plan.Id}：期望版本 {action.PlanRevision}，当前 {plan.Revision}。");
                continue;
            }

            var outcome = await _plans.ApplyAsync(plan.Id, $"scene:{sceneId}", scene.Priority, ct).ConfigureAwait(false);
            planIds.Add(plan.Id);

            switch (outcome.State)
            {
                case PlanApplyState.Applied:
                    applied++;
                    break;
                case PlanApplyState.NotApplied:
                    skipped++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        var decision = failed > 0
            ? $"部分生效：成功 {applied}，失败 {failed}，跳过 {skipped}。"
            : applied > 0
                ? $"已激活：{applied} 个方案生效。"
                : "已激活：没有可应用的方案。";

        Volatile.Write(ref _activeSceneId, sceneId);
        await UpdateSceneAsync(scene, decision, DateTimeOffset.Now, ct).ConfigureAwait(false);
        _log.Info($"场景 {sceneId} {reason}：{decision}");

        var result = new SceneActivation(sceneId, true, decision, planIds);
        Changed?.Invoke(result);
        return result;
    }

    private async Task<SceneActivation> DeactivateCoreAsync(string reason, CancellationToken ct)
    {
        var sceneId = ActiveSceneId;
        if (sceneId is null)
        {
            return new SceneActivation(string.Empty, false, "没有活动场景。", []);
        }

        var scene = _store.Current.Scenes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sceneId, StringComparison.Ordinal));

        var outcomes = scene is { RestoreOnExit: true }
            ? await _plans.RevertSourceAsync($"scene:{sceneId}", ct).ConfigureAwait(false)
            : Array.Empty<TargetOutcome>();

        Volatile.Write(ref _activeSceneId, null);

        var reverted = outcomes.Count(static outcome => outcome.Status == LedgerStatus.Reverted);
        var failed = outcomes.Count - reverted;
        var decision = scene is { RestoreOnExit: false }
            ? $"已停用（保留写入）：{reason}。"
            : failed > 0
                ? $"已停用：回退 {reverted} 项，失败 {failed} 项。"
                : $"已停用：回退 {reverted} 项。";

        if (scene is not null)
        {
            await UpdateSceneAsync(scene, decision, scene.LastActivatedAt, ct).ConfigureAwait(false);
        }

        _log.Info($"场景 {sceneId} 停用：{decision}");

        var result = new SceneActivation(sceneId, false, decision, []);
        Changed?.Invoke(result);
        return result;
    }

    private bool TriggersMatch(Scene scene)
    {
        var nonManual = 0;

        foreach (var trigger in scene.Triggers)
        {
            switch (trigger.Kind)
            {
                case SceneTriggerKinds.Manual:
                    continue;
                case SceneTriggerKinds.Process:
                {
                    nonManual++;
                    var name = trigger.Argument;
                    if (string.IsNullOrWhiteSpace(name) || _scheduler.Find(name).Count == 0)
                    {
                        return false;
                    }

                    break;
                }

                case SceneTriggerKinds.PowerSource:
                {
                    nonManual++;
                    var expected = string.Equals(trigger.Argument, "ac", StringComparison.OrdinalIgnoreCase);
                    if (_powerStatus.OnAcPower != expected)
                    {
                        return false;
                    }

                    break;
                }

                default:
                    return false;
            }
        }

        return nonManual > 0;
    }

    private Scene FindScene(string sceneId) =>
        _store.Current.Scenes.FirstOrDefault(scene => string.Equals(scene.Id, sceneId, StringComparison.Ordinal))
        ?? throw new CoreException(Zentwik.Contracts.Protocol.ErrorCodes.SceneNotFound, $"场景 {sceneId} 不存在。");

    private Task UpdateSceneAsync(Scene scene, string outcome, DateTimeOffset? activatedAt, CancellationToken ct) =>
        _store.UpdateAsync(
            document => document with
            {
                Scenes = document.Scenes
                    .Select(candidate => candidate.Id == scene.Id
                        ? candidate with { LastOutcome = outcome, LastActivatedAt = activatedAt }
                        : candidate)
                    .ToArray(),
            },
            ct);
}
