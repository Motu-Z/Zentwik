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

/// <summary>外观偏好落盘：%AppData%\Zentwik\theme.json</summary>
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
