using System;
using System.Collections.Generic;
using System.Threading;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 场景自动切换的事件源编排（v1.1.x 实装）。
    ///
    /// ScenarioService 是纯判定：给它「此刻哪些场景成立」，它回答「该切哪套方案」。
    /// 本类负责回答前半句 —— 谁在跑、插没插电、什么时候该重新问一次。
    /// 两层分开的原因和别处一样：判定逻辑要能被单独验证（甚至被界面拿去做预览），
    /// 而「怎么知道世界变了」牵扯线程、防抖、启停，混在一起就没法单独测了。
    ///
    ///   1. **不启用就零占用**：场景总闸关着、或一条规则都没配时，不订阅任何事件、
    ///      不持有任何活动定时器。这不是洁癖 —— 本工程对后台的要求是
    ///      「没人看的时候占用必须归零」，而这个功能天然是常驻的。
    ///   2. **按需订阅**：只有进程类规则才挂进程事件，只有电源类规则才挂电源监听。
    ///      用户只配了一条「插电」规则时，为一个用不上的输入付采集代价没有道理。
    ///   3. **防抖在调用侧**：游戏启动会瞬间拉起十几个子进程，逐个通知就是十几次
    ///      全量评估（每次都要采一遍进程名单）。这里合并成一个窗口内的最后一次。
    ///      按 ScenarioService.Notify 的约定，防抖不放进服务内部 ——
    ///      延时取多长只有调用方知道，服务里拍一个数字没有依据。
    /// </summary>
    public class ScenarioMonitor
    {
        private readonly ILogService _log;
        private readonly ScenarioService _scenarios;
        private readonly ProcessRefresher _refresher;
        private readonly IProcessWatcher _watcher;
        private readonly PlanService _plans;
        private readonly PowerStatusWatcher _power;

        public ScenarioMonitor(ILogService log, ScenarioService scenarios, ProcessRefresher refresher, IProcessWatcher watcher, PlanService plans, PowerStatusWatcher power)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(scenarios);
            _scenarios = scenarios;
            ArgumentNullException.ThrowIfNull(refresher);
            _refresher = refresher;
            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;
            ArgumentNullException.ThrowIfNull(plans);
            _plans = plans;
            ArgumentNullException.ThrowIfNull(power);
            _power = power;
        }

        /// <summary>
        /// 合并窗口。
        ///
        /// 800ms 的依据是「游戏启动拉起子进程的典型跨度」：主进程起来后，反作弊、
        /// 启动器、浏览器内核等子进程在几百毫秒内陆续出现。短于这个窗口会拆成
        /// 好几次评估（每次都白采一遍名单），长于它则用户能感觉到方案切晚了。
        /// </summary>
        private const int DebounceMs = 800;

        /// <summary>采一次进程名单最多等这么久（采样线程忙时不能无限等）。</summary>
        private readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);

        private readonly object Gate = new();
        private Timer? _debounce;
        private bool _processHooked;
        private bool _powerHooked;
        private bool _active;

        /// <summary>
        /// 上一拍还活着、且被退出规则盯着的进程名。
        ///
        /// 其余触发都是「此刻成立不成立」的纯状态，进程退出是**差集**：
        /// 不知道上一拍的名单，就无法区分「刚退出」和「一直没起来」。
        /// 只记规则里写到的名字，不是全机器几百个进程 —— 差集的范围由规则决定。
        /// 由 <see cref="Gate"/> 保护：写入在评估线程，清理在 <see cref="Sync"/> 调用方线程。
        /// </summary>
        private readonly HashSet<string> _lastSeen = new(StringComparer.OrdinalIgnoreCase);



        /// <summary>
        /// 按当前规则内容对齐监听范围。幂等，任何时刻调用都安全。
        /// 启动时、规则变更后都要调一次。
        /// </summary>
        public void Sync()
        {
            bool enabled = _scenarios.Enabled;
            bool wantProcess = enabled && _scenarios.HasProcessRules;
            bool wantPower = enabled && _scenarios.HasPowerRules;

            // Sync 的调用方不止一个（启动时一次、规则变更后从 Changed 再来一次），
            // 而 Changed 是在 PlanService.Apply 里触发的 —— 那可能从 UI 线程、
            // 也可能从场景自己的切换路径进来。若在锁外做「看了再改」，
            // 两个线程同时看到 _powerHooked == false 就会各订阅一次，
            // 同一条电源通知触发两轮评估。
            // 所以：决策在锁内一次做完（check-and-set 原子），实际订阅挪到锁外 ——
            // 订阅是外部代码，放在锁内等于让 Gate 保护一段我们管不到的执行。
            bool startProcess = false, stopProcess = false;
            bool startPower = false, stopPower = false;
            bool wasActive;

            lock (Gate)
            {
                if (wantProcess && !_processHooked)
                {
                    _processHooked = true;
                    startProcess = true;
                }
                else if (!wantProcess && _processHooked)
                {
                    _processHooked = false;
                    stopProcess = true;
                }

                if (wantPower && !_powerHooked)
                {
                    _powerHooked = true;
                    startPower = true;
                }
                else if (!wantPower && _powerHooked)
                {
                    _powerHooked = false;
                    stopPower = true;
                }

                wasActive = _active;
                _active = wantProcess || wantPower;
            }

            if (startProcess)
            {
                _watcher.ProcessSetChanged += OnProcessSetChanged;
            }
            else if (stopProcess)
            {
                _watcher.ProcessSetChanged -= OnProcessSetChanged;

                // 退订后再挂上时，上一拍的名单已经没有意义：留着它会把
                // 监听暂停期间退出的进程补报成一次「刚刚退出」。
                lock (Gate)
                    _lastSeen.Clear();
            }

            if (startPower)
            {
                _power.Changed += OnPowerChanged;
                _power.Start();
            }
            else if (stopPower)
            {
                _power.Changed -= OnPowerChanged;
                _power.Stop();
            }

            if (!_active)
            {
                DisposeDebounce();
                return;
            }

            // 刚从「没在监听」变成「在监听」时立刻评估一次：
            // 此刻可能已经有进程在跑、已经插着电，等下一个事件才切就是漏掉了当前状态。
            if (!wasActive)
                Schedule();
        }

        /// <summary>合并窗口内到达的事件只保留最后一次。</summary>
        private void Schedule()
        {
            lock (Gate)
            {
                if (!_active)
                    return;

                // 定时器按需创建、period 设为 Infinite：只在被 Schedule 推一把时醒一次，
                // 静默期它不产生任何唤醒 —— 这是「零占用」这条约束的具体兑现方式。
                _debounce ??= new Timer(_ => Evaluate(), null, Timeout.Infinite, Timeout.Infinite);
                _debounce.Change(DebounceMs, Timeout.Infinite);
            }
        }

        private void OnProcessSetChanged() => Schedule();

        private void OnPowerChanged() => Schedule();

        private void DisposeDebounce()
        {
            Timer? timer;
            lock (Gate)
                timer = Interlocked.Exchange(ref _debounce, null);

            timer?.Dispose();
        }

        /// <summary>
        /// 采一次当前状态，交给 ScenarioService 判定。
        ///
        /// ScenarioService.Notify 的契约就是要「此刻全部成立的触发」：
        /// 只报「游戏退出了」会被读成「没有进程命中了」，而浏览器可能还在跑。
        /// </summary>
        private void Evaluate()
        {
            if (!_scenarios.Enabled)
                return;

            List<(ScenarioTrigger Trigger, string Argument)> active = new();
            HashSet<string>? seen = null;

            if (_scenarios.HasPowerRules)
            {
                // 供电是二选一，两者都上报反而会让「插着电」和「用电池」同时命中。
                active.Add(_power.IsOnBattery
                    ? (ScenarioTrigger.OnBattery, string.Empty)
                    : (ScenarioTrigger.OnAcPower, string.Empty));
            }

            if (_scenarios.HasProcessRules)
            {
                IReadOnlyList<ProcessRefresher.Entry> snapshot;
                try
                {
                    snapshot = _refresher.SampleOnSamplerThread(SnapshotTimeout);
                }
                catch (Exception ex)
                {
                    // 采不到名单就不评估：带着一份残缺的名单去判定，
                    // 结果可能是「唯一命中的那条规则恰好没被采到」→ 误切成别的方案。
                    _log.Warn($"场景评估：进程名单采集失败，本次跳过：{ex.Message}");
                    return;
                }

                // 同名进程（chrome、svchost 动辄十几个）去重后再上报，
                // 否则规则匹配要对同一条规则重复比对几十次。
                seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ProcessRefresher.Entry entry in snapshot)
                {
                    if (!string.IsNullOrEmpty(entry.Name) && seen.Add(entry.Name))
                        active.Add((ScenarioTrigger.ProcessRunning, entry.Name));
                }
            }

            AppendExits(active, seen);

            _scenarios.Notify(active);
        }

        /// <summary>
        /// 把「这一拍退出的进程」补进触发集合。
        ///
        /// 只对规则里写到的名字做差集：全量进程留一份上一拍名单，
        /// 代价远大于它带来的收益。名单为空时顺手清掉基准，
        /// 用户删掉最后一条退出规则后不该继续留着状态。
        /// </summary>
        private void AppendExits(List<(ScenarioTrigger Trigger, string Argument)> active, HashSet<string>? seen)
        {
            IReadOnlyList<string> watched = _scenarios.ProcessExitArguments;

            lock (Gate)
            {
                if (watched.Count == 0)
                {
                    _lastSeen.Clear();
                    return;
                }

                // 采不到名单就不判退出：把一次采集失败当成「全都退出了」，
                // 会让退出规则集体误触发一次。
                if (seen is null)
                    return;

                foreach (string name in watched)
                {
                    if (seen.Contains(name))
                    {
                        _lastSeen.Add(name);
                        continue;
                    }

                    // 只有「上一拍在、这一拍不在」才算退出；
                    // 一直没起来过就是没命中，不是「退出」。
                    if (_lastSeen.Remove(name))
                        active.Add((ScenarioTrigger.ProcessExited, name));
                }
            }
        }
    }
}
