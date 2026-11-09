using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zentwik.UI.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>进程页。</summary>
public partial class ProcessView : UserControl
{
    private bool _initialized;
    public ProcessView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewMouseLeftButtonDown += OnSurfacePreviewMouseDown;
    }

    private ProcessViewModel? ViewModel => DataContext as ProcessViewModel;

    /// <summary>点空白处归还键盘焦点，搜索框下划线不会一直亮着。</summary>
    private void OnSurfacePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeAssist.IsInsideTextBox(e.OriginalSource as DependencyObject))
            return;

        if (VisualTreeAssist.IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
            return;

        Keyboard.ClearFocus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.DrawerStateChanged -= OnDrawerStateChanged;
        ViewModel.DrawerStateChanged += OnDrawerStateChanged;

        SnapDrawer(ViewModel.IsDrawerOpen);

        if (_initialized)
            return;

        _initialized = true;
        _ = ViewModel.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.DrawerStateChanged -= OnDrawerStateChanged;
        }
    }

    private void OnDrawerStateChanged(bool open)
    {
        // 诊断模式落终值：离屏截图不驱动动画。
        if (ProcessViewModel.SuppressWorkerInit)
        {
            SnapDrawer(open);
            return;
        }

        double target = open ? LayoutMetrics.ProcessDrawerWidth : 0;

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        animation.Completed += (_, _) =>
        {
            DrawerHost.BeginAnimation(WidthProperty, null);
            DrawerHost.Width = target;
        };

        DrawerHost.BeginAnimation(WidthProperty, animation);
    }

    /// <summary>跳过动画直接落到目标宽度。截图与自检走这里，避免拍到中间帧。</summary>
    public void SnapDrawer(bool open)
    {
        DrawerHost.BeginAnimation(WidthProperty, null);
        DrawerHost.Width = open ? LayoutMetrics.ProcessDrawerWidth : 0;
    }
}
