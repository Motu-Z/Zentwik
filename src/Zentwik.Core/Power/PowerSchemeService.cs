using Zentwik.Contracts.Protocol;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Domain.Power;
using Zentwik.Platform;

namespace Zentwik.Core.Power;

/// <summary>
/// 一个 Windows 电源方案的展示状态。Active 表示当前生效，BuiltIn 表示系统内置，
/// Visible 表示是否出现在方案枚举里（false = 键存在、可读写，但用户看不到）。
/// </summary>
public sealed record PowerSchemeState(Guid Guid, string Name, bool Active, bool BuiltIn, bool Visible);

/// <summary>一份已落盘的备份。UnreadableCount 是「两侧都读不到」的设置项数，它们没进备份。</summary>
public sealed record PowerSchemeBackup(
    string Id,
    string Path,
    Guid SourceGuid,
    string Name,
    bool Visible,
    DateTimeOffset CreatedAt,
    int SettingCount,
    int UnreadableCount,
    string? PortablePath);

/// <summary>删除结果。Backup 为空表示这次删除没有留备份（--no-backup）。</summary>
public sealed record PowerSchemeDeleteResult(Guid Guid, PowerSchemeBackup? Backup);

/// <summary>恢复结果。Failures 只列前几条原因，Failed 是完整失败数。</summary>
public sealed record PowerSchemeRestoreResult(
    PowerSchemeState Scheme,
    int Applied,
    int Failed,
    IReadOnlyList<string> Failures);

/// <summary>
/// Windows 电源方案的增删改查与备份恢复。只操作系统方案本身；Zentwik 的 PowerPlan 方案对象另在 PlanCoordinator。
/// 规则：内置不可删；改名/新建查重（忽略大小写）；删除活动方案前先切到别的方案。
/// </summary>
/// <remarks>
/// 「可见」与「存在」是两件事：<see cref="IPowerPolicyService.ListSchemes"/> 只回答后者中的可见部分。
/// 存在性、查重、定位一律走 <see cref="IPowerPolicyService.ListAllSchemes"/>，只有「切换活动方案」这类
/// 会改变用户可见状态的动作才限制在可见列表里。
/// </remarks>
public sealed class PowerSchemeService(
    IPowerPolicyService power,
    PowerSchemeBackupStore backups,
    DiagnosticLog? log = null)
{
    public const int MaxNameLength = 64;

    /// <summary>恢复时最多回报的失败条数：设置项可能上百，全列出来命令行没法看。</summary>
    private const int MaxReportedFailures = 5;

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

    public PowerSchemeDeleteResult Delete(Guid scheme, bool keepBackup = true)
    {
        if (PowerSchemePresets.IsBuiltIn(scheme))
        {
            throw new CoreException(ErrorCodes.PowerSchemeBuiltIn, "内置电源方案不可删除。");
        }

        _ = Find(scheme);

        // 先备份再删：删除不可逆，备份失败就中止。留一份多余备份无害，删掉再报错就没救。
        PowerSchemeBackup? backup = null;
        if (keepBackup)
        {
            try
            {
                backup = Backup(scheme);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CoreException(
                    ErrorCodes.PowerSchemeBackupFailed,
                    $"删除前备份失败，已中止删除：{ex.Message}");
            }
        }

        if (power.GetActiveScheme()?.Guid == scheme && !SwitchAway(scheme))
        {
            throw new CoreException(ErrorCodes.PowerSchemeDeleteFailed, "当前电源方案无法切换到其他方案，删除失败。");
        }

        if (!power.DeleteScheme(scheme, out var error))
        {
            throw new CoreException(ErrorCodes.PowerSchemeDeleteFailed, error ?? "删除电源方案失败。");
        }

        return new PowerSchemeDeleteResult(scheme, backup);
    }

    /// <summary>把一个方案的全部设置项、说明与显示名存成一份备份。</summary>
    /// <remarks>
    /// 只记能读到的东西：两侧都读不到的设置项跳过并计数，不让一项失败挡住整份备份。
    /// 附带的 .pow 是 Windows 原生原件，可脱离本工具手动导入；导出失败不影响备份本身。
    /// </remarks>
    public PowerSchemeBackup Backup(Guid scheme)
    {
        var entry = Find(scheme);
        var settings = new List<PowerSchemeSettingValue>();
        var unreadable = 0;

        foreach (var subGroup in power.EnumerateSubgroups(scheme))
        {
            foreach (var setting in power.EnumerateSettings(scheme, subGroup))
            {
                var hasAc = power.TryReadValue(scheme, subGroup, setting, ac: true, out var ac, out _);
                var hasDc = power.TryReadValue(scheme, subGroup, setting, ac: false, out var dc, out _);

                if (!hasAc && !hasDc)
                {
                    unreadable++;
                    continue;
                }

                settings.Add(new PowerSchemeSettingValue(subGroup, setting, hasAc ? ac : null, hasDc ? dc : null));
            }
        }

        var document = new PowerSchemeBackupDocument
        {
            SourceGuid = scheme,
            Name = entry.FriendlyName,
            Description = power.ReadDescription(scheme),
            Visible = entry.Visible,
            CreatedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            Settings = settings,
        };

        var id = backups.NewId(scheme);
        var path = backups.Save(document, id);
        var portable = ExportPortable(scheme, path);

        return new PowerSchemeBackup(
            id,
            path,
            scheme,
            document.Name,
            document.Visible,
            document.CreatedAt,
            settings.Count,
            unreadable,
            portable);
    }

    public IReadOnlyList<PowerSchemeBackup> ListBackups() =>
        backups.List()
            .Select(static entry => new PowerSchemeBackup(
                entry.Id,
                entry.Path,
                entry.Document.SourceGuid,
                entry.Document.Name,
                entry.Document.Visible,
                entry.Document.CreatedAt,
                entry.Document.Settings.Count,
                0,
                PortablePathOf(entry.Path)))
            .ToArray();

    /// <summary>按备份重建一个方案。原 GUID 与可见性都回不来，名字冲突时自动加「(恢复)」后缀。</summary>
    /// <remarks>
    /// 底子是「平衡」：它在所有机器上都可见。备份里有取值的设置项逐项写回，
    /// 备份里没有的保持平衡的取值——这是本方法的已知边界，恢复结果不一定与原方案逐字节相同。
    /// </remarks>
    public PowerSchemeRestoreResult Restore(string backupId)
    {
        var entry = backups.Load(backupId);
        var document = entry.Document;
        var name = UniqueName(string.IsNullOrWhiteSpace(document.Name) ? "恢复的方案" : document.Name);
        var created = Create(PowerSchemePresets.Balanced.Guid, name);

        var applied = 0;
        var failed = 0;
        var failures = new List<string>();

        foreach (var setting in document.Settings)
        {
            if (setting.Ac is { } ac)
            {
                if (power.TryWriteValue(created.Guid, setting.SubGroup, setting.Setting, ac: true, ac, out var error))
                {
                    applied++;
                }
                else
                {
                    failed++;
                    AddFailure(failures, setting, "AC", error);
                }
            }

            if (setting.Dc is { } dc)
            {
                if (power.TryWriteValue(created.Guid, setting.SubGroup, setting.Setting, ac: false, dc, out var error))
                {
                    applied++;
                }
                else
                {
                    failed++;
                    AddFailure(failures, setting, "DC", error);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(document.Description))
        {
            power.TryWriteDescription(created.Guid, document.Description, out _);
        }

        log?.Info($"已从备份 {entry.Id} 恢复方案「{name}」（{created.Guid:D}）：写入 {applied} 项，失败 {failed} 项。");
        return new PowerSchemeRestoreResult(created, applied, failed, failures);
    }

    /// <summary>导出一份 .pow 原件。powrprof 无导入导出接口，只能调 powercfg；失败只记日志。</summary>
    private string? ExportPortable(Guid scheme, string jsonPath)
    {
        var path = Path.ChangeExtension(jsonPath, ".pow");
        if (power.TryExportScheme(path, scheme, out var error))
        {
            return path;
        }

        log?.Warn($"附带 .pow 导出失败（备份本身有效，忽略）：{error}");
        return null;
    }

    private static string? PortablePathOf(string jsonPath)
    {
        var path = Path.ChangeExtension(jsonPath, ".pow");
        return File.Exists(path) ? path : null;
    }

    /// <summary>恢复出的方案要能用：名字被占用就加后缀，别让用户为了恢复先去删掉同名方案。</summary>
    private string UniqueName(string desired)
    {
        if (!NameExists(desired, Guid.Empty))
        {
            return desired;
        }

        for (var attempt = 1; attempt <= 99; attempt++)
        {
            var candidate = attempt == 1 ? $"{desired} (恢复)" : $"{desired} (恢复 {attempt})";
            if (candidate.Length <= MaxNameLength && !NameExists(candidate, Guid.Empty))
            {
                return candidate;
            }
        }

        throw new CoreException(ErrorCodes.PowerSchemeNameConflict, $"无法为「{desired}」找到可用的恢复名称。");
    }

    private static void AddFailure(List<string> failures, PowerSchemeSettingValue setting, string side, string? error)
    {
        if (failures.Count < MaxReportedFailures)
        {
            failures.Add($"{setting.SubGroup:D}/{setting.Setting:D} {side}：{error ?? "写入失败"}");
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
