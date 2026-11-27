using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Desktop.Services;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Localization;

namespace Zentwik.Desktop.ViewModels;

/// <summary>不引用 WPF 控件类型，便于将来替换 UI 框架。</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int HistoryLength = 120;
    private const int SampleIntervalMs = 500;

    private readonly LocalizationCatalog _catalog;
    private readonly ThemeService _themes;
    private readonly SynchronizationContext _ui;
    private readonly Queue<double> _history = new();
    private readonly CancellationTokenSource _cts = new();

    private AgentConnection? _connection;
    private bool _suppressPersist = true;
    private bool _isConnected;
    private string _statusText;
    private string _runtimeSummary = string.Empty;
    private string _activeSceneText = string.Empty;
    private string? _activeSceneId;
    private string _hardwareSummary = "—";
    private string _tuningSummary = "—";
    private string _intelTuningSummary = "—";

    private double _cpuPercent;
    private double _memoryPercent;
    private int _processCount;
    private int _coreCount;
    private int _physicalCoreCount;
    private double? _temperatureCelsius;
    private double? _packagePowerWatts;
    private double? _peakClockMhz;
    private string _sensorDetailText = string.Empty;
    private string _lastSampleTimeText = "等待采样…";

    private string _selectedLanguage;
    private string _selectedTheme;
    private string _selectedAccent;
    private double _uiScale = 1d;
    private int _selectedMonitorIntervalMs = 1000;
    private int _selectedSensorIntervalMs = 1000;

    private ModuleRow? _selectedModule;
    private bool _showPowerLimits;
    private string _powerLimitCurrentText = "—";
    private string _powerLimitFusedText = "—";
    private string _powerLimitStatusDetail = string.Empty;
    private string _powerLimitFastInput = string.Empty;
    private string _powerLimitSlowInput = string.Empty;
    private string _powerLimitTdcInput = string.Empty;
    private string _powerLimitEdcInput = string.Empty;
    private string _powerLimitApplySummary = string.Empty;
    private string _deploymentModeText = "—";
    private string _agentVersionText = "—";
    private string _stateCountsText = "—";
    private string _effectiveTelemetrySummary = "—";
    private string _dataDirectoryText = "—";
    private string _statePathText = "—";
    private string _logDirectoryText = "—";
    private PowerSchemeRow? _selectedPowerScheme;
    private PowerBackupRow? _selectedPowerBackup;
    private string _newPowerSchemeName = string.Empty;
    private string _coreClocksStatusText = "等待传感器采样…";

    private string? _noticeText;
    private bool _noticeIsError;

    private PlanRow? _selectedPlan;
    private SceneRow? _selectedScene;
    private LedgerRow? _selectedLedger;

    private bool _isCreatePlanOpen;
    private string _newPlanKind = "cpuTuning";
    private string _newPlanId = string.Empty;
    private string _newPlanName = string.Empty;
    private string _newPlanSpec = string.Empty;

    private bool _isCreateSceneOpen;
    private string _newSceneId = string.Empty;
    private string _newSceneName = string.Empty;
    private string _newScenePriority = "10";
    private string _newScenePlanId = string.Empty;
    private string _newSceneTriggerKind = SceneTriggerKinds.Manual;
    private string _newSceneTriggerArg = string.Empty;
    private bool _newSceneRestoreOnExit = true;

    public MainViewModel(LocalizationCatalog catalog, ThemeService themes)
    {
        _catalog = catalog;
        _themes = themes;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _selectedLanguage = catalog.CurrentCulture;
        _selectedTheme = themes.Theme;
        _selectedAccent = themes.Accent;
        _statusText = catalog["status.connecting"];
        _activeSceneText = catalog["status.no_scene"];

        RefreshCommand = new AsyncRelayCommand(RefreshAllAsync, () => IsConnected);
        DismissNoticeCommand = new RelayCommand(() => NoticeText = null);

        ToggleCreatePlanCommand = new RelayCommand(() => IsCreatePlanOpen = !IsCreatePlanOpen);
        CreatePlanCommand = new AsyncRelayCommand(CreatePlanAsync, () => IsConnected);
        ApplyPlanCommand = new AsyncRelayCommand(ApplySelectedPlanAsync, () => IsConnected && SelectedPlan is { Id.Length: > 0 });
        RevertPlanCommand = new AsyncRelayCommand(RevertSelectedPlanAsync, () => IsConnected && SelectedPlan is { CanRevert: true });
        DeletePlanCommand = new AsyncRelayCommand(DeleteSelectedPlanAsync, () => IsConnected && SelectedPlan is { Id.Length: > 0 });
        EditPlanCommand = new AsyncRelayCommand(EditSelectedPlanAsync, () => IsConnected && SelectedPlan is { Id.Length: > 0 });

        ToggleCreateSceneCommand = new RelayCommand(() => IsCreateSceneOpen = !IsCreateSceneOpen);
        CreateSceneCommand = new AsyncRelayCommand(CreateSceneAsync, () => IsConnected);
        ActivateSceneCommand = new AsyncRelayCommand(ActivateSelectedSceneAsync, () => IsConnected && SelectedScene is not null);
        DeactivateSceneCommand = new AsyncRelayCommand(DeactivateSceneAsync, () => IsConnected && !string.IsNullOrEmpty(_activeSceneId));
        ToggleSceneEnabledCommand = new AsyncRelayCommand(ToggleSelectedSceneAsync, () => IsConnected && SelectedScene is not null);
        DeleteSceneCommand = new AsyncRelayCommand(DeleteSelectedSceneAsync, () => IsConnected && SelectedScene is not null);
        EditSceneCommand = new AsyncRelayCommand(EditSelectedSceneAsync, () => IsConnected && SelectedScene is not null);

        RefreshTuningCommand = new AsyncRelayCommand(RefreshTuningAndLedgerAsync, () => IsConnected);
        ReleaseTakeoverCommand = new AsyncRelayCommand(ReleaseSelectedTakeoverAsync, () => IsConnected && SelectedLedger is { IsUserOwned: true });
        ToggleSelectedModuleCommand = new AsyncRelayCommand(ToggleSelectedModuleAsync, () => IsConnected && SelectedModule is { Optional: true });
        RefreshPowerLimitsCommand = new AsyncRelayCommand(RefreshPowerLimitsAsync, () => IsConnected && ShowPowerLimits);
        ApplyPowerLimitsCommand = new AsyncRelayCommand(ApplyPowerLimitsAsync, () => IsConnected && ShowPowerLimits);
        RefreshLogsCommand = new AsyncRelayCommand(RefreshLogsAsync, () => IsConnected);
        CreatePowerSchemeCommand = new AsyncRelayCommand(CreatePowerSchemeAsync, () => IsConnected && SelectedPowerScheme is not null && !string.IsNullOrWhiteSpace(NewPowerSchemeName));
        RenamePowerSchemeCommand = new AsyncRelayCommand(RenameSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is { BuiltIn: false } && !string.IsNullOrWhiteSpace(NewPowerSchemeName));
        BackupPowerSchemeCommand = new AsyncRelayCommand(BackupSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is not null);
        DeletePowerSchemeCommand = new AsyncRelayCommand(DeleteSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is { BuiltIn: false, Active: false });
        RestorePowerBackupCommand = new AsyncRelayCommand(RestoreSelectedPowerBackupAsync, () => IsConnected && SelectedPowerBackup is not null);
        UsePowerSchemeForPlanCommand = new RelayCommand(UseSelectedPowerSchemeForPlan, () => SelectedPowerScheme is not null);

        catalog.CultureChanged += OnCultureChanged;
    }

    /// <summary>采样历史更新。视图层据此重绘折线，视图模型不持有绘图对象。</summary>
    public event Action<double[]>? HistoryChanged;

    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    public ObservableCollection<PlanRow> Plans { get; } = [];

    public ObservableCollection<SceneRow> Scenes { get; } = [];

    public ObservableCollection<CoreOffsetRow> CoreOffsets { get; } = [];

    public ObservableCollection<LedgerRow> LedgerEntries { get; } = [];

    public ObservableCollection<ModuleRow> Modules { get; } = [];

    public ObservableCollection<LogRow> Logs { get; } = [];

    public ObservableCollection<LogRow> RecentErrors { get; } = [];

    public ObservableCollection<CoreClockRow> CoreClocks { get; } = [];

    public ObservableCollection<PowerSchemeRow> PowerSchemes { get; } = [];

    public ObservableCollection<PowerBackupRow> PowerBackups { get; } = [];

    public ObservableCollection<string> PlanIds { get; } = [];

    public IReadOnlyList<string> Languages => LocalizationCatalog.SupportedCultures;

    public IReadOnlyList<string> Themes { get; } = ["dark", "light"];

    public IReadOnlyList<string> Accents { get; } = ["blue", "violet", "teal", "amber"];

    public IReadOnlyList<double> ScaleOptions { get; } = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

    public IReadOnlyList<int> MonitorIntervalOptions { get; } = [500, 1000, 2000, 5000];

    public IReadOnlyList<int> SensorIntervalOptions { get; } = [500, 1000, 2000, 5000, 10000, 30000];

    public IReadOnlyList<string> PlanKinds { get; } = ["cpuTuning", "power", "scheduling"];

    public IReadOnlyList<string> TriggerKinds { get; } =
    [
        SceneTriggerKinds.Manual,
        SceneTriggerKinds.Process,
        SceneTriggerKinds.ProcessExited,
        SceneTriggerKinds.PowerSource,
    ];

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }
    public RelayCommand ToggleCreatePlanCommand { get; }
    public AsyncRelayCommand CreatePlanCommand { get; }
    public AsyncRelayCommand ApplyPlanCommand { get; }
    public AsyncRelayCommand RevertPlanCommand { get; }
    public AsyncRelayCommand DeletePlanCommand { get; }
    public AsyncRelayCommand EditPlanCommand { get; }
    public RelayCommand ToggleCreateSceneCommand { get; }
    public AsyncRelayCommand CreateSceneCommand { get; }
    public AsyncRelayCommand ActivateSceneCommand { get; }
    public AsyncRelayCommand DeactivateSceneCommand { get; }
    public AsyncRelayCommand ToggleSceneEnabledCommand { get; }
    public AsyncRelayCommand DeleteSceneCommand { get; }
    public AsyncRelayCommand EditSceneCommand { get; }
    public AsyncRelayCommand RefreshTuningCommand { get; }
    public AsyncRelayCommand ReleaseTakeoverCommand { get; }
    public AsyncRelayCommand ToggleSelectedModuleCommand { get; }
    public AsyncRelayCommand RefreshPowerLimitsCommand { get; }
    public AsyncRelayCommand ApplyPowerLimitsCommand { get; }
    public AsyncRelayCommand RefreshLogsCommand { get; }
    public AsyncRelayCommand CreatePowerSchemeCommand { get; }
    public AsyncRelayCommand RenamePowerSchemeCommand { get; }
    public AsyncRelayCommand BackupPowerSchemeCommand { get; }
    public AsyncRelayCommand DeletePowerSchemeCommand { get; }
    public AsyncRelayCommand RestorePowerBackupCommand { get; }
    public RelayCommand UsePowerSchemeForPlanCommand { get; }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
            {
                UpdateStatusText();
                RaiseCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string RuntimeSummary
    {
        get => _runtimeSummary;
        private set => Set(ref _runtimeSummary, value);
    }

    public string ActiveSceneText
    {
        get => _activeSceneText;
        private set => Set(ref _activeSceneText, value);
    }

    public string HardwareSummary
    {
        get => _hardwareSummary;
        private set => Set(ref _hardwareSummary, value);
    }

    public string TuningSummary
    {
        get => _tuningSummary;
        private set => Set(ref _tuningSummary, value);
    }

    public string IntelTuningSummary
    {
        get => _intelTuningSummary;
        private set => Set(ref _intelTuningSummary, value);
    }

    public string CpuText => $"{_cpuPercent:0}%";

    public string MemoryText => $"{_memoryPercent:0}%";

    public string ProcessText => _processCount.ToString(CultureInfo.InvariantCulture);

    public string CoreText => _coreCount == 0
        ? "—"
        : _physicalCoreCount > 0 && _physicalCoreCount != _coreCount
            ? $"{_physicalCoreCount}C / {_coreCount}T"
            : _coreCount.ToString(CultureInfo.InvariantCulture);

    public string TemperatureText => _temperatureCelsius is { } t ? $"{t:0.0} °C" : "—";

    public string PackagePowerText => _packagePowerWatts is { } w ? $"{w:0.0} W" : "—";

    public string PeakClockText => _peakClockMhz is { } mhz ? $"{mhz / 1000d:0.00} GHz" : "—";

    public string SensorDetailText
    {
        get => _sensorDetailText;
        private set => Set(ref _sensorDetailText, value);
    }

    public string LastSampleTimeText
    {
        get => _lastSampleTimeText;
        private set => Set(ref _lastSampleTimeText, value);
    }

    public int SelectedMonitorIntervalMs
    {
        get => _selectedMonitorIntervalMs;
        set
        {
            var clamped = Math.Clamp(value, 500, 5000);
            if (!Set(ref _selectedMonitorIntervalMs, clamped))
            {
                return;
            }

            PersistSettingsAsync(resubscribeTelemetry: true);
        }
    }

    public int SelectedSensorIntervalMs
    {
        get => _selectedSensorIntervalMs;
        set
        {
            var clamped = Math.Clamp(value, 500, 30000);
            if (!Set(ref _selectedSensorIntervalMs, clamped))
            {
                return;
            }

            PersistSettingsAsync(resubscribeTelemetry: true);
        }
    }

    public ModuleRow? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (Set(ref _selectedModule, value))
            {
                ToggleSelectedModuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowPowerLimits
    {
        get => _showPowerLimits;
        private set
        {
            if (Set(ref _showPowerLimits, value))
            {
                RefreshPowerLimitsCommand.RaiseCanExecuteChanged();
                ApplyPowerLimitsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PowerLimitCurrentText
    {
        get => _powerLimitCurrentText;
        private set => Set(ref _powerLimitCurrentText, value);
    }

    public string PowerLimitFusedText
    {
        get => _powerLimitFusedText;
        private set => Set(ref _powerLimitFusedText, value);
    }

    public string PowerLimitStatusDetail
    {
        get => _powerLimitStatusDetail;
        private set => Set(ref _powerLimitStatusDetail, value);
    }

    public string PowerLimitFastInput
    {
        get => _powerLimitFastInput;
        set => Set(ref _powerLimitFastInput, value);
    }

    public string PowerLimitSlowInput
    {
        get => _powerLimitSlowInput;
        set => Set(ref _powerLimitSlowInput, value);
    }

    public string PowerLimitTdcInput
    {
        get => _powerLimitTdcInput;
        set => Set(ref _powerLimitTdcInput, value);
    }

    public string PowerLimitEdcInput
    {
        get => _powerLimitEdcInput;
        set => Set(ref _powerLimitEdcInput, value);
    }

    public string PowerLimitApplySummary
    {
        get => _powerLimitApplySummary;
        private set => Set(ref _powerLimitApplySummary, value);
    }

    public string DeploymentModeText
    {
        get => _deploymentModeText;
        private set => Set(ref _deploymentModeText, value);
    }

    public string AgentVersionText
    {
        get => _agentVersionText;
        private set => Set(ref _agentVersionText, value);
    }

    public string StateCountsText
    {
        get => _stateCountsText;
        private set => Set(ref _stateCountsText, value);
    }

    public string EffectiveTelemetrySummary
    {
        get => _effectiveTelemetrySummary;
        private set => Set(ref _effectiveTelemetrySummary, value);
    }

    public string DataDirectoryText
    {
        get => _dataDirectoryText;
        private set => Set(ref _dataDirectoryText, value);
    }

    public string StatePathText
    {
        get => _statePathText;
        private set => Set(ref _statePathText, value);
    }

    public string LogDirectoryText
    {
        get => _logDirectoryText;
        private set => Set(ref _logDirectoryText, value);
    }

    public string CoreClocksStatusText
    {
        get => _coreClocksStatusText;
        private set => Set(ref _coreClocksStatusText, value);
    }

    public PowerSchemeRow? SelectedPowerScheme
    {
        get => _selectedPowerScheme;
        set
        {
            if (Set(ref _selectedPowerScheme, value))
            {
                CreatePowerSchemeCommand.RaiseCanExecuteChanged();
                RenamePowerSchemeCommand.RaiseCanExecuteChanged();
                BackupPowerSchemeCommand.RaiseCanExecuteChanged();
                DeletePowerSchemeCommand.RaiseCanExecuteChanged();
                UsePowerSchemeForPlanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public PowerBackupRow? SelectedPowerBackup
    {
        get => _selectedPowerBackup;
        set
        {
            if (Set(ref _selectedPowerBackup, value))
            {
                RestorePowerBackupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NewPowerSchemeName
    {
        get => _newPowerSchemeName;
        set
        {
            if (Set(ref _newPowerSchemeName, value))
            {
                CreatePowerSchemeCommand.RaiseCanExecuteChanged();
                RenamePowerSchemeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? NoticeText
    {
        get => _noticeText;
        private set
        {
            if (Set(ref _noticeText, value))
            {
                Raise(nameof(HasNotice));
            }
        }
    }

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => Set(ref _noticeIsError, value);
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(_noticeText);

    public PlanRow? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (Set(ref _selectedPlan, value))
            {
                ApplyPlanCommand.RaiseCanExecuteChanged();
                RevertPlanCommand.RaiseCanExecuteChanged();
                DeletePlanCommand.RaiseCanExecuteChanged();
                EditPlanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public SceneRow? SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (Set(ref _selectedScene, value))
            {
                ActivateSceneCommand.RaiseCanExecuteChanged();
                ToggleSceneEnabledCommand.RaiseCanExecuteChanged();
                DeleteSceneCommand.RaiseCanExecuteChanged();
                EditSceneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public LedgerRow? SelectedLedger
    {
        get => _selectedLedger;
        set
        {
            if (Set(ref _selectedLedger, value))
            {
                ReleaseTakeoverCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCreatePlanOpen
    {
        get => _isCreatePlanOpen;
        set => Set(ref _isCreatePlanOpen, value);
    }

    public string NewPlanKind
    {
        get => _newPlanKind;
        set
        {
            if (Set(ref _newPlanKind, value))
            {
                Raise(nameof(PlanSpecHint));
            }
        }
    }

    public string PlanSpecHint => NewPlanKind switch
    {
        "cpuTuning" => "格式：default=-15, ccd0=-20, 0=-10, 1=auto, fmax=5250, intel=-50",
        "power" => "格式：目标 Windows 电源方案 GUID（可在「Windows 电源方案」页查看）",
        _ => "格式：进程名=掩码:优先级[:cpusets]（如 game.exe=0xFF:High:0-7）",
    };

    public string NewPlanId
    {
        get => _newPlanId;
        set => Set(ref _newPlanId, value);
    }

    public string NewPlanName
    {
        get => _newPlanName;
        set => Set(ref _newPlanName, value);
    }

    public string NewPlanSpec
    {
        get => _newPlanSpec;
        set => Set(ref _newPlanSpec, value);
    }

    public bool IsCreateSceneOpen
    {
        get => _isCreateSceneOpen;
        set => Set(ref _isCreateSceneOpen, value);
    }

    public string NewSceneId
    {
        get => _newSceneId;
        set => Set(ref _newSceneId, value);
    }

    public string NewSceneName
    {
        get => _newSceneName;
        set => Set(ref _newSceneName, value);
    }

    public string NewScenePriority
    {
        get => _newScenePriority;
        set => Set(ref _newScenePriority, value);
    }

    public string NewScenePlanId
    {
        get => _newScenePlanId;
        set => Set(ref _newScenePlanId, value);
    }

    public string NewSceneTriggerKind
    {
        get => _newSceneTriggerKind;
        set
        {
            if (Set(ref _newSceneTriggerKind, value))
            {
                Raise(nameof(TriggerArgHint));
            }
        }
    }

    public string TriggerArgHint => NewSceneTriggerKind switch
    {
        SceneTriggerKinds.Process => "触发参数：进程名（如 game.exe，进程运行期间保持激活）",
        SceneTriggerKinds.ProcessExited => "触发参数：进程名（该进程退出时触发一次并保持激活）",
        SceneTriggerKinds.PowerSource => "触发参数：ac（交流电）或 dc（电池）",
        _ => "手动场景无需填写触发参数",
    };

    public string NewSceneTriggerArg
    {
        get => _newSceneTriggerArg;
        set => Set(ref _newSceneTriggerArg, value);
    }

    public bool NewSceneRestoreOnExit
    {
        get => _newSceneRestoreOnExit;
        set => Set(ref _newSceneRestoreOnExit, value);
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!Set(ref _selectedLanguage, value))
            {
                return;
            }

            _catalog.CurrentCulture = value;
            PersistSettingsAsync();
        }
    }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value))
            {
                return;
            }

            _themes.Apply(value, _selectedAccent);
            PersistSettingsAsync();
        }
    }

    public string SelectedAccent
    {
        get => _selectedAccent;
        set
        {
            if (!Set(ref _selectedAccent, value))
            {
                return;
            }

            _themes.Apply(_selectedTheme, value);
            PersistSettingsAsync();
        }
    }

    public double UiScale
    {
        get => _uiScale;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), 0.8, 1.6);
            if (!Set(ref _uiScale, clamped))
            {
                return;
            }

            PersistSettingsAsync();
        }
    }

    public async Task InitializeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var connection = await AgentConnection
                    .ConnectAsync(ProtocolInfo.PipeName, TimeSpan.FromSeconds(2), _cts.Token)
                    .ConfigureAwait(false);

                connection.EventReceived += OnEventReceived;
                connection.Closed += OnConnectionClosed;
                _connection = connection;

                var settings = await connection.GetSettingsAsync(_cts.Token).ConfigureAwait(false);
                var ack = await connection.SubscribeTelemetryAsync(null, null, _cts.Token).ConfigureAwait(false);

                _ui.Post(_ =>
                {
                    ApplySettings(settings);
                    EffectiveTelemetrySummary = $"推送 {ack.IntervalMs} ms · 传感器 {ack.SensorIntervalMs} ms";
                    IsConnected = true;
                }, null);

                await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IpcTransportException or IOException or AgentRequestException)
            {
                if (ex is AgentRequestException reqEx)
                {
                    _ui.Post(_ => ShowNotice(reqEx.Detail ?? reqEx.Message, isError: true), null);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RefreshAllAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task RefreshAllCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        var status = await connection.GetStatusAsync(ct).ConfigureAwait(false);
        var capabilities = await connection.ListCapabilitiesAsync(ct).ConfigureAwait(false);
        var modules = await connection.ListModulesAsync(ct).ConfigureAwait(false);
        var plans = await connection.ListPlansAsync(ct).ConfigureAwait(false);
        var scenes = await connection.ListScenesAsync(ct).ConfigureAwait(false);
        var ledger = await connection.ListLedgerAsync(ct).ConfigureAwait(false);
        var amdTuning = await connection.ReadTuningAsync(ct).ConfigureAwait(false);
        var intelTuning = await connection.ReadIntelTuningAsync(ct).ConfigureAwait(false);
        var doctor = await connection.GetDoctorAsync(ct).ConfigureAwait(false);
        var logs = await connection.GetLogAsync(80, ct).ConfigureAwait(false);
        var powerSchemes = await connection.ListPowerSchemesAsync(ct).ConfigureAwait(false);
        var powerBackups = await connection.ListPowerSchemeBackupsAsync(ct).ConfigureAwait(false);

        _ui.Post(_ =>
        {
            var selectedPlanId = SelectedPlan?.Id;
            var selectedSceneId = SelectedScene?.Id;
            var selectedLedgerTarget = SelectedLedger?.Target;
            var selectedModuleId = SelectedModule?.Id;
            var selectedSchemeGuid = SelectedPowerScheme?.Guid;
            var selectedBackupId = SelectedPowerBackup?.Id;

            Replace(Capabilities, capabilities.Select(static c => new CapabilityRow(c.Id, c.State, c.Detail ?? string.Empty)));
            Replace(Modules, modules.Select(static m => new ModuleRow(m.Id, m.Name, m.State, m.Detail ?? string.Empty, m.Optional, m.Enabled)));
            SelectedModule = Modules.FirstOrDefault(m => m.Id == selectedModuleId) ?? Modules.FirstOrDefault();
            ShowPowerLimits = modules.Any(static m => string.Equals(m.Id, "power-limits", StringComparison.OrdinalIgnoreCase) && m.Enabled);
            Replace(Plans, plans.Select(BuildPlanRow));
            Replace(PlanIds, plans.Select(static p => p.Id));
            Replace(PowerSchemes, powerSchemes.Select(static s => new PowerSchemeRow(s.Guid, s.Name, s.Active, s.BuiltIn, s.Visible)));
            Replace(PowerBackups, powerBackups.Select(static b => new PowerBackupRow(b.Id, b.Name, b.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), b.SettingCount, b.PortablePath is not null)));
            SelectedPowerScheme = PowerSchemes.FirstOrDefault(s => s.Guid == selectedSchemeGuid) ?? PowerSchemes.FirstOrDefault();
            SelectedPowerBackup = PowerBackups.FirstOrDefault(b => b.Id == selectedBackupId) ?? PowerBackups.FirstOrDefault();
            Replace(Scenes, scenes.Select(BuildSceneRow));
            Replace(CoreOffsets, amdTuning.Cores.Select(static c => new CoreOffsetRow(c.Core, c.Offset)));
            Replace(LedgerEntries, ledger.Select(BuildLedgerRow));
            Replace(Logs, logs.Select(static l => new LogRow(l.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), l.Level, l.Message)));
            DeploymentModeText = doctor.RunningAsService
                ? (doctor.Elevated ? "已注册系统服务（管理员）" : "已注册系统服务")
                : (doctor.Elevated ? "便携 / 控制台模式（管理员）" : "便携 / 控制台模式（标准权限）");
            AgentVersionText = $"v{doctor.Version} · 协议 v{doctor.ProtocolVersion} · 已运行 {FormatUptime(doctor.UptimeSeconds)}";
            StateCountsText = $"方案 {doctor.PlanCount} · 场景 {doctor.SceneCount} · 账本 {doctor.LedgerEntryCount}";
            Replace(RecentErrors, doctor.RecentErrors.Select(static l => new LogRow(l.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), l.Level, l.Message)));
            DataDirectoryText = doctor.DataDirectory;
            StatePathText = doctor.StatePath;
            LogDirectoryText = doctor.LogDirectory;

            SelectedPlan = Plans.FirstOrDefault(p => p.Id == selectedPlanId) ?? Plans.FirstOrDefault();
            SelectedScene = Scenes.FirstOrDefault(s => s.Id == selectedSceneId) ?? Scenes.FirstOrDefault();
            SelectedLedger = LedgerEntries.FirstOrDefault(l => l.Target == selectedLedgerTarget) ?? LedgerEntries.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(NewScenePlanId) && plans.Count > 0)
            {
                NewScenePlanId = plans[0].Id;
            }

            _coreCount = status.ProcessorCount;
            _physicalCoreCount = status.PhysicalCoreCount;
            _activeSceneId = status.ActiveSceneId;
            Raise(nameof(CoreText));
            UpdateRuntimeLabels(status);
            UpdateHardwareSummaries(status, amdTuning, intelTuning);
            DeactivateSceneCommand.RaiseCanExecuteChanged();
        }, null);
    }

    private async Task RefreshTuningAndLedgerAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            var status = await connection.GetStatusAsync(_cts.Token).ConfigureAwait(false);
            var ledger = await connection.ListLedgerAsync(_cts.Token).ConfigureAwait(false);
            var amdTuning = await connection.ReadTuningAsync(_cts.Token).ConfigureAwait(false);
            var intelTuning = await connection.ReadIntelTuningAsync(_cts.Token).ConfigureAwait(false);

            _ui.Post(_ =>
            {
                Replace(CoreOffsets, amdTuning.Cores.Select(static c => new CoreOffsetRow(c.Core, c.Offset)));
                Replace(LedgerEntries, ledger.Select(BuildLedgerRow));
                UpdateHardwareSummaries(status, amdTuning, intelTuning);
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task CreatePlanAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            var plan = BuildPlanFromForm();
            var saved = await connection.SavePlanAsync(plan, _cts.Token).ConfigureAwait(false);
            IsCreatePlanOpen = false;
            NewPlanId = string.Empty;
            NewPlanName = string.Empty;
            NewPlanSpec = string.Empty;
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已保存方案 {saved.Name} (rev {saved.Revision})。", isError: false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private Plan BuildPlanFromForm()
    {
        var id = NewPlanId.Trim();
        var name = NewPlanName.Trim();
        var spec = NewPlanSpec.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("方案 ID 和名称不能为空。");
        }

        var now = DateTimeOffset.Now;
        switch (NewPlanKind)
        {
            case "cpuTuning":
            {
                int? defaultCo = null;
                int? fmax = null;
                int? intelMv = null;
                var ccdOffsets = new Dictionary<int, int>();
                var cores = new List<CoreTuning>();
                foreach (var token in spec.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = token.Split('=', 2);
                    if (parts.Length != 2)
                    {
                        throw new FormatException($"无法解析 CO 配置 \"{token}\"，请使用 default=-15, ccd0=-20, 0=-10 或 1=auto。");
                    }

                    var key = parts[0].Trim();
                    var val = parts[1].Trim();
                    if (string.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var def))
                        {
                            throw new FormatException($"无法解析 default CO 值 \"{val}\"。");
                        }

                        defaultCo = def;
                        continue;
                    }

                    if (string.Equals(key, "fmax", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFmax))
                        {
                            throw new FormatException($"无法解析 fmax 值 \"{val}\"。");
                        }

                        fmax = parsedFmax;
                        continue;
                    }

                    if (string.Equals(key, "intel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMv))
                        {
                            throw new FormatException($"无法解析 intel 电压偏移 \"{val}\"。");
                        }

                        intelMv = parsedMv;
                        continue;
                    }

                    if (key.StartsWith("ccd", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(key.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ccd)
                        && ccd >= 0)
                    {
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ccdVal))
                        {
                            throw new FormatException($"无法解析 {key} 的 CO 值 \"{val}\"。");
                        }

                        ccdOffsets[ccd] = ccdVal;
                        continue;
                    }

                    if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var core))
                    {
                        throw new FormatException($"无法解析核配置 \"{token}\"，请使用 default=-15, ccd0=-20, 0=-10 或 1=auto。");
                    }

                    if (string.Equals(val, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        cores.Add(new CoreTuning { CoreIndex = core, Auto = true });
                        continue;
                    }

                    if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                    {
                        throw new FormatException($"无法解析核 {core} 的 CO 值 \"{val}\"。");
                    }

                    cores.Add(new CoreTuning { CoreIndex = core, Offset = offset });
                }

                if (defaultCo is null && ccdOffsets.Count == 0 && cores.Count == 0 && fmax is null && intelMv is null)
                {
                    throw new ArgumentException("CPU 调校方案至少需要一个 CO 配置（如 default=-15, ccd0=-20, 0=-10 或 1=auto）。");
                }

                return new CpuTuningPlan
                {
                    Id = id,
                    Name = name,
                    DefaultOffset = defaultCo,
                    CcdOffsets = ccdOffsets.Count == 0 ? null : ccdOffsets,
                    Cores = cores,
                    FMaxLimitMhz = fmax,
                    IntelVoltageOffsetMillivolts = intelMv,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            }
            case "power":
            {
                if (!Guid.TryParse(spec, out var schemeGuid))
                {
                    throw new FormatException("电源方案参数请输入目标电源方案 GUID。");
                }

                return new PowerPlan
                {
                    Id = id,
                    Name = name,
                    BaseSchemeGuid = schemeGuid,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            }
            default:
            {
                var assignment = spec.Split('=', 2);
                if (assignment.Length != 2 || string.IsNullOrWhiteSpace(assignment[0]))
                {
                    throw new FormatException("调度方案参数请使用 进程名=掩码:优先级[:cpusets]（如 game=0xF:High:0-3）。");
                }

                var parts = assignment[1].Split(':', 3);
                var mask = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0].Trim();
                var priority = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;
                var cpuSets = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;
                if (mask is null && priority is null && cpuSets is null)
                {
                    throw new ArgumentException("调度规则至少需要掩码、优先级或 CPU Sets。");
                }

                return new SchedulingPlan
                {
                    Id = id,
                    Name = name,
                    Rules = [new SchedulingRule { ProcessMatch = assignment[0].Trim(), CpuMask = mask, Priority = priority, CpuSets = cpuSets }],
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            }
        }
    }

    private async Task ApplySelectedPlanAsync()
    {
        var connection = _connection;
        var selected = SelectedPlan;
        if (connection is null || selected is null || string.IsNullOrEmpty(selected.Id))
        {
            return;
        }

        try
        {
            var result = await connection.ApplyPlanAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"{result.PlanId} ({result.State}): {result.Outcome}", isError: result.State is "failed");
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task RevertSelectedPlanAsync()
    {
        var connection = _connection;
        var selected = SelectedPlan;
        if (connection is null || selected is null || string.IsNullOrEmpty(selected.Id))
        {
            return;
        }

        try
        {
            var result = await connection.RevertPlanAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            var failed = result.Targets.Any(static t => t.Status == "failed");
            ShowNotice($"{result.PlanId}: {result.Outcome}", isError: failed);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task EditSelectedPlanAsync()
    {
        var connection = _connection;
        var selected = SelectedPlan;
        if (connection is null || selected is null || string.IsNullOrEmpty(selected.Id))
        {
            return;
        }

        try
        {
            var plan = await connection.GetPlanAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            var spec = FormatPlanSpecForEdit(plan);
            var kind = plan switch
            {
                CpuTuningPlan => "cpuTuning",
                PowerPlan => "power",
                _ => "scheduling",
            };

            _ui.Post(_ =>
            {
                NewPlanKind = kind;
                NewPlanId = plan.Id;
                NewPlanName = plan.Name;
                NewPlanSpec = spec;
                IsCreatePlanOpen = true;
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private static string FormatPlanSpecForEdit(Plan plan) => plan switch
    {
        CpuTuningPlan cpu => string.Join(", ", BuildCpuTokens(cpu)),
        PowerPlan power => power.BaseSchemeGuid?.ToString("D") ?? string.Empty,
        SchedulingPlan sched => sched.Rules.FirstOrDefault() is { } r
            ? $"{r.ProcessMatch}={r.CpuMask ?? string.Empty}:{r.Priority ?? string.Empty}{(r.CpuSets is not null ? $":{r.CpuSets}" : string.Empty)}"
            : string.Empty,
        _ => string.Empty,
    };

    private static IEnumerable<string> BuildCpuTokens(CpuTuningPlan cpu)
    {
        if (cpu.DefaultOffset is { } def)
        {
            yield return $"default={def}";
        }

        if (cpu.CcdOffsets is not null)
        {
            foreach (var kv in cpu.CcdOffsets.OrderBy(static k => k.Key))
            {
                yield return $"ccd{kv.Key}={kv.Value}";
            }
        }

        foreach (var c in cpu.Cores.OrderBy(static x => x.CoreIndex))
        {
            yield return c.Auto ? $"{c.CoreIndex}=auto" : $"{c.CoreIndex}={c.Offset}";
        }

        if (cpu.FMaxLimitMhz is { } fmax)
        {
            yield return $"fmax={fmax}";
        }

        if (cpu.IntelVoltageOffsetMillivolts is { } intel)
        {
            yield return $"intel={intel}";
        }
    }

    private async Task DeleteSelectedPlanAsync()
    {
        var connection = _connection;
        var selected = SelectedPlan;
        if (connection is null || selected is null || string.IsNullOrEmpty(selected.Id))
        {
            return;
        }

        try
        {
            await connection.DeletePlanAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已删除方案 {selected.Id}。", isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task CreateSceneAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            var id = NewSceneId.Trim();
            var name = NewSceneName.Trim();
            var planId = NewScenePlanId.Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(planId))
            {
                throw new ArgumentException("场景 ID、名称和关联方案 ID 不能为空。");
            }

            if (!int.TryParse(NewScenePriority.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
            {
                throw new FormatException("优先级必须为整数。");
            }

            var triggerArg = NewSceneTriggerKind == SceneTriggerKinds.Manual
                ? null
                : string.IsNullOrWhiteSpace(NewSceneTriggerArg)
                    ? null
                    : NewSceneTriggerArg.Trim();

            var scene = new Scene
            {
                Id = id,
                Name = name,
                Priority = priority,
                RestoreOnExit = NewSceneRestoreOnExit,
                Triggers = [new SceneTrigger { Kind = NewSceneTriggerKind, Argument = triggerArg }],
                Actions = [new SceneAction { PlanId = planId }],
            };

            var saved = await connection.SaveSceneAsync(scene, _cts.Token).ConfigureAwait(false);
            IsCreateSceneOpen = false;
            NewSceneId = string.Empty;
            NewSceneName = string.Empty;
            NewSceneTriggerArg = string.Empty;
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已保存场景 {saved.Name}。", isError: false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task ActivateSelectedSceneAsync()
    {
        var connection = _connection;
        var selected = SelectedScene;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var result = await connection.ActivateSceneAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"{result.SceneId}: {result.Outcome}", isError: !result.Active);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task DeactivateSceneAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            var result = await connection.DeactivateSceneAsync(_cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice(result.Outcome, isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task ToggleSelectedSceneAsync()
    {
        var connection = _connection;
        var selected = SelectedScene;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var scene = await connection.GetSceneAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            await connection.SaveSceneAsync(scene with { Enabled = !scene.Enabled }, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task EditSelectedSceneAsync()
    {
        var connection = _connection;
        var selected = SelectedScene;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var scene = await connection.GetSceneAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            var trigger = scene.Triggers.FirstOrDefault();
            var action = scene.Actions.FirstOrDefault();

            _ui.Post(_ =>
            {
                NewSceneId = scene.Id;
                NewSceneName = scene.Name;
                NewScenePriority = scene.Priority.ToString(CultureInfo.InvariantCulture);
                NewScenePlanId = action?.PlanId ?? string.Empty;
                NewSceneTriggerKind = trigger?.Kind ?? SceneTriggerKinds.Manual;
                NewSceneTriggerArg = trigger?.Argument ?? string.Empty;
                NewSceneRestoreOnExit = scene.RestoreOnExit;
                IsCreateSceneOpen = true;
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task DeleteSelectedSceneAsync()
    {
        var connection = _connection;
        var selected = SelectedScene;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            await connection.DeleteSceneAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已删除场景 {selected.Id}。", isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private async Task ReleaseSelectedTakeoverAsync()
    {
        var connection = _connection;
        var selected = SelectedLedger;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var released = await connection.ReleaseLedgerAsync(selected.Target, _cts.Token).ConfigureAwait(false);
            await RefreshTuningAndLedgerAsync().ConfigureAwait(false);
            ShowNotice(released ? $"已释放 {selected.Target} 的用户接管。" : $"{selected.Target} 当前非用户接管。", isError: !released);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private void OnEventReceived(ResponseEnvelope envelope)
    {
        if (envelope.Op == Events.TelemetrySample && envelope.Data is { } telemetryData)
        {
            var sample = telemetryData.Deserialize(ZentwikJson.Default.TelemetryDto);
            if (sample is not null)
            {
                _ui.Post(_ => ApplySample(sample), null);
            }

            return;
        }

        if (envelope.Op == Events.SceneChanged && envelope.Data is { } sceneData)
        {
            var changed = sceneData.Deserialize(ZentwikJson.Default.SceneActivationDto);
            if (changed is not null)
            {
                _ui.Post(_ =>
                {
                    _activeSceneId = changed.Active ? changed.SceneId : null;
                    UpdateActiveSceneText();
                    DeactivateSceneCommand.RaiseCanExecuteChanged();
                    _ = RefreshAllAsync();
                }, null);
            }
        }
    }

    private void ApplySample(TelemetryDto sample)
    {
        _cpuPercent = sample.CpuTotalPercent;
        _memoryPercent = sample.MemoryTotalBytes == 0
            ? 0d
            : (1d - (double)sample.MemoryAvailableBytes / sample.MemoryTotalBytes) * 100d;
        _processCount = sample.ProcessCount;
        _coreCount = sample.ProcessorCount;
        _temperatureCelsius = sample.TemperatureCelsius;
        _packagePowerWatts = sample.PackagePowerWatts;
        _peakClockMhz = sample.CoreClocks is { Count: > 0 } clocks ? clocks.Max(static c => c.Megahertz) : null;
        SensorDetailText = sample.SensorDetail ?? string.Empty;
        LastSampleTimeText = $"服务端采样 {sample.Timestamp.ToLocalTime():HH:mm:ss}";

        Raise(nameof(CpuText));
        Raise(nameof(MemoryText));
        Raise(nameof(ProcessText));
        Raise(nameof(CoreText));
        Raise(nameof(TemperatureText));
        Raise(nameof(PackagePowerText));
        Raise(nameof(PeakClockText));

        _history.Enqueue(_cpuPercent);
        while (_history.Count > HistoryLength)
        {
            _history.Dequeue();
        }

        if (sample.CoreClocks is { Count: > 0 } liveClocks)
        {
            Replace(CoreClocks, liveClocks
                .OrderBy(static c => c.Ccd)
                .ThenBy(static c => c.Core)
                .Select(static c => new CoreClockRow(c.Ccd, c.Core, c.Megahertz)));
            CoreClocksStatusText = $"活跃物理核 {liveClocks.Count} 个（睡眠核已省略）";
        }
        else
        {
            CoreClocks.Clear();
            CoreClocksStatusText = string.IsNullOrWhiteSpace(sample.SensorDetail)
                ? "当前无活跃核心频率数据"
                : sample.SensorDetail;
        }

        HistoryChanged?.Invoke([.. _history]);
    }

    private void ApplySettings(SettingsDto settings)
    {
        _suppressPersist = true;
        try
        {
            SelectedLanguage = settings.Language;
            SelectedTheme = settings.Theme;
            SelectedAccent = settings.Accent;
            UiScale = settings.UiScale;
            SelectedMonitorIntervalMs = settings.MonitorIntervalMs;
            SelectedSensorIntervalMs = settings.SensorIntervalMs;
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    private void PersistSettingsAsync(bool resubscribeTelemetry = false)
    {
        if (_suppressPersist)
        {
            return;
        }

        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        var patch = new SettingsPatch(
            _selectedLanguage,
            _selectedTheme,
            _selectedAccent,
            _uiScale,
            _selectedMonitorIntervalMs,
            _selectedSensorIntervalMs);

        _ = Task.Run(async () =>
        {
            try
            {
                await connection.SetSettingsAsync(patch, _cts.Token).ConfigureAwait(false);
                if (resubscribeTelemetry)
                {
                    var ack = await connection.SubscribeTelemetryAsync(null, null, _cts.Token).ConfigureAwait(false);
                    _ui.Post(_ => EffectiveTelemetrySummary = $"推送 {ack.IntervalMs} ms · 传感器 {ack.SensorIntervalMs} ms", null);
                }
            }
            catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
            {
                _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
            }
        });
    }

    private async Task ToggleSelectedModuleAsync()
    {
        var connection = _connection;
        var module = SelectedModule;
        if (connection is null || module is null || !module.Optional)
        {
            return;
        }

        try
        {
            var updated = module.Enabled
                ? await connection.DisableModuleAsync(module.Id, _cts.Token).ConfigureAwait(false)
                : await connection.EnableModuleAsync(module.Id, _cts.Token).ConfigureAwait(false);

            _ui.Post(_ =>
            {
                var selectedId = module.Id;
                Replace(Modules, updated.Select(static m => new ModuleRow(m.Id, m.Name, m.State, m.Detail ?? string.Empty, m.Optional, m.Enabled)));
                SelectedModule = Modules.FirstOrDefault(m => m.Id == selectedId) ?? Modules.FirstOrDefault();
                ShowPowerLimits = updated.Any(static m => string.Equals(m.Id, "power-limits", StringComparison.OrdinalIgnoreCase) && m.Enabled);
            }, null);

            if (string.Equals(module.Id, "power-limits", StringComparison.OrdinalIgnoreCase) && !module.Enabled)
            {
                await RefreshPowerLimitsAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task RefreshPowerLimitsAsync()
    {
        var connection = _connection;
        if (connection is null || !ShowPowerLimits)
        {
            return;
        }

        try
        {
            var state = await connection.ReadPowerLimitsAsync(_cts.Token).ConfigureAwait(false);
            _ui.Post(_ =>
            {
                PowerLimitCurrentText = state.Available
                    ? $"FAST {FormatWatts(state.FastWatts)} · SLOW {FormatWatts(state.SlowWatts)} · STAPM {FormatWatts(state.StapmWatts)}"
                    : "不可用";
                PowerLimitFusedText = $"熔断上限：FAST {FormatWatts(state.FusedFastWatts)} · SLOW {FormatWatts(state.FusedSlowWatts)} · TDC {FormatAmps(state.FusedTdcAmps)}";
                PowerLimitStatusDetail = state.Error ?? state.FusedDetail ?? string.Empty;
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task ApplyPowerLimitsAsync()
    {
        var connection = _connection;
        if (connection is null || !ShowPowerLimits)
        {
            return;
        }

        int? fast = ParseOptionalInt(PowerLimitFastInput);
        int? slow = ParseOptionalInt(PowerLimitSlowInput);
        int? tdc = ParseOptionalInt(PowerLimitTdcInput);
        int? edc = ParseOptionalInt(PowerLimitEdcInput);

        if (fast is null && slow is null && tdc is null && edc is null)
        {
            ShowNotice("请至少填写一项功耗墙参数（FAST / SLOW / TDC / EDC）。", isError: true);
            return;
        }

        try
        {
            var result = await connection.ApplyPowerLimitsAsync(new PowerLimitApplyRequest(fast, slow, tdc, edc), _cts.Token).ConfigureAwait(false);
            var lines = result.Items.Select(static item =>
            {
                var unit = item.Item is "tdc" or "edc" ? "A" : "W";
                return item.State == "diverged"
                    ? $"{item.Item.ToUpperInvariant()} [折算] 请求 {item.Requested} {unit} → 实际 {item.Actual} {unit}"
                    : $"{item.Item.ToUpperInvariant()} [{item.State}] {item.Requested} {unit}{(item.Actual is int a ? $" (回读 {a} {unit})" : string.Empty)}";
            });
            var summary = string.Join("；", lines);
            if (!string.IsNullOrWhiteSpace(result.Note))
            {
                summary = $"{summary} | {result.Note}";
            }

            _ui.Post(_ => PowerLimitApplySummary = summary, null);
            await RefreshPowerLimitsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private static int? ParseOptionalInt(string text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string FormatWatts(int? value) => value.HasValue ? $"{value} W" : "—";

    private static string FormatAmps(int? value) => value.HasValue ? $"{value} A" : "—";

    private async Task RefreshLogsAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            var logs = await connection.GetLogAsync(80, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ => Replace(Logs, logs.Select(static l => new LogRow(l.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), l.Level, l.Message))), null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task CreatePowerSchemeAsync()
    {
        var connection = _connection;
        var template = SelectedPowerScheme;
        var name = NewPowerSchemeName.Trim();
        if (connection is null || template is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var created = await connection.CreatePowerSchemeAsync(template.Guid, name, _cts.Token).ConfigureAwait(false);
            NewPowerSchemeName = string.Empty;
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已派生电源方案 {created.Name}。", isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task RenameSelectedPowerSchemeAsync()
    {
        var connection = _connection;
        var selected = SelectedPowerScheme;
        var name = NewPowerSchemeName.Trim();
        if (connection is null || selected is null || selected.BuiltIn || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var renamed = await connection.RenamePowerSchemeAsync(selected.Guid, name, _cts.Token).ConfigureAwait(false);
            NewPowerSchemeName = string.Empty;
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已将电源方案重命名为 {renamed.Name}。", isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task BackupSelectedPowerSchemeAsync()
    {
        var connection = _connection;
        var selected = SelectedPowerScheme;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var backup = await connection.BackupPowerSchemeAsync(selected.Guid, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已备份电源方案 {backup.Name}（{backup.SettingCount} 项）。", isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task DeleteSelectedPowerSchemeAsync()
    {
        var connection = _connection;
        var selected = SelectedPowerScheme;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            await connection.DeletePowerSchemeAsync(selected.Guid, keepBackup: true, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已删除电源方案 {selected.Name}（已自动备份）。", isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private void UseSelectedPowerSchemeForPlan()
    {
        var selected = SelectedPowerScheme;
        if (selected is null)
        {
            return;
        }

        NewPlanKind = "power";
        NewPlanId = $"power-{selected.Guid:N}"[..16];
        NewPlanName = selected.Name;
        NewPlanSpec = selected.GuidText;
        IsCreatePlanOpen = true;
        ShowNotice($"已将电源方案「{selected.Name}」带入新建表单，请切回「方案」页保存。", isError: false);
    }

    private static string FormatUptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
    }

    private async Task RestoreSelectedPowerBackupAsync()
    {
        var connection = _connection;
        var selected = SelectedPowerBackup;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var result = await connection.RestorePowerSchemeAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAllCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            ShowNotice($"已从备份恢复方案 {result.Scheme.Name}（成功 {result.Applied} 项，失败 {result.Failed} 项）。", isError: result.Failed > 0);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private void OnConnectionClosed(Exception? error)
    {
        _ = error;
        _ui.Post(_ =>
        {
            IsConnected = false;
            _connection = null;
            RuntimeSummary = string.Empty;
        }, null);

        if (!_cts.IsCancellationRequested)
        {
            _ = Task.Run(InitializeAsync);
        }
    }

    private void OnCultureChanged()
    {
        _ui.Post(_ =>
        {
            UpdateStatusText();
            UpdateActiveSceneText();
        }, null);
    }

    private void UpdateStatusText()
    {
        StatusText = IsConnected ? _catalog["status.connected"] : _catalog["status.disconnected"];
    }

    private void UpdateActiveSceneText()
    {
        ActiveSceneText = string.IsNullOrEmpty(_activeSceneId)
            ? _catalog["status.no_scene"]
            : $"{_catalog["status.active_scene"]}: {_activeSceneId}";
    }

    private void UpdateRuntimeLabels(AgentStatusDto status)
    {
        var privilege = status.Elevated ? _catalog["status.elevated"] : _catalog["status.standard"];
        var mode = status.RunningAsService ? "Service" : "Console";
        var sim = status.Simulated ? " · Fake" : string.Empty;
        RuntimeSummary = $"{status.MachineName} · {privilege} · {mode}{sim}";
        UpdateActiveSceneText();
    }

    private void UpdateHardwareSummaries(AgentStatusDto status, TuningSnapshotDto amd, IntelTuningSnapshotDto intel)
    {
        var active = string.IsNullOrEmpty(status.ActiveSceneId) ? _catalog["status.no_scene"] : status.ActiveSceneId;
        HardwareSummary =
            $"{status.MachineName} · {status.PhysicalCoreCount}C/{status.ProcessorCount}T · {active} · 账本 {status.LedgerEntryCount} 项 · AMD {amd.Availability}";

        var amdPersistence = amd.PersistsAcrossReboot ? "固件持久" : "会话有效";
        TuningSummary = $"AMD SMU: {amd.Availability} ({amd.Platform ?? "-"}, {amdPersistence}) — {amd.Detail ?? "-"}";

        var intelDetail = intel.VoltageOffsetMillivolts is { } mv
            ? $"电压偏移 {mv} mV · 睿频 1C {intel.TurboRatio1Core}x / 8C {intel.TurboRatio8Cores}x"
            : intel.Detail ?? "-";
        IntelTuningSummary = $"Intel MSR: {intel.Availability} ({intel.Platform ?? "-"}) — {intelDetail}";
    }

    private void ShowNotice(string text, bool isError)
    {
        NoticeIsError = isError;
        NoticeText = text;
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        CreatePlanCommand.RaiseCanExecuteChanged();
        ApplyPlanCommand.RaiseCanExecuteChanged();
        RevertPlanCommand.RaiseCanExecuteChanged();
        DeletePlanCommand.RaiseCanExecuteChanged();
        EditPlanCommand.RaiseCanExecuteChanged();
        CreateSceneCommand.RaiseCanExecuteChanged();
        ActivateSceneCommand.RaiseCanExecuteChanged();
        DeactivateSceneCommand.RaiseCanExecuteChanged();
        ToggleSceneEnabledCommand.RaiseCanExecuteChanged();
        DeleteSceneCommand.RaiseCanExecuteChanged();
        EditSceneCommand.RaiseCanExecuteChanged();
        RefreshTuningCommand.RaiseCanExecuteChanged();
        ReleaseTakeoverCommand.RaiseCanExecuteChanged();
        ToggleSelectedModuleCommand.RaiseCanExecuteChanged();
        RefreshPowerLimitsCommand.RaiseCanExecuteChanged();
        ApplyPowerLimitsCommand.RaiseCanExecuteChanged();
        RefreshLogsCommand.RaiseCanExecuteChanged();
        CreatePowerSchemeCommand.RaiseCanExecuteChanged();
        RenamePowerSchemeCommand.RaiseCanExecuteChanged();
        BackupPowerSchemeCommand.RaiseCanExecuteChanged();
        DeletePowerSchemeCommand.RaiseCanExecuteChanged();
        RestorePowerBackupCommand.RaiseCanExecuteChanged();
        UsePowerSchemeForPlanCommand.RaiseCanExecuteChanged();
    }

    private static PlanRow BuildPlanRow(Plan plan)
    {
        var state = plan.LastApply?.State switch
        {
            PlanApplyState.Applied => "applied",
            PlanApplyState.Partial => "partial",
            PlanApplyState.Failed => "failed",
            _ => "draft",
        };
        var detail = plan switch
        {
            CpuTuningPlan cpu => CurveOptimizerConfig.FormatPlanSummary(cpu),
            PowerPlan power => power.BaseSchemeGuid?.ToString("D") ?? $"设置 {power.Settings.Count} 项",
            SchedulingPlan sched => string.Join(", ", sched.Rules.Select(static r => r.CpuSets is null ? $"{r.ProcessMatch}({r.CpuMask ?? "-"}:{r.Priority ?? "-"})" : $"{r.ProcessMatch}({r.CpuMask ?? "-"}:{r.Priority ?? "-"}:{r.CpuSets})")),
            _ => string.Empty,
        };
        return new PlanRow(
            plan.Kind.ToString(),
            plan.Name,
            $"rev {plan.Revision} · {state}",
            plan.Id,
            state,
            plan.LastApply?.Outcome ?? string.Empty,
            CanRevert: plan.LastApply?.State is PlanApplyState.Applied or PlanApplyState.Partial,
            Detail: detail);
    }

    private static SceneRow BuildSceneRow(SceneSummaryDto scene)
    {
        var state = scene.Active ? "active" : scene.Enabled ? "enabled" : "disabled";
        return new SceneRow(
            scene.Name,
            $"P{scene.Priority} · {state}",
            string.Join(" · ", scene.TargetPlanIds),
            scene.Id,
            scene.Enabled,
            scene.Active,
            scene.LastOutcome ?? string.Empty);
    }

    private static LedgerRow BuildLedgerRow(LedgerEntry entry) => new(
        entry.Target,
        entry.Owner.ToString(),
        entry.Status.ToString(),
        $"prev={entry.PreviousValue ?? "-"} → desired={entry.DesiredValue ?? "-"} → actual={entry.ActualValue ?? "-"}",
        entry.Failure ?? string.Empty,
        entry.Owner == OwnerKind.User);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}

internal static class ExceptionTextExtensions
{
    public static string DetailOrMessage(this Exception ex) =>
        ex is AgentRequestException req ? req.Detail ?? req.Message : ex.Message;
}

public sealed record CapabilityRow(string Id, string State, string Detail);

public sealed record PlanRow(
    string Kind,
    string Name,
    string Caption,
    string Id = "",
    string State = "",
    string Outcome = "",
    bool CanRevert = false,
    string Detail = "");

public sealed record SceneRow(
    string Name,
    string Priority,
    string Targets,
    string Id = "",
    bool Enabled = true,
    bool Active = false,
    string LastOutcome = "");

public sealed record CoreOffsetRow(int Core, int Offset)
{
    public string Label => $"Core {Core}";
    public string ValueText => Offset.ToString(CultureInfo.InvariantCulture);
}

public sealed record LedgerRow(
    string Target,
    string Owner,
    string Status,
    string ValuesSummary,
    string Failure,
    bool IsUserOwned);

public sealed record ModuleRow(
    string Id,
    string Name,
    string State,
    string Detail,
    bool Optional,
    bool Enabled)
{
    public string ActionLabel => Optional ? (Enabled ? "停用" : "启用") : "内置";
    public string OptionalBadge => Optional ? (Enabled ? "可选 · 已启用" : "可选 · 未启用") : "核心模块";
}

public sealed record LogRow(string TimeText, string Level, string Message);

public sealed record CoreClockRow(int Ccd, int Core, double Megahertz)
{
    public string Label => $"CCD{Ccd} · C{Core}";
    public string ValueText => $"{Megahertz:0} MHz";
}

public sealed record PowerSchemeRow(
    Guid Guid,
    string Name,
    bool Active,
    bool BuiltIn,
    bool Visible)
{
    public string Badges => string.Join(" · ", new[]
    {
        Active ? "当前活动" : string.Empty,
        BuiltIn ? "系统内置" : "自定义",
        Visible ? string.Empty : "隐藏",
    }.Where(static x => x.Length > 0));

    public string GuidText => Guid.ToString("D");
}

public sealed record PowerBackupRow(
    string Id,
    string Name,
    string CreatedAtText,
    int SettingCount,
    bool HasPortable)
{
    public string Summary => $"{CreatedAtText} · {SettingCount} 项{(HasPortable ? " · 含 .pow" : string.Empty)}";
}
