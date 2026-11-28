using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Zentwik.Desktop.Services;

/// <summary>
/// 外壳自己的持久化偏好：只放和这台机器的界面行为有关、代理不关心的东西。
/// 代理侧的设置（语言、采样间隔等）仍走 settings.get / settings.set，不要混进来。
/// </summary>
internal sealed class ShellPreferences
{
    /// <summary>关闭窗口时留在托盘，而不是退出进程。</summary>
    public bool CloseToTray { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zentwik",
        "desktop-shell.json");

    public static ShellPreferences Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return new ShellPreferences();
            }

            return JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(path)) ?? new ShellPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 偏好读不出来不该拦住开窗，退回默认值即可。
            return new ShellPreferences();
        }
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不下就算了，不打断用户操作。
        }
    }
}

/// <summary>开机自启动：写当前用户的 Run 键，不需要管理员。</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Zentwik";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool TrySetEnabled(bool enabled, out string error)
    {
        error = string.Empty;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = RunKeyPath;
                return false;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    error = "Environment.ProcessPath";
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>给界面显示用：注册表里记的启动命令。</summary>
    public static string? ReadCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
