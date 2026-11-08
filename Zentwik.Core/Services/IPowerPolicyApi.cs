using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 电源策略的底层访问通道（Windows <c>powrprof.dll</c> 的薄封装）。
    ///
    /// 【为什么单独抽一层接口】
    /// 上层（<see cref="PowerPolicyService"/>）要做的是「范围校验、错误聚合、快照组装」，
    /// 这些逻辑全都可以用假数据驱动；而真正碰注册表与 P/Invoke 的部分只有五个调用。
    /// 把五个调用关在一个接口后面，测试就能在不改电源方案、不需要管理员权限的前提下
    /// 覆盖所有分支 —— 尤其是「主板不支持某一项」这种在开发机上不一定复现得出来的情况。
    ///
    /// 【为什么返回值是错误码而不是 bool】
    /// <c>PowerReadACValueIndex</c> 的非 0 返回值有两个不同含义：
    /// 「这一项不存在 / 不受支持」与「读取出错」。上层要据此把 <c>Supported</c>
    /// 置 false 并把错误码原样带到界面上，一个 bool 会把这两种情况抹平。
    /// </summary>
    public interface IPowerPolicyApi
    {
        /// <summary>
        /// 当前活动电源方案的 GUID。读取失败或指针为空时返回 null。
        /// </summary>
        Guid? GetActiveScheme();

        /// <summary>
        /// 把指定方案设为活动方案。返回是否成功（Win32 结果码为 0）。
        /// </summary>
        bool SetActiveScheme(Guid schemeGuid);

        /// <summary>
        /// 读取某个设置项的交流电（AC）取值。
        /// <c>Result</c> 是 Win32 结果码：0 = 成功，其余 = 该项不受支持或读取失败。
        /// </summary>
        (uint Result, uint Value) ReadAcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting);

        /// <summary>
        /// 写入某个设置项的交流电（AC）取值。返回 Win32 结果码，0 = 成功。
        /// </summary>
        uint WriteAcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting, uint value);

        /// <summary>
        /// 读取某个设置项的电池（DC）取值。语义与 AC 版相同。
        /// </summary>
        (uint Result, uint Value) ReadDcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting);

        /// <summary>
        /// 写入某个设置项的电池（DC）取值。返回 Win32 结果码，0 = 成功。
        /// </summary>
        uint WriteDcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting, uint value);

        /// <summary>
        /// 方案的显示名（如「平衡」「高性能」）。读取失败返回 null。
        /// </summary>
        string? ReadFriendlyName(Guid schemeGuid);

        /// <summary>
        /// 本机已有的全部电源方案（GUID + 名称）。枚举失败返回空集合。
        /// </summary>
        /// <remarks>
        /// 名称为 null 的方案仍会被列出：GUID 有效就说明方案真实存在，
        /// 只是读不到名字。丢掉它会让用户在界面上找不到自己刚建的方案。
        /// </remarks>
        IReadOnlyList<(Guid Guid, string? Name)> EnumerateSchemes();

        /// <summary>
        /// 以现有方案为模板复制出一份新方案，返回新方案的 GUID；失败返回 null。
        /// </summary>
        /// <remarks>
        /// 这是 Windows 唯一可用的「新建电源方案」途径 —— <c>powrprof.dll</c> 里
        /// 没有 <c>PowerCreatePlan</c>（Win10/11 均未导出），系统里也不存在
        /// 「空白方案」这种东西，所有方案都是从某个模板派生的。
        ///
        /// 复制出来的方案**继承源方案的名字**，不改名会出现两张同名卡片 ——
        /// 调用方必须紧接着改名。
        /// </remarks>
        Guid? DuplicateScheme(Guid sourceSchemeGuid);

        /// <summary>
        /// 某个方案下全部设置子组的 GUID。枚举失败返回空集合。
        /// </summary>
        IReadOnlyList<Guid> EnumerateSubgroups(Guid schemeGuid);

        /// <summary>
        /// 某个方案、某个子组下全部设置项的 GUID。枚举失败返回空集合。
        /// </summary>
        IReadOnlyList<Guid> EnumerateSettings(Guid schemeGuid, Guid subgroup);

        /// <summary>
        /// 改方案显示名。返回是否成功。
        /// </summary>
        /// <remarks>
        /// 只改方案本身的 <c>FriendlyName</c>，不动子组与设置项的名字。
        /// 名字是 UTF-16、长度按**字节**计（含结尾符）。
        /// </remarks>
        bool RenameScheme(Guid schemeGuid, string name);

        /// <summary>
        /// 方案的说明文字（系统控制面板里名字下面那一行）。读不到返回 null。
        /// </summary>
        /// <remarks>
        /// 说明文字不是 API 给的，只存在方案的注册表键里，而且是一个
        /// 「@dll,-资源号,英文兜底」形式的本地化引用串。派生出来的方案会继承
        /// 派生源的那一份 —— 从平衡派生的「卓越性能」，说明写的还是
        /// 「自动平衡功耗与性能」。所以新建时必须连同说明一起搬。
        /// </remarks>
        string? ReadSchemeDescription(Guid schemeGuid);

        /// <summary>
        /// 改写方案的说明文字。返回是否成功。
        /// </summary>
        /// <remarks>
        /// 写的是 HKLM，只有提权进程能成功；失败不阻断新建，说明文字属于锦上添花。
        /// </remarks>
        bool WriteSchemeDescription(Guid schemeGuid, string description);

        /// <summary>
        /// 删除方案。返回是否成功。
        /// </summary>
        /// <remarks>
        /// 删除活动方案后，系统会自行回落到别的方案 —— 这一点不由我们决定，
        /// 删除后必须重新枚举才能知道当前生效的是哪个。
        /// </remarks>
        bool DeleteScheme(Guid schemeGuid);

        /// <summary>
        /// 供电事实：本机有没有电池，以及此刻是否在用交流电。
        /// </summary>
        /// <remarks>
        /// 两个事实来自同一次 <c>GetSystemPowerStatus</c>，分两次问要多调一次系统 API，
        /// 还会出现「第一次说有电池、第二次说正在用电池」这种自相矛盾的组合。
        ///
        /// 严格说这是 <c>kernel32</c> 的调用，不属于 powrprof。放在这个接口里是为了
        /// 不让 <see cref="PowerPolicyService"/> 多一个构造依赖 —— 它读快照时两样都要。
        ///
        /// <c>OnAcPower</c> 为 null 表示读不到供电状态（API 失败或返回「未知」）。
        /// 这与「在用电池」不是一回事，界面上应该不显示，而不是显示成电池供电。
        /// </remarks>
        (bool HasBattery, bool? OnAcPower) ReadPowerSupply();
    }
}
