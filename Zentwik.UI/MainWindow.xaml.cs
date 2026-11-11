using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Zentwik.UI.Services;
using Zentwik.UI.ViewModels;
using Zentwik.UI.Views;

namespace Zentwik.UI;

public partial class MainWindow : Window
{
    private const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>进后台多久之后才做效率模式处理。</summary>
    private const int IdleDelaySeconds = 20;

    private readonly DispatcherTimer _idleTimer;

    /// <summary>后台自己切了方案（场景规则）时把顶栏跟过去。</summary>
    private readonly PlanWatcher _planWatcher;

    public MainWindow()
    {
        // 资源字典必须先于 InitializeComponent 就位。
        AppThemeService.Instance.Initialize();

        InitializeComponent();

        _planWatcher = new PlanWatcher(() => IsForeground);

        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(WindowStateProperty, typeof(Window))
            .AddValueChanged(this, (_, _) =>
            {
                UpdateMaximizeButton();
                UpdateIdleState();
            });

        // 后台效率模式：进后台 20 秒才动手，免得切来切去反复换入换出。
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(IdleDelaySeconds)
        };
        _idleTimer.Tick += OnIdleTick;
        IsVisibleChanged += (_, _) => UpdateIdleState();

        // 版本号来自程序集元数据，不在 XAML / C# 里硬编码
        SidebarHeader.ToolTip = $"Zentwik v{GetVersion()}\n点击展开侧栏";

        // 侧栏宽度跟随外壳状态
        ShellState.Instance.PropertyChanged += OnShellStateChanged;
        // 窗口激活时对一次生效方案：场景规则在后台自己切方案，不带这条
        // 顶栏会一直停在旧方案上。
        Activated += (_, _) => _planWatcher.Poke();

        Closed += (_, _) =>
        {
            ShellState.Instance.PropertyChanged -= OnShellStateChanged;
            _idleTimer.Stop();
            _planWatcher.Stop();
        };

        Navigate("Misc");

        _ = BootWorkerAsync();
    }

    // Worker 连通性

    /// <summary>启动后把 Worker 拉起来并刷新状态灯</summary>
    private async Task BootWorkerAsync()
    {
        try
        {
            await WorkerLauncher.EnsureRunningAsync();
        }
        catch (Exception)
        {
            // EnsureRunningAsync 自身不抛；这里只兜底，避免未观察的任务异常。
        }

        await ProbeWorkerAsync(force: true);

        // 连上了才拉方案列表：没连上拉也是白等，界面继续用本地占位方案。
        if (ViewModels.WorkerStatus.Instance.IsConnected)
        {
            await ViewModels.PlanHub.InitializeAllAsync();

            _planWatcher.Start();

            // 后台那份 ui.json 是权威值；本地文件只是上一次的缓存。
            await AppSettings.Instance.SyncFromWorkerAsync();
        }
    }

    private DateTime _nextProbeAt = DateTime.MinValue;

    private void OnWorkerStatusClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = ProbeWorkerAsync(force: true);
    }

    /// <summary>ping 一次 Worker 并刷新左下角状态</summary>
    private async Task ProbeWorkerAsync(bool force)
    {
        if (!force && DateTime.UtcNow < _nextProbeAt)
        {
            return;
        }

        _nextProbeAt = DateTime.UtcNow.AddSeconds(5);

        try
        {
            await Services.WorkerBridge.Instance.RefreshStatusAsync();

            if (!ViewModels.WorkerStatus.Instance.IsConnected)
            {
                _nextProbeAt = DateTime.UtcNow.AddSeconds(15);
            }
        }
        catch (Exception)
        {
            _nextProbeAt = DateTime.UtcNow.AddSeconds(15);
        }
    }

    private static string GetVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(version)) return "0.0.0";

        // InformationalVersion 可能带 "+commit" 后缀，去掉只留主版本号
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    // 侧栏展开 / 收起

    /// <summary>侧栏宽度过渡</summary>
    private void AnimateSidebar(bool animate = true)
    {
        double target = ShellState.Instance.Width;

        if (!animate)
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = target;
            return;
        }

        Sidebar.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void OnShellStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellState.IsExpanded))
        {
            AnimateSidebar();
        }
    }

    /// <summary>让侧栏立刻落到当前状态的宽度，跳过 300ms 过渡（截图 / 自检用）</summary>
    public void SnapSidebar()
    {
        AnimateSidebar(animate: false);
        UpdateLayout();
    }

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        ShellState.Instance.IsExpanded = false;
    }

    private void OnSidebarHeaderClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 收缩态下点 logo 就是展开；展开态下点 logo 不做任何事
        // （收起只能走右上角的箭头，避免误触）。
        if (ShellState.Instance.IsCollapsed)
        {
            ShellState.Instance.IsExpanded = true;
        }
    }

    // 导航路由（Code-Behind）

    private readonly Dictionary<string, FrameworkElement> _viewCache = new();

    // 后台效率模式

    /// <summary>窗口是否在前台可见。最小化时 IsVisible 仍为 true，必须一起看。</summary>
    private bool IsForeground => IsVisible && WindowState != WindowState.Minimized;

    private void UpdateIdleState()
    {
        if (IsForeground)
        {
            _idleTimer.Stop();

            if (IdleMemoryTrimmer.IsEntered)
                IdleMemoryTrimmer.Leave();

            RestoreCurrentView();
            return;
        }

        if (IdleTrimSettings.Current == IdleTrimMode.Off)
            return;

        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        _idleTimer.Stop();

        if (IsForeground)
            return;

        IdleTrimMode mode = IdleTrimSettings.Current;
        if (mode == IdleTrimMode.Off)
            return;

        IdleMemoryTrimmer.Enter();

        if (mode != IdleTrimMode.Full)
            return;

        ReleaseCachedViews();

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        IdleMemoryTrimmer.TrimWorkingSet();
    }

    /// <summary>丢掉除当前页以外的控件树。切回去会按需重建。</summary>
    private void ReleaseCachedViews()
    {
        if (_viewCache.Count <= 1)
            return;

        List<string> drop = new(_viewCache.Count - 1);

        foreach (string key in _viewCache.Keys)
        {
            if (!string.Equals(key, _currentTag, StringComparison.Ordinal))
                drop.Add(key);
        }

        foreach (string key in drop)
            _viewCache.Remove(key);
    }

    /// <summary>当前页不在缓存里就重建。正常不会发生 —— 当前页永远保留。</summary>
    private void RestoreCurrentView()
    {
        if (ContentHost?.Content is not null || string.IsNullOrEmpty(_currentTag))
            return;

        Navigate(_currentTag);
    }

    /// <summary>当前显示的页面标签。用来判断「这次是不是真的要离开」。</summary>
    private string _currentTag = "Misc";

    /// <summary>正在把导航项勾回原位。此时触发的那次 Checked 必须忽略。</summary>
    private bool _restoringNavigation;

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (_restoringNavigation || tag == _currentTag)
        {
            return;
        }

        if (!TryLeaveCurrentPage(tag))
        {
            return;
        }

        // 点图标只切页，不动侧栏开合 —— 开合只有用户点 logo / 箭头才算。
        Navigate(tag);
    }

    /// <summary>离开有未保存改动的页面时拦住导航</summary>
    private bool TryLeaveCurrentPage(string targetTag)
    {
        if (ContentHost?.Content is not FrameworkElement page)
        {
            return true;
        }

        if (page.DataContext is not ViewModels.TuningViewModel tuning || !tuning.IsDirty)
        {
            return true;
        }

        _restoringNavigation = true;

        try
        {
            foreach (RadioButton radio in NavHost.Children.OfType<RadioButton>())
            {
                radio.IsChecked = radio.Tag as string == _currentTag;
            }
        }
        finally
        {
            _restoringNavigation = false;
        }

        ViewModels.ToastService.Instance.Show("调校页有未保存的改动，请先应用或放弃");
        return false;
    }

    private void Navigate(string tag)
    {
        // 初始 IsChecked 会在 XAML 加载阶段触发 Checked。
        if (ContentHost is null) return;

        if (!_viewCache.TryGetValue(tag, out var view))
        {
            view = CreateView(tag);
            _viewCache[tag] = view;
        }

        ContentHost.Content = view;
        _currentTag = tag;

        _ = ProbeWorkerAsync(force: false);
    }

    public void NavigateTo(string tag)
    {
        if (tag != _currentTag && !TryLeaveCurrentPage(tag))
        {
            // 托盘等外部入口同样不能绕过未保存拦截。
            return;
        }

        Navigate(tag);

        // 同步侧栏选中项（Checked 只做路由，不会再动侧栏开合）
        foreach (var radio in NavHost.Children.OfType<RadioButton>())
        {
            if (radio.Tag as string == tag)
            {
                radio.IsChecked = true;
            }
        }
    }

    private static FrameworkElement CreateView(string tag) => tag switch
    {
        "Misc"     => new SettingsView(),
        "Tuning"   => new TuningView(),
        "Process"  => new ProcessView(),
        "Power"    => new PowerView(),
        "Scenarios" => new ScenariosView(),
        "Behavior" => new BehaviorView(),
        "Logs"     => new LogsView(),
        "Advanced" => new AdvancedView(),
        _ => new PlaceholderView { Title = tag },
    };

    public void ScrollPageToEnd()
    {
        if (ContentHost.Content is not DependencyObject root)
        {
            return;
        }

        FindFirst<ScrollViewer>(root)?.ScrollToEnd();
    }

    public void OpenMaskManager()
    {
        if (ContentHost.Content is ProcessView view
            && view.DataContext is ProcessViewModel vm)
        {
            vm.OpenMaskManager();
            UpdateLayout();
        }
    }

    /// <summary>直接进掩码编辑器。给 <c>--mask</c> 截图用：截图模式没有鼠标，点不到列表卡片。</summary>
    public void OpenMaskEditor()
    {
        if (ContentHost.Content is ProcessView view
            && view.DataContext is ProcessViewModel vm)
        {
            vm.MaskManager?.EditSelected();
            UpdateLayout();
        }
    }

    private static T? FindFirst<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match)
        {
            return match;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            T? found = FindFirst<T>(VisualTreeHelper.GetChild(root, i));

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // 窗口控制（Code-Behind）

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        // 走 WM_SYSCOMMAND，不用 WindowState 赋值：
        // 后者内部是 SetWindowPos，跳过了 DWM 的最小化缩放动画，看起来是硬消失。
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // 托盘图标是否创建成功。没有托盘时窗口不能隐藏，否则没人能把它叫回来。
    public bool MinimizeToTrayEnabled { get; set; }

    internal enum WindowCloseAction
    {
        /// <summary>退 UI，并连带关掉本次 UI 拉起的 Worker。</summary>
        ExitAll,

        /// <summary>只退 UI，Worker 常驻。</summary>
        ExitUiKeepWorker,

        /// <summary>窗口收进托盘，UI 进程存活。</summary>
        Hide,
    }

    internal static WindowCloseAction ResolveCloseAction(
        bool trayAvailable,
        MainWindowCloseBehavior behavior)
    {
        if (behavior == MainWindowCloseBehavior.CloseWindowOnly)
            return WindowCloseAction.ExitUiKeepWorker;

        if (behavior == MainWindowCloseBehavior.ExitAll || !trayAvailable)
            return WindowCloseAction.ExitAll;

        return WindowCloseAction.Hide;
    }

    /// <summary>退出时给 Worker 发 shutdown 的超时。分连接与收发两段，最坏约两倍。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_allowClose)
            return;

        WindowCloseAction action = ResolveCloseAction(
            MinimizeToTrayEnabled, AppSettings.Instance.CloseBehavior);

        if (action == WindowCloseAction.ExitAll)
        {
            // 窗口关掉之后 WPF 立刻结束进程，fire-and-forget 的关闭请求经常还没连上。
            WorkerLauncher.RequestShutdown(ShutdownTimeout, evenIfNotOwned: true);
        }

        // 两档退出都让窗口正常关闭。
        if (action != WindowCloseAction.Hide)
            return;

        e.Cancel = true;
        Hide();
    }

    private bool _allowClose;

    public void ExitApplication()
    {
        _allowClose = true;

        // 托盘里的「退出」是显式关整个应用，不受「仅留服务」影响。
        WorkerLauncher.RequestShutdown(ShutdownTimeout, evenIfNotOwned: true);
        Close();
    }

    private void ToggleMaximize()
    {
        // 同上：MaximizeWindow / RestoreWindow 发的是 SC_MAXIMIZE / SC_RESTORE，
        // DWM 才会给出 Windows 原生的铺展与收回动画。
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void UpdateMaximizeButton()
    {
        bool maximized = WindowState == WindowState.Maximized;

        MaxButton.ToolTip = maximized ? "还原" : "最大化";
        MaxIcon.Data = System.Windows.Media.Geometry.Parse(maximized
            ? "M2.5 0.5 H9.5 V7.5 M0.5 2.5 H7.5 V9.5 H0.5 Z"
            : "M1.5 0.5 H8.5 A1,1 0 0 1 9.5 1.5 V8.5 A1,1 0 0 1 8.5 9.5 H1.5 A1,1 0 0 1 0.5 8.5 V1.5 A1,1 0 0 1 1.5 0.5 Z");
    }

    // 最大化工作区修正

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        }

        // 窗口句柄此时才存在 —— 挂系统主题监听
        AppThemeService.Instance.AttachWindow(this);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public NativeMethods.POINT ptReserved;
        public NativeMethods.POINT ptMaxSize;
        public NativeMethods.POINT ptMaxPosition;
        public NativeMethods.POINT ptMinTrackSize;
        public NativeMethods.POINT ptMaxTrackSize;
    }
}
