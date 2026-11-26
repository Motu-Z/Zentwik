using System.Text.Json;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Domain.Power;

namespace Zentwik.Core.Power;

/// <summary>一份落盘的备份：Id 是文件名（不含扩展名），Path 是 JSON 清单的完整路径。</summary>
public sealed record PowerSchemeBackupEntry(string Id, string Path, PowerSchemeBackupDocument Document);

/// <summary>
/// 电源方案备份目录。一份备份 = 一个 JSON 清单，外加一个可选的同名 .pow 原件。
/// 目录放在服务的公共数据目录下：那里的默认 ACL 允许普通用户读取，用户能自己翻出来拷走。
/// </summary>
public sealed class PowerSchemeBackupStore(string dataDirectory, DiagnosticLog? log = null)
{
    public string DirectoryPath { get; } = Path.Combine(dataDirectory, "power-backups");

    /// <summary>备份 ID 带时间戳：同一方案反复删建不会互相覆盖，按名字排序即按时间排序。</summary>
    public string NewId(Guid scheme) =>
        $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{scheme:D}";

    /// <summary>写清单。先写临时文件再替换：备份自己写坏等于没备份。</summary>
    public string Save(PowerSchemeBackupDocument document, string id)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);

        var path = Path.Combine(DirectoryPath, id + ".json");
        var temp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ZentwikJson.Default.PowerSchemeBackupDocument);

        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        log?.Info($"已备份电源方案「{document.Name}」到 {path}。");
        return path;
    }

    /// <summary>按创建时间倒序列出全部备份。读不出来的文件跳过，不影响其余备份。</summary>
    public IReadOnlyList<PowerSchemeBackupEntry> List()
    {
        if (!System.IO.Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var entries = new List<PowerSchemeBackupEntry>();
        foreach (var path in System.IO.Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            var document = TryRead(path);
            if (document is not null)
            {
                entries.Add(new PowerSchemeBackupEntry(Path.GetFileNameWithoutExtension(path), path, document));
            }
        }

        return entries.OrderByDescending(static entry => entry.Document.CreatedAt).ToArray();
    }

    /// <summary>按备份 ID 或完整路径加载。路径先判断，用户从资源管理器拖过来的绝对路径也能用。</summary>
    public PowerSchemeBackupEntry Load(string idOrPath)
    {
        var path = Resolve(idOrPath) ?? throw new CoreException(
            ErrorCodes.PowerSchemeBackupNotFound,
            $"找不到电源方案备份：{idOrPath}。");

        var document = TryRead(path) ?? throw new CoreException(
            ErrorCodes.PowerSchemeRestoreFailed,
            $"备份文件无法解析：{path}。");

        if (document.SchemaVersion > PowerSchemeBackupDocument.CurrentSchemaVersion)
        {
            throw new CoreException(
                ErrorCodes.PowerSchemeRestoreFailed,
                $"备份文件版本 {document.SchemaVersion} 高于当前支持 {PowerSchemeBackupDocument.CurrentSchemaVersion}，拒绝恢复。");
        }

        return new PowerSchemeBackupEntry(Path.GetFileNameWithoutExtension(path), path, document);
    }

    private string? Resolve(string idOrPath)
    {
        if (File.Exists(idOrPath))
        {
            return idOrPath;
        }

        var candidate = Path.Combine(DirectoryPath, idOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? idOrPath
            : idOrPath + ".json");

        return File.Exists(candidate) ? candidate : null;
    }

    private PowerSchemeBackupDocument? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ZentwikJson.Default.PowerSchemeBackupDocument);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Warn($"备份文件读取失败，已跳过：{path}（{ex.Message}）");
            return null;
        }
    }
}
