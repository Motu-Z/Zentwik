using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Zentwik.UI.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>进程页。</summary>
public partial class ProcessView : UserControl
{
    private const int AutoRefreshSeconds = 3;

    private bool _initialized;
    private bool _refreshing;
    private readonly DispatcherTimer _autoRefreshTimer;

    public ProcessView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewMouseLeftButtonDown += OnSurfacePreviewMouseDown;

        _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(AutoRefreshSeconds)
        };
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
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
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.DrawerStateChanged -= OnDrawerStateChanged;
        vm.DrawerStateChanged += OnDrawerStateChanged;

        SnapDrawer(vm.IsDrawerOpen);

        if (!ProcessViewModel.SuppressWorkerInit)
        {
            _autoRefreshTimer.Start();
        }

        if (!_initialized)
        {
            // 首次要连拓扑一起拉：下拉里的 CCD 条数由它决定。
            _initialized = true;
            RequestRefresh(quiet: false, () => vm.InitializeAsync());
            return;
        }

        // 页面实例被缓存，切回来不会再走 InitializeAsync，手动切排序也是全量刷新。
        RequestRefresh(quiet: false, () => vm.RefreshAsync());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoRefreshTimer.Stop();

        if (ViewModel is not null)
        {
            ViewModel.DrawerStateChanged -= OnDrawerStateChanged;
        }
    }

    /// <summary>停留在本页时定时采一次占用；切走即停，不做常驻轮询。</summary>
    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        // 有未提交的改动、抽屉开着、下拉正展开时都不动列表，
        // 否则重建会把用户刚选的值冲掉。
        if (_refreshing
            || vm.IsDrawerOpen
            || vm.RunningProcesses.Any(p => p.IsDirty)
            || vm.SavedRules.Any(r => r.IsDirty)
            || VisualTreeAssist.HasOpenDropDown(this))
        {
            return;
        }

        RequestRefresh(quiet: true, () => vm.RefreshProcessesLightAsync(quiet: true));
    }

    private void RequestRefresh(bool quiet, Func<Task> action)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        AsyncRunner.Fire(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch when (quiet)
            {
                // 后台没就绪时不每轮弹一次气泡。
            }
            finally
            {
                _refreshing = false;
            }
        });
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
