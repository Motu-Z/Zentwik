using System;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

public class BehaviorViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppSettings.Instance;

    public BehaviorViewModel()
    {
        // 后台那份设置是权威值：连上 Worker 后会被覆盖一次，界面要跟着刷。
        _settings.PropertyChanged += (_, e) => OnSettingChanged(e.PropertyName);

        // 与侧栏「调校」共用同一判定：非 AMD 平台整行隐藏，厂商未知时照常显示。
        WorkerStatus.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkerStatus.IsTuningPlanSupported))
                OnPropertyChanged(nameof(TuningSupported));
        };
    }

    /// <summary>平台不支持调校时，依赖 SMU 的设置整行隐藏。</summary>
    public bool TuningSupported => WorkerStatus.Instance.IsTuningPlanSupported;

    private void OnSettingChanged(string? name)
    {
        switch (name)
        {
            case nameof(AppSettings.Startup):
                OnPropertyChanged(nameof(StartupNone));
                OnPropertyChanged(nameof(StartupServiceOnly));
                OnPropertyChanged(nameof(StartupServiceTray));
                OnPropertyChanged(nameof(StartupServiceWindow));
                OnPropertyChanged(nameof(StartupHint));
                break;

            case nameof(AppSettings.CloseBehavior):
                OnPropertyChanged(nameof(CloseMinimizeToTray));
                OnPropertyChanged(nameof(CloseExitAll));
                OnPropertyChanged(nameof(CloseWindowOnly));
                OnPropertyChanged(nameof(CloseHint));
                break;

            case nameof(AppSettings.AutoApplyTuning):
                OnPropertyChanged(nameof(AutoApplyTuning));
                break;

            case nameof(AppSettings.WatchProcesses):
                OnPropertyChanged(nameof(WatchProcesses));
                break;
        }
    }

    // 启动方式（四选一，含「不启动」）

    public bool StartupNone
    {
        get => _settings.Startup == StartupBehavior.None;
        set { if (value) SetStartup(StartupBehavior.None); }
    }

    public bool StartupServiceOnly
    {
        get => _settings.Startup == StartupBehavior.ServiceOnly;
        set { if (value) SetStartup(StartupBehavior.ServiceOnly); }
    }

    public bool StartupServiceTray
    {
        get => _settings.Startup == StartupBehavior.ServiceTray;
        set { if (value) SetStartup(StartupBehavior.ServiceTray); }
    }

    public bool StartupServiceWindow
    {
        get => _settings.Startup == StartupBehavior.ServiceWindow;
        set { if (value) SetStartup(StartupBehavior.ServiceWindow); }
    }

    /// <summary>当前启动方式的一句话说明。行右侧只有四个按钮，不解释就成了猜谜。</summary>
    public string StartupHint => _settings.Startup switch
    {
        StartupBehavior.None => "开机时不启动任何组件",
        StartupBehavior.ServiceOnly => "仅启动后台服务，不启动托盘与界面",
        StartupBehavior.ServiceTray => "启动后台服务并显示托盘",
        _ => "启动后台服务并打开界面"
    };

    // 关闭主窗口

    public bool CloseMinimizeToTray
    {
        get => _settings.CloseBehavior == MainWindowCloseBehavior.MinimizeToTray;
        set { if (value) SetCloseBehavior(MainWindowCloseBehavior.MinimizeToTray); }
    }

    public bool CloseExitAll
    {
        get => _settings.CloseBehavior == MainWindowCloseBehavior.ExitAll;
        set { if (value) SetCloseBehavior(MainWindowCloseBehavior.ExitAll); }
    }

    public bool CloseWindowOnly
    {
        get => _settings.CloseBehavior == MainWindowCloseBehavior.CloseWindowOnly;
        set { if (value) SetCloseBehavior(MainWindowCloseBehavior.CloseWindowOnly); }
    }

    /// <summary>当前关闭行为的一句话说明。</summary>
    public string CloseHint => _settings.CloseBehavior switch
    {
        MainWindowCloseBehavior.MinimizeToTray => "隐藏窗口，后台服务继续运行",
        MainWindowCloseBehavior.ExitAll => "退出应用并停止后台服务",
        _ => "关闭界面，后台服务继续运行"
    };

    // 后台效率

    public bool IdleTrimOff
    {
        get => IdleTrimSettings.Current == IdleTrimMode.Off;
        set { if (value) SetIdleTrim(IdleTrimMode.Off); }
    }

    public bool IdleTrimLight
    {
        get => IdleTrimSettings.Current == IdleTrimMode.Light;
        set { if (value) SetIdleTrim(IdleTrimMode.Light); }
    }

    public bool IdleTrimFull
    {
        get => IdleTrimSettings.Current == IdleTrimMode.Full;
        set { if (value) SetIdleTrim(IdleTrimMode.Full); }
    }

    /// <summary>当前档位的一句话说明。</summary>
    public string IdleTrimHint => IdleTrimSettings.Current switch
    {
        IdleTrimMode.Off => "窗口进入后台后不做额外处理",
        IdleTrimMode.Light => "降低进程优先级，内存交还系统，页面保留",
        _ => "降低进程优先级，丢弃未打开页面的视图，切回时重建"
    };

    private void SetIdleTrim(IdleTrimMode mode)
    {
        if (IdleTrimSettings.Current == mode)
            return;

        IdleTrimSettings.Current = mode;

        OnPropertyChanged(nameof(IdleTrimOff));
        OnPropertyChanged(nameof(IdleTrimLight));
        OnPropertyChanged(nameof(IdleTrimFull));
        OnPropertyChanged(nameof(IdleTrimHint));
    }

    // 后台运行

    public bool AutoApplyTuning
    {
        get => _settings.AutoApplyTuning;
        set
        {
            if (_settings.AutoApplyTuning == value) return;

            _settings.AutoApplyTuning = value;

            OnPropertyChanged();
        }
    }

    public bool WatchProcesses
    {
        get => _settings.WatchProcesses;
        set
        {
            if (_settings.WatchProcesses == value) return;

            _settings.WatchProcesses = value;

            OnPropertyChanged();
        }
    }

    private void SetStartup(StartupBehavior mode)
    {
        if (_settings.Startup == mode) return;

        _settings.Startup = mode;

        OnPropertyChanged(nameof(StartupNone));
        OnPropertyChanged(nameof(StartupServiceOnly));
        OnPropertyChanged(nameof(StartupServiceTray));
        OnPropertyChanged(nameof(StartupServiceWindow));
        OnPropertyChanged(nameof(StartupHint));

        // 诊断模式不碰系统状态。
        if (!WorkerBridge.AllowAutoStart)
            return;

        string? wire = mode switch
        {
            StartupBehavior.None => null,
            StartupBehavior.ServiceOnly => "WorkerOnly",
            StartupBehavior.ServiceTray => "WorkerWithTray",
            _ => "WorkerWithUI"
        };

        _ = ApplyStartupAsync(wire);
    }

    private static async Task ApplyStartupAsync(string? wire)
    {
        try
        {
            var outcome = await WorkerBridge.Instance.SetAutoStartAsync(wire).ConfigureAwait(true);

            if (!outcome.Success && !outcome.TransportFailed)
                ToastService.Instance.ShowError(outcome.Message);
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show(ex.Message);
        }
    }

    private void SetCloseBehavior(MainWindowCloseBehavior mode)
    {
        if (_settings.CloseBehavior == mode) return;

        _settings.CloseBehavior = mode;

        OnPropertyChanged(nameof(CloseMinimizeToTray));
        OnPropertyChanged(nameof(CloseExitAll));
        OnPropertyChanged(nameof(CloseWindowOnly));
        OnPropertyChanged(nameof(CloseHint));
    }
}
