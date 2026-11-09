using System.Windows;

namespace Zentwik.UI.Controls;

/// <summary>
/// 导航项的文字标签（侧栏展开时才显示）。
/// </summary>
/// <remarks>
/// Tag 已经用来放路由键（Tuning / Process …），ToolTip 是浮层内容 ——
/// 两者都不该兼职当可见文字。单独挂一个附加属性，模板里直接绑，
/// 以后要把标签与提示语分开也不用改结构。
/// </remarks>
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
