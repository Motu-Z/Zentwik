using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>视觉树查询。</summary>
public static class VisualTreeAssist
{
    /// <summary>
    /// 取父级。视觉元素走视觉树，其余走逻辑树。
    ///
    /// <c>VisualTreeHelper.GetParent</c> 只接受 Visual / Visual3D，碰到
    /// <c>Run</c>、<c>Hyperlink</c> 这类 <see cref="System.Windows.Documents.ContentElement"/>
    /// 会直接抛「不是 Visual 或 Visual3D」。
    /// 而点在内联文本上是常规操作 —— 掩码卡片上的「CORE 0,2,4…」就是一串 Run，
    /// 进程页的空白点击处理一路向上找 TextBox 时就会撞上它。
    /// </summary>
    public static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    /// <summary>该元素是否位于某个 TextBox 内部</summary>
    public static bool IsInsideTextBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox)
                return true;

            source = ParentOf(source);
        }

        return false;
    }

    /// <summary>该元素是否位于需要保留焦点的交互控件内部</summary>
    public static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ComboBox or ComboBoxItem or ButtonBase or ScrollBar or Slider)
                return true;

            source = ParentOf(source);
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

        // 非视觉节点没有子级可枚举，GetChildrenCount 会抛。
        if (root is not Visual)
            return false;

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            if (HasOpenDropDown(VisualTreeHelper.GetChild(root, i)))
                return true;
        }

        return false;
    }
}
