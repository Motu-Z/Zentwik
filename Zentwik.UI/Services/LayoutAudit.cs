using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>布局越界审计：把页面按给定尺寸排一遍，找出任何右边界越过内容区的交互元素</summary>
public static class LayoutAudit
{
    /// <summary>右边界允许的亚像素误差。</summary>
    private const double Tolerance = 1.0;

    /// <summary>找出越过页面右边界的可交互元素，返回「类型 + 自动化名称」描述</summary>
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
