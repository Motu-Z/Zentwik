using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zentwik.UI.Services;

/// <summary>
/// 处理器型号：侧栏字标下方那行小字。读一次注册表就固定，读不到用占位值。
/// 界面只吃精简后的型号（去掉核数后缀与核显后缀），全名进 ToolTip。
/// </summary>
public static class CpuInfo
{
    private const string Fallback = "AMD Ryzen 9 9955HX";

    /// <summary>注册表里的原始串（ToolTip 用）。</summary>
    /// <remarks>声明顺序即在先：Name 由它派生，字段按声明顺序初始化。</remarks>
    public static string RawName { get; } = ReadRaw();

    /// <summary>精简型号，例如 “AMD Ryzen 9 9950X”。</summary>
    public static string Name { get; } = Shorten(RawName);

    private static string ReadRaw()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            if (key?.GetValue("ProcessorNameString") is string raw && !string.IsNullOrWhiteSpace(raw))
            {
                return Collapse(raw);
            }
        }
        catch
        {
            // 读注册表失败不该影响启动
        }

        return Fallback;
    }

    /// <summary>
    /// “AMD Ryzen 9 9950X 16-Core Processor” → “AMD Ryzen 9 9950X”
    /// “... w/ Radeon Graphics” → 去掉核显尾巴
    /// </summary>
    private static string Shorten(string raw)
    {
        string s = Regex.Replace(raw, @"\s*\b\d+-Core Processor\b", string.Empty,
                                 RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(w/|with)\s+.*$", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*\((R|TM)\)", string.Empty, RegexOptions.IgnoreCase);
        return Collapse(s);
    }

    private static string Collapse(string raw) =>
        Regex.Replace(raw.Replace('\0', ' ').Trim(), @"\s+", " ");
}
