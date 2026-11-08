using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>方案面板：列表、新建、改名、删除、应用。</summary>
public partial class ProfileOverlay : UserControl
{
    public ProfileOverlay()
    {
        InitializeComponent();
    }

    /// <summary>点面板空白处退出命名态；行内点击交给行自己的处理。</summary>
    /// <remarks>
    /// 【输入框与按钮必须先排除】
    /// 这里是 Preview，早于控件自己的处理。点新建输入框或「创建」按钮时
    /// 命中源不在任何方案行里，于是被当成「点了空白」→ 取消新建 ——
    /// 表现为输入框刚点就消失、名字都打不进去。
    /// </remarks>
    private void OnOverlayPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PlanScope scope)
            return;

        if (IsInteractiveChild(e.OriginalSource as DependencyObject))
            return;

        for (DependencyObject? node = e.OriginalSource as DependencyObject;
             node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: PlanItem })
                return;
        }

        if (scope.IsCreating)
            scope.CancelCreateCommand.Execute(null);

        foreach (PlanItem item in scope.Items)
        {
            if (item.IsRenaming)
                scope.CancelRenameCommand.Execute(item);
        }
    }

    /// <summary>退出改名态时把输入框收起来用的标记，见 <see cref="OnRowMouseLeftButtonDown"/>。</summary>
    private static PlanScope? ScopeOf(object? dataContext) => dataContext as PlanScope;

    // ==================== 行的点击：切换 ====================

    /// <summary>点行 = 选为当前方案；子控件的点击由 IsInteractiveChild 排除。</summary>
    private void OnRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ScopeOf(DataContext) is not { } scope)
            return;

        if (IsInteractiveChild(e.OriginalSource as DependencyObject))
            return;

        if (sender is not FrameworkElement { DataContext: PlanItem item })
            return;

        // 改名态下点行 = 提交改名 + 选中（一次点击干完两件事）。
        if (item.IsRenaming)
        {
            scope.CommitRenameCommand.Execute(item);
            return;
        }

        // 删除确认态下点行不做切换 —— 用户正在做危险操作，
        // 误点行就换方案会让人以为「删掉了还顺手换了一份」。
        if (item.IsConfirmingDelete)
            return;

        scope.SelectCommand.Execute(item);
    }

    /// <summary>命中源是否落在交互子控件里（按钮、输入框）。</summary>
    /// <remarks>
    /// 必须用 <see cref="VisualTreeHelper.GetParent"/> 而不是逻辑树 ——
    /// 后者对非 FrameworkElement 节点（TextBox 的内部绘制对象）会抛异常。
    /// </remarks>
    internal static bool IsInteractiveChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase || source is TextBox)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    // ==================== 改名 ====================

    /// <summary>重命名输入框：回车提交、ESC 取消。</summary>
    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (ScopeOf(DataContext) is not { } scope)
            return;

        if ((sender as TextBox)?.DataContext is not PlanItem item)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                scope.CommitRenameCommand.Execute(item);
                e.Handled = true;
                break;

            case Key.Escape:
                scope.CancelRenameCommand.Execute(item);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 重命名输入框出现时自动聚焦并选中原名。
    /// </summary>
    /// <remarks>
    /// 不聚焦的话，用户还得先点一下输入框才知道能打字 —— 看起来像「点了没反应」。
    /// 输入框靠 Visibility 切换显示，切进来时会重新 Loaded 一次，时机正好。
    /// </remarks>
    private void OnRenameTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        box.Focus();
        box.SelectAll();
        box.BringIntoView();
    }

    // ==================== 内联新建 ====================

    /// <summary>新建输入框出现时聚焦并全选默认名，用户可以直接覆盖。</summary>
    private void OnCreateBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        box.Focus();
        box.SelectAll();
    }

    /// <summary>新建输入框：回车创建、ESC 取消。</summary>
    private void OnCreateKeyDown(object sender, KeyEventArgs e)
    {
        if (ScopeOf(DataContext) is not { } scope)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                scope.CommitCreateCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                scope.CancelCreateCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
