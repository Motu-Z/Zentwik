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
        /// 正在处理或已成功绑定的 PID。失败会移除，交给重试账本排队。
        /// 用带锁容器：扫描线程与 UI 线程（ResetCache）都会碰它。
        /// </summary>
        private readonly PidSet _handledPids = new();

        /// <summary>本工具**成功**绑过的 PID。解绑只碰这个集合里的进程。</summary>
        private readonly PidSet _boundPids = new();

        /// <summary>
        /// 其中用关联掩码绑过的那部分。
        /// 解绑要按当初的方式各清各的：CPU 集用空集合清除，关联掩码要还原成系统掩码，
        /// 混着清会动到用户自己设的亲和性。
        /// </summary>
        private readonly PidSet _affinityPids = new();

        /// <summary>场景下发的绑定。与规则路径分开记，解绑时才分得清谁该放掉。</summary>
        private readonly ScenarioBindingLedger _scenario = new();

        /// <summary>绑定失败的重试计划。失败的 PID 不占「已处理」位，由这里排期。</summary>
        private readonly ScheduleRetryLedger _retry = new();

        /// <summary>已下发的绑定原值，读回比对时当期望值用。</summary>
        private readonly ScheduleDriftTracker _drift = new();

        /// <summary>已处理 PID 的实例标识。全量轮比对，识别 PID 复用。</summary>
        private readonly ProcessInstanceLedger _instances = new();

        /// <summary>上次成功下发的目标。重扫时目标没变的进程不重复写入。</summary>
        private readonly AppliedTargetLedger _applied = new();

        /// <summary>已告警过的失效掩码 Id，掩码恢复后移除。</summary>
        private readonly HashSet<string> _missingMaskIds = new(StringComparer.Ordinal);

        /// <summary>读回比对的轮数计数器。约 5 秒一次：漂移是秒级现象，不必每轮都读。</summary>
        private int _driftRound;

        private const int DriftProbeEveryRounds = 5;

        /// <summary>串行化下发：启动补扫、按新规则重扫、每秒扫描共用，避免同一 PID 交叉清另一侧。</summary>
        private readonly object _applyGate = new();

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
                _affinityPids.Clear();
                _scenario.Clear();
                _retry.Clear();
                _drift.Clear();
                _instances.Clear();
                _applied.Clear();
                _lastAlive = null;
                _roundsSinceFullScan = 0;
                _driftRound = 0;

                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;
                _loop = Task.Run(() => LoopAsync(token), token);
                _running = true;

                _log.Info("进程调度监听已启动（按快照差集，轮询间隔 1 秒）");
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
            CancellationTokenSource? cts;
            Task? loop;

            lock (_lock)
            {
                if (!_running)
                    return;

                _running = false;
                cts = _cts;
                loop = _loop;
                _cts = null;
                _loop = null;
            }

            if (cts != null)
            {
                try { cts.Cancel(); } catch { /* 忽略 */ }
                cts.Dispose();
            }

            // 等扫描退出再清账本：否则最后一轮会把 PID 回填进刚清空的表。
            try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 忽略 */ }

            lock (_lock)
            {
                _handledPids.Clear();
                _boundPids.Clear();
                _affinityPids.Clear();
                _scenario.Clear();
                _retry.Clear();
                _drift.Clear();
                _instances.Clear();
                _applied.Clear();
                _missingMaskIds.Clear();
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
            if (!_running)
                return;

            List<ProcessIdentity> frame = NativeProcessList.Capture(_frame);

            // 快照失败时不能修剪：那会把全部 PID 当成已退出。
            if (frame.Count == 0)
                return;

            HashSet<int> alive = new(frame.Count);
            foreach (ProcessIdentity item in frame)
                alive.Add(item.Pid);

            bool changed;
            bool probeDrift = false;

            lock (_applyGate)
            {
                // 兜底：每分钟全量重扫一次，覆盖 PID 复用（同 PID 换了进程）这类漏网情况。
                if (++_roundsSinceFullScan >= FullScanEveryRounds)
                {
                    _roundsSinceFullScan = 0;
                    _lastAlive = null;
                }

                HashSet<int>? previous = _lastAlive;

                // 全量轮先校验 PID 是否还指向同一个进程。被复用的必须清掉旧账本，
                // 否则下面这轮会被「已处理」挡住，新进程一直不绑。
                if (previous is null)
                    PurgeReusedInstances();

                changed = previous is null || !previous.SetEquals(alive);

                if (changed)
                {
                    foreach (ProcessIdentity item in frame)
                    {
                        // 首帧全量；之后只处理上一帧没有的 PID。
                        if (previous is not null && previous.Contains(item.Pid))
                            continue;

                        TryHandle(item.Pid, item.Name);
                    }
                }

                RetryDue(alive);

                // 全量轮（切换方案、改删规则后都会走这里）顺手把「规则已不覆盖」的绑定放掉：
                // 光靠重扫只会重绑命中的进程，规则删掉的那批会一直留在旧核上。
                if (previous is null)
                    UnbindStaleRules(alive);

                if (++_driftRound >= DriftProbeEveryRounds)
                {
                    _driftRound = 0;
                    probeDrift = true;
                }

                // 按存活名单裁剪，防止 PID 复用后被误判为已处理。
                _handledPids.PruneTo(alive);
                _boundPids.PruneTo(alive);
                _affinityPids.PruneTo(alive);
                _scenario.PruneTo(alive);
                _retry.PruneTo(alive);
                _drift.PruneTo(alive);
                _instances.PruneTo(alive);
                _applied.PruneTo(alive);

                _lastAlive = alive;
            }

            // 读回是跨进程调用，放到锁外做：它不该占住补扫与解绑都要等的这把锁。
            if (probeDrift)
                ProbeDrift(alive);

            if (changed)
                RaiseProcessSetChanged();
        }

        /// <summary>到期且仍存活的失败项再试一次。</summary>
        private void RetryDue(HashSet<int> alive)
        {
            foreach ((int pid, string name) in _retry.Due(alive))
                TryHandle(pid, name);
        }

        /// <summary>
        /// 全量轮清理 PID 复用：同一个 PID 换了进程，旧账本必须作废，
        /// 否则新进程被「已处理」挡住永远不会绑，漂移日志也会拿旧期望值对着它报。
        ///
        /// 只查已记账的 PID（规则命中的那几个），每分钟一次，不在每轮扫描里做。
        /// </summary>
        private void PurgeReusedInstances()
        {
            foreach (int pid in _instances.TakeReused(_cpuSets.GetInstanceTicks))
            {
                ForgetInstance(pid);
                _log.Info($"PID {pid} 已换成新进程，重新判定绑定");
            }
        }

        /// <summary>把一个 PID 从全部账本里清掉，下一轮全量扫描按新进程处理。</summary>
        private void ForgetInstance(int pid)
        {
            _instances.Forget(pid);
            _handledPids.Remove(pid);
            _boundPids.Remove(pid);
            _affinityPids.Remove(pid);
            _scenario.Remove(pid);
            _retry.Remove(pid);
            _drift.Forget(pid);
            _applied.Forget(pid);
        }

        /// <summary>规则已删或已禁用，但进程还绑着的，放回系统默认。</summary>
        private void UnbindStaleRules(HashSet<int> alive)
        {
            foreach (int pid in _boundPids.ToArray())
            {
                if (!alive.Contains(pid) || _handledPids.Contains(pid) || _scenario.Contains(pid))
                    continue;

                if (!Unbind(pid, out string? error) && !string.IsNullOrEmpty(error))
                    _log.Info($"跳过解绑 PID {pid}：{error}");

                _boundPids.Remove(pid);
            }
        }

        /// <summary>
        /// 读回已绑定进程的实际状态，与下发值比对，不一致只记日志。
        ///
        /// 先看清漂移从哪来、量级多大，再决定要不要重写：现在这一步不动任何进程。
        /// </summary>
        private void ProbeDrift(HashSet<int> alive)
        {
            foreach (ScheduleDriftTracker.Entry entry in _drift.Snapshot())
            {
                if (!alive.Contains(entry.Pid))
                    continue;

                BindingProbe probe = _cpuSets.ProbeBinding(entry.Pid, entry.Mask, entry.ByAffinityMask);

                // 读不到（进程正在退出、权限不足）不是漂移。
                if (!probe.Readable || probe.Matches)
                    continue;

                // 探测期间账本可能已被改掉（用户解绑、规则改绑、场景回收）。读到的是旧
                // 期望值，拿它报漂移就是纯误报 —— 账本已经变了就把这次结果丢掉。
                if (!_drift.TryGet(entry.Pid, out ScheduleDriftTracker.Entry current) || current != entry)
                    continue;

                if (_throttle.TryAcquire($"drift|{entry.Name}", out int suppressed))
                {
                    _log.Info(
                        $"绑定漂移：{entry.Name}（PID {entry.Pid}）期望 0x{probe.ExpectedMask:X}，" +
                        $"实际 0x{probe.ActualMask:X}{LogThrottle.SuppressedSuffix(suppressed)}");
                }
            }
        }

        private void TryHandle(int pid, string name)
        {
            try
            {
                HandleProcess(pid, name);
            }
            catch (Exception ex)
            {
                _log.Warn($"绑定 {name}（PID {pid}）时异常：{ex.Message}");
            }
        }

        /// <summary>启动补扫：把已经在跑的程序过一遍，事件式监听盖不到它们。</summary>
        private void InitialScan()
        {
            if (!_running)
                return;

            long started = Environment.TickCount64;

            try
            {
                List<ProcessIdentity> frame = NativeProcessList.Capture(new List<ProcessIdentity>(512));

                lock (_applyGate)
                {
                    foreach (ProcessIdentity item in frame)
                        TryHandle(item.Pid, item.Name);
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

                        // 防抖窗口内用户可能已经关掉监听，这一轮不能再扫。
                        if (!_running)
                            return;

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
                    _log.Warn($"按新规则重新扫描进程失败：{ex.Message}");
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
                _retry.Clear();

                // _boundPids 是「已绑、未解绑」的账本，重扫不解绑，不能清 ——
                // 清了之后 ClearAllSchedules 会静默什么都不做。
            }

            // 下一轮走「全量重扫」路径，正在运行的进程才会被重新绑到新规则上。
            lock (_applyGate)
            {
                _lastAlive = null;
                _roundsSinceFullScan = 0;
            }

            InvalidateRuleCache();
        }

        /// <summary>本工具成功绑过、且尚未解绑的 PID 快照（规则与场景两条路径合计）。</summary>
        public int[] GetBoundProcessIds()
        {
            HashSet<int> all = new(_boundPids.ToArray());
            all.UnionWith(_scenario.ToArray());
            return all.ToArray();
        }

        /// <summary>
        /// 解绑全部「本工具绑过」的进程，返回真正清掉 CPU Set 的数量。
        /// 只遍历已绑集合，不扫全系统 —— 碰用户别处绑的进程不是本工具的职责。
        /// </summary>
        public int ClearAllSchedules()
        {
            HashSet<int> all = new(_boundPids.ToArray());
            all.UnionWith(_scenario.Take(owner: null));

            int unbound = 0;

            foreach (int pid in all)
            {
                if (Unbind(pid, out string? error))
                {
                    unbound++;
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    _log.Info($"跳过解绑 PID {pid}：{error}");
                }

                _boundPids.Remove(pid);
            }

            return unbound;
        }

        /// <summary>解绑单个 PID。返回是否真的清掉了 CPU Set。</summary>
        public bool ClearSchedule(int pid)
        {
            bool ok = Unbind(pid, out string? error);

            if (!ok && !string.IsNullOrEmpty(error))
                _log.Info($"跳过解绑 PID {pid}：{error}");

            _boundPids.Remove(pid);
            _scenario.Remove(pid);
            return ok;
        }

        /// <summary>
        /// 记一次场景下发的绑定。场景不经过规则表，不记进来就没人解得掉它。
        /// </summary>
        public void NoteBinding(int pid, string name, ulong mask, bool byAffinityMask, string owner)
        {
            _scenario.Note(pid, owner);
            _instances.Note(pid, _cpuSets.GetInstanceTicks(pid));

            if (mask != 0)
            {
                _drift.Note(pid, name, mask, byAffinityMask);
                _applied.Note(pid, new AppliedTargetLedger.Target(mask, byAffinityMask, string.Empty));
            }

            if (byAffinityMask)
            {
                _affinityPids.TryAdd(pid);
            }
            else
            {
                _affinityPids.Remove(pid);
            }
        }

        /// <summary>
        /// 切换绑定方式前清掉本工具用另一种方式留下的限制。
        /// 只清自己设过的：别处设的亲和性不归我们管。
        /// </summary>
        public void ClearOwnAffinity(int pid)
        {
            if (!_affinityPids.Contains(pid))
                return;

            _affinityPids.Remove(pid);
            _applied.Forget(pid);

            if (!_cpuSets.ClearAffinityMask(pid, out string? error) && !string.IsNullOrEmpty(error))
                _log.Info($"改用 CPU Sets 前清除 Affinity Mask 失败（PID {pid}）：{error}");
        }

        /// <summary>
        /// 解绑场景下发的绑定。<paramref name="owner"/> 为空表示全部。
        /// 返回真正清掉的数量。
        /// </summary>
        public int ReleaseScenarioBindings(string? owner)
        {
            int unbound = 0;

            foreach (int pid in _scenario.Take(owner))
            {
                // 规则还管着它的话，场景退出不该把它从规则手里一起解掉。
                if (_boundPids.Contains(pid))
                    continue;

                if (Unbind(pid, out string? error))
                {
                    unbound++;
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    _log.Info($"跳过解绑 PID {pid}：{error}");
                }
            }

            return unbound;
        }

        /// <summary>按当初的绑定方式解绑，两类互不牵连。</summary>
        private bool Unbind(int pid, out string? error)
        {
            error = null;
            _drift.Forget(pid);
            _applied.Forget(pid);

            if (_affinityPids.Contains(pid))
            {
                _affinityPids.Remove(pid);

                if (_cpuSets.ClearAffinityMask(pid, out error))
                    return true;

                // 关联掩码没清掉也别放弃另一侧：这个 PID 可能先后被两种方式绑过。
                bool ok = _cpuSets.ClearDefaultCpuSets(pid, out string? other);
                error ??= other;
                return ok;
            }

            return _cpuSets.ClearDefaultCpuSets(pid, out error);
        }

        /// <summary>
        /// 按 CPU 集绑定。同一 PID 之前若用关联掩码绑过，先把亲和性放回系统值 ——
        /// 两者是求交集的关系，留着旧掩码会让新的核集合看上去没生效。
        /// </summary>
        private CpuSetScheduleResult BindCpuSets(int pid, ulong mask)
        {
            if (_affinityPids.Contains(pid))
            {
                _affinityPids.Remove(pid);

                if (!_cpuSets.ClearAffinityMask(pid, out string? error) && !string.IsNullOrEmpty(error))
                    _log.Info($"改用 CPU Sets 前清除 Affinity Mask 失败（PID {pid}）：{error}");
            }

            return _cpuSets.ScheduleToMask(pid, mask);
        }

        /// <summary>
        /// 按关联掩码绑定。默认 CPU 集还留着时，进程实际可跑的核是两者交集，
        /// 所以先清掉旧的那条限制，再写亲和性。
        /// </summary>
        private CpuSetScheduleResult BindAffinity(int pid, ulong mask)
        {
            if (!_cpuSets.IsAffinityMaskSupported)
            {
                return new CpuSetScheduleResult
                {
                    Success = false,
                    ErrorCode = -1,
                    CcdIndex = -1,
                    Error = "本机有多个处理器组，Affinity Mask 只能覆盖处理器组 0，请改用 CPU Sets"
                };
            }

            if (!_cpuSets.ClearDefaultCpuSets(pid, out string? error) && !string.IsNullOrEmpty(error))
                _log.Info($"按 Affinity Mask 绑定前清除默认 CPU Sets 失败（PID {pid}）：{error}");

            CpuSetScheduleResult result = _cpuSets.ScheduleToAffinityMask(pid, mask);

            if (result.Success)
                _affinityPids.TryAdd(pid);

            return result;
        }

        /// <summary>
        /// 这条规则该按关联掩码下发还是按 CPU Sets 下发。
        /// 掩码还在就以它的当前方式为准；掩码被删了才退回规则里的快照。
        /// </summary>
        internal static bool UseAffinityMask(ScheduleRule rule, CustomMask? found) =>
            !rule.IsLegacyCcd && (found?.Type ?? rule.BindType) == MaskType.AffinityMask;

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

            // 掩码内容与下发方式都取自掩码本身：规则只存 Id，
            // 用户改掩码的核或改方式之后，下一次命中就该按新的一组来。
            CustomMask? found = rule.Target == SchedulingTarget.TargetMask
                ? _masks.FindById(rule.MaskId)
                : null;

            if (rule.Target == SchedulingTarget.TargetMask)
            {
                if (found is null)
                {
                    // 每条规则只提一次：掩码删除后每秒扫描都会走到这里。
                    if (!string.IsNullOrEmpty(rule.MaskId) && _missingMaskIds.Add(rule.MaskId))
                        _log.Warn($"{name} 的规则指向的掩码已被删除，按删除前的设置绑定");
                }
                else
                {
                    _missingMaskIds.Remove(rule.MaskId);
                }
            }

            ulong mask = rule.Target == SchedulingTarget.TargetMask
                ? (found?.Mask ?? rule.TargetMask)
                : 0;

            // 掩码还在就用它的当前方式，掩码被删了才退回规则里的快照 ——
            // 少了这份快照，删除掩码会让 Affinity Mask 规则静默改按 CPU Sets 下发。
            bool byAffinityMask = UseAffinityMask(rule, found);

            AppliedTargetLedger.Target target = new(mask, byAffinityMask, rule.Priority);

            // 目标与上次成功下发的一致：这次重扫不是为它来的，不必再写一遍。
            if (_applied.Matches(pid, target))
                return;

            // 旧版 CCD 规则只在迁移还没跑过时走到这里
            CpuSetScheduleResult result = rule.IsLegacyCcd
                ? _cpuSets.ScheduleToCcd(pid, rule.LegacyCcdIndex)
                : byAffinityMask
                    ? BindAffinity(pid, mask)
                    : BindCpuSets(pid, mask);

            string label = rule.Label;

            // 节流键 = 进程名 + 错误码。PID 不进键：每次启动都是新 PID，带了等于没节流。
            string throttleKey = $"bind|{name}|{result.ErrorCode}";

            if (result.Success)
            {
                // 成功过就说明进程可达，把窗口清掉：之后再失败应当立刻看到。
                _throttle.Reset(throttleKey);
                _retry.Remove(pid);

                _boundPids.TryAdd(pid);
                _instances.Note(pid, _cpuSets.GetInstanceTicks(pid));
                _applied.Note(pid, target);

                if (mask != 0)
                    _drift.Note(pid, name, mask, byAffinityMask);

                // 关联掩码是硬约束，停泊不会缩范围，不追加警告。
                string parkingSuffix = byAffinityMask ? string.Empty : ParkingSuffix();
                string scope = byAffinityMask
                    ? $"{result.AppliedCount} 个逻辑处理器"
                    : $"{result.AppliedCount} 个 CPU Sets";
                _log.Info($"已绑定 {name}（PID {pid}）→ {label}，{scope}{parkingSuffix}");
            }
            else
            {
                // 失败不占位：交给重试账本排队，否则这个 PID 到退出都不会再试。
                _handledPids.Remove(pid);
                _applied.Forget(pid);

                int attempts = _retry.Bump(pid, name);

                if (attempts == _retry.MaxAttempts + 1)
                {
                    _log.Warn(
                        $"绑定失败 {name}（PID {pid}）→ {label}：{result.Error}（已重试 {_retry.MaxAttempts} 次，暂不重试）");
                }
                else if (attempts <= _retry.MaxAttempts && _throttle.TryAcquire(throttleKey, out int suppressed))
                {
                    _log.Warn(
                        $"绑定失败 {name}（PID {pid}）→ {label}：{result.Error}{LogThrottle.SuppressedSuffix(suppressed)}");
                }
            }

            // 优先级跟规则一起下发：否则「cs2.exe → High」只对当前那批实例生效，
            // 关掉再开就还是 Normal。放在绑定之后 —— 绑都没绑上就别继续动手。
            if (result.Success && !string.IsNullOrEmpty(rule.Priority))
                ApplyPriority(pid, name, rule.Priority);
        }

        /// <summary>
        /// CPU Set 是软约束：停泊开启时实际范围是默认集合减停泊核。缓存 10 秒。
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
                _log.Debug($"跳过优先级设置：PID {pid} 已退出");
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
