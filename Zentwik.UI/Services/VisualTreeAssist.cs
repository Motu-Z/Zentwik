using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>视觉树查询。</summary>
public static class VisualTreeAssist
{
    /// <summary>
    /// 该元素是否位于某个 <see cref="TextBox"/> 内部。
    /// 用于「点页面空白清焦点」时排除输入区域。
    /// </summary>
    public static bool IsInsideTextBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
