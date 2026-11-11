using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Services;

/// <summary>
/// 盯住后台的生效方案。场景规则由 Worker 自己切方案，界面收不到任何事件，
/// 只靠应用时刷新就会一直停在旧方案上。
///
/// 主路径是窗口激活时问一句 —— 用户切回来那一刻看到的必然是最新的；
/// 另有 30 秒兜底，覆盖「窗口一直摆在旁边不切走」的情形。
/// </summary>
internal sealed class PlanWatcher
{
    /// <summary>兜底间隔。主要靠激活事件，这里只防漏。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private static readonly PlanKind[] Kinds =
    {
        PlanKind.Tuning,
        PlanKind.Scheduling,
        PlanKind.Power
    };

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isVisible;
    private readonly Dictionary<PlanKind, string?> _seen = new();

    private bool _busy;

    public PlanWatcher(Func<bool> isVisible)
    {
        _isVisible = isVisible;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += (_, _) => _ = TickAsync();
    }

    public void Start()
    {
        if (PlanHub.SuppressWorkerInit || _timer.IsEnabled)
            return;

        _timer.Start();

        // 立刻对一次：启动时初始化刚选完方案，这里只用于建立基线。
        _ = TickAsync();
    }

    public void Stop() => _timer.Stop();

    /// <summary>窗口激活 / 启动时对一次。失败静默，不影响界面。</summary>
    public void Poke() => _ = TickAsync();

    private async Task TickAsync()
    {
        if (_busy || !_isVisible())
            return;

        _busy = true;

        try
        {
            IpcOutcome<ActivePlansPayload> outcome = await WorkerBridge.Instance
                .QueryActivePlansAsync()
                .ConfigureAwait(true);

            // 连不上时 SendAsync 已经把关掉状态灯，这里只负责不继续往下跑。
            if (outcome.TransportFailed || !outcome.Success || outcome.Value is null)
                return;

            foreach (PlanKind kind in Kinds)
            {
                string? id = outcome.Value.For(kind);
                bool known = _seen.TryGetValue(kind, out string? previous);
                _seen[kind] = id;

                if (known && string.Equals(previous, id, StringComparison.Ordinal))
                    continue;

                await PlanHub.Of(kind).AdoptActiveAsync(id).ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // 轮询失败不是用户能处理的事：下一拍重试，不弹提示。
        }
        finally
        {
            _busy = false;
        }
    }
}
