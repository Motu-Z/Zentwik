using Zentwik.Contracts.Protocol;
using Zentwik.Core.Plans;
using Zentwik.Domain.Power;
using Zentwik.Platform;

namespace Zentwik.Core.Power;

/// <summary>一个 Windows 电源方案的展示状态。Active 表示当前生效，BuiltIn 表示系统内置。</summary>
public sealed record PowerSchemeState(Guid Guid, string Name, bool Active, bool BuiltIn);

/// <summary>
/// Windows 电源方案的增删改查。只操作系统方案本身；Zentwik 的 PowerPlan 方案对象另在 PlanCoordinator。
/// 规则：内置不可删；改名/新建查重（忽略大小写）；删除活动方案前先切到别的方案。
/// </summary>
public sealed class PowerSchemeService(IPowerPolicyService power)
{
    public const int MaxNameLength = 64;

    public IReadOnlyList<PowerSchemeState> List()
    {
        var active = power.GetActiveScheme()?.Guid;
        return power.ListSchemes()
            .Select(scheme => new PowerSchemeState(
                scheme.Guid,
                scheme.FriendlyName,
                active == scheme.Guid,
                PowerSchemePresets.IsBuiltIn(scheme.Guid)))
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

        var created = power.DuplicateScheme(template, out var error);
        if (created is null || created == Guid.Empty)
        {
            throw new CoreException(ErrorCodes.PowerSchemeCreateFailed, error ?? "派生电源方案失败。");
        }

        if (!power.RenameScheme(created.Value, normalized, out error))
        {
            power.DeleteScheme(created.Value, out _);
            throw new CoreException(ErrorCodes.PowerSchemeCreateFailed, error ?? "方案已派生但改名失败，已回滚删除。");
        }

        return new PowerSchemeState(
            created.Value,
            normalized,
            power.GetActiveScheme()?.Guid == created.Value,
            PowerSchemePresets.IsBuiltIn(created.Value));
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

        return new PowerSchemeState(
            scheme,
            normalized,
            power.GetActiveScheme()?.Guid == scheme,
            PowerSchemePresets.IsBuiltIn(scheme));
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

    private bool SwitchAway(Guid exclude)
    {
        var target = power.ListSchemes().FirstOrDefault(scheme => scheme.Guid != exclude);
        return target is not null && power.TrySetActiveScheme(target.Guid, out _);
    }

    private PowerSchemeInfo Find(Guid scheme) =>
        power.ListSchemes().FirstOrDefault(candidate => candidate.Guid == scheme)
        ?? throw new CoreException(ErrorCodes.PowerSchemeNotFound, $"电源方案 {scheme:D} 不存在。");

    private bool Exists(Guid scheme) => power.ListSchemes().Any(candidate => candidate.Guid == scheme);

    private bool NameExists(string name, Guid exclude) =>
        power.ListSchemes().Any(candidate =>
            candidate.Guid != exclude
            && string.Equals(candidate.FriendlyName, name, StringComparison.OrdinalIgnoreCase));

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
