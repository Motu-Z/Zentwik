using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Worker
{
    /// <summary>
    /// Worker 的后端宿主服务：把 Core 的服务按固定顺序拉起来，退出时按相反顺序停掉。
    ///
    /// 本服务没有自己的执行循环 —— 它的全部工作是「启动时按顺序做一串同步动作，
    /// 退出时做一串清理」。继承 BackgroundService 会凭空多出一个 ExecuteAsync，
    /// 里面只能写 await Task.Delay(Timeout.Infinite)，那是一个假的循环。
    ///
    ///   ① 各 store 必须先 Load：后面的 profiles / scenarios / 调校都从它们读盘；
    ///   ② profiles 先于 scenarios：场景规则指向方案 Id，方案表没加载完无法校验；
    ///   ③ 采样线程（ProcessRefresher）先于 ProcessWatcher.Start()：
    ///      ProcessWatcher 的启动补扫走 SampleOnSamplerThread，采样线程不存在时
    ///      只会白等 5 秒超时、拿到空快照，且不抛异常、不写日志（静默失效）；
    ///   ④ 监听最后起：前面任何一步失败都不该让一个「监听着一半状态」的进程常驻。
    ///
    /// 【红线】本类不读 UiSettings 里任何「要不要有后台」的开关 —— 双进程下常驻与否
    /// 由「谁拉起它」和「关闭行为」决定，与配置项无关；
    /// 也不调 SetHostVisible（Worker 无窗口，那两道闸门对它没有语义）。
    /// </summary>
    internal sealed class BackendHostService : IHostedService
    {
        private readonly ILogService _log;
        private readonly ITuningStore _tuning;
        private readonly ISchedulingStore _scheduling;
        private readonly IUiSettingsStore _settings;
        private readonly IMaskStore _masks;
        private readonly PlanService _plans;
        private readonly ScenarioService _scenarios;
        private readonly ScenarioMonitor _monitor;
        private readonly TuningApplyService _tuningApply;
        private readonly IBootGuard _bootGuard;
        private readonly ISmuReader _smu;
        private readonly IProcessWatcher _watcher;
        private readonly ProcessRefresher _refresher;
        private readonly AutoStartService _autoStart;

        public BackendHostService(
            ILogService log,
            ITuningStore tuning,
            ISchedulingStore bindings,
            IUiSettingsStore settings,
            IMaskStore masks,
            PlanService plans,
            ScenarioService scenarios,
            ScenarioMonitor monitor,
            TuningApplyService tuningApply,
            IBootGuard bootGuard,
            ISmuReader smu,
            IProcessWatcher watcher,
            ProcessRefresher refresher,
            AutoStartService autoStart)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;

            ArgumentNullException.ThrowIfNull(tuning);
            _tuning = tuning;

            ArgumentNullException.ThrowIfNull(bindings);
            _scheduling = bindings;

            ArgumentNullException.ThrowIfNull(settings);
            _settings = settings;

            ArgumentNullException.ThrowIfNull(masks);
            _masks = masks;

            ArgumentNullException.ThrowIfNull(plans);
            _plans = plans;

            ArgumentNullException.ThrowIfNull(scenarios);
            _scenarios = scenarios;

            ArgumentNullException.ThrowIfNull(monitor);
            _monitor = monitor;

            ArgumentNullException.ThrowIfNull(tuningApply);
            _tuningApply = tuningApply;

            ArgumentNullException.ThrowIfNull(bootGuard);
            _bootGuard = bootGuard;

            ArgumentNullException.ThrowIfNull(smu);
            _smu = smu;

            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;

            ArgumentNullException.ThrowIfNull(refresher);
            _refresher = refresher;

            ArgumentNullException.ThrowIfNull(autoStart);
            _autoStart = autoStart;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // ---------- 0. 计划任务路径自愈 ----------
            // 任务存的是绝对路径，程序换了安装目录后任务仍指着旧位置，开机拉起的是旧构建。
            // 这里在启动时校正一次：路径一致则什么都不做。
            _autoStart.RepairRegisteredPath();

            // ---------- 1. 各 store 先落盘内容进内存 ----------
            // 掩码表必须在这里加载：它不随构造自动读盘，漏掉这次调用会让
            // Worker 侧的掩码恒为空，下一次保存就把盘上原有的掩码整体覆盖掉。
            _tuning.Load();
            _scheduling.Load();
            _settings.Load();
            _masks.Load();

            // ---------- 2. 方案表 ----------
            _plans.Load();

            // ---------- 3. 场景规则 + 监听范围对齐 ----------
            _scenarios.Load();

            // 规则变更后重新对齐监听范围。回调在触发线程上同步执行（Core 不做封送），
            // 这里只调 Sync，不碰任何界面状态。
            _scenarios.Changed += () => _monitor.Sync();
            _monitor.Sync();

            // ---------- 4. 启动护栏 + 启动时应用调校 ----------
            // 护栏必须在这里判定：它决定这次到底要不要写 SMU。
            // 连续多次「写入后没走完」意味着那套参数很可能让机器起不来，
            // 这一次一条都不写，让机器以出厂参数起来。
            BootGuardSnapshot guard = _bootGuard.Enter();

            // 拦截时不删 tuning.json：数据留着，用户在调校页手动点「应用参数」即可恢复
            //（那条路径不经过启动闸门，成功后会 MarkHealthy 把计数清零）。
            if (!guard.Blocked && _settings.Settings.ApplyTuningOnStartup)
            {
                // 受「启动时自动应用调校」开关约束；关闭时整个分支不执行。
                TuningApplyOutcome outcome = _tuningApply.ApplyFromStore();

                // 失败不阻断：SMU 不可用是「部分功能降级」，不是「Worker 无意义」。
                // 没装 PawnIO 的机器上，进程监听、场景切换、调度必须继续可用。
                if (!outcome.Success)
                    _log.Warn($"启动时应用调校未完成：{outcome.Summary}");
            }

            // ---------- 5. SMU 探测 ----------
            // 只读一次属性触发探测。异常只 Warn，绝不 throw、绝不 Environment.Exit。
            try
            {
                _ = _smu.IsAvailable;
            }
            catch (Exception ex)
            {
                _log.Warn($"SMU 探测失败：{ex.Message}");
            }

            // ---------- 6. 采样线程 → 进程监听 ----------
            // 顺序不可颠倒：ProcessWatcher 的启动补扫依赖采样线程已在跑。
            _refresher.Start();

            // 「监听进程并自动调度」关掉时不起监听：进程页照样能看，只是不自动绑核。
            if (_settings.Settings.WatchProcessBinding)
                _watcher.Start();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // 先停消费者（ProcessWatcher 不再要快照），再停生产者（采样线程），最后刷日志。
            _watcher.Stop();
            _refresher.Stop();
            _log.Flush();

            return Task.CompletedTask;
        }
    }
}
