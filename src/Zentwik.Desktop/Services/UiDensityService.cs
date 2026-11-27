using System.Windows;

namespace Zentwik.Desktop.Services;

public static class UiDensityService
{
    public const string Comfortable = "comfortable";
    public const string Standard = "standard";
    public const string Compact = "compact";

    public static string CurrentDensity { get; private set; } = Standard;

    public static bool ShowUnavailableModules { get; set; }

    public static void Apply(string density)
    {
        var normalized = Normalize(density);
        CurrentDensity = normalized;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        (Thickness rowMargin, Thickness cardPadding) = normalized switch
        {
            Comfortable => (new Thickness(16, 13, 16, 13), new Thickness(18, 16, 18, 16)),
            Compact => (new Thickness(10, 6, 10, 6), new Thickness(12, 10, 12, 10)),
            _ => (new Thickness(14, 10, 14, 10), new Thickness(16, 14, 16, 14)),
        };

        resources["DensityRowMargin"] = rowMargin;
        resources["DensityCardPadding"] = cardPadding;
    }

    private static string Normalize(string? density) => density switch
    {
        Comfortable => Comfortable,
        Compact => Compact,
        _ => Standard,
    };
}
