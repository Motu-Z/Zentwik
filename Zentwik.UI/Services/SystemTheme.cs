using Microsoft.Win32;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>系统主题与强调色读取，全走注册表。</summary>
/// <remarks>DWM\AccentColor 是 COLORREF（0x00BBGGRR）；AccentPalette 每 4 字节为 RGBA，主色在索引 3。</remarks>
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

    /// <summary>读取系统强调色</summary>
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

    /// <summary>读 DWM\AccentColor（COLORREF 0x00BBGGRR）</summary>
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

    /// <summary>读 Explorer\Accent\AccentPalette 的主色组（索引 3），RGBA 字节序</summary>
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
