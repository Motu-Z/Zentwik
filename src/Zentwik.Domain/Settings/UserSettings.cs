namespace Zentwik.Domain.Settings;

public sealed record UserSettings
{
    public string Language { get; init; } = "zh-CN";

    public string Theme { get; init; } = "dark";

    public string Accent { get; init; } = "blue";

    public double UiScale { get; init; } = 1d;
}

public static class UserSettingsRules
{
    public static readonly string[] Languages = ["zh-CN", "zh-TW", "en-US"];

    public static readonly string[] Themes = ["dark", "light"];

    public static readonly string[] Accents = ["blue", "violet", "teal", "amber"];

    public const double MinScale = 0.8d;

    public const double MaxScale = 1.6d;

    /// <summary>非法值整条拒绝：静默修正会让界面显示与实际生效不一致。</summary>
    public static bool TryApply(
        UserSettings current,
        string? language,
        string? theme,
        string? accent,
        double? uiScale,
        out UserSettings updated,
        out string? error)
    {
        updated = current;
        error = null;

        if (language is not null)
        {
            var match = Languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"不支持的语言：{language}";
                return false;
            }

            updated = updated with { Language = match };
        }

        if (theme is not null)
        {
            var match = Themes.FirstOrDefault(t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"不支持的主题：{theme}";
                return false;
            }

            updated = updated with { Theme = match };
        }

        if (accent is not null)
        {
            var match = Accents.FirstOrDefault(a => string.Equals(a, accent, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"不支持的强调色：{accent}";
                return false;
            }

            updated = updated with { Accent = match };
        }

        if (uiScale is not null)
        {
            if (uiScale is < MinScale or > MaxScale)
            {
                error = $"界面缩放超出范围 {MinScale}–{MaxScale}。";
                return false;
            }

            updated = updated with { UiScale = Math.Round(uiScale.Value, 2) };
        }

        return true;
    }
}
