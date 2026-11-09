using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>
/// 全局焦点视觉兜底 —— 保证应用内**不存在任何原生点划线焦点框**。
///
/// </summary>
/// <remarks>
/// WPF 的 FrameworkElement.FocusVisualStyle 默认值来自系统主题
/// （SystemParameters.FocusVisualStyle），它画的是一圈**点划线**矩形。
/// 这个默认值只会在两种情况下被替换：
///   1. 控件显式设置了 FocusVisualStyle
///   2. Application.Resources 里用 FocusVisualStyleKey 覆盖主题资源
///
/// 但【情况 2 实测无效】：SystemParameters.FocusVisualStyle 走的是
/// SystemResources（主题资源字典），不走 Application.Resources，
/// 所以在 App.xaml 里覆盖那个键并不能阻止虚线出现。
/// 唯一可靠的办法就是**逐个控件显式设置**。
///
/// 而「逐个设置」天然会漏 —— 漏一个就是一处丑陋虚线。
/// 这里用类处理器兜底：任何 FrameworkElement 进入视觉树（Loaded）时，
/// 若它的 FocusVisualStyle 仍是系统默认那份，就替换成空模板样式。
///
/// 系统默认焦点样式的 TargetType 是 IFrameworkInputElement；
/// 我们自己设的（NoFocusVisual / KeyboardFocusRing）TargetType 是 Control。
/// 只替换前者 —— 否则会把我们自绘的焦点指示也抹掉。
///
/// 焦点虚线在控件**获得键盘焦点**时才被绘制；
/// Loaded 早于首次获得焦点，此时替换来得及。
/// </remarks>
public static class FocusVisualKiller
{
    /// <summary>
    /// 空模板焦点样式 —— 什么都不画。
    /// </summary>
    private static readonly Style _emptyStyle = CreateEmptyStyle();

    private static bool _installed;

    /// <summary>
    /// 安装全局兜底。应用启动时调用一次。
    /// </summary>
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

    /// <summary>
    /// 构造一个「空模板」的焦点样式。
    /// </summary>
    /// <remarks>
    /// TargetType 必须是 Control —— FocusVisualStyle 由控件的
    /// adorner 机制应用，要求样式目标类型与控件兼容。
    /// </remarks>
    private static Style CreateEmptyStyle()
    {
        var style = new Style(typeof(Control));

        // 空 ControlTemplate = 不画任何东西 = 没有虚线
        var template = new ControlTemplate(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }
}
