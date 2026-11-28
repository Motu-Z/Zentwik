using System.Drawing;

namespace Zentwik.Desktop.Services;

/// <summary>
/// 只记录主题选择并提供两套基础配色，不做控件级换肤。
///
/// 这一版 UI 是 WinForms 裸控件：原生控件的绘制由系统主题决定，
/// 强行改色只会得到半深半浅的杂色，所以这里只负责「窗口底色 + 前景色」，
/// 面板和列表一律交给系统默认。等交互都通了再谈换肤。
/// </summary>
public sealed class ThemeService
{
    private static readonly IReadOnlyDictionary<string, (int Dark, int Light)> Accents =
        new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["blue"] = (0x4C8DFF, 0x2F6FE4),
            ["violet"] = (0xA97BFF, 0x7C4DFF),
            ["teal"] = (0x35C2B0, 0x0E9C8A),
            ["amber"] = (0xE2A93B, 0xB07A0F),
        };

    /// <summary>主题或强调色变化时触发，供窗口重刷底色。</summary>
    public event Action? Changed;

    public string Theme { get; private set; } = "dark";

    public string Accent { get; private set; } = "blue";

    public bool IsDark => Theme == "dark";

    public Color WindowBack => IsDark ? Color.FromArgb(0x1E, 0x1E, 0x1E) : SystemColors.Window;

    public Color WindowFore => IsDark ? Color.FromArgb(0xF0, 0xF0, 0xF0) : SystemColors.WindowText;

    /// <summary>强调色，用于需要「一眼看出是主操作」的少数位置（如状态点）。</summary>
    public Color AccentColor
    {
        get
        {
            var (dark, light) = Accents.TryGetValue(Accent, out var pair) ? pair : Accents["blue"];
            var rgb = IsDark ? dark : light;
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
    }

    public void Apply(string theme, string accent)
    {
        var nextTheme = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        var nextAccent = Accents.ContainsKey(accent) ? accent.ToLowerInvariant() : "blue";
        if (nextTheme == Theme && nextAccent == Accent)
        {
            return;
        }

        Theme = nextTheme;
        Accent = nextAccent;
        Changed?.Invoke();
    }
}
