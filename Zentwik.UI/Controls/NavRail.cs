using System.Windows;

namespace Zentwik.UI.Controls;

/// <summary>导航项的文字标签（侧栏展开时才显示）</summary>
public static class NavRail
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached(
            "Label",
            typeof(string),
            typeof(NavRail),
            new FrameworkPropertyMetadata(string.Empty));

    public static string GetLabel(DependencyObject element) =>
        (string)element.GetValue(LabelProperty);

    public static void SetLabel(DependencyObject element, string value) =>
        element.SetValue(LabelProperty, value);
}
