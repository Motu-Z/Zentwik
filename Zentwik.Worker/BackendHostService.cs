using System;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Worker
{
    /// <summary>
    /// Worker 的后端宿主服务：按固定顺序拉起 Core 的服务，退出时按相反顺序停掉。
    ///
    /// 每一步都以模块为单位隔离：某一模块初始化失败只把它标记为失败，
    /// 其余模块继续启动 —— 没装 PawnIO 的机器上，调度、电源、场景必须照常可用。
    /// </summary>
    internal sealed class BackendHostService : IWorkerService
    {
        private const string ModuleCore = "core.runtime";
        private const string ModuleScheduling = "generic.scheduling";
        private const string ModulePlan = "generic.plan";
        private const string ModuleScenario = "generic.scenario";
        private const string ModuleShell = "generic.shell";
        private const string ModuleAmd = "amd.cpu";
        private const string ModuleIntel = "intel.cpu";

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
        private readonly IAmdTuningChannel _amd;
        private readonly IIntelTuningChannel _intel;
        private readonly IProcessWatcher _watcher;
        private readonly ProcessRefresher _refresher;
        private readonly AutoStartService _autoStart;
        private readonly SleepResumeWatcher _sleepWatcher;
        private readonly CapabilityCatalog _catalog;

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
            IAmdTuningChannel amd,
            IIntelTuningChannel intel,
            IProcessWatcher watcher,
            ProcessRefresher refresher,
            AutoStartService autoStart,
            SleepResumeWatcher sleepWatcher,
            CapabilityCatalog catalog)
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

            ArgumentNullException.ThrowIfNull(amd);
            _amd = amd;

            ArgumentNullException.ThrowIfNull(intel);
            _intel = intel;

            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;

            ArgumentNullException.ThrowIfNull(refresher);
            _refresher = refresher;

            ArgumentNullException.ThrowIfNull(autoStart);
            _autoStart = autoStart;

            ArgumentNullException.ThrowIfNull(sleepWatcher);
            _sleepWatcher = sleepWatcher;

            ArgumentNullException.ThrowIfNull(catalog);
            _catalog = catalog;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 0. 计划任务路径自愈
            // 任务存的是绝对路径，程序换了安装目录后任务仍指着旧位置，开机拉起的是旧构建。
            RunStep(ModuleShell, "修复自启路径", () => _autoStart.RepairRegisteredPath());

            // 1. 各 store 先落盘内容进内存
            // 掩码表必须在这里加载：它不随构造自动读盘，漏掉这次调用会让
            // Worker 侧的掩码恒为空，下一次保存就把盘上原有的掩码整体覆盖掉。
            RunStep(ModuleCore, "加载配置", () =>
            {
                _tuning.Load();
                _scheduling.Load();
                _settings.Load();
                _masks.Load();
            });

            // 2. 方案表
            RunStep(ModulePlan, "加载方案", _plans.Load);

            // 3. 场景规则 + 监听范围对齐
            RunStep(ModuleScenario, "加载场景规则", () =>
            {
                _scenarios.Load();

                // 规则变更后重新对齐监听范围。回调在触发线程上同步执行（Core 不做封送），
                // 这里只调 Sync，不碰任何界面状态。
                _scenarios.Changed += () => _monitor.Sync();
                _monitor.Sync();
            });

            // 4. 启动护栏 + 启动时应用调校
            RunStep(ModulePlan, "启动时应用调校", ApplyStartupTuning);

            // 4b. 休眠感知
            RunStep(ModuleScenario, "休眠恢复监听", () =>
            {
                _sleepWatcher.Resumed += OnResumedFromSleep;

                if (_settings.Settings.SmartHibernateAware)
                    _sleepWatcher.Start();
            });

            // 5. 调校通道探测。只读一次属性触发一次探测；
            // 失败只记状态，绝不 throw、绝不 Environment.Exit。
            RunStep(ModuleAmd, "AMD 调校通道探测",
                () => ProbeChannel(ModuleAmd, _amd.Status, _amd.LastError), markAvailable: false);
            RunStep(ModuleIntel, "Intel 调校通道探测",
                () => ProbeChannel(ModuleIntel, _intel.Status, _intel.LastError), markAvailable: false);

            // 6. 采样线程 → 进程监听
            RunStep(ModuleScheduling, "启动采样与监听", () =>
            {
                _refresher.Start();

                // 「监听进程并自动调度」关掉时不起监听：进程页照样能看，只是不自动绑核。
                if (_settings.Settings.WatchProcessBinding)
                    _watcher.Start();
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // 先停消费者（ProcessWatcher 不再要快照），再停生产者（采样线程），最后刷日志。
            RunStep(ModuleScenario, "停止休眠监听", () =>
            {
                _sleepWatcher.Resumed -= OnResumedFromSleep;
                _sleepWatcher.Stop();
            });

            RunStep(ModuleScheduling, "停止进程监听", _watcher.Stop);
            RunStep(ModuleScheduling, "停止采样", _refresher.Stop);

            _log.Flush();

            return Task.CompletedTask;
        }

        /// <summary>
        /// 启动护栏判定 + 按开关应用调校。
        /// 护栏必须在这里判定：它决定这次到底要不要写 SMU。
        /// 连续多次「写入后没走完」意味着那套参数很可能让机器起不来，
        /// 这一次一条都不写，让机器以出厂参数起来。
        /// </summary>
        private void ApplyStartupTuning()
        {
            BootGuardSnapshot guard = _bootGuard.Enter();

            // 「启动异常自动拦截」关掉时，计数照记（关了再开还能接上），但这次不拦。
            if (guard.Blocked && !_settings.Settings.SmartAntiBrick)
            {
                _log.Warn(
                    $"启动护栏本应拦截（连续 {guard.Failures} 次写入后未正常完成），" +
                    "但「启动异常自动拦截」已关闭：本次仍应用调校");

                guard = guard with { Blocked = false };
            }

            // 拦截时不删 tuning.json：数据留着，用户在调校页手动点「应用参数」即可恢复
            //（那条路径不经过启动闸门，成功后会 MarkHealthy 把计数清零）。
            if (guard.Blocked || !_settings.Settings.ApplyTuningOnStartup)
                return;

            // 受「启动时自动应用调校」开关约束；关闭时整个分支不执行。
            TuningApplyOutcome outcome = _tuningApply.ApplyFromStore();

            // 失败不阻断：SMU 不可用是「部分功能降级」，不是「Worker 无意义」。
            if (!outcome.Success && !outcome.Skipped)
                _log.Warn($"启动时应用调校未完成：{outcome.Summary}");
        }

        /// <summary>把通道探测结果写回能力目录。平台不匹配属正常分支，不进启动日志。</summary>
        private void ProbeChannel(string moduleId, TuningChannelStatus status, string? error)
        {
            bool available = status == TuningChannelStatus.Ready;

            _catalog.SetAvailability(moduleId, available, error);

            if (status is TuningChannelStatus.DriverUnavailable or TuningChannelStatus.ProbeFailed
                && error is not null)
                _log.Info($"{moduleId}：{error}");
        }

        /// <summary>执行一个启动步骤。失败只把对应模块标记为失败，不中断后续步骤。</summary>
        private void RunStep(string moduleId, string label, Action action, bool markAvailable = true)
        {
            try
            {
                action();

                if (markAvailable)
                    _catalog.SetAvailability(moduleId, true, null);
            }
            catch (Exception ex)
            {
                _catalog.MarkFailed(moduleId, $"{label}失败：{ex.Message}");
                _log.Error($"{label}失败（模块 {moduleId} 已标记为失败，其余模块继续）：{ex}");
            }
        }

        /// <summary>从睡眠 / 休眠恢复：把当前调校重新写进硬件。</summary>
        private void OnResumedFromSleep()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                    TuningApplyOutcome outcome = _tuningApply.ApplyFromStore();

                    if (outcome.Skipped)
                        _log.Info($"休眠感知：本次未补写（{outcome.SkipReason}）");
                    else if (outcome.Success)
                        _log.Info("休眠感知：已从睡眠恢复，调校参数重新下发");
                    else
                        _log.Warn($"休眠感知：恢复后重新应用调校未完成：{outcome.Summary}");
                }
                catch (Exception ex)
                {
                    _log.Warn($"休眠感知：恢复后应用调校异常：{ex.Message}");
                }
            });
        }
    }
}
