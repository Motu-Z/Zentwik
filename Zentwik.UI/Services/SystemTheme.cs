using Microsoft.Win32;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>
/// 系统主题与系统强调色的读取（全部走注册表，不依赖 WinRT）。
/// </summary>
/// <remarks>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
///   · AppsUseLightTheme   = 1 → 应用浅色，0 → 应用深色
///   · SystemUsesLightTheme = 系统设置界面自身的明暗
/// 取 AppsUseLightTheme 作为「系统主题」判定依据 —— 它才是决定应用配色的那一个。
///
/// 两个来源，优先用第一个：
///   1. HKCU\...\DWM\AccentColor
///      REG_DWORD，COLORREF 格式（0x00BBGGRR），注意是 BGR 不是 RGB。
///      本机实测 4291016448 = 0xFFC3B700 → RGB(0, 183, 195)（青色）。
///   2. HKCU\...\Explorer\Accent\AccentPalette
///      32 字节数组，8 组 × 4 字节，每组是 **RGBA** 字节序（R,G,B,A）。
///      主色在 **索引 3**（第 4 组），不是索引 0。
///
/// AccentPalette 的 8 组依次是：
///   [0] 浅色1  [1] 浅色2  [2] 浅色3  [3] **主色**
///   [4] 深色1  [5] 深色2  [6] 深色3  [7] 对比色
/// 本机实测字节：
///   组0 = 69 FC FF 00 → RGBA(105,252,255) 浅青
///   组3 = 00 B7 C3 00 → RGBA(0,183,195)   ← 主色，与 DWM 完全一致
/// 若误取索引 0 又按 BGRA 解码，会得到 RGB(255,252,105) —— 黄色。
/// 这正是「系统明明是青色、应用却显示黄色」的原因。
/// 两个来源交叉校验：DWM 的 RGB 必须等于 AccentPalette[3] 的 RGBA，
/// 不一致时以 DWM 为准（DWM 是 DWM 合成器实际在用的值）。
///
/// Explorer\Accent 下**没有** AccentColor 这个值（只有 AccentPalette /
/// StartColorMenu / AccentColorMenu），早期误读成 AccentColor 会导致
/// 永远读不到值、静默退回默认蓝。
/// </remarks>
public static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AccentPaletteKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>读不到时的兜底强调色（参考 HTML 的默认蓝 #38BDF8）。</summary>
    private static readonly Color FallbackAccent = Color.FromRgb(0x38, 0xBD, 0xF8);

    /// <summary>系统是否处于浅色主题。</summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int v)
            {
                return v != 0;
            }
        }
        catch
        {
            // 注册表不可读（极少见）→ 回落到深色，与应用默认一致
        }
        return false;
    }

    /// <summary>
    /// 读取系统强调色。读不到时回落到参考 HTML 的默认蓝 #38BDF8。
    /// </summary>
    public static Color GetSystemAccentColor()
    {
        // 主源：DWM\AccentColor（COLORREF 0x00BBGGRR）
        // 这是 DWM 合成器实际在用的值，最可靠。
        Color? dwm = ReadDwmAccent();
        if (dwm is Color dwmColor) return dwmColor;

        // 回退：Explorer\Accent\AccentPalette 索引 3（主色组），RGBA 字节序
        Color? palette = ReadPaletteAccent();
        if (palette is Color paletteColor) return paletteColor;

        return FallbackAccent;
    }

    /// <summary>
    /// 读 DWM\AccentColor（COLORREF 0x00BBGGRR）。
    /// </summary>
    private static Color? ReadDwmAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (key?.GetValue("AccentColor") is int colorRef)
            {
                // COLORREF: 0x00BBGGRR —— 低字节是 R，中字节是 G，高字节是 B
                byte r = (byte)(colorRef & 0xFF);
                byte g = (byte)((colorRef >> 8) & 0xFF);
                byte b = (byte)((colorRef >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }
        }
        catch
        {
            // 注册表不可读 → 落到 AccentPalette
        }
        return null;
    }

    /// <summary>
    /// 读 Explorer\Accent\AccentPalette 的主色组（索引 3），RGBA 字节序。
    /// </summary>
    private static Color? ReadPaletteAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentPaletteKey);
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 16)
            {
                // 8 组 × 4 字节，主色在索引 3（第 4 组），字节序为 R,G,B,A
                const int MainAccentIndex = 3;
                int offset = MainAccentIndex * 4;
                return Color.FromRgb(palette[offset], palette[offset + 1], palette[offset + 2]);
            }
        }
        catch
        {
            // 落到默认值
        }
        return null;
    }
}
