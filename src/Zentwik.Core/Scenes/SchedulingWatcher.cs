using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;

namespace Zentwik.Core.Scenes;

/// <summary>
/// 场景评估与进程调度持续监控的生命周期管理：支持启动、停止、释放，以及配置变更时立即唤醒。
/// </summary>
/// <remarks>
/// 轮询间隔按「有没有活要干」自适应：没有自动场景、账本里也没有要维持的记录时降频。
/// 后台定时器空转是旧版付出过代价的坑，无事可做还每两秒枚举一遍进程属于纯浪费。
/// </remarks>
public sealed class SchedulingWatcher : IAsyncDisposable
{
    /// <summary>无事可做时的轮询间隔。仍保留轮询，避免漏掉外部改动后无法自愈。</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(20);

    private readonly SceneEngine _scenes;
    private readonly PlanCoordinator _plans;
    private readonly DiagnosticLog _log;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public SchedulingWatcher(
        SceneEngine scenes,
        PlanCoordinator plans,
        DiagnosticLog log,
        TimeSpan? interval = null)
    {
        _scenes = scenes;
        _plans = plans;
        _log = log;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loopTask is { IsCompleted: false };
            }
        }
    }

    public void Start(CancellationToken externalCt = default)
    {
        lock (_gate)
        {
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var token = _cts.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        }
    }

    public void NotifyConfigChanged()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await _scenes.EvaluateAsync(ct).ConfigureAwait(false);
        await _plans.EnforceSchedulingAsync(_scenes.ActiveSceneId, ct).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loopTask;
        lock (_gate)
        {
            cts = _cts;
            loopTask = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error($"场景评估与调度监控失败：{ex.Message}");
            }

            try
            {
                await _signal.WaitAsync(NextDelay(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>本轮结束后该等多久：有自动场景或有要维持的账本时用正常间隔，否则降频。</summary>
    private TimeSpan NextDelay()
    {
        try
        {
            return _scenes.HasAutoScenes || _plans.HasHeldEntries ? _interval : IdleInterval;
        }
        catch (ObjectDisposedException)
        {
            return IdleInterval;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _signal.Dispose();
    }
}