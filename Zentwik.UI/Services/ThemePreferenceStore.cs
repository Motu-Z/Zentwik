using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentwik.UI.Services;

/// <summary>用户选的外观：主题模式、强调色来源、自定义强调色。</summary>
internal sealed class ThemePreference
{
    public AppTheme Mode { get; set; } = AppTheme.Dark;

    public AccentSource AccentMode { get; set; } = AccentSource.Custom;

    public string AccentColor { get; set; } = "#38BDF8";
}

/// <summary>
/// 外观偏好落盘：<c>%AppData%\Zentwik\theme.json</c>。
/// </summary>
/// <remarks>
/// 【为什么不进 Worker 的 ui.json】
/// 主题与强调色只决定这个进程怎么画，后台没有任何消费方。
/// 与 ui-settings.json 分开存，是为了让外观配置坏了不牵连行为开关。
///
/// 【为什么读失败要静默】
/// 文件缺失或写坏时退回默认外观即可 —— 为一个配色问题挡住启动不值得。
/// </remarks>
internal static class ThemePreferenceStore
{
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zentwik", "theme.json");

    /// <summary>改写落盘路径。只给自检用 —— 断言不该碰用户的真实配置。</summary>
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? DefaultFilePath;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool TryLoad(out ThemePreference value)
    {
        value = new ThemePreference();

        try
        {
            if (!File.Exists(FilePath))
                return false;

            ThemePreference? saved = JsonSerializer.Deserialize<ThemePreference>(
                File.ReadAllText(FilePath), Options);

            if (saved is null)
                return false;

            value = saved;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Save(ThemePreference value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(value, Options));
        }
        catch (Exception)
        {
            // 写盘失败不影响当前外观。
        }
    }
}
