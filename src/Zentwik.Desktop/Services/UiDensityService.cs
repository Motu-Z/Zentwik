using System.Windows;

namespace Zentwik.Desktop.Services;

public static class UiDensityService
{
    public static string CurrentDensity { get; private set; } = "标准";

    public static bool ShowUnavailableModules { get; set; }

    public static void Apply(string density)
    {
        CurrentDensity = density;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        (Thickness rowMargin, Thickness cardPadding) = density switch
        {
            "宽松" => (new Thickness(16, 13, 16, 13), new Thickness(18, 16, 18, 16)),
            "紧凑" => (new Thickness(10, 6, 10, 6), new Thickness(12, 10, 12, 10)),
            _ => (new Thickness(14, 10, 14, 10), new Thickness(16, 14, 16, 14)),
        };

        resources["DensityRowMargin"] = rowMargin;
        resources["DensityCardPadding"] = cardPadding;
    }
}
