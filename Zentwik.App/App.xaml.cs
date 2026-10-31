using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Zentwik.App.Services;
using Zentwik.App.ViewModels;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Core.Threading;

namespace Zentwik.App;

/// <summary>
/// 界面对象。业务逻辑一律在 Core，这里只做四件事：
/// 挂调度器、应用主题、建窗口、接住 XAML 线程异常。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // XAML 线程上的异常如果没人接，WinUI 默认是静默终止进程，
        // 用户看到的就是「双击没反应」。这里至少让它落进日志。
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 真正的 UI 调度器此刻才有：Application.Start 已经把 DispatcherQueue
        // 建在 STA 线程上，SynchronizationContext 也跟着可用了。
        SynchronizationContext? context = SynchronizationContext.Current;
        if (context is null)
        {
            context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
        }

        AppHost.Dispatcher.Attach(new SynchronizationContextDispatcher(context));
        IServiceProvider services = AppHost.Services;
        ILogService log = services.GetRequiredService<ILogService>();

        // 主题必须在任何 Window / Page 创建之前应用：
        // 强调色是替换应用资源里的 Brush 键，控件一旦加载过就不会回头重算。
        ThemeController theme = services.GetRequiredService<ThemeController>();
        try
        {
            theme.Initialize();
        }
        catch (Exception ex)
        {
            // 关键兜底：主题初始化发生在「窗口尚未创建」之前，
            // 此处抛出 = 进程在没有任何窗口的情况下退出 = 用户只看到黑屏。
            // ThemeController 内部本身已做降级，这里是第二道防线：
            // 无论如何都要把窗口建起来。
            log.Error($"主题初始化失败，已用默认主题继续启动：{ex}");
        }

        try
        {
            _window = new MainWindow(
                theme,
                services.GetRequiredService<SettingsViewModel>(),
                services.GetRequiredService<TuningViewModel>(),
                services.GetRequiredService<ProcessViewModel>(),
                services.GetRequiredService<ConfigViewModel>(),
                services.GetRequiredService<BehaviorViewModel>(),
                services.GetRequiredService<AdvancedViewModel>(),
                services.GetRequiredService<LogViewModel>(),
                log,
                services.GetRequiredService<IProcessWatcher>());

            _window.Activate();
        }
        catch (Exception ex)
        {
            // 建窗口失败同样不能静默退进程：落盘 + 弹原生对话框，
            // 让用户知道发生了什么，而不是只看到一片黑屏。
            log.Error($"窗口创建失败：{ex}");
            StartupCrashReporter.Report(ex, interactive: true);
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // WinUI 的 XAML 失败会以 stowed exception（0xc000027b）终止进程，
        // 事件查看器里只有一句「Microsoft.UI.Xaml.dll 出错」，没有堆栈。
        // 这条通道是唯一能在崩溃前把真实异常写下来的地方。
        StartupCrashReporter.Report(e.Exception, interactive: false);

        if (AppHost.Services.GetService(typeof(ILogService)) is ILogService log)
            log.Error($"UI 线程未处理异常：{e.Exception}");

        // 不置 Handled：带着未知状态继续跑，比崩掉更容易把用户配置写坏。
        // 让进程退出，日志已经落盘，下次启动能看到原因。
    }
}
