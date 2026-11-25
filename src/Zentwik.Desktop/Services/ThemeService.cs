using System.Windows;
using System.Windows.Media;

namespace Zentwik.Desktop.Services;

/// <summary>只替换资源值，不重建控件树。</summary>
public sealed class ThemeService
{
    private const int ThemeDictionaryIndex = 1;

    private static readonly IReadOnlyDictionary<string, (Color Dark, Color Light)> Accents =
        new Dictionary<string, (Color, Color)>(StringComparer.OrdinalIgnoreCase)
        {
            ["blue"] = (Color.FromRgb(0x4C, 0x8D, 0xFF), Color.FromRgb(0x2F, 0x6F, 0xE4)),
            ["violet"] = (Color.FromRgb(0xA9, 0x7B, 0xFF), Color.FromRgb(0x7C, 0x4D, 0xFF)),
            ["teal"] = (Color.FromRgb(0x35, 0xC2, 0xB0), Color.FromRgb(0x0E, 0x9C, 0x8A)),
            ["amber"] = (Color.FromRgb(0xE2, 0xA9, 0x3B), Color.FromRgb(0xB0, 0x7A, 0x0F)),
        };

    public string Theme { get; private set; } = "dark";

    public string Accent { get; private set; } = "blue";

    public void Apply(string theme, string accent)
    {
        Theme = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        Accent = Accents.ContainsKey(accent) ? accent.ToLowerInvariant() : "blue";

        var resources = Application.Current.Resources;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Zentwik.Desktop;component/Themes/{(Theme == "light" ? "Light" : "Dark")}.xaml",
                UriKind.Absolute),
        };

        while (resources.MergedDictionaries.Count > ThemeDictionaryIndex)
        {
            resources.MergedDictionaries.RemoveAt(ThemeDictionaryIndex);
        }

        resources.MergedDictionaries.Insert(ThemeDictionaryIndex, dictionary);

        var (darkColor, lightColor) = Accents[Accent];
        var color = Theme == "light" ? lightColor : darkColor;
        resources["Brush.Accent"] = new SolidColorBrush(color);
        resources["Brush.Accent.Soft"] = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B));
    }
}
