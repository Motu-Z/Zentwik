using System.Text;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>
/// 颜色数学：HSL ↔ RGB 互转 + 品牌色衍生。
/// </summary>
/// <remarks>
/// 纯函数，无副作用，便于单测。
/// H 用「度」（0~360），S/L 用「百分数」（0~100），
/// 与参考 HTML 的 hslToHex / hexToHsl 语义保持一致。
/// </remarks>
public static class ColorMath
{
    /// <summary>
    /// RGB → HSL。
    /// </summary>
    public static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2.0;

        if (max == min)
        {
            // 灰色：色相无意义
            return (0, 0, l * 100.0);
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == rd) h = (gd - bd) / d + (gd < bd ? 6 : 0);
        else if (max == gd) h = (bd - rd) / d + 2;
        else h = (rd - gd) / d + 4;
        h *= 60.0;

        return (h, s * 100.0, l * 100.0);
    }

    /// <summary>
    /// HSL → RGB。H 为度，S/L 为百分数。
    /// </summary>
    public static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        // 先把 H 归一到 [0,360)
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 100.0) / 100.0;
        l = Math.Clamp(l, 0.0, 100.0) / 100.0;

        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        double rd, gd, bd;
        if (h < 60) { rd = c; gd = x; bd = 0; }
        else if (h < 120) { rd = x; gd = c; bd = 0; }
        else if (h < 180) { rd = 0; gd = c; bd = x; }
        else if (h < 240) { rd = 0; gd = x; bd = c; }
        else if (h < 300) { rd = x; gd = 0; bd = c; }
        else { rd = c; gd = 0; bd = x; }

        return (
            (byte)Math.Round((rd + m) * 255.0),
            (byte)Math.Round((gd + m) * 255.0),
            (byte)Math.Round((bd + m) * 255.0));
    }

    /// <summary>
    /// 品牌色（twik）衍生算法。
    ///
    /// 规则（与参考 HTML 的 applyAccentColor 一致）：
    ///   H ← (H + 20) mod 360
    ///   S ← min(100, S + 15)      （15 个百分点，不是乘 1.15）
    ///   L ← 深色模式加亮 / 浅色模式压暗
    /// </summary>
    /// <param name="accent">当前强调色。</param>
    /// <param name="isDark">当前是否为深色主题。</param>
    public static Color DeriveBrandColor(Color accent, bool isDark)
    {
        var (h, s, l) = RgbToHsl(accent.R, accent.G, accent.B);

        double nh = (h + 20.0) % 360.0;
        double ns = Math.Min(100.0, s + 15.0);
        double nl = isDark ? Math.Min(100.0, l + 20.0) : Math.Max(0.0, l - 20.0);

        var (r, g, b) = HslToRgb(nh, ns, nl);
        return Color.FromRgb(r, g, b);
    }

    /// <summary>
    /// 派生品牌色的三层调色板（主色 / 中间调 / 高光）。
    /// </summary>
    /// <remarks>
    /// 用于 Zentwik 应用图标的三层填充 ——
    /// 原 SVG 是灰阶三档（#000000 / #7F7F7F / #C3C3C3），
    /// 换成品牌色系后图标能跟随强调色变化。
    ///
    /// 原图三层的**相对亮度**是 0% / 50% / 77%。
    /// 若直接对品牌色做 ±12 点的偏移，在浅色品牌色（如 #C6DAFF，
    /// L 已经接近 90）上会被夹住 —— 三层挤在一起糊成一团。
    ///
    /// 所以这里按品牌色自身的亮度**等比映射**：
    ///   中间调 = L × 0.72（对应原图的 50% 档）
    ///   高光   = L × 0.92 + 6（对应原图的 77% 档，且保留一点亮度差）
    /// 无论品牌色多亮多暗，三层之间都保持可见的层次。
    /// </remarks>
    public static (Color Main, Color Mid, Color Light) DeriveBrandPalette(Color brand)
    {
        var (h, s, l) = RgbToHsl(brand.R, brand.G, brand.B);

        // 中间调：压暗到主色的 72%
        var (mr, mg, mb) = HslToRgb(h, s, l * 0.72);

        // 高光：略提亮但明显比主色亮
        var (lr, lg, lb) = HslToRgb(h, Math.Min(100.0, s * 0.55), Math.Min(100.0, l * 0.92 + 6.0));

        return (
            brand,
            Color.FromRgb(mr, mg, mb),
            Color.FromRgb(lr, lg, lb)
        );
    }

    /// <summary>
    /// 解析 HEX。支持 #RGB / #RRGGBB，带不带 # 都能解析。
    /// </summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];

        if (s.Length == 3)
        {
            // #RGB → #RRGGBB
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length != 6) return false;

        if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        color = Color.FromRgb(
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
        return true;
    }

    /// <summary>
    /// 格式化为 #RRGGBB 大写。
    /// </summary>
    public static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}

/// <summary>
/// 颜色算法的自检。数值基准取自参考 HTML 的 JS 实现（逐字节复算对齐）。
/// </summary>
/// <remarks>
/// 这套断言的「期望值」不是拍脑袋写的，而是把参考 HTML 里的
/// hslToHex / hexToHsl / applyAccentColor 三个 JS 函数原样搬到 C# 侧复算出来的。
/// 两边结果必须逐字节相同，否则说明转写有偏差。
///
/// 运行方式：dotnet run --project Zentwik.UI.csproj -- --selftest
/// </remarks>
public static class ColorMathSelfTest
{
    private static int _failures;

    public static int Run() => Run(null);

    /// <summary>
    /// 执行自检。<paramref name="output"/> 非空时把结果写入该缓冲区。
    /// </summary>
    public static int Run(StringBuilder? output)
    {
        _failures = 0;
        _sink = output;

        // ---- HSL 往返 ----
        // 基准色 = 参考 HTML 的默认强调色 #38BDF8
        var (h, s, l) = ColorMath.RgbToHsl(0x38, 0xBD, 0xF8);
        Check("HSL: #38BDF8 的 H ≈ 198.44", Math.Abs(h - 198.44) < 0.05, $"H={h:F2}");
        Check("HSL: #38BDF8 的 S ≈ 93.20", Math.Abs(s - 93.20) < 0.05, $"S={s:F2}");
        Check("HSL: #38BDF8 的 L ≈ 59.61", Math.Abs(l - 59.61) < 0.05, $"L={l:F2}");

        // ---- HSL -> RGB：与 JS hslToHex 逐字节对齐 ----
        Check("HSL→RGB: hsl(198,85,65) = #5AC4F2",
              ColorMath.ToHex(ToColor(ColorMath.HslToRgb(198, 85, 65))) == "#5AC4F2");
        Check("HSL→RGB: hsl(0,85,65) = #F25A5A",
              ColorMath.ToHex(ToColor(ColorMath.HslToRgb(0, 85, 65))) == "#F25A5A");
        Check("HSL→RGB: hsl(120,85,65) = #5AF25A",
              ColorMath.ToHex(ToColor(ColorMath.HslToRgb(120, 85, 65))) == "#5AF25A");

        // ---- 品牌色衍生：与 JS applyAccentColor 对齐 ----
        var accent = Color.FromRgb(0x38, 0xBD, 0xF8);
        var brandDark = ColorMath.DeriveBrandColor(accent, isDark: true);
        var brandLight = ColorMath.DeriveBrandColor(accent, isDark: false);

        Check("品牌色: 深色模式 = #97BCFF", ColorMath.ToHex(brandDark) == "#97BCFF",
              ColorMath.ToHex(brandDark));
        Check("品牌色: 浅色模式 = #0049CA", ColorMath.ToHex(brandLight) == "#0049CA",
              ColorMath.ToHex(brandLight));

        var (bh, bs, bl) = ColorMath.RgbToHsl(brandDark.R, brandDark.G, brandDark.B);
        Check("品牌色: H ≈ 218.65 (原 H + 20)", Math.Abs(bh - 218.65) < 0.05, $"H={bh:F2}");
        Check("品牌色: S = 100 (原 S + 15，已封顶)", Math.Abs(bs - 100) < 0.5, $"S={bs:F2}");
        Check("品牌色: 深色 L ≈ 79.61 (原 L + 20)", Math.Abs(bl - 79.61) < 0.05, $"L={bl:F2}");

        // ---- HEX 解析 ----
        Check("HEX: 解析 #38BDF8", TryParse("#38BDF8", out var c1) && c1 == accent);
        Check("HEX: 解析无 # 且小写", TryParse("38bdf8", out var c2) && c2 == accent);
        Check("HEX: 解析三位 #ABC", TryParse("#ABC", out var c3) && c3 == Color.FromRgb(0xAA, 0xBB, 0xCC));

        foreach (var bad in new[] { "#12", "#", "", "#GGGGGG", "xyz" })
        {
            Check($"HEX: 拒绝非法输入 '{bad}'", !ColorMath.TryParseHex(bad, out _));
        }

        return _failures;
    }

    private static bool TryParse(string s, out Color c) => ColorMath.TryParseHex(s, out c);

    private static Color ToColor((byte R, byte G, byte B) t) => Color.FromRgb(t.R, t.G, t.B);

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        string line = $"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}";
        Console.WriteLine(line);
        _sink?.AppendLine(line);
    }

    private static StringBuilder? _sink;
}
