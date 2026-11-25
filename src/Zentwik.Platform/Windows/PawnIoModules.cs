namespace Zentwik.Platform.Windows;

/// <summary>PawnIO 模块文件的查找顺序：环境变量覆盖、程序目录、Program Files。</summary>
internal static class PawnIoModules
{
    private const string OverrideVariable = "ZENTWIK_PAWNIO_MODULE";

    public static string? Resolve(string fileName, out string searched)
    {
        var candidates = new List<string>(3);

        var configured = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured)
            && string.Equals(Path.GetFileName(configured), fileName, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(configured);
            if (File.Exists(configured))
            {
                searched = configured;
                return configured;
            }
        }

        var local = Path.Combine(AppContext.BaseDirectory, "PawnIO", fileName);
        candidates.Add(local);
        if (File.Exists(local))
        {
            searched = local;
            return local;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "Zentwik", "PawnIO", fileName);
        candidates.Add(installed);
        if (File.Exists(installed))
        {
            searched = installed;
            return installed;
        }

        searched = string.Join("、", candidates);
        return null;
    }
}
