using System;
using System.Collections.Generic;
using Zentwik.Core.Services;

namespace Zentwik.Tests.Fakes
{
    /// <summary>
    /// <see cref="IPowerPolicyApi"/> 的可编程替身。
    ///
    /// 写入之后紧接着读一次快照，是 apply 的正常路径（返回写入后的结果，省掉一次查询）。
    /// 一个不记忆写入值的假实现会让这条路径读到旧值，测试就只能断言
    /// 「写调用发生过」，而没法断言「读回来的就是刚写进去的」——
    /// 后者才是用户真正关心的事。
    ///
    /// 只记 <see cref="SetActiveScheme"/> 收到的方案 GUID。它是「重新激活」这一步的唯一痕迹：
    /// 一次批量写入无论改了几项，都应该只激活一次 —— 每项激活一次会把
    /// 电源管理器反复拉起，在低端机上表现为应用设置时整机卡顿几秒。
    /// 用 Count 就能钉住这个性质，不必再数别的调用。
    /// </summary>
    public sealed class FakePowerPolicyApi : IPowerPolicyApi
    {
        /// <summary>「平衡」方案，与真实系统上的默认方案同一个 GUID。</summary>
        public static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

        /// <summary>当前活动方案。置 null 用来模拟「取不到方案」的降级路径。</summary>
        public Guid? ActiveScheme { get; set; } = BalancedScheme;

        /// <summary>
        /// 前 N 次 <see cref="GetActiveScheme"/> 正常返回，之后返回 null。
        ///
        /// 用来模拟「写入成功、但重读时方案已经不可用」——
        /// 这条路径决定失败响应里的 Payload 要不要兜底成 null。
        /// 默认值足够大，等价于「一直可用」。
        /// </summary>
        public int SchemeAvailableCalls { get; set; } = int.MaxValue;

        /// <summary>方案显示名。置 null 用来模拟「读得到方案、读不到名字」。</summary>
        public string? SchemeName { get; set; } = "平衡";

        /// <summary><see cref="SetActiveScheme"/> 的返回值。false 模拟激活失败。</summary>
        public bool SetActiveSchemeSucceeds { get; set; } = true;

        /// <summary>设置项 GUID → 当前 AC 值。没登记过的项读出来是 0（系统的默认值语义）。</summary>
        public Dictionary<Guid, uint> AcValues { get; } = new();

        /// <summary>设置项 GUID → 当前 DC 值。与 AC 分开存，模拟插电 / 电池两套值不同。</summary>
        public Dictionary<Guid, uint> DcValues { get; } = new();

        /// <summary>
        /// 本机是否有电池。默认 false（台式机），模拟笔记本时置 true。
        /// </summary>
        /// <remarks>
        /// 名字不能叫 <c>HasBattery</c>：接口里的同名方法是这个属性的取值入口，
        /// 撞名之后 <c>HasBattery()</c> 会解析到属性本身。
        /// </remarks>
        public bool HasBatteryHardware { get; set; }

        /// <summary>此刻是否在用交流电。null = 读不到。</summary>
        public bool? OnAcPower { get; set; } = true;

        /// <summary>
        /// 枚举出来的方案列表。默认只有「平衡」。
        /// </summary>
        public List<(Guid Guid, string? Name)> Schemes { get; } =
            new() { (BalancedScheme, "平衡") };

        /// <summary>本机不支持的设置项：读返回 87，写也返回 87。</summary>
        public HashSet<Guid> Unsupported { get; } = new();

        /// <summary><see cref="SetActiveScheme"/> 收到的方案 GUID 序列（一次激活一条）。</summary>
        public List<Guid> CalledGuids { get; } = new();

        /// <summary>成功写入的 (设置项, 值) 序列，用来钉住 C-State 的 1/0 映射。</summary>
        public List<(Guid Setting, uint Value)> Writes { get; } = new();

        /// <summary><see cref="GetActiveScheme"/> 被调用的次数。</summary>
        public int GetSchemeCalls { get; private set; }

        public Guid? GetActiveScheme()
        {
            GetSchemeCalls++;
            return GetSchemeCalls <= SchemeAvailableCalls ? ActiveScheme : null;
        }

        public bool SetActiveScheme(Guid schemeGuid)
        {
            CalledGuids.Add(schemeGuid);
            return SetActiveSchemeSucceeds;
        }

        public (uint Result, uint Value) ReadAcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting)
        {
            if (Unsupported.Contains(setting))
                return (87, 0);

            if (SchemeValues.TryGetValue((schemeGuid, setting), out (uint Ac, uint Dc) scoped))
                return (0, scoped.Ac);

            return (0, AcValues.TryGetValue(setting, out uint value) ? value : 0u);
        }

        public (uint Result, uint Value) ReadDcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting)
        {
            if (Unsupported.Contains(setting))
                return (87, 0);

            if (SchemeValues.TryGetValue((schemeGuid, setting), out (uint Ac, uint Dc) scoped))
                return (0, scoped.Dc);

            return (0, DcValues.TryGetValue(setting, out uint value) ? value : 0u);
        }

        public uint WriteAcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting, uint value)
        {
            if (Unsupported.Contains(setting))
                return 87;

            AcValues[setting] = value;

            (uint Ac, uint Dc) scoped = SchemeValues.TryGetValue((schemeGuid, setting), out (uint Ac, uint Dc) existing)
                ? existing
                : (0u, 0u);

            SchemeValues[(schemeGuid, setting)] = (value, scoped.Dc);

            Writes.Add((setting, value));
            return 0;
        }

        public uint WriteDcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting, uint value)
        {
            if (Unsupported.Contains(setting))
                return 87;

            DcValues[setting] = value;

            (uint Ac, uint Dc) scoped = SchemeValues.TryGetValue((schemeGuid, setting), out (uint Ac, uint Dc) existing)
                ? existing
                : (0u, 0u);

            SchemeValues[(schemeGuid, setting)] = (scoped.Ac, value);

            Writes.Add((setting, value));
            return 0;
        }

        public string? ReadFriendlyName(Guid schemeGuid) => SchemeName;

        public string? ReadSchemeDescription(Guid schemeGuid)
            => Descriptions.TryGetValue(schemeGuid, out string? description) ? description : null;

        public bool WriteSchemeDescription(Guid schemeGuid, string description)
        {
            if (!WriteDescriptionSucceeds)
                return false;

            Descriptions[schemeGuid] = description;
            return true;
        }

        /// <summary>下一次 <see cref="WriteSchemeDescription"/> 是否失败（模拟非提权进程写 HKLM）。</summary>
        public bool WriteDescriptionSucceeds { get; set; } = true;

        public IReadOnlyList<(Guid Guid, string? Name)> EnumerateSchemes() => Schemes;

        /// <summary>
        /// 子组 GUID → 其下的设置项 GUID，用来驱动「新建方案时把模板设置搬过去」。
        /// 为空等价于「这个方案一项设置都没有」。
        /// </summary>
        public Dictionary<Guid, List<Guid>> SettingTree { get; } = new();

        /// <summary>
        /// 按方案区分的设置树：方案 GUID → 子组 → 设置项。
        /// </summary>
        /// <remarks>
        /// 真实系统里每个方案的设置项是**各自一份**：卓越性能模板只写了硬盘与 PCIe 两项，
        /// 它的处理器子组根本不存在。共用一张全局树会让「模板 A 搬完再搬模板 B」
        /// 这条叠加逻辑没法验证 —— 后搬的那份会把前一份覆盖成默认值。
        /// 登记过的方案走这里，没登记的仍回落到 <see cref="SettingTree"/>。
        /// </remarks>
        public Dictionary<Guid, Dictionary<Guid, List<Guid>>> SchemeTrees { get; } = new();

        /// <summary>方案专属取值：方案 GUID → 设置项 GUID → (AC, DC)。</summary>
        public Dictionary<(Guid Scheme, Guid Setting), (uint Ac, uint Dc)> SchemeValues { get; } = new();

        /// <summary>方案 GUID → 说明文字。派生不会自动带过来，要 Service 显式搬。</summary>
        public Dictionary<Guid, string> Descriptions { get; } = new();

        public IReadOnlyList<Guid> EnumerateSubgroups(Guid schemeGuid)
        {
            if (SchemeTrees.TryGetValue(schemeGuid, out Dictionary<Guid, List<Guid>>? tree))
                return new List<Guid>(tree.Keys);

            return new List<Guid>(SettingTree.Keys);
        }

        public IReadOnlyList<Guid> EnumerateSettings(Guid schemeGuid, Guid subgroup)
        {
            Dictionary<Guid, List<Guid>> tree = SettingTree;

            if (SchemeTrees.TryGetValue(schemeGuid, out Dictionary<Guid, List<Guid>>? scoped))
                tree = scoped;

            return tree.TryGetValue(subgroup, out List<Guid>? settings)
                ? settings
                : Array.Empty<Guid>();
        }

        public (bool HasBattery, bool? OnAcPower) ReadPowerSupply()
            => (HasBatteryHardware, OnAcPower);

        // ---------------- 方案增删改 ----------------

        private int _duplicateCounter;

        /// <summary>下一次 <see cref="DuplicateScheme"/> 是否失败（模拟模板不存在或系统拒绝）。</summary>
        public bool DuplicateSchemeFails { get; set; }

        /// <summary>
        /// 模拟「从隐藏模板派生出来的方案不在枚举里」。
        ///
        /// 真实机制：节能 / 高性能 / 卓越性能这三个模板带隐藏标记，派生品会继承它，
        /// 只在被激活的那一刻临时可见，切到别的方案后又消失；从「平衡」派生则始终可见。
        /// 所以这里只对非平衡源生效 —— 替身跟着真实规则走，
        /// 才有资格验证「派生源必须是平衡」这条约束。
        /// </summary>
        public bool HideDuplicatedScheme { get; set; }

        /// <summary>下一次 <see cref="RenameScheme"/> 是否失败。</summary>
        public bool RenameSchemeSucceeds { get; set; } = true;

        /// <summary>下一次 <see cref="DeleteScheme"/> 是否失败。</summary>
        public bool DeleteSchemeSucceeds { get; set; } = true;

        /// <summary>最近一次派生用的模板 GUID。</summary>
        public Guid LastDuplicateSource { get; private set; }

        /// <summary>最近一次改名的目标 GUID。</summary>
        public Guid LastRenameTarget { get; private set; }

        /// <summary>最近一次删除的目标 GUID。</summary>
        public Guid LastDeleteTarget { get; private set; }

        public Guid? DuplicateScheme(Guid sourceSchemeGuid)
        {
            LastDuplicateSource = sourceSchemeGuid;

            if (DuplicateSchemeFails)
                return null;

            // 这是真实系统的行为（实测：复制「平衡」得到的新方案也叫「平衡」）。
            // 替身不模拟这一点，Service 里「改名失败要回滚」那条分支就测不出来 ——
            // 因为替身一复制就给了个不同的名字，看起来本来就不需要改名。
            string? sourceName = null;
            foreach ((Guid guid, string? name) in Schemes)
            {
                if (guid == sourceSchemeGuid)
                {
                    sourceName = name;
                    break;
                }
            }

            Guid created = new($"00000000-0000-0000-0000-{++_duplicateCounter:D12}");

            if (!HideDuplicatedScheme || sourceSchemeGuid == BalancedScheme)
                Schemes.Add((created, sourceName));

            return created;
        }

        public bool RenameScheme(Guid schemeGuid, string name)
        {
            LastRenameTarget = schemeGuid;

            if (!RenameSchemeSucceeds)
                return false;

            for (int i = 0; i < Schemes.Count; i++)
            {
                if (Schemes[i].Guid == schemeGuid)
                    Schemes[i] = (schemeGuid, name);
            }

            return true;
        }

        public bool DeleteScheme(Guid schemeGuid)
        {
            LastDeleteTarget = schemeGuid;

            if (!DeleteSchemeSucceeds)
                return false;

            Schemes.RemoveAll(s => s.Guid == schemeGuid);
            return true;
        }
    }
}
