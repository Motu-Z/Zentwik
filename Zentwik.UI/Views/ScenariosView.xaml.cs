using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

public partial class ScenariosView : UserControl
{
    private Point _gripOrigin;

    private ScenarioRuleItem? _pendingDrag;

    private bool _initialized;

    private Window? _hostWindow;

    public ScenariosView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private ScenariosViewModel? ViewModel => DataContext as ScenariosViewModel;

    /// <summary>Popup 在独立窗口里，不随页面卸载自动消失。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachHostWindow();

        if (ViewModel is { } vm)
            vm.IsProcessPickerOpen = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachHostWindow();

        if (ViewModel is null)
            return;

        // 主窗口缓存页面实例：总闸 / 规则只在首次拉，动作候选每次可见重拉。
        if (_initialized)
        {
            ViewModel.IsProcessPickerOpen = false;
            _ = ViewModel.RefreshActionOptionsAsync();
            return;
        }

        _initialized = true;
        _ = ViewModel.InitializeAsync();
    }

    /// <summary>窗口失焦时收起浮层：Popup 是独立 HWND，会盖在别的应用上面。</summary>
    private void OnHostWindowDeactivated(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm)
            vm.IsProcessPickerOpen = false;
    }

    private void AttachHostWindow()
    {
        Window? window = Window.GetWindow(this);

        if (ReferenceEquals(window, _hostWindow))
            return;

        DetachHostWindow();

        _hostWindow = window;

        if (_hostWindow is not null)
            _hostWindow.Deactivated += OnHostWindowDeactivated;
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is null)
            return;

        _hostWindow.Deactivated -= OnHostWindowDeactivated;
        _hostWindow = null;
    }

    /// <summary>
    /// 点页面别处收起进程候选。
    /// </summary>
    /// <remarks>
    /// Popup 用 StaysOpen=True：StaysOpen=False 会在按下鼠标的那一刻抢走捕获，
    /// 输入框刚获得焦点、浮层刚打开就会被同一次点击判成「外部点击」关掉。
    /// 收起交给这里，同时要放行输入框与浮层内部的点击。
    /// </remarks>
    private void OnSurfacePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        DependencyObject? source = e.OriginalSource as DependencyObject;

        if (ShouldDismissProcessPicker(
                vm.IsProcessPickerOpen,
                IsInsideDetachedPopup(source),
                IsInsideProcessField(source)))
        {
            vm.IsProcessPickerOpen = false;
        }
    }

    internal static bool ShouldDismissProcessPicker(
        bool isPickerOpen,
        bool insidePopup,
        bool insideField)
        => isPickerOpen && !insidePopup && !insideField;

    /// <summary>Popup 的内容在独立视觉树里，按根节点类型判断。</summary>
    private static bool IsInsideDetachedPopup(DependencyObject? source)
    {
        if (source is not Visual visual)
            return false;

        return PresentationSource.FromVisual(visual)?.RootVisual?.GetType().Name == "PopupRoot";
    }

    /// <summary>点击落在输入框本身或它所在的那一格上。</summary>
    private static bool IsInsideProcessField(DependencyObject? source)
    {
        for (DependencyObject? node = source; node is not null; node = ParentOf(node))
        {
            if (node is TextBox)
                return true;

            if (node is FrameworkElement { Name: "ProcBox" })
                return true;
        }

        return false;
    }

    /// <summary>水印那类 ContentElement 走逻辑树，Visual 走视觉树。</summary>
    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        _gripOrigin = e.GetPosition(null);
        _pendingDrag = (sender as FrameworkElement)?.DataContext as ScenarioRuleItem;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pendingDrag = null;
            return;
        }

        if (_pendingDrag is null)
            return;

        Point now = e.GetPosition(null);

        if (Math.Abs(now.X - _gripOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _gripOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ScenarioRuleItem dragged = _pendingDrag;
        _pendingDrag = null;

        DragDrop.DoDragDrop((DependencyObject)sender, dragged, DragDropEffects.Move);
    }

    /// <summary>不设置 Effects 时 Drop 永远不触发，拖拽看着「没反应」。</summary>
    private void OnRuleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ScenarioRuleItem))
            ? DragDropEffects.Move
            : DragDropEffects.None;

        e.Handled = true;
    }

    /// <summary>松手时落在哪张卡上，就插到那个位置。</summary>
    private void OnRuleDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ScenarioRuleItem)) is not ScenarioRuleItem dragged)
            return;

        if ((sender as FrameworkElement)?.DataContext is not ScenarioRuleItem target)
            return;

        if (ViewModel is not { } vm || ReferenceEquals(dragged, target))
            return;

        vm.MoveRuleTo(dragged, vm.Rules.IndexOf(target));
        e.Handled = true;
    }

    private void OnProcessBoxFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { IsProcessKind: true } vm)
            vm.OpenProcessPickerCommand.Execute(null);
    }

    private void OnProcessPick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ScenarioProcessOption option)
            return;

        ViewModel?.PickProcessCommand.Execute(option);
        e.Handled = true;
    }
}
