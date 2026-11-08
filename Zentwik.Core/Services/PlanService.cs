using System;
using System.Collections.Generic;
using System.Linq;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 方案的三块。三者各有各的列表、各有各的「当前」，互不影响。
    /// </summary>
    /// <remarks>
    /// 【为什么按块拆，而不是一份方案包三块】
    /// 用户的抱怨是「改一个方案，三页全跟着变」。合并式快照听起来整齐，
    /// 实际语义是「切调校方案也顺手把调度覆盖了」——用户只想换负压，
    /// 结果进程页的绑核全被换掉。三块拆开之后，每一块的「应用」只写自己那一份 store，
    /// 谁都不会顺手改别人。
    /// </remarks>
    public enum PlanKind
    {
        /// <summary>调校方案：逐核 CO + FMax。</summary>
        Tuning = 0,

        /// <summary>调度方案：一组进程绑核规则。</summary>
        Scheduling = 1,

        /// <summary>电源方案：指向一个 Windows 电源方案的 GUID。</summary>
        Power = 2
    }

    /// <summary>
    /// 一份方案。三种内容共用这一个形状，靠 <see cref="Kind"/> 区分用哪几块。
    /// </summary>
    /// <remarks>
    /// 【为什么不定义三个类】
    /// 三种方案在存储、列表、改名、删除上的行为完全一致，唯一的差别是内容字段。
    /// 拆成三个类会让存储与 IPC 各写三遍，且以后加第四种又要复制一遍。
    /// 反序列化上是平铺的 —— 一份电源方案里 <c>CurveOptimizer</c> 是空数组，不占什么空间。
    /// </remarks>
    public sealed class PlanEntry
    {
        /// <summary>32 位无横线 GUID。改名不改 Id。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        public PlanKind Kind { get; set; }

        public string UpdatedAt { get; set; } = DateTime.Now.ToString("o");

        /// <summary>逐核 CO。仅 <see cref="PlanKind.Tuning"/> 使用。</summary>
        public int[] CurveOptimizer { get; set; } = Array.Empty<int>();

        /// <summary>FMax（MHz），0 = 不碰。仅 <see cref="PlanKind.Tuning"/> 使用。</summary>
        public uint FMaxMHz { get; set; }

        /// <summary>绑核规则快照。仅 <see cref="PlanKind.Scheduling"/> 使用。</summary>
        public List<ScheduleRule> ScheduleRules { get; set; } = new();

        /// <summary>Windows 电源方案 GUID。仅 <see cref="PlanKind.Power"/> 使用。</summary>
        public string PowerSchemeGuid { get; set; } = string.Empty;

        /// <summary>
        /// 该电源方案存入时的系统名。只用于界面上显示「已失效」（方案被系统删了）。
        /// 应用时以 GUID 为准，名字变了不算失效。
        /// </summary>
        public string PowerSchemeName { get; set; } = string.Empty;

        /// <summary>当前生效标记。由服务统一刷新，不落盘。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsActive { get; set; }
    }

    /// <summary><c>plans.json</c> 的根对象。</summary>
    public sealed class PlanFile
    {
        /// <summary>三类方案平铺在这里，按 <see cref="PlanEntry.Kind"/> 分拣。</summary>
        public List<PlanEntry> Plans { get; set; } = new();

        /// <summary>三类各自的生效方案 Id。各自独立，互不覆盖。</summary>
        public string? ActiveTuningId { get; set; }

        public string? ActiveSchedulingId { get; set; }

        public string? ActivePowerId { get; set; }
    }

    /// <summary>
    /// 三类方案的保存、切换、删除。
    /// </summary>
    /// <remarks>
    /// 【应用只写自己那一份 store】
    /// 合并式方案切换时会把调校与调度一起写回，导致「换个负压，绑核被清空」。
    /// 这里按 <see cref="PlanKind"/> 分流，调校只碰 TuningStore、调度只碰 SchedulingStore、
    /// 电源只调 <c>PowerSetActiveScheme</c>。谁都不会顺手改别人。
    ///
    /// 【硬件动作用现成的服务】
    /// 调校交给 <see cref="TuningApplyService"/>（已封装「写 SMU + 回读校验」），
    /// 调度交给后台的 ProcessWatcher 重扫，电源走 <see cref="IPowerPolicyApi"/>。
    /// 本类不直接 P/Invoke，避免长出第二套行为。
    /// </remarks>
    public class PlanService
    {
        private readonly ILogService _log;
        private readonly ITuningStore _tuning;
        private readonly ISchedulingStore _scheduling;
        private readonly TuningApplyService _tuningApply;
        private readonly IProcessWatcher _watcher;
        private readonly IAppPaths _paths;
        private readonly IPowerPolicyApi _power;

        public PlanService(
            ILogService log,
            ITuningStore tuning,
            ISchedulingStore scheduling,
            TuningApplyService tuningApply,
            IProcessWatcher watcher,
            IAppPaths paths,
            IPowerPolicyApi power)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(tuning);
            _tuning = tuning;
            ArgumentNullException.ThrowIfNull(scheduling);
            _scheduling = scheduling;
            ArgumentNullException.ThrowIfNull(tuningApply);
            _tuningApply = tuningApply;
            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
            ArgumentNullException.ThrowIfNull(power);
            _power = power;
        }

        private string FilePath => _paths.File("plans.json");

        private string LegacyPath => _paths.File("profiles.json");

        private readonly object Sync = new();

        private readonly List<PlanEntry> _items = new();

        /// <summary>三类方案各自的只读快照。</summary>
        /// <remarks>
        /// 返回副本：应用动作可能由场景服务在别的线程触发，而消费方在遍历列表。
        /// 方案数量是个位数，复制成本可以忽略。
        /// </remarks>
        public IReadOnlyList<PlanEntry> Items(PlanKind kind)
        {
            lock (Sync)
                return _items.Where(p => p.Kind == kind).ToArray();
        }

        /// <summary>方案集合变化（增删改、切换生效）时触发，在触发线程上同步调用。</summary>
        public event Action? Changed;

        public string? ActiveId(PlanKind kind)
        {
            lock (Sync)
                return ActiveIdCore(kind);
        }

        private string? ActiveIdCore(PlanKind kind) => kind switch
        {
            PlanKind.Tuning => _activeTuningId,
            PlanKind.Scheduling => _activeSchedulingId,
            _ => _activePowerId
        };

        private string? _activeTuningId;
        private string? _activeSchedulingId;
        private string? _activePowerId;

        private void SetActiveIdCore(PlanKind kind, string? id)
        {
            switch (kind)
            {
                case PlanKind.Tuning:
                    _activeTuningId = id;
                    break;
                case PlanKind.Scheduling:
                    _activeSchedulingId = id;
                    break;
                default:
                    _activePowerId = id;
                    break;
            }
        }

        /// <summary>程序启动、或 store 被别处改动后重新读盘。</summary>
        public void Load()
        {
            lock (Sync)
            {
                PlanFile? file = null;

                try
                {
                    file = JsonFile.Read(FilePath, AppJsonContext.Default.PlanFile);
                }
                catch (Exception ex)
                {
                    _log.Error($"读取方案列表失败: {ex.Message}");
                }

                if (file is null)
                {
                    file = MigrateLegacy();
                }

                _items.Clear();
                _items.AddRange(file.Plans ?? new List<PlanEntry>());
                _activeTuningId = file.ActiveTuningId;
                _activeSchedulingId = file.ActiveSchedulingId;
                _activePowerId = file.ActivePowerId;

                // 旧迁移、用户删方案、或外部改过 plans.json，都可能留下指向已不存在
                // 方案的生效 Id。它不会自己消失：删同类方案时按 Id 相等才清标记，
                // 悬空值永远匹配不上，界面上就一直「没有生效方案」。
                if (PruneDanglingActiveIds())
                {
                    try
                    {
                        Persist();
                    }
                    catch (Exception ex)
                    {
                        // 自愈失败不该拖垮启动：内存里已经清干净，下次启动会再试。
                        _log.Warn($"清理悬空生效方案失败: {ex.Message}");
                    }
                }

                RefreshActiveMarks();
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// 老 <c>profiles.json</c>（一份方案包三块）拆成三类独立方案。
        /// </summary>
        /// <remarks>
        /// 【为什么一定要迁移，而不是「没有就重新开始」】
        /// 老文件里存着用户的调校值 —— 那是整晚跑稳定性测试试出来的数字。
        /// 静默丢弃等于让用户重调一遍，而他根本不知道发生了什么。
        ///
        /// 【拆法】
        /// 一份合并方案 → 一条调校方案（若它带 CO / FMax）+ 一条调度方案（若它带规则）。
        /// 两边沿用旧 Id：三类列表彼此独立，Id 相同不会冲突。
        /// 老文件改名成 <c>profiles.json.bak</c>，出问题还能翻回去。
        /// </remarks>
        private PlanFile MigrateLegacy()
        {
            var result = new PlanFile();

            LegacyProfileFile? legacy = null;

            try
            {
                if (System.IO.File.Exists(LegacyPath))
                    legacy = JsonFile.Read(LegacyPath, AppJsonContext.Default.LegacyProfileFile);
            }
            catch (Exception ex)
            {
                _log.Error($"读取旧方案文件失败: {ex.Message}");
            }

            if (legacy is not null)
            {
                foreach (LegacyProfileSnapshot item in legacy.Profiles ?? new List<LegacyProfileSnapshot>())
                {
                    string name = string.IsNullOrWhiteSpace(item.Name) ? "未命名" : item.Name;

                    bool hasTuning = (item.CurveOptimizer?.Length ?? 0) > 0 || item.FMaxMHz > 0;
                    if (hasTuning)
                    {
                        result.Plans.Add(new PlanEntry
                        {
                            Id = item.Id,
                            Kind = PlanKind.Tuning,
                            Name = name,
                            CurveOptimizer = item.CurveOptimizer ?? Array.Empty<int>(),
                            FMaxMHz = item.FMaxMHz,
                        });
                    }

                    if ((item.ScheduleRules?.Count ?? 0) > 0)
                    {
                        // 同一份合并方案已经用掉这个 Id，调度那条再加个后缀区分。
                        result.Plans.Add(new PlanEntry
                        {
                            Id = hasTuning ? item.Id + "-sched" : item.Id,
                            Kind = PlanKind.Scheduling,
                            Name = name,
                            ScheduleRules = item.ScheduleRules!,
                        });
                    }
                }

                // 一份合并方案拆成调校 + 调度两条时，调度那条的 Id 带 -sched 后缀。
                // 两类都写旧 Id 会让调度侧的生效标记指向一个根本不存在的方案，
                // 之后用户删掉调度方案，「Id 相等才清标记」的判断也永远不成立。
                if (!string.IsNullOrEmpty(legacy.ActiveId))
                {
                    string legacyId = legacy.ActiveId;

                    result.ActiveTuningId = result.Plans.Any(
                        p => p.Kind == PlanKind.Tuning && p.Id == legacyId) ? legacyId : null;

                    result.ActiveSchedulingId = result.Plans.FirstOrDefault(
                        p => p.Kind == PlanKind.Scheduling
                          && (p.Id == legacyId || p.Id == legacyId + "-sched"))?.Id;
                }
            }

            // 三类列表都不能是空的：空列表意味着用户进页面连「新建」之外的落点都没有，
            // 而「新建」从空开始也得先有名字。给一份以当前状态为内容的默认方案。
            if (!result.Plans.Any(p => p.Kind == PlanKind.Tuning))
            {
                TuningProfile tuning = _tuning.Profile;
                result.Plans.Add(new PlanEntry
                {
                    Kind = PlanKind.Tuning,
                    Name = "默认",
                    CurveOptimizer = (int[])tuning.CurveOptimizer.Clone(),
                    FMaxMHz = tuning.FMaxMHz,
                });
            }

            if (!result.Plans.Any(p => p.Kind == PlanKind.Scheduling))
            {
                result.Plans.Add(new PlanEntry
                {
                    Kind = PlanKind.Scheduling,
                    Name = "默认",
                    ScheduleRules = _scheduling.Profile.Rules.Select(Clone).ToList(),
                });
            }

            if (!result.Plans.Any(p => p.Kind == PlanKind.Power))
            {
                // 电源方案必须指向一个真实存在的系统方案，否则「应用」必然失败。
                // 拿不到活动方案时留空 GUID —— 界面上显示「未绑定」，用户自己挑一个。
                Guid? active = null;

                try
                {
                    active = _power.GetActiveScheme();
                }
                catch (Exception ex)
                {
                    _log.Warn($"迁移方案时读取当前电源方案失败: {ex.Message}");
                }

                result.Plans.Add(new PlanEntry
                {
                    Kind = PlanKind.Power,
                    Name = active is null ? "默认" : NameOf(active.Value),
                    PowerSchemeGuid = active?.ToString("D") ?? string.Empty,
                    PowerSchemeName = active is null ? string.Empty : NameOf(active.Value),
                });
            }

            // 兜底写在要落盘的 result 上（不是三个字段）：迁移的产物就是这份
            // 文件，写字段会在下面的 Load 里被 file.ActiveXxxId 重新覆盖掉。
            result.ActiveTuningId ??= FirstId(result, PlanKind.Tuning);
            result.ActiveSchedulingId ??= FirstId(result, PlanKind.Scheduling);
            result.ActivePowerId ??= FirstId(result, PlanKind.Power);

            try
            {
                JsonFile.Write(FilePath, result, AppJsonContext.Default.PlanFile);

                if (System.IO.File.Exists(LegacyPath))
                    System.IO.File.Move(LegacyPath, LegacyPath + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                // 迁移失败不影响本次运行（内存里有数据），但下次启动会再迁一次。
                _log.Error($"写入方案文件失败: {ex.Message}");
            }

            return result;
        }

        private static string? FirstId(PlanFile file, PlanKind kind)
            => file.Plans.FirstOrDefault(p => p.Kind == kind)?.Id;

        private string NameOf(Guid guid)
        {
            try
            {
                return _power.ReadFriendlyName(guid) ?? guid.ToString("D");
            }
            catch (Exception)
            {
                return guid.ToString("D");
            }
        }

        /// <summary>
        /// 清掉指向不存在方案的生效 Id，返回是否真的改动过。
        /// 三类 Id 各自独立命名空间，校验时必须连 Kind 一起比。
        /// </summary>
        private bool PruneDanglingActiveIds()
        {
            bool changed = false;

            foreach (PlanKind kind in new[] { PlanKind.Tuning, PlanKind.Scheduling, PlanKind.Power })
            {
                string? id = ActiveIdCore(kind);

                if (id is null || _items.Any(p => p.Kind == kind && p.Id == id))
                    continue;

                SetActiveIdCore(kind, null);
                changed = true;
            }

            return changed;
        }

        private void RefreshActiveMarks()
        {
            foreach (PlanEntry entry in _items)
                entry.IsActive = entry.Id == ActiveIdCore(entry.Kind);
        }

        /// <summary>按 Id 查方案。Kind 不匹配则视为不存在 —— 三类 Id 各自独立命名空间。</summary>
        public PlanEntry? Find(PlanKind kind, string id)
        {
            lock (Sync)
                return _items.FirstOrDefault(p => p.Kind == kind && p.Id == id);
        }

        /// <summary>同名检查。新建与改名都要过这一道。</summary>
        public bool NameExists(PlanKind kind, string name, string? exceptId = null)
        {
            lock (Sync)
                return _items.Any(p => p.Kind == kind
                                       && !string.Equals(p.Id, exceptId, StringComparison.Ordinal)
                                       && string.Equals(p.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>保存（新增或按 Id 覆盖）一份方案。不触发应用。</summary>
        public void Save(PlanEntry entry)
        {
            entry.UpdatedAt = DateTime.Now.ToString("o");

            lock (Sync)
            {
                int index = _items.FindIndex(p => p.Kind == entry.Kind && p.Id == entry.Id);
                if (index >= 0)
                    _items[index] = entry;
                else
                    _items.Add(entry);

                Persist();
            }

            _log.Info($"方案已保存: [{entry.Kind}] {entry.Name}");
            Changed?.Invoke();
        }

        /// <summary>删除一份方案。删的正好是生效中方案时只清标记，不回滚硬件。</summary>
        public bool Delete(PlanKind kind, string id)
        {
            lock (Sync)
            {
                PlanEntry? target = _items.FirstOrDefault(p => p.Kind == kind && p.Id == id);
                if (target == null)
                    return false;

                _items.Remove(target);

                // 刻意不回滚硬件：删掉方案只是不再留这张卡片，
                // 用户没说「把机器恢复原样」—— 那是「清除全部数据」的职责。
                // 统一走自愈：被删的这份正好生效时清标记，别的类别若还留着
                // 历史悬空值也一并清掉，不留半份脏数据。
                PruneDanglingActiveIds();
                RefreshActiveMarks();

                Persist();
            }

            Changed?.Invoke();
            return true;
        }

        /// <summary>改名。改名不改 Id —— 场景规则引用的是 Id。</summary>
        public bool Rename(PlanKind kind, string id, string newName, out string message)
        {
            lock (Sync)
            {
                PlanEntry? target = _items.FirstOrDefault(p => p.Kind == kind && p.Id == id);
                if (target is null)
                {
                    message = $"方案不存在：{id}";
                    return false;
                }

                if (NameExists(kind, newName, id))
                {
                    message = $"已存在同名方案：{newName}";
                    return false;
                }

                target.Name = newName;
                target.UpdatedAt = DateTime.Now.ToString("o");
                Persist();
                message = $"已重命名为「{newName}」";
            }

            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// 应用一份方案：只写它自己那一块，不碰另外两块。
        /// </summary>
        /// <remarks>
        /// 【为什么调度方案应用时要重扫】
        /// WMI 事件只覆盖之后启动的进程。不补这一下，用户看到的是
        /// 「规则写了，但那个游戏还绑在旧核心上」—— 要等下次重启才生效。
        /// </remarks>
        public PlanApplyResult Apply(PlanKind kind, string id, bool ignoreStartupGate = false)
        {
            PlanEntry? entry = Find(kind, id);
            if (entry is null)
                return new PlanApplyResult(false, $"方案不存在：{id}", "failed");

            string status = "applied";

            switch (kind)
            {
                case PlanKind.Tuning:
                {
                    // FMax 基线不进方案：它是这台机器的出厂值，属于硬件属性。
                    _tuning.Save(new TuningProfile
                    {
                        CurveOptimizer = (int[])entry.CurveOptimizer.Clone(),
                        FMaxMHz = entry.FMaxMHz,
                        FMaxBaselineMHz = _tuning.Profile.FMaxBaselineMHz
                    });

                    TuningApplyOutcome? outcome = _tuningApply.ApplyFromStore(ignoreStartupGate);

                    if (outcome is not null && !outcome.Success)
                    {
                        return new PlanApplyResult(
                            false,
                            outcome.Summary,
                            outcome.Skipped ? "skipped" : "failed");
                    }

                    if (outcome is not null && outcome.Skipped)
                        status = "skipped";

                    break;
                }

                case PlanKind.Scheduling:
                {
                    _scheduling.Save(entry.ScheduleRules.Select(Clone));
                    _watcher.ReapplyNow();
                    break;
                }

                default:
                {
                    if (!Guid.TryParse(entry.PowerSchemeGuid, out Guid guid))
                        return new PlanApplyResult(false, "这份方案还没有绑定电源方案", "failed");

                    bool exists = false;

                    try
                    {
                        exists = _power.EnumerateSchemes().Any(s => s.Guid == guid);
                    }
                    catch (Exception ex)
                    {
                        return new PlanApplyResult(false, $"读取电源方案失败：{ex.Message}", "failed");
                    }

                    if (!exists)
                        return new PlanApplyResult(false, "这个电源方案已被系统删除，请重新指定", "failed");

                    if (!_power.SetActiveScheme(guid))
                        return new PlanApplyResult(false, "电源方案切换失败，请重启系统后重试", "failed");

                    break;
                }
            }

            lock (Sync)
            {
                SetActiveIdCore(kind, id);
                RefreshActiveMarks();
                Persist();
            }

            _log.Info($"已应用方案: [{kind}] {entry.Name}");
            Changed?.Invoke();
            return new PlanApplyResult(true, $"已应用「{entry.Name}」", status);
        }

        /// <summary>只记录生效标记，不动硬件。用于「外部已经把状态改了，界面同步一下」。</summary>
        public void MarkActive(PlanKind kind, string? id)
        {
            lock (Sync)
            {
                SetActiveIdCore(kind, id);
                RefreshActiveMarks();
                Persist();
            }

            Changed?.Invoke();
        }

        /// <summary>把当前的实际状态抓成一份新方案的内容（不落盘，由调用方 Save）。</summary>
        public PlanEntry CaptureCurrent(PlanKind kind, string name)
        {
            switch (kind)
            {
                case PlanKind.Tuning:
                {
                    TuningProfile tuning = _tuning.Profile;
                    return new PlanEntry
                    {
                        Kind = kind,
                        Name = name,
                        CurveOptimizer = (int[])tuning.CurveOptimizer.Clone(),
                        FMaxMHz = tuning.FMaxMHz,
                    };
                }

                case PlanKind.Scheduling:
                    return new PlanEntry
                    {
                        Kind = kind,
                        Name = name,
                        ScheduleRules = _scheduling.Profile.Rules.Select(Clone).ToList(),
                    };

                default:
                    return new PlanEntry
                    {
                        Kind = kind,
                        Name = name,
                    };
            }
        }

        private void Persist()
        {
            try
            {
                JsonFile.Write(FilePath, new PlanFile
                {
                    Plans = _items.ToList(),
                    ActiveTuningId = _activeTuningId,
                    ActiveSchedulingId = _activeSchedulingId,
                    ActivePowerId = _activePowerId
                }, AppJsonContext.Default.PlanFile);
            }
            catch (Exception ex)
            {
                // 不吞：写盘失败必须让调用方知道，否则界面会显示「已保存」而盘上没有。
                throw new InvalidOperationException($"保存方案列表失败: {ex.Message}", ex);
            }
        }

        /// <summary>规则浅拷贝：方案里的规则必须与当前规则集是两批对象。</summary>
        private static ScheduleRule Clone(ScheduleRule rule) => new()
        {
            Name = rule.Name,
            Target = rule.Target,
            MaskId = rule.MaskId,
            TargetMask = rule.TargetMask,
            Path = rule.Path,
            Priority = rule.Priority,
            Enabled = rule.Enabled
        };
    }

    /// <summary>应用一份方案的结果。</summary>
    /// <remarks>
    /// <c>Status</c> 与 <c>apply-tuning</c> 的三个取值（applied / skipped / failed）对齐：
    /// 调校页靠它区分「写进去了」与「驱动没就绪跳过了」，界面上是两种不同的提示。
    /// 调度与电源没有「跳过」这一态，成功即 applied。
    /// </remarks>
    public readonly record struct PlanApplyResult(bool Success, string Message, string Status = "applied");

    // ==================== 旧格式（只用于迁移） ====================

    /// <summary>老 <c>profiles.json</c> 的根对象。只读，用于拆分成三类方案。</summary>
    public sealed class LegacyProfileFile
    {
        public List<LegacyProfileSnapshot>? Profiles { get; set; }

        public string? ActiveId { get; set; }
    }

    /// <summary>老的合并式方案：调校 + 调度塞在一份里。</summary>
    public sealed class LegacyProfileSnapshot
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;

        public string CreatedAt { get; set; } = string.Empty;

        public string UpdatedAt { get; set; } = string.Empty;

        public int[]? CurveOptimizer { get; set; }

        public uint FMaxMHz { get; set; }

        public List<ScheduleRule>? ScheduleRules { get; set; }
    }
}
