using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>全局焦点视觉兜底：元素进视觉树时若 FocusVisualStyle 仍是系统默认就换成空模板。</summary>
public static class FocusVisualKiller
{
    /// <summary>空模板焦点样式 —— 什么都不画</summary>
    private static readonly Style _emptyStyle = CreateEmptyStyle();

    private static bool _installed;

    /// <summary>安装全局兜底</summary>
    public static void Initialize()
    {
        if (_installed) return;
        _installed = true;

        // 类处理器对所有 FrameworkElement 派生类型生效（含 ScrollViewer、
        // ContentControl、RadioButton 等所有可获焦点的控件）。
        System.Windows.EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded));
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var style = element.FocusVisualStyle;

        // 已经是系统默认那份 → 换成空模板，虚线消失
        if (style is null || style.TargetType == typeof(IFrameworkInputElement))
        {
            element.FocusVisualStyle = _emptyStyle;
        }
    }

    /// <summary>构造一个「空模板」的焦点样式</summary>
    private static Style CreateEmptyStyle()
    {
        var style = new Style(typeof(Control));

        // 空 ControlTemplate = 不画任何东西 = 没有虚线
        var template = new ControlTemplate(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }
}
