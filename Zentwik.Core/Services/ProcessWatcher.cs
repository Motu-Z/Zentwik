using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Helpers;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 进程调度后台监听：按 schedules.json 的规则给新出现的进程自动绑定。
    ///
    /// 进程变化来自两帧 Toolhelp32 快照的差集（<see cref="NativeProcessList"/>），
    /// 可见时 1 秒一轮、隐藏时 3 秒一轮。
    ///
    /// 早先订阅 WMI 的 <c>__InstanceCreationEvent</c>，那是轮询型事件 ——
    /// WMI 每 2 秒把 Win32_Process 全表枚举一遍做 diff，创建与删除两个订阅叠起来
    /// 后 WmiPrvSE 常驻吃 8% 单核，并拖慢同样依赖 WMI 的任务管理器。现在不碰 WMI。
    /// </summary>
    public class ProcessWatcher : IProcessWatcher
    {
        public ProcessWatcher(
            ILogService log,
            LogThrottle throttle,
            ICpuSetScheduler cpuSets,
            ISchedulingStore bindings,
            IMaskStore masks,
            PowerPolicyService power)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(throttle);
            _throttle = throttle;
            ArgumentNullException.ThrowIfNull(cpuSets);
            _cpuSets = cpuSets;
            ArgumentNullException.ThrowIfNull(bindings);
            _scheduling = bindings;
            ArgumentNullException.ThrowIfNull(masks);
            _masks = masks;
            ArgumentNullException.ThrowIfNull(power);
            _power = power;
        }

        private readonly ILogService _log;
        private readonly LogThrottle _throttle;
        private readonly ICpuSetScheduler _cpuSets;
        private readonly ISchedulingStore _scheduling;
        private readonly IMaskStore _masks;
        private readonly PowerPolicyService _power;

        private readonly object _lock = new();

        private CancellationTokenSource? _cts;
        private Task? _loop;

        /// <summary>
        /// 扫描周期。1 秒对「进程启动后多久被绑定」几乎无感，而一次 Toolhelp32
        /// 快照在 400 进程下约 1ms，常驻占用不到 0.1%。
        /// </summary>
        private const int ScanIntervalMs = 1000;

        /// <summary>全局互斥体：同一台机器只让一个 Zentwik 实例执行绑定。</summary>
        private const string MutexName = @"Global\Zentwik.Singleton";

        private Mutex? _mutex;

        private bool _running;

        public bool HasOwnership => _mutex != null;

        public bool IsRunning
        {
            get
            {
                lock (_lock)
                    return _running;
            }
        }

        /// <summary>
        /// 已尝试绑定的 PID（成功或失败都记，避免重复尝试刷日志）。
        /// 用带锁容器：扫描线程与 UI 线程（ResetCache）都会碰它。
        /// </summary>
        private readonly PidSet _handledPids = new();

        /// <summary>本工具**成功**绑过的 PID。解绑只碰这个集合里的进程。</summary>
        private readonly PidSet _boundPids = new();

        /// <summary>上一轮的存活 PID 集合，用于判断「进程集合是否真的变了」。</summary>
        private HashSet<int>? _lastAlive;

        /// <summary>距离上次全量重扫的轮数。</summary>
        private int _roundsSinceFullScan;

        /// <summary>全量重扫间隔（轮）。60 轮 = 1 分钟。</summary>
        private const int FullScanEveryRounds = 60;

        /// <summary>复用的快照缓冲，避免每轮新建几百项。</summary>
        private readonly List<ProcessIdentity> _frame = new(512);

        public void Start()
        {
            lock (_lock)
            {
                if (_running)
                    return;

                if (!TryAcquireMutex())
                {
                    _log.Warn("已有另一个 Zentwik 实例在运行进程调度监听，本实例跳过（避免重复绑定）");
                    return;
                }

                _handledPids.Clear();
                _boundPids.Clear();
                _lastAlive = null;
                _roundsSinceFullScan = 0;

                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;
                _loop = Task.Run(() => LoopAsync(token), token);
                _running = true;

                _log.Info("进程调度监听已启动（快照差集，1 秒一轮）");
            }
        }

        private bool TryAcquireMutex()
        {
            if (_mutex != null)
                return true;

            try
            {
                Mutex mutex = new(initiallyOwned: false, MutexName);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    mutex.Dispose();
                    return false;
                }

                _mutex = mutex;
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"创建监听互斥体失败（降级为无保护运行）：{ex.Message}");
                return true;
            }
        }

        private void ReleaseMutex()
        {
            if (_mutex == null)
                return;

            try { _mutex.ReleaseMutex(); } catch { /* 未持有时释放会抛 */ }
            try { _mutex.Dispose(); } catch { /* 忽略 */ }

            _mutex = null;
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running)
                    return;

                if (_cts != null)
                {
                    try { _cts.Cancel(); } catch { /* 忽略 */ }
                    _cts.Dispose();
                    _cts = null;
                }

                _loop = null;
                _running = false;
                _handledPids.Clear();
                _boundPids.Clear();
                _lastAlive = null;

                ReleaseMutex();
            }

            _log.Info("进程调度监听已停止");
        }

        private async Task LoopAsync(CancellationToken token)
        {
            InitialScan();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ScanIntervalMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    ScanOnce();
                }
                catch (Exception ex)
                {
                    _log.Warn($"进程调度扫描异常（已跳过本轮）：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 采一帧、处理新进程、清理退出 PID。
        /// 稳态下只做一次快照和一次整数集合比较；只有集合真的变了才重扫并广播。
        /// </summary>
        private void ScanOnce()
        {
            List<ProcessIdentity> frame = NativeProcessList.Capture(_frame);

            // 快照失败时不能修剪：那会把全部 PID 当成已退出。
            if (frame.Count == 0)
                return;

            HashSet<int> alive = new(frame.Count);
            foreach (ProcessIdentity item in frame)
                alive.Add(item.Pid);

            // 兜底：每分钟全量重扫一次，覆盖 PID 复用（同 PID 换了进程）这类漏网情况。
            if (++_roundsSinceFullScan >= FullScanEveryRounds)
            {
                _roundsSinceFullScan = 0;
                _lastAlive = null;
            }

            HashSet<int>? previous = _lastAlive;

            if (previous is not null && previous.SetEquals(alive))
            {
                return;
            }

            foreach (ProcessIdentity item in frame)
            {
                // 首帧全量；之后只处理上一帧没有的 PID。
                if (previous is not null && previous.Contains(item.Pid))
                    continue;

                try
                {
                    HandleProcess(item.Pid, item.Name);
                }
                catch (Exception ex)
                {
                    _log.Warn($"绑定 {item.Name}（PID {item.Pid}）时异常：{ex.Message}");
                }
            }

            // 按存活名单裁剪，防止 PID 复用后被误判为已处理。
            _handledPids.PruneTo(alive);
            _boundPids.PruneTo(alive);

            _lastAlive = alive;
            RaiseProcessSetChanged();
        }

        /// <summary>启动补扫：把已经在跑的程序过一遍，事件式监听盖不到它们。</summary>
        private void InitialScan()
        {
            long started = Environment.TickCount64;

            try
            {
                List<ProcessIdentity> frame = NativeProcessList.Capture(new List<ProcessIdentity>(512));

                foreach (ProcessIdentity item in frame)
                {
                    try
                    {
                        HandleProcess(item.Pid, item.Name);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"启动补扫单个进程失败：{ex.Message}");
                    }
                }

                // 不设 _lastAlive：让第一轮 ScanOnce 走全量路径并广播一次，
                // 场景规则才知道启动时都有哪些进程在跑。
                _log.Info($"进程调度启动补扫完成，共检查 {frame.Count} 个进程，耗时 {Environment.TickCount64 - started} ms");
            }
            catch (Exception ex)
            {
                _log.Warn($"进程调度启动补扫失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 进程集合发生变化（有新进程创建，或有进程退出）。
        ///
        /// 订阅方（场景自动切换）要的是「此刻有哪些进程在跑」这个全量事实，
        /// 而不是一条一条的事件。防抖放在订阅侧（见 ScenarioMonitor）。
        /// </summary>
        public event Action? ProcessSetChanged;

        private void RaiseProcessSetChanged()
        {
            Action? handler = ProcessSetChanged;
            if (handler == null)
                return;

            try { handler(); }
            catch (Exception ex) { _log.Warn($"进程变化订阅者处理失败：{ex.Message}"); }
        }

        /// <summary>
        /// 按当前规则对已经在跑的进程重扫一遍。切换方案 / 编辑规则后调用 ——
        /// 正在运行的游戏不会自己再触发一次，不重扫就得等它下次启动才生效。
        /// </summary>
        public void ReapplyNow()
        {
            bool running;
            lock (_lock)
                running = _running;

            if (!running)
                return;

            Volatile.Write(ref _reapplyDirty, 1);

            // 标记要撑到 worker 退出。在 worker 开头清零，防抖窗口内每次调用都会另起一个，
            // 18 条规则能凑出二十多个 InitialScan 并发，互相把线程搬回另一个 CCD。
            if (Interlocked.CompareExchange(ref _reapplyRunning, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(ReapplyDebounceMs).ConfigureAwait(false);

                        // 先清旗标再扫，扫描期间进来的请求才不会被本轮吞掉。
                        Interlocked.Exchange(ref _reapplyDirty, 0);
                        ResetCache();
                        InitialScan();

                        if (Volatile.Read(ref _reapplyDirty) == 0)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"按新规则重扫进程失败：{ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref _reapplyRunning, 0);

                    if (Volatile.Read(ref _reapplyDirty) == 1)
                        ReapplyNow();
                }
            });
        }

        private const int ReapplyDebounceMs = 400;

        private int _reapplyRunning;

        private int _reapplyDirty;

        /// <summary>清空「已处理」缓存，让规则立即重新应用（保存规则后调用）。</summary>
        public void ResetCache()
        {
            lock (_lock)
            {
                _handledPids.Clear();

                // _boundPids 是「已绑、未解绑」的账本，重扫不解绑，不能清 ——
                // 清了之后 ClearAllSchedules 会静默什么都不做。

                // 下一轮走「全量重扫」路径，正在运行的进程才会被重新绑到新规则上。
                _lastAlive = null;
                _roundsSinceFullScan = 0;
            }

            InvalidateRuleCache();
        }

        /// <summary>本工具成功绑过、且尚未解绑的 PID 快照。</summary>
        public int[] GetBoundProcessIds() => _boundPids.ToArray();

        /// <summary>
        /// 解绑全部「本工具绑过」的进程，返回真正清掉 CPU Set 的数量。
        /// 只遍历已绑集合，不扫全系统 —— 碰用户别处绑的进程不是本工具的职责。
        /// </summary>
        public int ClearAllSchedules()
        {
            int[] pids = _boundPids.ToArray();
            int unbound = 0;

            foreach (int pid in pids)
            {
                if (_cpuSets.ClearDefaultCpuSets(pid, out string? error))
                {
                    unbound++;
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    _log.Info($"解绑 PID {pid} 跳过：{error}");
                }

                _boundPids.Remove(pid);
            }

            return unbound;
        }

        /// <summary>解绑单个 PID。返回是否真的清掉了 CPU Set。</summary>
        public bool ClearSchedule(int pid)
        {
            bool ok = _cpuSets.ClearDefaultCpuSets(pid, out string? error);

            if (!ok && !string.IsNullOrEmpty(error))
                _log.Info($"解绑 PID {pid} 跳过：{error}");

            _boundPids.Remove(pid);
            return ok;
        }

        /// <summary>处理一个进程：查规则 → 命中且未处理则下发 CPU Set。</summary>
        private void HandleProcess(int pid, string name)
        {
            if (pid <= 0 || string.IsNullOrEmpty(name))
                return;

            // 规则查询要遍历规则表，先挡掉已处理的 PID。
            if (_handledPids.Contains(pid))
                return;

            ScheduleRule? rule = FindRuleFor(name);
            if (rule == null)
                return;

            // 命中才记录。TryAdd 保证「查 + 写」原子，同一进程只有一方能下发。
            if (!_handledPids.TryAdd(pid))
                return;

            ulong mask = rule.Target == SchedulingTarget.TargetMask
                ? (_masks.FindById(rule.MaskId)?.Mask ?? rule.TargetMask)
                : 0;

            // 旧版 CCD 规则只在迁移还没跑过时走到这里
            CpuSetScheduleResult result = rule.IsLegacyCcd
                ? _cpuSets.ScheduleToCcd(pid, rule.LegacyCcdIndex)
                : _cpuSets.ScheduleToMask(pid, mask);

            string label = rule.Label;

            // 节流键 = 进程名 + 错误码。PID 不进键：每次启动都是新 PID，带了等于没节流。
            string throttleKey = $"bind|{name}|{result.ErrorCode}";

            if (result.Success)
            {
                // 成功过就说明进程可达，把窗口清掉：之后再失败应当立刻看到。
                _throttle.Reset(throttleKey);

                _boundPids.TryAdd(pid);
                _log.Info($"已绑定 {name}（PID {pid}）→ {label}，{result.AppliedCount} 个 CPU Set{ParkingSuffix()}");
            }
            else if (_throttle.TryAcquire(throttleKey, out int suppressed))
            {
                _log.Warn(
                    $"绑定失败 {name}（PID {pid}）→ {label}：{result.Error}{LogThrottle.SuppressedSuffix(suppressed)}");
            }

            // 优先级跟规则一起下发：否则「cs2.exe → High」只对当前那批实例生效，
            // 关掉再开就还是 Normal。放在绑定之后 —— 绑都没绑上就别继续动手。
            if (result.Success && !string.IsNullOrEmpty(rule.Priority))
                ApplyPriority(pid, name, rule.Priority);
        }

        /// <summary>
        /// 停泊开启时绑定只是「写进了默认集合」，实际调度范围是默认集合减停泊核。
        /// 缓存 10 秒：每次绑定都读一次电源策略不划算。
        /// </summary>
        private string ParkingSuffix()
        {
            int? parked = CoreParkingPercent();
            return parked is int value && value < 100
                ? $"，核心停泊 {value}%，实际范围可能更大"
                : string.Empty;
        }

        private long _parkingTick = long.MinValue / 2;
        private int? _parkingValue;

        private int? CoreParkingPercent()
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _parkingTick) < 10_000)
                return _parkingValue;

            Interlocked.Exchange(ref _parkingTick, now);

            try
            {
                PowerPolicySnapshot snapshot = _power.ReadSnapshot();
                bool onBattery = snapshot.OnAcPower == false;
                bool supported = onBattery
                    ? snapshot.DcCoreParkingMinSupported
                    : snapshot.CoreParkingMinSupported;

                _parkingValue = supported
                    ? (onBattery ? snapshot.DcCoreParkingMin : snapshot.CoreParkingMin)
                    : null;
            }
            catch
            {
                _parkingValue = null;
            }

            return _parkingValue;
        }

        /// <summary>把单个进程设成指定优先级档位。后台路径只记日志，不弹 Toast。</summary>
        private void ApplyPriority(int pid, string name, string label)
        {
            PriorityApplyResult result = ProcessPriority.Apply(pid, label);

            if (result.Ok > 0)
            {
                _log.Info($"已设置优先级 PID {pid} → {ProcessPriority.DisplayOf(label)}");
                return;
            }

            // 开机补扫必然撞上一堆已退出 / 受保护进程，记 Info 就够。
            if (ProcessPriority.IsTransient(result))
            {
                _log.Debug($"跳过优先级 PID {pid}：进程已退出");
                return;
            }

            string throttleKey = $"priority|{name}|{result.ErrorCode}";

            if (_throttle.TryAcquire(throttleKey, out int suppressed))
            {
                _log.Warn(
                    $"设置优先级失败 {name}（PID {pid}）→ {ProcessPriority.DisplayOf(label)}：{result.Error}{LogThrottle.SuppressedSuffix(suppressed)}");
            }
        }

        /// <summary>按进程名找规则，null 表示无需绑定（无规则 / 被禁用 / 目标为 None）。</summary>
        private ScheduleRule? FindRuleFor(string name)
        {
            RefreshRulesIfStale();

            long stamp = Interlocked.Read(ref _lastRuleReloadTick);
            RuleIndexSnapshot? snapshot = _ruleIndex;

            if (snapshot is null || snapshot.Stamp != stamp)
            {
                lock (_ruleIndexGate)
                {
                    snapshot = _ruleIndex;
                    if (snapshot is null || snapshot.Stamp != stamp)
                    {
                        snapshot = new RuleIndexSnapshot
                        {
                            Stamp = stamp,
                            ByName = BuildRuleIndex(_scheduling.Profile.Rules),
                        };
                        _ruleIndex = snapshot;
                    }
                }
            }

            return snapshot.ByName.TryGetValue(name.Trim(), out ScheduleRule? rule) ? rule : null;
        }

        /// <summary>规则索引快照。Stamp 与 _lastRuleReloadTick 配对，整体一次发布。</summary>
        private sealed class RuleIndexSnapshot
        {
            public long Stamp;
            public Dictionary<string, ScheduleRule> ByName = new(StringComparer.OrdinalIgnoreCase);
        }

        private volatile RuleIndexSnapshot? _ruleIndex;

        private readonly object _ruleIndexGate = new();

        /// <summary>按进程名建规则索引（去首尾空格、忽略大小写），同名保留第一条。</summary>
        public static Dictionary<string, ScheduleRule> BuildRuleIndex(IReadOnlyList<ScheduleRule> rules)
        {
            Dictionary<string, ScheduleRule> index = new(StringComparer.OrdinalIgnoreCase);

            foreach (ScheduleRule rule in rules)
            {
                if (rule.Enabled &&
                    rule.Target != SchedulingTarget.None &&
                    !string.IsNullOrWhiteSpace(rule.Name))
                {
                    index.TryAdd(rule.Name.Trim(), rule);
                }
            }

            return index;
        }

        /// <summary>
        /// 规则是否命中给定进程名。
        /// public static：Worker 的 query-processes 要用同一份匹配逻辑算 matchedRuleName。
        /// </summary>
        public static bool Matches(ScheduleRule rule, string name) =>
            rule.Enabled &&
            rule.Target != SchedulingTarget.None &&
            !string.IsNullOrWhiteSpace(rule.Name) &&
            string.Equals(rule.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 规则去抖窗口。稳态下每秒只有新进程才走到这里，窗口放大不影响「保存后立即生效」——
        /// 保存时 ResetCache 会直接让缓存失效。
        /// </summary>
        private const int RuleReloadWindowMs = 3000;

        private long _lastRuleReloadTick = long.MinValue / 2;

        private void RefreshRulesIfStale()
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastRuleReloadTick);
            if (now - last < RuleReloadWindowMs)
                return;

            // 抢占式：只有推进成功的那一个线程去读盘。
            if (Interlocked.CompareExchange(ref _lastRuleReloadTick, now, last) != last)
                return;

            _scheduling.Reload();
        }

        /// <summary>强制下一次查找重新读盘（保存规则后由 ResetCache 触发）。</summary>
        private void InvalidateRuleCache()
        {
            Interlocked.Exchange(ref _lastRuleReloadTick, long.MinValue / 2);
            _ruleIndex = null;
        }

        /// <summary>
        /// 线程安全的 PID 集合。组合操作必须自己带锁：
        /// 拆成 Contains + Add 两步会在两步之间被其他线程插入。
        /// </summary>
        private sealed class PidSet
        {
            private readonly HashSet<int> _set = new();

            public int Count
            {
                get { lock (_set) return _set.Count; }
            }

            /// <summary>不存在则加入；true 表示本次是新加入的。</summary>
            public bool TryAdd(int pid)
            {
                lock (_set) return _set.Add(pid);
            }

            public bool Contains(int pid)
            {
                lock (_set) return _set.Contains(pid);
            }

            public void Remove(int pid)
            {
                lock (_set) _set.Remove(pid);
            }

            public void Clear()
            {
                lock (_set) _set.Clear();
            }

            public int[] ToArray()
            {
                lock (_set) return _set.ToArray();
            }

            /// <summary>只保留 <paramref name="alive"/> 中仍存在的 PID。</summary>
            public void PruneTo(HashSet<int> alive)
            {
                lock (_set) _set.RemoveWhere(pid => !alive.Contains(pid));
            }
        }
    }
}
