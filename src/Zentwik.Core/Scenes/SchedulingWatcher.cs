using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;

namespace Zentwik.Core.Scenes;

/// <summary>
/// 场景评估与进程调度持续监控的生命周期管理：支持启动、停止、释放，以及配置变更时立即唤醒。
/// </summary>
public sealed class SchedulingWatcher : IAsyncDisposable
{
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
                await _signal.WaitAsync(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _signal.Dispose();
    }
}