using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Zentwik.UI.Services;

/// <summary>
/// 桌面空白处的右键菜单项。写在 HKCU 下，不需要提权，也不归安装包管。
/// 命令值永远是「当前 exe + --show」：换了目录或便携版换了盘，读回不一致就重写。
/// </summary>
public static class DesktopContextMenuService
{
    private const string KeyPath = @"Software\Classes\DesktopBackground\Shell\Zentwik";

    private const string Label = "Zentwik";

    private const string ShowArgument = " --show";

    /// <summary>菜单命令里的可执行文件路径；取不到返回空串。</summary>
    public static string ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            try
            {
                return System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty;
            }
            catch (NotSupportedException)
            {
                return string.Empty;
            }
        }
    }

    public static string CommandText => string.Concat("\"", ExecutablePath, "\"", ShowArgument);

    /// <summary>键在、且命令仍指向当前 exe，才算已安装。</summary>
    public static bool IsInstalled()
    {
        try
        {
            using RegistryKey? command = Registry.CurrentUser.OpenSubKey(KeyPath + @"\command");
            return string.Equals(command?.GetValue(null) as string, CommandText, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    public static bool Install()
    {
        string exe = ExecutablePath;

        if (exe.Length == 0)
        {
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(null, Label);
            key.SetValue("Icon", exe);

            using RegistryKey command = key.CreateSubKey("command", writable: true);
            command.SetValue(null, CommandText);

            RefreshShell();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    public static bool Uninstall()
    {
        try
        {
            if (Registry.CurrentUser.OpenSubKey(KeyPath) is not null)
            {
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
                RefreshShell();
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 按开关对齐注册表：开着但命令过期（换了目录）就重写，关着但还留着键就删掉。
    /// 启动时调一次，用户不用管换过几台机器、几个目录。
    /// </summary>
    public static void Sync(bool enabled)
    {
        if (enabled)
        {
            if (!IsInstalled())
            {
                Install();
            }

            return;
        }

        Uninstall();
    }

    /// <summary>通知外壳重读，否则用户要等下一次登录才看得见菜单项。</summary>
    private static void RefreshShell() =>
        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
}
