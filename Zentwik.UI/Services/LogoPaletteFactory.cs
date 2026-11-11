using System;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>应用图标的配色方案</summary>
public static class LogoPaletteFactory
{
    /// <summary>图标原始灰阶（来自 SVG 定义源码）</summary>
    private static readonly Color SourceBody = Color.FromRgb(0xA3, 0xA3, 0xA5);
    private static readonly Color SourceShadow = Color.FromRgb(0x73, 0x73, 0x75);

    /// <summary>计算图标调色板</summary>
    public static AppThemeService.LogoPalette Build(Color accent, bool isDark, Color surface)
    {
        // 主体：强调色，必要时微调明度以保证与表面的对比度
        Color body = EnsureContrast(accent, surface, minRatio: 3.0);

        // 阴影：主体色的暗版（同色相压暗）
        Color shadow = BuildShadow(body, surface);

        return new AppThemeService.LogoPalette(body, shadow);
    }

    /// <summary>保持色相与饱和度，只调整明度，直到与表面的对比度达标</summary>
    private static Color EnsureContrast(Color color, Color surface, double minRatio)
    {
        var (h, s, l) = ColorMath.RgbToHsl(color.R, color.G, color.B);

        // 原色已经达标 → 直接用，不做任何改动。
        if (ContrastRatio(color, surface) >= minRatio)
        {
            return color;
        }

        // 目标方向：深色表面 → 往亮处走；浅色表面 → 往暗处走。
        bool needsLighter = RelativeLuminance(surface) < 0.5;

        // 在明度轴上找最近的达标点。
        const double Step = 1.0;
        for (double delta = Step; delta <= 100.0; delta += Step)
        {
            double target = needsLighter
                ? Math.Min(100.0, l + delta)
                : Math.Max(0.0, l - delta);

            if (Math.Abs(target - l) < 0.001) break;  // 已经到边界

            var (r, g, b) = ColorMath.HslToRgb(h, s, target);
            var candidate = Color.FromRgb(r, g, b);

            if (ContrastRatio(candidate, surface) >= minRatio)
            {
                return candidate;
            }
        }

        // 走到边界仍不达标（极端色相，如纯黄在白底上）→ 退到最大明度/最小明度
        var (fr, fg, fb) = ColorMath.HslToRgb(h, s, needsLighter ? 100.0 : 0.0);
        return Color.FromRgb(fr, fg, fb);
    }

    private static Color BuildShadow(Color body, Color surface)
    {
        var (h, s, l) = ColorMath.RgbToHsl(body.R, body.G, body.B);

        // 暗部：明度压到主体的 45%，饱和度略升（暗部更浓）
        double shadowL = l * 0.45;
        double shadowS = Math.Min(100.0, s * 1.15);

        var (r, g, b) = ColorMath.HslToRgb(h, shadowS, shadowL);
        var shadow = Color.FromRgb(r, g, b);

        // 若阴影与表面糊在一起（对比度 < 1.5:1），再压暗一档
        if (ContrastRatio(shadow, surface) < 1.5)
        {
            var (r2, g2, b2) = ColorMath.HslToRgb(h, shadowS, shadowL * 0.6);
            shadow = Color.FromRgb(r2, g2, b2);
        }

        return shadow;
    }

    /// <summary>相对亮度（WCAG 定义）。</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>对比度（WCAG 定义）。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb);
        double lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>托盘图标配色：不跟随强调色，只跟随任务栏明暗（深→亮灰，浅→暗灰）。</summary>
    public static AppThemeService.LogoPalette BuildForTray(bool darkTaskbar)
    {
        return darkTaskbar
            ? new AppThemeService.LogoPalette(
                Color.FromRgb(0xD8, 0xD8, 0xDC),
                Color.FromRgb(0xA8, 0xA8, 0xAC))
            : new AppThemeService.LogoPalette(
                Color.FromRgb(0x5A, 0x5A, 0x60),
                Color.FromRgb(0x33, 0x33, 0x38));
    }
}
