using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Ipc;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

public enum StartupBehavior
{
    None,
    ServiceOnly,
    ServiceTray,
    ServiceWindow,
}

public enum MainWindowCloseBehavior
{
    MinimizeToTray,
    ExitAll,
    CloseWindowOnly,
}

/// <summary>
/// 界面设置的绑定源。权威值在 Worker 的 <c>ui.json</c>，本地文件只是离线缓存。
/// </summary>
/// <remarks>
/// 三个后台开关由 Worker 消费（启动应用调校、进程监听），
/// 界面各存一份的结果是两边永远不同步 —— 关掉开关后台照旧执行。
/// 因此连上 Worker 后先读回一次覆盖本地，之后每次改动防抖回写。
/// </remarks>
public sealed class AppSettings : ViewModelBase
{
    public static AppSettings Instance { get; } = new();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zentwik", "ui-settings.json");

    private bool _persistEnabled;
    private bool _applyingRemote;
    private Timer? _pushTimer;

    private AppSettings()
    {
    }

    /// <summary>正常启动时读回本地缓存并开启自动保存；诊断模式不碰用户配置。</summary>
    public void Load()
    {
        if (_persistEnabled)
            return;

        try
        {
            if (File.Exists(SettingsPath))
            {
                UiSettingsSnapshot? saved =
                    JsonSerializer.Deserialize<UiSettingsSnapshot>(File.ReadAllText(SettingsPath));

                if (saved is not null)
                {
                    _smartAntiBrick = saved.SmartAntiBrick;
                    _smartHibernateAware = saved.SmartHibernateAware;
                    _toastDurationSeconds = Math.Clamp(saved.ToastDurationSeconds, 1, 30);
                    _startup = saved.Startup;
                    _closeBehavior = saved.CloseBehavior;
                    _autoApplyTuning = saved.AutoApplyTuning;
                    _watchProcesses = saved.WatchProcesses;
                    _errorBubble = saved.ErrorBubble;

                    OnPropertyChanged(nameof(SmartAntiBrick));
                    OnPropertyChanged(nameof(SmartHibernateAware));
                    OnPropertyChanged(nameof(ToastDurationSeconds));
                    OnPropertyChanged(nameof(Startup));
                    OnPropertyChanged(nameof(CloseBehavior));
                    OnPropertyChanged(nameof(AutoApplyTuning));
                    OnPropertyChanged(nameof(WatchProcesses));
                    OnPropertyChanged(nameof(ErrorBubble));
                }
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值，不能挡住启动。
        }

        PropertyChanged += (_, _) => OnSettingsChanged();
        _persistEnabled = true;
    }

    /// <summary>连上 Worker 后用它那份覆盖本地缓存。</summary>
    public async Task SyncFromWorkerAsync()
    {
        if (!WorkerBridge.AllowAutoStart)
            return;

        IpcOutcome<UiSettingsPayload> outcome;

        try
        {
            outcome = await WorkerBridge.Instance.QueryUiSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            return;
        }

        if (!outcome.Success || outcome.Value is null)
            return;

        ApplyRemote(outcome.Value);
    }

    /// <summary>把 Worker 的设置写进绑定源。不发回写。</summary>
    public void ApplyRemote(UiSettingsPayload remote)
    {
        _applyingRemote = true;

        try
        {
            if (remote.AutoStartMode is { } autoStart)
                Startup = ParseStartup(autoStart);

            if (remote.CloseBehavior is { } close)
                CloseBehavior = ParseClose(close);

            if (remote.ApplyTuningOnStartup is { } applyTuning)
                AutoApplyTuning = applyTuning;

            if (remote.WatchProcessBinding is { } watch)
                WatchProcesses = watch;

            if (remote.NotifyOnError is { } notify)
                ErrorBubble = notify;

            if (remote.ToastSeconds is { } toast)
                ToastDurationSeconds = toast;
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private void OnSettingsChanged()
    {
        Save();

        if (_applyingRemote || !WorkerBridge.AllowAutoStart)
            return;

        // 拖动滑块会连续触发，250ms 合并成一次往返。
        _pushTimer ??= new Timer(_ => _ = PushAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _pushTimer.Change(250, Timeout.Infinite);
    }

    private async Task PushAsync()
    {
        try
        {
            await WorkerBridge.Instance.SaveUiSettingsAsync(BuildPayload()).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 后台未就绪时静默：本地缓存仍在，下次改动会重试。
        }
    }

    private UiSettingsPayload BuildPayload() => new()
    {
        AutoStartMode = ToWire(_startup),
        CloseBehavior = ToWire(_closeBehavior),
        ApplyTuningOnStartup = _autoApplyTuning,
        WatchProcessBinding = _watchProcesses,
        NotifyOnError = _errorBubble,
        ToastSeconds = _toastDurationSeconds,
    };

    private static string ToWire(StartupBehavior value) => value switch
    {
        StartupBehavior.ServiceOnly => "WorkerOnly",
        StartupBehavior.ServiceTray => "WorkerWithTray",
        StartupBehavior.ServiceWindow => "WorkerWithUI",
        _ => "None",
    };

    private static StartupBehavior ParseStartup(string value) => value switch
    {
        "WorkerOnly" => StartupBehavior.ServiceOnly,
        "WorkerWithTray" => StartupBehavior.ServiceTray,
        "WorkerWithUI" => StartupBehavior.ServiceWindow,
        _ => StartupBehavior.None,
    };

    private static string ToWire(MainWindowCloseBehavior value) => value switch
    {
        MainWindowCloseBehavior.ExitAll => "Exit",
        MainWindowCloseBehavior.CloseWindowOnly => "WorkerOnly",
        _ => "Tray",
    };

    private static MainWindowCloseBehavior ParseClose(string value) => value switch
    {
        "Exit" => MainWindowCloseBehavior.ExitAll,
        "WorkerOnly" => MainWindowCloseBehavior.CloseWindowOnly,
        _ => MainWindowCloseBehavior.MinimizeToTray,
    };

    private void Save()
    {
        if (!_persistEnabled)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

            UiSettingsSnapshot snapshot = new()
            {
                SmartAntiBrick = _smartAntiBrick,
                SmartHibernateAware = _smartHibernateAware,
                ToastDurationSeconds = _toastDurationSeconds,
                Startup = _startup,
                CloseBehavior = _closeBehavior,
                AutoApplyTuning = _autoApplyTuning,
                WatchProcesses = _watchProcesses,
                ErrorBubble = _errorBubble,
            };

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 写盘失败不打断界面操作。
        }
    }

    private sealed class UiSettingsSnapshot
    {
        public bool SmartAntiBrick { get; set; }

        public bool SmartHibernateAware { get; set; }

        public int ToastDurationSeconds { get; set; }

        public StartupBehavior Startup { get; set; }

        public MainWindowCloseBehavior CloseBehavior { get; set; }

        public bool AutoApplyTuning { get; set; }

        public bool WatchProcesses { get; set; }

        public bool ErrorBubble { get; set; }
    }

    private bool _smartAntiBrick = true;

    public bool SmartAntiBrick
    {
        get => _smartAntiBrick;
        set => SetProperty(ref _smartAntiBrick, value);
    }

    private bool _smartHibernateAware = true;

    public bool SmartHibernateAware
    {
        get => _smartHibernateAware;
        set => SetProperty(ref _smartHibernateAware, value);
    }

    private int _toastDurationSeconds = 3;

    public int ToastDurationSeconds
    {
        get => _toastDurationSeconds;
        set => SetProperty(ref _toastDurationSeconds, Math.Clamp(value, 1, 30));
    }

    private StartupBehavior _startup = StartupBehavior.ServiceTray;

    public StartupBehavior Startup
    {
        get => _startup;
        set => SetProperty(ref _startup, value);
    }

    private MainWindowCloseBehavior _closeBehavior = MainWindowCloseBehavior.MinimizeToTray;

    public MainWindowCloseBehavior CloseBehavior
    {
        get => _closeBehavior;
        set => SetProperty(ref _closeBehavior, value);
    }

    private bool _autoApplyTuning = true;

    public bool AutoApplyTuning
    {
        get => _autoApplyTuning;
        set => SetProperty(ref _autoApplyTuning, value);
    }

    private bool _watchProcesses = true;

    public bool WatchProcesses
    {
        get => _watchProcesses;
        set => SetProperty(ref _watchProcesses, value);
    }

    private bool _errorBubble = true;

    public bool ErrorBubble
    {
        get => _errorBubble;
        set => SetProperty(ref _errorBubble, value);
    }
}
