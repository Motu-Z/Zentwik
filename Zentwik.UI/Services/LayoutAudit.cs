using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>
/// 布局越界审计：把页面按给定尺寸排一遍，找出任何右边界越过内容区的交互元素。
/// </summary>
/// <remarks>
/// 「窗口一窄，最右一列被切掉半截」这个问题反复出现过三次，
/// 每次都是靠修完再肉眼比对截图才发现。截图只能证明「这一次对了」，
/// 证明不了「以后谁改坏了会有人知道」。
/// 把判断写成断言，改坏就当场红。
///
/// 只审计可交互控件（按钮、下拉框、输入框、复选框），不审计文本与装饰，
/// 因为文本被 TextTrimming 截断是设计允许的，而控件被切掉就是不能用。
/// </remarks>
public static class LayoutAudit
{
    /// <summary>右边界允许的亚像素误差。</summary>
    private const double Tolerance = 1.0;

    /// <summary>
    /// 找出越过页面右边界的可交互元素，返回「类型 + 自动化名称」描述。
    /// </summary>
    public static List<string> FindOverflow(FrameworkElement root)
    {
        var result = new List<string>();

        root.UpdateLayout();

        double limit = root.ActualWidth;

        if (limit <= 0)
        {
            return result;
        }

        Walk(root, root, limit, result);
        return result;
    }

    private static void Walk(DependencyObject parent, FrameworkElement root, double limit, List<string> result)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is not FrameworkElement element)
            {
                continue;
            }

            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0)
            {
                continue;
            }

            if (IsInteractive(element))
            {
                double right = GetRightEdge(element, root);

                if (right > limit + Tolerance)
                {
                    string name = AutomationProperties.GetName(element);

                    result.Add(
                        $"{element.GetType().Name}" +
                        (string.IsNullOrEmpty(name) ? string.Empty : $"「{name}」") +
                        $" 右边界 {right:F0} > {limit:F0}");
                }
            }

            Walk(element, root, limit, result);
        }
    }

    private static double GetRightEdge(FrameworkElement element, FrameworkElement root)
    {
        try
        {
            var bounds = element
                .TransformToAncestor(root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            return bounds.Right;
        }
        catch (System.InvalidOperationException)
        {
            // 不在同一棵可视树里（例如被移到 Popup），跳过。
            return 0;
        }
    }

    private static bool IsInteractive(FrameworkElement element) =>
        element is Button
        or TextBox
        or ComboBox
        or CheckBox
        or RadioButton
        or ToggleButton
        or Slider;
}
