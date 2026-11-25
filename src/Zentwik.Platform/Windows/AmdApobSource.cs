namespace Zentwik.Platform.Windows;

/// <summary>可选的 APOB 原始表来源：环境变量指向 APOB 表转储，用于有固件映射时优先采用。</summary>
internal static class AmdApobSource
{
    private const string TableVariable = "ZENTWIK_APOB_TABLE";

    /// <summary>
    /// true 表示读到了表；未配置返回 false 且 error 为 null；配置了但读不到返回 false 且带原因。
    /// </summary>
    public static bool TryLoad(out byte[]? table, out string? error)
    {
        table = null;
        error = null;

        var path = Environment.GetEnvironmentVariable(TableVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            table = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"读取 {TableVariable} 指向的 APOB 表失败：{ex.Message}";
            return false;
        }
    }
}
