using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Zentwik.Core.Services;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Helpers
{
    /// <summary>进程调度目标。</summary>
    public enum SchedulingTarget
    {
        None = 0,

        /// <summary>第一个 CCD（CCD0）。</summary>
        CCD0 = 1,

        /// <summary>第二个 CCD（CCD1）。</summary>
        CCD1 = 2,

        /// <summary>
        /// 自定义掩码：具体逻辑核集合由 <see cref="ScheduleRule.MaskId"/> 指向的掩码给出。
        ///
        /// 与 <see cref="ScheduleRule.TargetMask"/>（快照值）同名同义：
        /// 一个是「目标是掩码这一类」，一个是「那个掩码的值是多少」。
        /// 旧名 Custom 说的是「自定义」，但真正的区分点是「有没有掩码」。
        /// </summary>
        TargetMask = 3
    }

    /// <summary>
    /// 单条调度规则：进程名 → 目标 CCD / 自定义掩码。
    /// 规则按「进程名」（如 msedge.exe）记录，不再记录具体 PID 或路径，
    /// 这样同一程序的所有实例（PID）都会自动命中。
    /// </summary>
    public sealed class ScheduleRule
    {
        /// <summary>
        /// 规则 Id（32 位无横线 GUID）。删除与更新时的**唯一句柄**。
        ///
        /// 规则的业务键一直是进程名（同名覆盖、同名唯一），删除也只传名字。
        /// 这在「一个进程一条规则」的前提下够用，但界面一旦要支持
        /// 「选中某一行、编辑、再删掉它」，名字就不够用了：
        /// 用户在编辑框里改了进程名，界面上那行的身份就跟着变了 ——
        /// 按名字删会删不到（已改名），按名字改会多出一条（新旧名字都在）。
        /// Id 把「这条记录是谁」与「它调度哪个进程」分开。
        ///
        /// schedules.json 是新文件名，不存在「老文件没有 id 键」的历史包袱；
        /// 本工程已裁定不做任何数据迁移，因此 Id 只有一条产生路径：
        /// 对象构造时生成，随下一次 Save 落盘。
        ///
        /// schedules.json 会被多台机器 / 多次重装共享（用户直接拷贝配置目录）。
        /// 自增序号在那种场景下必然撞号，而撞号的后果是删错规则。
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>进程名（含 .exe，如 "msedge.exe"）。</summary>
        public string Name { get; set; } = string.Empty;

        public SchedulingTarget Target { get; set; } = SchedulingTarget.None;

        /// <summary>自定义掩码引用（Target = TargetMask 时有效），指向 <see cref="MaskStore"/> 中的 Id。</summary>
        public string MaskId { get; set; } = string.Empty;

        /// <summary>
        /// 自定义掩码的快照值（Target = TargetMask 时有效）。
        /// 用于掩码被删除后仍可按快照下发，提高后台监听的健壮性。
        /// </summary>
        public ulong TargetMask { get; set; }

        /// <summary>
        /// 规则来源路径（仅用于排障与界面回显）。
        ///
        /// 规则本身按进程名匹配，路径不参与任何判断 —— 同一程序的多个安装位置
        /// 应该共用同一条规则，否则用户换个目录就要重配一次。
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 掩码名称快照。掩码被删除或改名后，旧规则仍能显示当初绑的区域，
        /// 且模型不必反查 <see cref="IMaskStore"/> —— 反查会让 POCO 依赖服务，
        /// 序列化与 AOT 裁剪都会跟着变复杂。
        /// </summary>
        public string MaskName { get; set; } = string.Empty;

        /// <summary>
        /// 随规则一起保存的优先级档位（High / Above Normal / Normal / Below Normal / Low）。
        ///
        /// 空字符串表示「这条规则不碰优先级」，只有掩码调度 —— 旧配置文件没有这个字段，
        /// 反序列化后就是空串，行为与升级前完全一致。
        ///
        /// 为什么优先级要跟着规则存：只存掩码的话，用户把游戏设成 High + CCD0，
        /// 下次游戏启动时 ProcessWatcher 只恢复掩码，优先级又变回 Normal，
        /// 用户会以为「设置没保存」。跟着规则存才能让「一条规则 = 一套完整调度」成立。
        /// </summary>
        public string Priority { get; set; } = string.Empty;

        /// <summary>
        /// 该规则是否启用。关闭后记录仍然保留，但后台监听不会自动调度，
        /// 适合「先存着、暂时不想生效」的场景。默认启用（旧配置文件缺此字段时也为启用）。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>规则的展示名（如 "CCD0"、"纯大核"、"0-7,16-23"）。</summary>
        public string Label
        {
            get
            {
                if (Target == SchedulingTarget.CCD0) return "CCD0";
                if (Target == SchedulingTarget.CCD1) return "CCD1";
                if (Target == SchedulingTarget.TargetMask)
                {
                    return string.IsNullOrEmpty(MaskName) ? MaskFormatter.ToRangeText(TargetMask) : MaskName;
                }
                return "不调度";
            }
        }
    }

    /// <summary>schedules.json 的根对象。</summary>
    public sealed class SchedulingProfile
    {
        public List<ScheduleRule> Rules { get; set; } = new();
    }

    /// <summary>
    /// 调度规则持久化：保存到 %APPDATA%\Zentwik\schedules.json。
    /// 键为进程名，值为目标 CCD 或自定义掩码引用。
    ///
    /// 掩码名称的快照（<see cref="ScheduleRule.MaskName"/>）在 save-scheduling-rule
    /// 时由 Worker 一并写入，读盘时不需要反查掩码表。旧实现在这里反查，
    /// 只是为了让「掩码改名后旧规则显示新名字」—— 而那恰好与快照的设计意图相反
    /// （快照存在的意义就是掩码删改后规则仍能显示当初绑的区域）。
    /// </summary>
    public class SchedulingStore : ISchedulingStore
    {
        private readonly ILogService _log;
        private readonly IAppPaths _paths;

        public SchedulingStore(ILogService log, IAppPaths paths)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
        }

        private string FilePath => _paths.File("schedules.json");

        /// <summary>
        /// 保护 Profile 引用与磁盘读写。
        /// 后台监听线程每轮 ScanOnce 都会 Reload()，而 UI 线程保存后调 Save()：
        /// 两者并发时，后完成的一方会把对方的改动整个覆盖掉（Save 用界面旧快照
        /// 覆盖磁盘新内容，或 Reload 把刚保存的 Profile 换成磁盘旧内容）。
        /// </summary>
        private readonly object _sync = new();

        private SchedulingProfile _profile = new();

        /// <summary>
        /// 当前规则集。这里返回的是**快照引用**而非拷贝：
        /// 读取方通常只做 Where/ToList 遍历，拷贝整份列表在每秒一次的轮询里没有必要。
        /// </summary>
        public SchedulingProfile Profile
        {
            get { lock (_sync) return _profile; }
        }

        public void Load()
        {
            lock (_sync)
            {
                try
                {
                    _profile = ReadFromDisk() ?? new SchedulingProfile();
                }
                catch
                {
                    _profile = new SchedulingProfile();
                }
            }
        }

        /// <summary>
        /// 读盘。文件不存在或反序列化出 null 时返回 null，
        /// 让调用方自己决定「落成空档」还是「保持现状」。
        ///
        /// **不做任何旧格式兼容**：规则文件从 schedules.json 换成了 schedules.json，
        /// 旧文件不读、不迁移、不删除（留在原地由用户自己处置）。
        /// </summary>
        private SchedulingProfile? ReadFromDisk()
            => JsonFile.Read(FilePath, AppJsonContext.Default.SchedulingProfile);

        /// <summary>用给定规则集合覆盖并落盘（原子替换）。</summary>
        public void Save(IEnumerable<ScheduleRule> rules)
        {
            SchedulingProfile profile = new() { Rules = new List<ScheduleRule>(rules) };
            lock (_sync)
            {
                try
                {
                    JsonFile.Write(FilePath, profile, AppJsonContext.Default.SchedulingProfile);
                    _profile = profile;
                }
                catch (Exception ex)
                {
                    _log.Error($"保存调度规则失败: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 恢复出厂：清空全部调度规则。
        ///
        /// 只清规则，不动进程页的掩码 —— 两者是独立概念，
        /// 一个管「哪个进程绑到哪」，一个管「绑的区域长什么样」。
        /// 掩码由 <see cref="MaskStore.ResetToFactory"/> 单独清。
        /// </summary>
        public void ResetToFactory()
        {
            if (Profile.Rules.Count == 0)
                return;

            Save(Array.Empty<ScheduleRule>());
            _log.Info("全局重置：调度规则已清空");
        }

        /// <summary>
        /// 从磁盘重新读取规则（供后台监听在每轮扫描前调用，让界面改动立即生效）。
        /// 读取失败或文件不存在时保留现有内存规则。
        /// </summary>
        public void Reload()
        {
            lock (_sync)
            {
                try
                {
                    SchedulingProfile? loaded = ReadFromDisk();
                    if (loaded != null)
                        _profile = loaded;
                }
                catch
                {
                    // 读取失败：保持当前内存中的规则不变
                }
            }
        }

        /// <summary>
        /// 是否存在「会实际生效」的调度规则。
        ///
        /// 用于判断后台监听值不值得常驻：规则全被关掉、目标为 None、或进程名为空时，
        /// 监听起来也只是空转，白占一份常驻内存。后台常驻的唯一价值是持续调度，
        /// 没有可调度的规则，就没有常驻的理由。
        /// </summary>
        public bool HasEnabledRules => Profile.Rules.Any(IsEffective);

        /// <summary>根据进程名查询调度目标（无规则返回 null）。</summary>
        public ScheduleRule? LookupByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string normalized = name.Trim().ToLowerInvariant();
            return Profile.Rules.FirstOrDefault(rule =>
                !string.IsNullOrWhiteSpace(rule.Name) &&
                rule.Name.Trim().ToLowerInvariant() == normalized);
        }

        /// <summary>规则是否真会生效：启用、目标不是 None、进程名不为空，缺一不可。</summary>
        private static bool IsEffective(ScheduleRule rule) =>
            rule.Enabled &&
            rule.Target != SchedulingTarget.None &&
            !string.IsNullOrWhiteSpace(rule.Name);
    }
}
