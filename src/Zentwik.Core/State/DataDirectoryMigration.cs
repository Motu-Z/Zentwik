namespace Zentwik.Core.State;

/// <summary>搬迁结果。调用方在日志就绪后再记录，避免搬迁发生在日志目录创建之前。</summary>
public readonly record struct DataMigrationOutcome(bool Executed, string? Source, string? Error)
{
    public static readonly DataMigrationOutcome NotNeeded = new(false, null, null);
}

/// <summary>
/// 数据目录改名后的一次性搬迁：`ZentwikNext` → `Zentwik`。
/// </summary>
/// <remarks>
/// 只在目标还没有状态文件、且源目录存在时执行，避免覆盖已运行实例的数据。
/// 搬迁失败不阻塞启动——宁可让用户看到空状态并留下日志，也不要起不来。
/// 源永远是**本产品**改名前的目录；旧版 Zentwik 的 `%APPDATA%\Zentwik` 绝不能作为源读取。
/// </remarks>
public static class DataDirectoryMigration
{
    public static DataMigrationOutcome TryMigrate(string? source, string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (string.IsNullOrWhiteSpace(source))
        {
            return DataMigrationOutcome.NotNeeded;
        }

        try
        {
            if (!Directory.Exists(source))
            {
                return DataMigrationOutcome.NotNeeded;
            }

            if (Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar)
                == Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar))
            {
                return DataMigrationOutcome.NotNeeded;
            }

            // 目标已有状态就认为它已经是活动目录，重复搬迁会覆盖正在使用的数据。
            if (File.Exists(Path.Combine(target, "state.json")))
            {
                return DataMigrationOutcome.NotNeeded;
            }

            CopyDirectory(source, target);
            return new DataMigrationOutcome(true, source, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DataMigrationOutcome(false, source, ex.Message);
        }
    }

    /// <summary>
    /// 合并不是覆盖：目标里已有的文件（`owner.sid`、当天同名日志）保留，
    /// 缺的才补齐。用 <c>File.Copy(overwrite:false)</c> 直接复制会因为第一个同名文件就整体失败。
    /// </summary>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Copy(file, destination, overwrite: false);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
