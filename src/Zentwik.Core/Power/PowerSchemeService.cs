using Zentwik.Contracts.Protocol;
using Zentwik.Core.Plans;
using Zentwik.Domain.Power;
using Zentwik.Platform;

namespace Zentwik.Core.Power;

/// <summary>
/// 一个 Windows 电源方案的展示状态。Active 表示当前生效，BuiltIn 表示系统内置，
/// Visible 表示是否出现在方案枚举里（false = 键存在、可读写，但用户看不到）。
/// </summary>
public sealed record PowerSchemeState(Guid Guid, string Name, bool Active, bool BuiltIn, bool Visible);

/// <summary>
/// Windows 电源方案的增删改查。只操作系统方案本身；Zentwik 的 PowerPlan 方案对象另在 PlanCoordinator。
/// 规则：内置不可删；改名/新建查重（忽略大小写）；删除活动方案前先切到别的方案。
/// </summary>
/// <remarks>
/// 「可见」与「存在」是两件事：<see cref="IPowerPolicyService.ListSchemes"/> 只回答后者中的可见部分。
/// 存在性、查重、定位一律走 <see cref="IPowerPolicyService.ListAllSchemes"/>，只有「切换活动方案」这类
/// 会改变用户可见状态的动作才限制在可见列表里。
/// </remarks>
public sealed class PowerSchemeService(IPowerPolicyService power)
{
    public const int MaxNameLength = 64;

    public IReadOnlyList<PowerSchemeState> List()
    {
        var active = power.GetActiveScheme()?.Guid;

        // 可见的排前面：用户先看到平时能操作的那几个，隐藏的排在后面并带标记。
        return power.ListAllSchemes()
            .OrderByDescending(static entry => entry.Visible)
            .ThenBy(static entry => entry.FriendlyName, StringComparer.CurrentCulture)
            .Select(entry => ToState(entry.Guid, entry.FriendlyName, entry.Visible, active))
            .ToArray();
    }

    public PowerSchemeState Create(Guid template, string name)
    {
        var normalized = NormalizeName(name);

        if (!PowerSchemePresets.IsBuiltIn(template) && !Exists(template))
        {
            throw new CoreException(ErrorCodes.PowerSchemeNotFound, $"模板电源方案 {template:D} 不存在。");
        }

        if (NameExists(normalized, Guid.Empty))
        {
            throw new CoreException(ErrorCodes.PowerSchemeNameConflict, $"已存在同名电源方案：{normalized}。");
        }

        var created = Duplicate(template);

        // 部分笔记本把「平衡」以外的方案标记为隐藏：从这些模板派生的方案会继承隐藏标记，
        // 它不出现在枚举结果里，只在被激活的那一刻临时可见，切到别的方案就消失——用户看到的是
        // 「刚建的方案被删了」。隐藏标记在注册表里由 SYSTEM 独占写入，管理员也改不了。
        // 所以检测到派生品不可见时，丢掉它改从「平衡」派生，再把模板的设置项与说明搬过去：
        // 平衡在所有机器上都可见。
        if (IsHidden(created))
        {
            power.DeleteScheme(created, out _);
            created = Duplicate(PowerSchemePresets.Balanced.Guid);
            CopySettings(template, created);
            CopyDescription(template, created);
        }

        if (!power.RenameScheme(created, normalized, out var error))
        {
            power.DeleteScheme(created, out _);
            throw new CoreException(ErrorCodes.PowerSchemeCreateFailed, error ?? "方案已派生但改名失败，已回滚删除。");
        }

        return State(created, normalized);
    }

    public PowerSchemeState Rename(Guid scheme, string name)
    {
        var normalized = NormalizeName(name);
        _ = Find(scheme);

        if (NameExists(normalized, scheme))
        {
            throw new CoreException(ErrorCodes.PowerSchemeNameConflict, $"已存在同名电源方案：{normalized}。");
        }

        if (!power.RenameScheme(scheme, normalized, out var error))
        {
            throw new CoreException(ErrorCodes.PowerSchemeCreateFailed, error ?? "电源方案改名失败。");
        }

        return State(scheme, normalized);
    }

    public void Delete(Guid scheme)
    {
        if (PowerSchemePresets.IsBuiltIn(scheme))
        {
            throw new CoreException(ErrorCodes.PowerSchemeBuiltIn, "内置电源方案不可删除。");
        }

        _ = Find(scheme);

        if (power.GetActiveScheme()?.Guid == scheme && !SwitchAway(scheme))
        {
            throw new CoreException(ErrorCodes.PowerSchemeDeleteFailed, "当前电源方案无法切换到其他方案，删除失败。");
        }

        if (!power.DeleteScheme(scheme, out var error))
        {
            throw new CoreException(ErrorCodes.PowerSchemeDeleteFailed, error ?? "删除电源方案失败。");
        }
    }

    private Guid Duplicate(Guid template)
    {
        var created = power.DuplicateScheme(template, out var error);
        if (created is null || created == Guid.Empty)
        {
            throw new CoreException(ErrorCodes.PowerSchemeCreateFailed, error ?? "派生电源方案失败。");
        }

        return created.Value;
    }

    /// <summary>
    /// 派生品是否不可见。整个枚举都为空时不判定为隐藏——那更像枚举本身失败，
    /// 此时把方案当可见、交给后续步骤，比再派生一次安全。
    /// </summary>
    private bool IsHidden(Guid scheme)
    {
        var schemes = power.ListSchemes();
        return schemes.Count > 0 && schemes.All(candidate => candidate.Guid != scheme);
    }

    /// <summary>把模板的全部设置项取值搬到目标方案（AC 与 DC 各一套）。</summary>
    private void CopySettings(Guid source, Guid target)
    {
        if (source == target)
        {
            return;
        }

        // 卓越性能在微软的模板体系里是「高性能之上再压一层」：模板键只显式写了少数几项，
        // 单独搬它等于复制平衡。先铺高性能的底，再让模板自己的取值覆盖。
        if (source == PowerSchemePresets.UltimatePerformance.Guid)
        {
            CopyAllSettings(PowerSchemePresets.HighPerformance.Guid, target);
        }

        CopyAllSettings(source, target);
    }

    /// <remarks>单项失败直接跳过：部分设置项在目标方案上不接受写入（受硬件或驱动限制），
    /// 为一项失败放弃整次新建，用户会连方案都建不出来。</remarks>
    private void CopyAllSettings(Guid source, Guid target)
    {
        if (source == target)
        {
            return;
        }

        foreach (var subGroup in power.EnumerateSubgroups(source))
        {
            foreach (var setting in power.EnumerateSettings(source, subGroup))
            {
                if (power.TryReadValue(source, subGroup, setting, ac: true, out var ac, out _))
                {
                    power.TryWriteValue(target, subGroup, setting, ac: true, ac, out _);
                }

                if (power.TryReadValue(source, subGroup, setting, ac: false, out var dc, out _))
                {
                    power.TryWriteValue(target, subGroup, setting, ac: false, dc, out _);
                }
            }
        }
    }

    /// <summary>搬说明文字。不搬的话「卓越性能」下面写着「自动平衡功耗与性能」，与名字自相矛盾。</summary>
    /// <remarks>失败不阻断：非提权进程写不了 HKLM，那也不该让方案建不出来。</remarks>
    private void CopyDescription(Guid source, Guid target)
    {
        var description = power.ReadDescription(source);
        if (!string.IsNullOrWhiteSpace(description))
        {
            power.TryWriteDescription(target, description, out _);
        }
    }

    private bool SwitchAway(Guid exclude)
    {
        // 只切到可见方案：切到隐藏方案会让它临时冒出来，用户会以为自己多了一个方案。
        var target = power.ListSchemes().FirstOrDefault(scheme => scheme.Guid != exclude);
        return target is not null && power.TrySetActiveScheme(target.Guid, out _);
    }

    /// <summary>按 GUID 定位方案。隐藏方案同样算存在——枚举结果不能用作存在性判断。</summary>
    private PowerSchemeEntry Find(Guid scheme) =>
        power.ListAllSchemes().FirstOrDefault(candidate => candidate.Guid == scheme)
        ?? throw new CoreException(ErrorCodes.PowerSchemeNotFound, $"电源方案 {scheme:D} 不存在。");

    private bool Exists(Guid scheme) => power.ListAllSchemes().Any(candidate => candidate.Guid == scheme);

    private bool NameExists(string name, Guid exclude) =>
        power.ListAllSchemes().Any(candidate =>
            candidate.Guid != exclude
            && string.Equals(candidate.FriendlyName, name, StringComparison.OrdinalIgnoreCase));

    private PowerSchemeState State(Guid guid, string name) =>
        ToState(
            guid,
            name,
            power.ListAllSchemes().FirstOrDefault(entry => entry.Guid == guid)?.Visible ?? true,
            power.GetActiveScheme()?.Guid);

    private static PowerSchemeState ToState(Guid guid, string name, bool visible, Guid? active) =>
        new(guid, name, active == guid, PowerSchemePresets.IsBuiltIn(guid), visible);

    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CoreException(ErrorCodes.PayloadInvalid, "电源方案名称不能为空。");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new CoreException(ErrorCodes.PayloadInvalid, $"电源方案名称不能超过 {MaxNameLength} 个字符。");
        }

        return trimmed;
    }
}
