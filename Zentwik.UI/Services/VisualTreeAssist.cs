using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>
    /// 该元素是否位于需要保留焦点的交互控件内部。
    /// ComboBox 候选项在 Popup 里，选中的那一刻若被外部清走键盘焦点，
    /// 下拉会先关闭、选择随之回滚，界面表现为「选完文字没变」。
    /// </summary>
    public static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ComboBox or ComboBoxItem or ButtonBase or ScrollBar or Slider)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>树里是否有正展开的下拉。自动刷新前查一次，避免重建列表把候选项收走。</summary>
    public static bool HasOpenDropDown(DependencyObject? root)
    {
        if (root is null)
            return false;

        if (root is ComboBox { IsDropDownOpen: true })
            return true;

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            if (HasOpenDropDown(VisualTreeHelper.GetChild(root, i)))
                return true;
        }

        return false;
    }
}
