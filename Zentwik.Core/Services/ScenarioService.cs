using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 场景触发源。
    ///
    /// 只列「能靠一个事件判定、且判定成本≈0」的源。
    /// CPU 占用率、温度这类需要采样的量刻意不在这里：它们要常驻轮询才有意义，
    /// 而本工程对后台的硬约束是「没人看的时候占用必须归零」——
    /// 为一个「负载高就换方案」的功能赔上常驻采样，不划算。
    /// </summary>
    public enum ScenarioTrigger
    {
        /// <summary>指定进程正在运行（参数 = 进程名，如 game.exe）。进程退出即视为不再命中。</summary>
        ProcessRunning = 0,

        /// <summary>电池供电（笔记本拔掉电源）。</summary>
        OnBattery = 1,

        /// <summary>交流供电（插上电源）。</summary>
        OnAcPower = 2,

        /// <summary>
        /// 指定进程刚刚退出（参数 = 进程名）。
        ///
        /// 其余三个触发都是「此刻成立不成立」，进程退出只在退出的那一拍成立。
        /// <see cref="ScenarioMonitor"/> 比对相邻两次进程名单得出「这一拍谁没了」，
        /// 交给 <see cref="Notify"/> 评估一次；下一拍它就不在触发集合里了。
        /// 因此这条规则只会生效一次 —— 恰好是「退出时切回平衡」要的语义，
        /// 也符合本服务「不负责切回去」的既有约定。
        /// </summary>
        ProcessExited = 3
    }

    /// <summary>
    /// 一条场景规则：什么情况 → 切到哪套方案。
    /// </summary>
    public sealed class ScenarioRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public ScenarioTrigger Trigger { get; set; } = ScenarioTrigger.ProcessRunning;

        /// <summary>
        /// 触发参数。进程类触发填进程名；电源类触发留空。
        /// 用一个字符串而不是给每种触发各开一个字段：新增触发源时不必改数据结构，
        /// 代价只是约定写在注释里。
        /// </summary>
        public string Argument { get; set; } = string.Empty;

        /// <summary>
        /// 调校动作：命中后切换到的调校方案 Id（指向 <see cref="PlanService"/> 中的调校方案）。
        /// 空串表示这条规则不动调校。
        /// </summary>
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>
        /// 调度动作：命中后套用到触发进程上的自定义掩码 Id（指向 <see cref="Helpers.IMaskStore"/>）。
        /// 空串表示这条规则不动调度。
        /// </summary>
        public string MaskId { get; set; } = string.Empty;

        /// <summary>
        /// 电源动作：命中后切换到的电源方案 Guid。空串表示这条规则不动电源方案。
        /// </summary>
        public string PowerSchemeId { get; set; } = string.Empty;

        /// <summary>
        /// 仲裁优先级：同时命中多条规则时，数字小的赢。
        ///
        /// 为什么需要：插着电打游戏时「插电」和「游戏在跑」两条都命中，
        /// 没有仲裁就会看规则在列表里的先后，而那个顺序取决于用户哪条先建 ——
        /// 不可预测的规则等于没有规则。
        /// </summary>
        public int Priority { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>触发源文案（列表行左侧）。写法与 ScheduleRule.Label 一致：显示名归模型管。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string TriggerText => Trigger switch
        {
            ScenarioTrigger.ProcessRunning => string.IsNullOrEmpty(Argument) ? "进程运行" : $"运行 {Argument}",
            ScenarioTrigger.ProcessExited => string.IsNullOrEmpty(Argument) ? "进程退出" : $"退出 {Argument}",
            ScenarioTrigger.OnBattery => "电池供电",
            _ => "交流供电"
        };

        /// <summary>
        /// 目标方案名。方案被删后显示「（已删除）」而不是空白 ——
        /// 空白会被读成「这条规则没配方案」，而实际是配过的那个没了。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string ProfileName { get; set; } = "（已删除）";
    }

    /// <summary>scenarios.json 的根对象。</summary>
    public sealed class ScenarioFile
    {
        public bool Enabled { get; set; }

        public List<ScenarioRule> Rules { get; set; } = new();
    }

    /// <summary>
    /// 一条命中规则要执行的全部动作。三个字段各自可为空串（空 = 不动那一块），
    /// 由界面按「有值才点亮」展示。
    /// </summary>
    public sealed record ScenarioOutcome(
        string RuleId,
        string TriggerArgument,
        string ProfileId,
        string MaskId,
        string PowerSchemeId);

    /// <summary>
    /// 场景自动切换（v1.1.x）。
    ///
    /// 数据结构、落盘、仲裁逻辑、以及唯一的外部入口 <see cref="Notify"/> 都已可用；
    /// 事件源在 <see cref="ScenarioMonitor"/> 里按规则内容按需挂载。
    ///
    /// 一条规则可以只改调校、只改调度、只改电源，也可以三者都改。
    /// 任一字段为空表示「这一块保持原样」，不是「清空它」。
    ///
    /// 本类不持有任何定时器、不启动任何线程。所有状态变化都来自外部调用 Notify。
    /// </summary>
    public class ScenarioService
    {
        private readonly ILogService _log;
        private readonly PlanService _plans;
        private readonly IAppPaths _paths;
        private readonly IMaskStore? _masks;
        private readonly ICpuSetScheduler? _scheduler;
        private readonly PowerPolicyService? _power;

        public ScenarioService(
            ILogService log,
            PlanService plans,
            IAppPaths paths,
            IMaskStore? masks = null,
            ICpuSetScheduler? scheduler = null,
            PowerPolicyService? power = null)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(plans);
            _plans = plans;
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
            _masks = masks;
            _scheduler = scheduler;
            _power = power;
        }

        /// <summary>
        /// 上一次已下发的「掩码 | 进程」组合。评估是周期性的（事件合并窗口 800ms），
        /// 同一个组合重复下发结果一样，只会把日志刷满。
        /// </summary>
        private string? _lastMaskKey;

        private string FilePath => _paths.File("scenarios.json");

        private readonly object Sync = new();

        /// <summary>
        /// 规则主副本。所有读写都在 <see cref="Sync"/> 保护下进行，
        /// 对外只通过 <see cref="Rules"/> 返回的副本暴露。
        /// </summary>
        private readonly List<ScenarioRule> _rules = new();

        /// <summary>
        /// 规则集合的只读快照。
        ///
        /// <see cref="Notify"/> 由 WMI 回调线程调用，保存/删除来自其它线程。
        /// 把可变集合直接交出去，等于让调用方在别人改写时遍历它 ——
        /// 这正是偶发 InvalidOperationException 的来源。规则数量是个位数，
        /// 每次复制一份的代价远低于一个偶发并发故障。
        /// </summary>
        public IReadOnlyList<ScenarioRule> Rules
        {
            get
            {
                lock (Sync)
                    return _rules.ToArray();
            }
        }

        /// <summary>规则或总闸变化时触发，在触发变化的那条线程上同步调用。</summary>
        public event Action? Changed;

        /// <summary>
        /// 总闸。关掉时 <see cref="Notify"/> 直接返回，一条规则都不评估。
        /// </summary>
        public bool Enabled { get; private set; }

        /// <summary>最近一次实际切换到的方案 Id（供界面显示当前场景出自哪条规则）。</summary>
        public string? LastAppliedProfileId { get; private set; }

        /// <summary>
        /// 是否存在启用中的「进程运行」类规则。
        ///
        /// 一个用户可能只配了「插电 → 性能方案」这一条电源规则。若监听方不分青红皂白
        /// 把进程事件也挂上，那么机器上每一个进程的启动都会换来一次全量进程名单采集
        /// —— 而采集名单是为了判定进程规则，一条都没配时它是纯浪费。
        /// 「要监听什么」由规则内容决定，不是由「场景功能开着」决定。
        /// </summary>
        public bool HasProcessRules
        {
            get
            {
                lock (Sync)
                    return _rules.Any(r => r.Enabled &&
                        (r.Trigger == ScenarioTrigger.ProcessRunning || r.Trigger == ScenarioTrigger.ProcessExited));
            }
        }

        /// <summary>
        /// 需要盯「还在不在」的进程名（启用中的退出规则所指向的那些）。
        ///
        /// 进程退出是差集运算：要知道谁没了，就得留一份上一拍的名单。全机器几百个进程
        /// 都留一份是纯浪费 —— 只有规则里写到的名字才有人关心。
        /// 用户一条退出规则都没配时返回空集合，监视方连这份差集都不做。
        /// </summary>
        public IReadOnlyList<string> ProcessExitArguments
        {
            get
            {
                lock (Sync)
                {
                    // 这份名单每次进程集合变化都会被重新取一次。直接一次遍历去重，
                    // 省掉 Where / Select / Distinct 三层迭代器与中间数组。
                    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                    List<string> result = new();

                    foreach (ScenarioRule r in _rules)
                    {
                        if (r.Enabled
                            && r.Trigger == ScenarioTrigger.ProcessExited
                            && !string.IsNullOrEmpty(r.Argument)
                            && seen.Add(r.Argument))
                        {
                            result.Add(r.Argument);
                        }
                    }

                    return result.Count == 0 ? Array.Empty<string>() : result.ToArray();
                }
            }
        }

        /// <summary>是否存在启用中的电源类规则（电池 / 交流）。同上，决定要不要挂电源监听。</summary>
        public bool HasPowerRules
        {
            get
            {
                lock (Sync)
                    return _rules.Any(r => r.Enabled &&
                        (r.Trigger == ScenarioTrigger.OnBattery || r.Trigger == ScenarioTrigger.OnAcPower));
            }
        }

        public void Load()
        {
            lock (Sync)
            {
                ScenarioFile? file = null;
                try
                {
                    file = JsonFile.Read(FilePath, AppJsonContext.Default.ScenarioFile);
                }
                catch (Exception ex)
                {
                    _log.Error($"读取场景规则失败: {ex.Message}");
                }

                Enabled = file?.Enabled ?? false;

                // 整批替换而不是增量对齐：集合通知成本已经不存在，
                // 「谁在哪个下标」也不再对外可见，替换是唯一不会留下中间态的做法。
                _rules.Clear();
                if (file?.Rules != null)
                    _rules.AddRange(file.Rules);
            }

            Changed?.Invoke();
        }

        /// <summary>切换总闸。</summary>
        public void SetEnabled(bool enabled)
        {
            lock (Sync)
            {
                if (Enabled == enabled)
                    return;

                Enabled = enabled;
                Persist();
            }

            _log.Info(enabled ? "场景自动切换：已开启" : "场景自动切换：已关闭");
            Changed?.Invoke();
        }

        /// <summary>新增或按 Id 覆盖一条规则。</summary>
        public void Save(ScenarioRule rule)
        {
            lock (Sync)
            {
                int index = IndexOfId(rule.Id);
                if (index >= 0)
                    _rules[index] = rule;
                else
                    _rules.Add(rule);

                Persist();
            }

            Changed?.Invoke();
        }

        public bool Delete(string id)
        {
            lock (Sync)
            {
                int index = IndexOfId(id);
                if (index < 0)
                    return false;

                _rules.RemoveAt(index);
                Persist();
            }

            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// 方案被删除后，解除规则里对它的引用。
        /// </summary>
        /// <remarks>
        /// 只把对应那一个动作置空（空串 = 保持原样），规则本身与另外两个动作都不动。
        /// 留着失效 Id 的后果不是「看起来有个名字」——仲裁仍会选中它、应用仍会失败，
        /// 每次命中都在日志里刷一条失败，用户体验上等于这条规则莫名失效。
        /// </remarks>
        /// <returns>被解除引用的规则条数。</returns>
        public int DetachPlan(PlanKind kind, string planId)
        {
            if (string.IsNullOrEmpty(planId))
                return 0;

            int affected = 0;

            lock (Sync)
            {
                foreach (ScenarioRule rule in _rules)
                {
                    bool hit = kind switch
                    {
                        PlanKind.Tuning => string.Equals(rule.ProfileId, planId, StringComparison.Ordinal),
                        PlanKind.Scheduling => string.Equals(rule.MaskId, planId, StringComparison.Ordinal),
                        PlanKind.Power => string.Equals(rule.PowerSchemeId, planId, StringComparison.Ordinal),
                        _ => false
                    };

                    if (!hit)
                        continue;

                    switch (kind)
                    {
                        case PlanKind.Tuning:
                            rule.ProfileId = string.Empty;
                            break;
                        case PlanKind.Scheduling:
                            rule.MaskId = string.Empty;
                            break;
                        case PlanKind.Power:
                            rule.PowerSchemeId = string.Empty;
                            break;
                    }

                    affected++;
                }

                if (affected > 0)
                    Persist();
            }

            if (affected > 0)
            {
                _log.Info($"方案已删除：{affected} 条场景规则解除了对它的引用，对应动作改为「保持原样」");
                Changed?.Invoke();
            }

            return affected;
        }

        /// <summary>
        /// 外部事件源的唯一入口：告诉服务「此刻有哪些场景成立」。
        ///
        /// 传的是**全部当前成立的触发**而不是单个事件：进程 A 退出时，
        /// 进程 B 可能还在跑，只看「A 退出了」会误判成「没有进程命中」。
        ///
        ///   · 进程：ProcessWatcher 每次扫描/事件回调时，把当前存活的进程名集合
        ///     转成 ProcessRunning 触发传入；
        ///   · 电源：系统电源模式变更通知回调时，按当前供电状态传 OnBattery / OnAcPower。
        ///
        /// 纯同步方法，可在任意线程调用（WMI 回调线程直接调用即可）。
        /// 它不会再向任何 UI 线程封送：内部集合的并发安全由 <see cref="Sync"/> 保证，
        /// 需要回到界面线程刷新界面是订阅 <see cref="Changed"/> 的一方自己的责任。
        ///
        /// 游戏启动会瞬间拉起十几个子进程，逐个通知会触发十几次评估。
        /// 实装时应在调用侧合并（同一次扫描只调一次），而不是在这里加延时 ——
        /// 延时窗口取多长没有依据，只能靠猜。
        /// </summary>
        public void Notify(IEnumerable<(ScenarioTrigger Trigger, string Argument)> active)
        {
            if (!Enabled)
                return;

            List<(ScenarioTrigger Trigger, string Argument)> snapshot = active.ToList();
            ScenarioOutcome? outcome = ResolveOutcomeCore(snapshot);

            // 没有规则命中时**刻意什么都不做**。
            // 场景只负责「切到某套」，不负责「切回去」——
            // 用户手动选的那套不该被一条没命中的规则覆盖掉。
            if (outcome is null)
                return;

            ApplyProfile(outcome, snapshot);
            ApplyScheduling(outcome);
            ApplyPowerScheme(outcome);
        }

        /// <summary>
        /// 调校动作。沿用原有语义：比的是「当前实际生效的是哪套」，不是自己记的上次目标 ——
        /// 中间用户可能手动切过，比自己那份会让手动选择被无声保留。
        /// </summary>
        private void ApplyProfile(
            ScenarioOutcome outcome,
            List<(ScenarioTrigger Trigger, string Argument)> active)
        {
            if (string.IsNullOrEmpty(outcome.ProfileId))
                return;

            bool alreadyActive = string.Equals(outcome.ProfileId, _plans.ActiveId(PlanKind.Tuning), StringComparison.Ordinal);
            LastAppliedProfileId = outcome.ProfileId;

            if (alreadyActive)
                return;

            if (_plans.Apply(PlanKind.Tuning, outcome.ProfileId).Success)
            {
                _lastMissingProfileId = null;
                _log.Info($"场景切换：{Describe(outcome.ProfileId)} ← {DescribeHit(active)}");
            }
            else if (!string.Equals(_lastMissingProfileId, outcome.ProfileId, StringComparison.Ordinal))
            {
                // 评估每 2 秒来一次，同一条失效规则会一直命中。不去重的话日志里
                // 每两秒一行「目标方案不存在」，真正有用的上下文全被冲掉。
                _lastMissingProfileId = outcome.ProfileId;
                _log.Warn("场景切换：目标方案不存在，已跳过（方案可能已被删除）");
            }
        }

        /// <summary>上一条因方案被删而跳过的规则 Id，用来抑制重复告警。</summary>
        private string? _lastMissingProfileId;

        /// <summary>
        /// 调度动作：把掩码套到触发进程上。
        ///
        /// 只对「进程启动」类规则有意义 —— 电源类触发没有具体进程，没有绑核对象。
        /// 同一 (掩码, 进程) 组合只下发一次：评估每 800ms 可能来一次，
        /// 每次都重绑会把日志刷满，而结果完全一样。
        /// </summary>
        private void ApplyScheduling(ScenarioOutcome outcome)
        {
            if (_scheduler is null || string.IsNullOrEmpty(outcome.MaskId))
                return;

            // 新规则引用调度方案：整套用户规则一次下发。
            PlanEntry? plan = _plans.Find(PlanKind.Scheduling, outcome.MaskId);

            if (plan is not null)
            {
                string planKey = plan.Id + "|" + outcome.TriggerArgument;

                if (string.Equals(Volatile.Read(ref _lastMaskKey), planKey, StringComparison.Ordinal))
                    return;

                Volatile.Write(ref _lastMaskKey, planKey);

                PlanApplyResult result = _plans.Apply(PlanKind.Scheduling, plan.Id);

                if (result.Success)
                    _log.Info($"场景调度：已应用「{plan.Name}」");
                else
                    _log.Warn($"场景调度：应用「{plan.Name}」失败：{result.Message}");

                return;
            }

            // 旧规则里存的是掩码 Id，继续按绑核处理。
            if (_masks is null || string.IsNullOrEmpty(outcome.TriggerArgument))
                return;

            CustomMask? mask = _masks.FindById(outcome.MaskId);

            if (mask is null)
            {
                _log.Warn($"场景调度：掩码 {outcome.MaskId} 已不存在，已跳过");
                return;
            }

            string key = outcome.MaskId + "|" + outcome.TriggerArgument;

            if (string.Equals(Volatile.Read(ref _lastMaskKey), key, StringComparison.Ordinal))
                return;

            Volatile.Write(ref _lastMaskKey, key);

            int applied = 0;

            foreach (int pid in FindProcessIds(outcome.TriggerArgument))
            {
                CpuSetScheduleResult result = _scheduler.ScheduleToMask(pid, mask.Mask);

                if (result.Success)
                {
                    applied++;
                }
                else
                {
                    _log.Warn($"场景调度：{outcome.TriggerArgument}（PID {pid}）→ {mask.Name} 失败：{result.Error}");
                }
            }

            if (applied > 0)
                _log.Info($"场景调度：{outcome.TriggerArgument} → {mask.Name}（{applied} 个进程）");
        }

        /// <summary>电源动作：切到规则指定的电源方案。已经是那一套就不重复下发。</summary>
        private void ApplyPowerScheme(ScenarioOutcome outcome)
        {
            if (_power is null || string.IsNullOrEmpty(outcome.PowerSchemeId))
                return;

            PlanEntry? plan = _plans.Find(PlanKind.Power, outcome.PowerSchemeId);
            string guidText = plan?.PowerSchemeGuid ?? outcome.PowerSchemeId;

            if (!Guid.TryParse(guidText, out Guid guid) || guid == Guid.Empty)
                return;

            if (_power.GetActiveScheme() == guid)
                return;

            (bool Success, string? Error) result = _power.SetActiveScheme(guid);

            if (result.Success)
            {
                // 系统计划换人了，「生效中」标记要跟着走，否则界面还写上一份。
                try
                {
                    _plans.MarkActive(PlanKind.Power, plan?.Id ?? _plans.FindPowerByScheme(guid)?.Id);
                }
                catch (Exception)
                {
                    // 记账失败不影响「系统已切过去」这件事。
                }

                _log.Info($"场景电源：已切换到电源方案 {guid}");
            }
            else
            {
                _log.Warn($"场景电源：切换失败：{result.Error}");
            }
        }

        /// <summary>
        /// 把触发进程名展开成 PID。进程可能在两次评估之间退了，
        /// 取 Id 前要防 InvalidOperationException（拿不到就跳过这一个）。
        /// </summary>
        private static List<int> FindProcessIds(string processName)
        {
            List<int> ids = new();

            string bare = Path.GetFileNameWithoutExtension(processName);

            if (string.IsNullOrEmpty(bare))
                return ids;

            System.Diagnostics.Process[] processes;

            try
            {
                processes = System.Diagnostics.Process.GetProcessesByName(bare);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return ids;
            }

            foreach (System.Diagnostics.Process process in processes)
            {
                try
                {
                    ids.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                    // 进程已退出，跳过。
                }
                finally
                {
                    process.Dispose();
                }
            }

            return ids;
        }

        /// <summary>
        /// 仲裁：从命中的规则里挑出该生效的那条。纯函数，不落盘、不下发，
        /// 因此界面可以拿它做「当前会切到哪套」的预览。
        ///
        /// 三个动作一起生效：优先级最高的那条有效规则决定全部三块，
        /// 不是每块各自挑一条 —— 后者会让「游戏规则 + 插电规则」同时改不同模块，
        /// 用户看不出是哪条在管事。
        /// </summary>
        public ScenarioOutcome? ResolveOutcome(IEnumerable<(ScenarioTrigger Trigger, string Argument)> active)
        {
            // 外部调用方可能在枚举过程中改动集合，这里仍复制一份；
            // Notify 传进来的已是不可变快照，走 ResolveOutcomeCore 省掉第二次复制。
            return ResolveOutcomeCore(active.ToList());
        }

        private ScenarioOutcome? ResolveOutcomeCore(
            List<(ScenarioTrigger Trigger, string Argument)> snapshot)
        {
            if (snapshot.Count == 0)
                return null;

            // 每条规则原来都要把全部触发线性扫一遍（进程名常有几百条）。
            // 一次建集合，之后每条规则只查一次表。
            HashSet<(ScenarioTrigger Trigger, string Argument)> activeSet =
                new(ActiveTriggerComparer.Instance);

            foreach ((ScenarioTrigger trigger, string argument) in snapshot)
                activeSet.Add((trigger, argument));

            lock (Sync)
            {
                foreach (ScenarioRule rule in _rules
                             .Where(rule => rule.Enabled && Matches(rule, activeSet))
                             .OrderBy(rule => rule.Priority))
                {
                    bool hasAction = !string.IsNullOrEmpty(rule.ProfileId)
                                  || !string.IsNullOrEmpty(rule.MaskId)
                                  || !string.IsNullOrEmpty(rule.PowerSchemeId);

                    if (!hasAction)
                        continue;

                    return new ScenarioOutcome(
                        rule.Id,
                        rule.Argument,
                        rule.ProfileId,
                        rule.MaskId,
                        rule.PowerSchemeId);
                }
            }

            return null;
        }

        /// <summary>方案名；查不到就退回 Id —— 日志里写「（已删除）」会被读成别的问题。</summary>
        private string Describe(string profileId)
            => _plans.Find(PlanKind.Tuning, profileId)?.Name ?? profileId;

        /// <summary>
        /// 把「此刻成立的触发」写成一句人话，只服务于日志。
        ///
        /// 「它为什么自己把方案切了」是这个功能最难自查的问题：日志里只有一句
        /// 「已应用方案 X」，用户对着它没法判断是哪条规则命中的。补上触发源，
        /// 排查时一眼就能对上。
        /// </summary>
        private string DescribeHit(IEnumerable<(ScenarioTrigger Trigger, string Argument)> active)
        {
            List<string> parts = new();
            foreach ((ScenarioTrigger trigger, string argument) in active)
            {
                parts.Add(trigger switch
                {
                    ScenarioTrigger.ProcessRunning =>
                        string.IsNullOrEmpty(argument) ? "进程" : $"进程 {argument}",
                    ScenarioTrigger.ProcessExited =>
                        string.IsNullOrEmpty(argument) ? "进程退出" : $"进程 {argument} 退出",
                    ScenarioTrigger.OnBattery => "电池供电",
                    _ => "交流供电"
                });

                // 进程能有几百个，全列出来会把一行日志撑成几 KB —— 而它只是背景信息。
                if (parts.Count >= 4)
                {
                    parts.Add("…");
                    break;
                }
            }

            return parts.Count == 0 ? "无任何触发" : string.Join("、", parts);
        }

        private static bool Matches(ScenarioRule rule,
                                    HashSet<(ScenarioTrigger Trigger, string Argument)> active)
            => active.Contains((rule.Trigger, rule.Argument));

        /// <summary>
        /// 与原来的 <c>string.Equals(..., OrdinalIgnoreCase)</c> 完全同口径，
        /// 包括 null 与空串不相等 —— 所以这里不做 null 规整。
        /// </summary>
        private sealed class ActiveTriggerComparer : IEqualityComparer<(ScenarioTrigger Trigger, string Argument)>
        {
            public static readonly ActiveTriggerComparer Instance = new();

            public bool Equals((ScenarioTrigger Trigger, string Argument) x,
                               (ScenarioTrigger Trigger, string Argument) y)
                => x.Trigger == y.Trigger &&
                   string.Equals(x.Argument, y.Argument, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((ScenarioTrigger Trigger, string Argument) obj)
                => HashCode.Combine(
                    (int)obj.Trigger,
                    obj.Argument is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Argument));
        }

        private int IndexOfId(string id)
        {
            for (int i = 0; i < _rules.Count; i++)
                if (_rules[i].Id == id)
                    return i;
            return -1;
        }

        private void Persist()
        {
            try
            {
                JsonFile.Write(FilePath, new ScenarioFile
                {
                    Enabled = Enabled,
                    Rules = _rules.ToList()
                }, AppJsonContext.Default.ScenarioFile);
            }
            catch (Exception ex)
            {
                _log.Error($"保存场景规则失败: {ex.Message}");
            }
        }
    }
}
