using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
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
    private const int LogFetchLimit = 200;

    private readonly LocalizationCatalog _catalog;
    private readonly SynchronizationContext _ui;
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
    private string _powerLimitsSummary = "—";
    private string _doctorSummary = "—";

    private double _cpuPercent;
    private double _memoryPercent;
    private int _processCount;
    private int _coreCount;
    private int _physicalCoreCount;

    private double? _temperatureCelsius;
    private double? _packagePowerWatts;
    private IReadOnlyList<TelemetryCoreClockDto>? _coreClocks;
    private IReadOnlyList<double>? _processorUtilization;

    private ProcessRowView? _selectedProcess;

    private string _selectedLanguage;

    // 协议里的 settings 仍有 theme / accent / uiScale 三个字段。界面不再提供入口，
    // 但读取后原样留存、原样回写，避免把后台已有的配置擦成默认值。
    private string _selectedTheme = "dark";
    private string _selectedAccent = "blue";
    private double _uiScale = 1d;

    private string? _noticeText;
    private bool _noticeIsError;

    private PlanRow? _selectedPlan;
    private SceneRow? _selectedScene;
    private LedgerRow? _selectedLedger;
    private ModuleRow? _selectedModule;
    private PowerSchemeRow? _selectedPowerScheme;
    private PowerBackupRow? _selectedPowerBackup;

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

    private string _powerSchemeNewName = string.Empty;
    private string _fastWattsInput = string.Empty;
    private string _slowWattsInput = string.Empty;
    private string _tdcAmpsInput = string.Empty;
    private string _edcAmpsInput = string.Empty;

    public MainViewModel(LocalizationCatalog catalog)
    {
        _catalog = catalog;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _selectedLanguage = Languages.Contains(catalog.CurrentCulture) ? catalog.CurrentCulture : "zh-CN";
        _statusText = catalog["status.connecting"];
        _activeSceneText = catalog["status.no_scene"];

        RefreshCommand = new AsyncRelayCommand(RefreshAllAsync, () => IsConnected);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync, () => IsConnected);
        RefreshProcessesCommand = new AsyncRelayCommand(RefreshProcessesAsync, () => IsConnected);
        DismissNoticeCommand = new RelayCommand(() => NoticeText = null);

        ToggleCreatePlanCommand = new RelayCommand(() => IsCreatePlanOpen = !IsCreatePlanOpen);
        CreatePlanCommand = new AsyncRelayCommand(CreatePlanAsync, () => IsConnected);
        ApplyPlanCommand = new AsyncRelayCommand(ApplySelectedPlanAsync, () => IsConnected && SelectedPlan is { Id.Length: > 0 });
        RevertPlanCommand = new AsyncRelayCommand(RevertSelectedPlanAsync, () => IsConnected && SelectedPlan is { CanRevert: true });
        DeletePlanCommand = new AsyncRelayCommand(DeleteSelectedPlanAsync, () => IsConnected && SelectedPlan is { Id.Length: > 0 });

        ToggleCreateSceneCommand = new RelayCommand(() => IsCreateSceneOpen = !IsCreateSceneOpen);
        CreateSceneCommand = new AsyncRelayCommand(CreateSceneAsync, () => IsConnected);
        ActivateSceneCommand = new AsyncRelayCommand(ActivateSelectedSceneAsync, () => IsConnected && SelectedScene is not null);
        DeactivateSceneCommand = new AsyncRelayCommand(DeactivateSceneAsync, () => IsConnected && !string.IsNullOrEmpty(_activeSceneId));
        ToggleSceneEnabledCommand = new AsyncRelayCommand(ToggleSelectedSceneAsync, () => IsConnected && SelectedScene is not null);
        DeleteSceneCommand = new AsyncRelayCommand(DeleteSelectedSceneAsync, () => IsConnected && SelectedScene is not null);

        RefreshTuningCommand = new AsyncRelayCommand(RefreshTuningAndLedgerAsync, () => IsConnected);
        ReleaseTakeoverCommand = new AsyncRelayCommand(ReleaseSelectedTakeoverAsync, () => IsConnected && SelectedLedger is { IsUserOwned: true });

        RefreshModulesCommand = new AsyncRelayCommand(RefreshModulesAsync, () => IsConnected);
        EnableModuleCommand = new AsyncRelayCommand(EnableSelectedModuleAsync, () => IsConnected && SelectedModule is { Enabled: false });
        DisableModuleCommand = new AsyncRelayCommand(DisableSelectedModuleAsync, () => IsConnected && SelectedModule is { Enabled: true });

        RefreshPowerCommand = new AsyncRelayCommand(RefreshPowerAsync, () => IsConnected);
        ReadPowerLimitsCommand = new AsyncRelayCommand(ReadPowerLimitsAsync, () => IsConnected);
        ApplyPowerLimitsCommand = new AsyncRelayCommand(ApplyPowerLimitsAsync, () => IsConnected && HasAnyLimitInput);
        CreatePowerSchemeCommand = new AsyncRelayCommand(CreatePowerSchemeAsync, () => IsConnected && SelectedPowerScheme is not null && !string.IsNullOrWhiteSpace(PowerSchemeNewName));
        RenamePowerSchemeCommand = new AsyncRelayCommand(RenameSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is { BuiltIn: false } && !string.IsNullOrWhiteSpace(PowerSchemeNewName));
        BackupPowerSchemeCommand = new AsyncRelayCommand(BackupSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is not null);
        DeletePowerSchemeCommand = new AsyncRelayCommand(DeleteSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is { BuiltIn: false });
        RestorePowerBackupCommand = new AsyncRelayCommand(RestoreSelectedPowerBackupAsync, () => IsConnected && SelectedPowerBackup is not null);

        RefreshLogCommand = new AsyncRelayCommand(RefreshLogAsync, () => IsConnected);
        RefreshDoctorCommand = new AsyncRelayCommand(RefreshDoctorAsync, () => IsConnected);

        catalog.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// 每来一帧遥测追加一行历史。视图只负责往表格末尾加一行，不重排整表，
    /// 所以这里传单行而不是整段序列。
    /// </summary>
    public event Action<TelemetryHistoryRow>? SampleAppended;

    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    public ObservableCollection<TelemetryHistoryRow> TelemetryHistory { get; } = [];

    public ObservableCollection<ProcessRowView> Processes { get; } = [];

    public ObservableCollection<PlanRow> Plans { get; } = [];

    public ObservableCollection<SceneRow> Scenes { get; } = [];

    public ObservableCollection<CoreOffsetRow> CoreOffsets { get; } = [];

    public ObservableCollection<LedgerRow> LedgerEntries { get; } = [];

    public ObservableCollection<ModuleRow> Modules { get; } = [];

    public ObservableCollection<PowerSchemeRow> PowerSchemes { get; } = [];

    public ObservableCollection<PowerBackupRow> PowerBackups { get; } = [];

    public ObservableCollection<LogRow> Logs { get; } = [];

    public ObservableCollection<LogRow> DoctorErrors { get; } = [];

    public IReadOnlyList<string> Languages => LocalizationCatalog.SupportedCultures;

    public IReadOnlyList<string> PlanKinds { get; } = ["cpuTuning", "power", "scheduling"];

    public IReadOnlyList<string> TriggerKinds { get; } =
    [
        SceneTriggerKinds.Manual,
        SceneTriggerKinds.Process,
        SceneTriggerKinds.PowerSource,
    ];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand RefreshProcessesCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }
    public RelayCommand ToggleCreatePlanCommand { get; }
    public AsyncRelayCommand CreatePlanCommand { get; }
    public AsyncRelayCommand ApplyPlanCommand { get; }
    public AsyncRelayCommand RevertPlanCommand { get; }
    public AsyncRelayCommand DeletePlanCommand { get; }
    public RelayCommand ToggleCreateSceneCommand { get; }
    public AsyncRelayCommand CreateSceneCommand { get; }
    public AsyncRelayCommand ActivateSceneCommand { get; }
    public AsyncRelayCommand DeactivateSceneCommand { get; }
    public AsyncRelayCommand ToggleSceneEnabledCommand { get; }
    public AsyncRelayCommand DeleteSceneCommand { get; }
    public AsyncRelayCommand RefreshTuningCommand { get; }
    public AsyncRelayCommand ReleaseTakeoverCommand { get; }
    public AsyncRelayCommand RefreshModulesCommand { get; }
    public AsyncRelayCommand EnableModuleCommand { get; }
    public AsyncRelayCommand DisableModuleCommand { get; }
    public AsyncRelayCommand RefreshPowerCommand { get; }
    public AsyncRelayCommand ReadPowerLimitsCommand { get; }
    public AsyncRelayCommand ApplyPowerLimitsCommand { get; }
    public AsyncRelayCommand CreatePowerSchemeCommand { get; }
    public AsyncRelayCommand RenamePowerSchemeCommand { get; }
    public AsyncRelayCommand BackupPowerSchemeCommand { get; }
    public AsyncRelayCommand DeletePowerSchemeCommand { get; }
    public AsyncRelayCommand RestorePowerBackupCommand { get; }
    public AsyncRelayCommand RefreshLogCommand { get; }
    public AsyncRelayCommand RefreshDoctorCommand { get; }

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

    // 文案一律走本地化表；单字符串参数必须走 params，避免误命中 Get(key, culture) 重载。
    private string T(string key, params object[] args) =>
        args.Length == 0 ? _catalog[key] : string.Format(CultureInfo.CurrentCulture, _catalog[key], args);

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

    public string PowerLimitsSummary
    {
        get => _powerLimitsSummary;
        private set => Set(ref _powerLimitsSummary, value);
    }

    public string DoctorSummary
    {
        get => _doctorSummary;
        private set => Set(ref _doctorSummary, value);
    }

    public string CpuText => $"{_cpuPercent:0}%";

    public string MemoryText => $"{_memoryPercent:0}%";

    public string ProcessText => _processCount.ToString(CultureInfo.InvariantCulture);

    public string CoreText => _coreCount == 0
        ? "—"
        : _physicalCoreCount > 0 && _physicalCoreCount != _coreCount
            ? $"{_physicalCoreCount}C / {_coreCount}T"
            : _coreCount.ToString(CultureInfo.InvariantCulture);

    public string TemperatureText => _temperatureCelsius is { } t
        ? string.Format(CultureInfo.CurrentCulture, T("monitor.temperature_value"), t)
        : "—";

    public string PackagePowerText => _packagePowerWatts is { } p
        ? string.Format(CultureInfo.CurrentCulture, T("monitor.power_value"), p)
        : "—";

    /// <summary>逐核频率：只报均值与区间，32 个核逐个列出来在 Label 里读不了。</summary>
    public string CoreClocksText
    {
        get
        {
            if (_coreClocks is not { Count: > 0 } clocks)
            {
                return "—";
            }

            var average = clocks.Average(static clock => clock.Megahertz);
            var min = clocks.Min(static clock => clock.Megahertz);
            var max = clocks.Max(static clock => clock.Megahertz);
            return string.Format(
                CultureInfo.CurrentCulture,
                T("monitor.clocks_value"),
                average / 1000d,
                min / 1000d,
                max / 1000d,
                clocks.Count);
        }
    }

    /// <summary>逐逻辑处理器占用：同样只报均值、峰值与线程数。</summary>
    public string UtilizationText => _processorUtilization is { Count: > 0 } utilization
        ? string.Format(
            CultureInfo.CurrentCulture,
            T("monitor.utilization_value"),
            utilization.Average(),
            utilization.Max(),
            utilization.Count)
        : "—";

    public ProcessRowView? SelectedProcess
    {
        get => _selectedProcess;
        set => Set(ref _selectedProcess, value);
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

    public ModuleRow? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (Set(ref _selectedModule, value))
            {
                EnableModuleCommand.RaiseCanExecuteChanged();
                DisableModuleCommand.RaiseCanExecuteChanged();
            }
        }
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

    public bool IsCreatePlanOpen
    {
        get => _isCreatePlanOpen;
        set => Set(ref _isCreatePlanOpen, value);
    }

    public string NewPlanKind
    {
        get => _newPlanKind;
        set => Set(ref _newPlanKind, value);
    }

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
        set => Set(ref _newSceneTriggerKind, value);
    }

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

    public string PowerSchemeNewName
    {
        get => _powerSchemeNewName;
        set
        {
            if (Set(ref _powerSchemeNewName, value))
            {
                CreatePowerSchemeCommand.RaiseCanExecuteChanged();
                RenamePowerSchemeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FastWattsInput
    {
        get => _fastWattsInput;
        set => SetLimitInput(ref _fastWattsInput, value);
    }

    public string SlowWattsInput
    {
        get => _slowWattsInput;
        set => SetLimitInput(ref _slowWattsInput, value);
    }

    public string TdcAmpsInput
    {
        get => _tdcAmpsInput;
        set => SetLimitInput(ref _tdcAmpsInput, value);
    }

    public string EdcAmpsInput
    {
        get => _edcAmpsInput;
        set => SetLimitInput(ref _edcAmpsInput, value);
    }

    /// <summary>四项功耗墙输入至少填了一项，「应用功耗墙」才有意义。</summary>
    private bool HasAnyLimitInput =>
        !string.IsNullOrWhiteSpace(_fastWattsInput)
        || !string.IsNullOrWhiteSpace(_slowWattsInput)
        || !string.IsNullOrWhiteSpace(_tdcAmpsInput)
        || !string.IsNullOrWhiteSpace(_edcAmpsInput);

    private void SetLimitInput(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name))
        {
            return;
        }

        ApplyPowerLimitsCommand.RaiseCanExecuteChanged();
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
                await connection.SubscribeTelemetryAsync(SampleIntervalMs, null, _cts.Token).ConfigureAwait(false);

                _ui.Post(_ =>
                {
                    ApplySettings(settings);
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
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private async Task RefreshAllCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        var status = await connection.GetStatusAsync(ct).ConfigureAwait(false);
        var capabilities = await connection.ListCapabilitiesAsync(ct).ConfigureAwait(false);
        var plans = await connection.ListPlansAsync(ct).ConfigureAwait(false);
        var scenes = await connection.ListScenesAsync(ct).ConfigureAwait(false);
        var ledger = await connection.ListLedgerAsync(ct).ConfigureAwait(false);
        var amdTuning = await connection.ReadTuningAsync(ct).ConfigureAwait(false);
        var intelTuning = await connection.ReadIntelTuningAsync(ct).ConfigureAwait(false);

        _ui.Post(_ =>
        {
            var selectedPlanId = SelectedPlan?.Id;
            var selectedSceneId = SelectedScene?.Id;
            var selectedLedgerTarget = SelectedLedger?.Target;

            Replace(Capabilities, capabilities.Select(static c => new CapabilityRow(c.Id, c.State, c.Detail ?? string.Empty)));
            Replace(Plans, plans.Select(BuildPlanRow));
            Replace(Scenes, scenes.Select(BuildSceneRow));
            Replace(CoreOffsets, amdTuning.Cores.Select(static c => new CoreOffsetRow(c.Core, c.Offset)));
            Replace(LedgerEntries, ledger.Select(BuildLedgerRow));

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

        await RefreshHistoryCoreAsync(connection, ct).ConfigureAwait(false);
        await RefreshModulesCoreAsync(connection, ct).ConfigureAwait(false);
        await RefreshPowerCoreAsync(connection, ct).ConfigureAwait(false);
        await RefreshLogCoreAsync(connection, ct).ConfigureAwait(false);
        await RefreshDoctorCoreAsync(connection, ct).ConfigureAwait(false);
    }

    private async Task RefreshHistoryAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        await RefreshHistoryCoreAsync(connection, _cts.Token).ConfigureAwait(false);
    }

    private async Task RefreshHistoryCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var history = await connection.GetTelemetryHistoryAsync(HistoryLength, ct).ConfigureAwait(false);
            var rows = history.Samples
                .Reverse()
                .Select(BuildTelemetryHistoryRow)
                .ToList();

            _ui.Post(_ => Replace(TelemetryHistory, rows), null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private async Task RefreshProcessesAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            // 只把当前选中行带上：路径与亲和性只对它单查，全表逐进程开句柄是被禁止的做法。
            var rows = await connection.ListProcessesAsync(SelectedProcess?.Pid, _cts.Token).ConfigureAwait(false);
            var mapped = rows.Select(BuildProcessRow).ToList();

            _ui.Post(_ =>
            {
                var selectedPid = SelectedProcess?.Pid;
                Replace(Processes, mapped);
                SelectedProcess = Processes.FirstOrDefault(row => row.Pid == selectedPid);
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private static ProcessRowView BuildProcessRow(ProcessEntryDto row) => new(
        row.Pid,
        row.Name,
        row.Path ?? "—",
        row.Priority,
        row.Affinity ?? "—",
        row.CpuSets ?? "—",
        row.CpuPercent is { } percent ? $"{percent:0.0}%" : "—",
        row.Rule ?? "—");

    private async Task RefreshModulesAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        await RefreshModulesCoreAsync(connection, _cts.Token).ConfigureAwait(false);
    }

    private async Task RefreshModulesCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var modules = await connection.ListModulesAsync(ct).ConfigureAwait(false);
            _ui.Post(_ => ApplyModules(modules), null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private void ApplyModules(IReadOnlyList<ModuleDto> modules)
    {
        var selectedId = SelectedModule?.Id;
        Replace(Modules, modules.Select(BuildModuleRow));
        SelectedModule = Modules.FirstOrDefault(m => m.Id == selectedId) ?? Modules.FirstOrDefault();
    }

    private async Task EnableSelectedModuleAsync()
    {
        var connection = _connection;
        var selected = SelectedModule;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var modules = await connection.EnableModuleAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ =>
            {
                ApplyModules(modules);
                ShowNotice(T("notice.module.enabled", selected.Name), isError: false);
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private async Task DisableSelectedModuleAsync()
    {
        var connection = _connection;
        var selected = SelectedModule;
        if (connection is null || selected is null)
        {
            return;
        }

        try
        {
            var modules = await connection.DisableModuleAsync(selected.Id, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ =>
            {
                ApplyModules(modules);
                ShowNotice(T("notice.module.disabled", selected.Name), isError: false);
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private async Task RefreshPowerAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        await RefreshPowerCoreAsync(connection, _cts.Token).ConfigureAwait(false);
    }

    private async Task RefreshPowerCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var schemes = await connection.ListPowerSchemesAsync(ct).ConfigureAwait(false);
            var backups = await connection.ListPowerSchemeBackupsAsync(ct).ConfigureAwait(false);
            _ui.Post(_ =>
            {
                var selectedSchemeGuid = SelectedPowerScheme?.Guid;
                var selectedBackupId = SelectedPowerBackup?.Id;

                Replace(PowerSchemes, schemes.Select(BuildPowerSchemeRow));
                Replace(PowerBackups, backups.Select(BuildPowerBackupRow));

                SelectedPowerScheme = PowerSchemes.FirstOrDefault(s => s.Guid == selectedSchemeGuid) ?? PowerSchemes.FirstOrDefault();
                SelectedPowerBackup = PowerBackups.FirstOrDefault(b => b.Id == selectedBackupId) ?? PowerBackups.FirstOrDefault();
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }

        await ReadPowerLimitsCoreAsync(connection, ct).ConfigureAwait(false);
    }

    private async Task ReadPowerLimitsAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        await ReadPowerLimitsCoreAsync(connection, _cts.Token).ConfigureAwait(false);
    }

    private async Task ReadPowerLimitsCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var limits = await connection.ReadPowerLimitsAsync(ct).ConfigureAwait(false);
            _ui.Post(_ =>
            {
                if (!limits.Supported || !limits.Available)
                {
                    PowerLimitsSummary = T("power.limits.unavailable_detail", limits.Error ?? T("power.limits.unavailable_unknown"));
                    return;
                }

                var sim = limits.Simulated ? " · Simulated" : string.Empty;
                var pm = $"STAPM {FormatWatts(limits.StapmWatts)} / FAST {FormatWatts(limits.FastWatts)} / SLOW {FormatWatts(limits.SlowWatts)}";
                var fused = $"Fused FAST {FormatWatts(limits.FusedFastWatts)} / SLOW {FormatWatts(limits.FusedSlowWatts)} / TDC {FormatAmps(limits.FusedTdcAmps)}";
                PowerLimitsSummary = $"{pm}  |  {fused}{sim}";
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ =>
            {
                PowerLimitsSummary = "unavailable";
                ShowNotice(ex.DetailOrMessage(), isError: true);
            }, null);
        }
    }

    private async Task ApplyPowerLimitsAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            var fast = ParseOptionalInt(FastWattsInput, T("power.limits_fast"));
            var slow = ParseOptionalInt(SlowWattsInput, T("power.limits_slow"));
            var tdc = ParseOptionalInt(TdcAmpsInput, T("power.limits_tdc"));
            var edc = ParseOptionalInt(EdcAmpsInput, T("power.limits_edc"));

            if (fast is null && slow is null && tdc is null && edc is null)
            {
                throw new ArgumentException(T("error.power_limits_empty"));
            }

            var result = await connection.ApplyPowerLimitsAsync(new PowerLimitApplyRequest(fast, slow, tdc, edc), _cts.Token).ConfigureAwait(false);
            await ReadPowerLimitsCoreAsync(connection, _cts.Token).ConfigureAwait(false);

            var hasError = !string.IsNullOrWhiteSpace(result.RejectedReason) || result.Items.Any(static i => i.State is "rejected");
            var summary = string.IsNullOrWhiteSpace(result.RejectedReason)
                ? string.Join("；", result.Items.Select(static i => $"{i.Item}: {i.Requested}→{i.Actual?.ToString(CultureInfo.InvariantCulture) ?? "-"} ({i.State})"))
                : result.RejectedReason;
            if (!string.IsNullOrWhiteSpace(result.Note))
            {
                summary = $"{summary} — {result.Note}";
            }

            _ui.Post(_ => ShowNotice(summary, isError: hasError), null);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private async Task CreatePowerSchemeAsync()
    {
        var connection = _connection;
        var selected = SelectedPowerScheme;
        var newName = PowerSchemeNewName.Trim();
        if (connection is null || selected is null || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        try
        {
            var created = await connection.CreatePowerSchemeAsync(selected.Guid, newName, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ =>
            {
                PowerSchemeNewName = string.Empty;
                ShowNotice(T("notice.power.scheme.created", created.Name), isError: false);
            }, null);
            await RefreshPowerCoreAsync(connection, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private async Task RenameSelectedPowerSchemeAsync()
    {
        var connection = _connection;
        var selected = SelectedPowerScheme;
        var newName = PowerSchemeNewName.Trim();
        if (connection is null || selected is null || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        try
        {
            var renamed = await connection.RenamePowerSchemeAsync(selected.Guid, newName, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ =>
            {
                PowerSchemeNewName = string.Empty;
                ShowNotice(T("notice.power.scheme.renamed", renamed.Name), isError: false);
            }, null);
            await RefreshPowerCoreAsync(connection, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
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
            await RefreshPowerCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ => ShowNotice(T("notice.power.scheme.backed_up", selected.Name, backup.Id), isError: false), null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
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
            var result = await connection.DeletePowerSchemeAsync(selected.Guid, keepBackup: true, _cts.Token).ConfigureAwait(false);
            await RefreshPowerCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ => ShowNotice(result.Deleted
                ? T("notice.power.scheme.deleted", selected.Name)
                : T("notice.power.scheme.delete_failed", selected.Name), isError: !result.Deleted), null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
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
            await RefreshPowerCoreAsync(connection, _cts.Token).ConfigureAwait(false);
            _ui.Post(_ => ShowNotice(T("notice.power.backup.restored", result.Scheme.Name, result.Applied, result.Failed), isError: result.Failed > 0), null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private async Task RefreshLogAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        await RefreshLogCoreAsync(connection, _cts.Token).ConfigureAwait(false);
    }

    private async Task RefreshLogCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var entries = await connection.GetLogAsync(LogFetchLimit, ct).ConfigureAwait(false);
            var rows = entries.Reverse().Select(BuildLogRow).ToList();
            _ui.Post(_ => Replace(Logs, rows), null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
        }
    }

    private async Task RefreshDoctorAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        await RefreshDoctorCoreAsync(connection, _cts.Token).ConfigureAwait(false);
    }

    private async Task RefreshDoctorCoreAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var doc = await connection.GetDoctorAsync(ct).ConfigureAwait(false);
            var summary = string.Join(Environment.NewLine,
                T("runtime.summary.line1", doc.Version, doc.ProtocolVersion, doc.UptimeSeconds, doc.PlanCount, doc.SceneCount, doc.LedgerEntryCount),
                T("runtime.summary.data_dir", doc.DataDirectory),
                T("runtime.summary.state_path", doc.StatePath),
                T("runtime.summary.log_dir", doc.LogDirectory));
            var errorRows = doc.RecentErrors.Reverse().Select(BuildLogRow).ToList();
            _ui.Post(_ =>
            {
                DoctorSummary = summary;
                Replace(DoctorErrors, errorRows);
            }, null);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            _ui.Post(_ =>
            {
                DoctorSummary = "unavailable";
                ShowNotice(ex.DetailOrMessage(), isError: true);
            }, null);
        }
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
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
            ShowNotice(T("notice.plan.saved", saved.Name, saved.Revision), isError: false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
        }
    }

    private Plan BuildPlanFromForm()
    {
        var id = NewPlanId.Trim();
        var name = NewPlanName.Trim();
        var spec = NewPlanSpec.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(T("error.plan.id_name_required"));
        }

        var now = DateTimeOffset.Now;
        switch (NewPlanKind)
        {
            case "cpuTuning":
            {
                int? defaultCo = null;
                var ccdOffsets = new Dictionary<int, int>();
                var cores = new List<CoreTuning>();
                foreach (var token in spec.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = token.Split('=', 2);
                    if (parts.Length != 2)
                    {
                        throw new FormatException(T("error.plan.co_config_invalid", token));
                    }

                    var key = parts[0].Trim();
                    var val = parts[1].Trim();
                    if (string.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var def))
                        {
                            throw new FormatException(T("error.plan.co_default_invalid", val));
                        }

                        defaultCo = def;
                        continue;
                    }

                    if (key.StartsWith("ccd", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(key.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ccd)
                        && ccd >= 0)
                    {
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ccdVal))
                        {
                            throw new FormatException(T("error.plan.co_key_invalid", key, val));
                        }

                        ccdOffsets[ccd] = ccdVal;
                        continue;
                    }

                    if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var core))
                    {
                        throw new FormatException(T("error.plan.core_config_invalid", token));
                    }

                    if (string.Equals(val, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        cores.Add(new CoreTuning { CoreIndex = core, Auto = true });
                        continue;
                    }

                    if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                    {
                        throw new FormatException(T("error.plan.core_value_invalid", core, val));
                    }

                    cores.Add(new CoreTuning { CoreIndex = core, Offset = offset });
                }

                if (defaultCo is null && ccdOffsets.Count == 0 && cores.Count == 0)
                {
                    throw new ArgumentException(T("error.plan.co_required"));
                }

                return new CpuTuningPlan
                {
                    Id = id,
                    Name = name,
                    DefaultOffset = defaultCo,
                    CcdOffsets = ccdOffsets.Count == 0 ? null : ccdOffsets,
                    Cores = cores,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            }
            case "power":
            {
                if (!Guid.TryParse(spec, out var schemeGuid))
                {
                    throw new FormatException(T("error.plan.power_guid_required"));
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
                    throw new FormatException(T("error.plan.scheduling_format"));
                }

                var parts = assignment[1].Split(':', 3);
                var mask = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0].Trim();
                var priority = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;
                var cpuSets = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;
                if (mask is null && priority is null && cpuSets is null)
                {
                    throw new ArgumentException(T("error.plan.scheduling_empty"));
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
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
            ShowNotice(T("notice.plan.deleted", selected.Id), isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
                throw new ArgumentException(T("error.scene.fields_required"));
            }

            if (!int.TryParse(NewScenePriority.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
            {
                throw new FormatException(T("error.scene.priority_integer"));
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
            ShowNotice(T("notice.scene.saved", saved.Name), isError: false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
            ShowNotice(T("notice.scene.deleted", selected.Id), isError: false);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
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
            ShowNotice(released
                ? T("notice.takeover.released", selected.Target)
                : T("notice.takeover.not_user_owned", selected.Target), isError: !released);
        }
        catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
        {
            ShowNotice(ex.DetailOrMessage(), isError: true);
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

        // 传感器按自己的节拍采样：这一帧没带传感器值时保持上一帧，不要把「这次没读」显示成 0。
        if (sample.TemperatureCelsius is { } temperature)
        {
            _temperatureCelsius = temperature;
        }

        if (sample.PackagePowerWatts is { } power)
        {
            _packagePowerWatts = power;
        }

        if (sample.CoreClocks is { Count: > 0 } clocks)
        {
            _coreClocks = clocks;
        }

        if (sample.ProcessorUtilization is { Count: > 0 } utilization)
        {
            _processorUtilization = utilization;
        }

        Raise(nameof(CpuText));
        Raise(nameof(MemoryText));
        Raise(nameof(ProcessText));
        Raise(nameof(CoreText));
        Raise(nameof(TemperatureText));
        Raise(nameof(PackagePowerText));
        Raise(nameof(CoreClocksText));
        Raise(nameof(UtilizationText));

        SampleAppended?.Invoke(BuildTelemetryHistoryRow(sample));
    }

    private void ApplySettings(SettingsDto settings)
    {
        _suppressPersist = true;
        try
        {
            var language = settings.Language;
            if (!Languages.Contains(language))
            {
                language = "zh-CN";
            }

            SelectedLanguage = language;
            _selectedTheme = settings.Theme;
            _selectedAccent = settings.Accent;
            _uiScale = Math.Clamp(settings.UiScale, 0.8, 1.6);
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    private void PersistSettingsAsync()
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

        var patch = new SettingsPatch(_selectedLanguage, _selectedTheme, _selectedAccent, _uiScale);
        _ = Task.Run(async () =>
        {
            try
            {
                await connection.SetSettingsAsync(patch, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IpcTransportException or TimeoutException or AgentRequestException)
            {
                _ui.Post(_ => ShowNotice(ex.DetailOrMessage(), isError: true), null);
            }
        });
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
        HardwareSummary = T("status.hardware_summary",
            status.MachineName, status.PhysicalCoreCount, status.ProcessorCount, active, status.LedgerEntryCount, amd.Availability);

        var amdPersistence = amd.PersistsAcrossReboot
            ? T("status.tuning.firmware_persistent")
            : T("status.tuning.session_only");
        TuningSummary = $"AMD SMU: {amd.Availability} ({amd.Platform ?? "-"}, {amdPersistence}) — {amd.Detail ?? "-"}";

        var intelDetail = intel.VoltageOffsetMillivolts is { } mv
            ? T("status.intel_tuning_detail", mv,
                intel.TurboRatio1Core?.ToString(CultureInfo.InvariantCulture) ?? "-",
                intel.TurboRatio8Cores?.ToString(CultureInfo.InvariantCulture) ?? "-")
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
        RefreshHistoryCommand.RaiseCanExecuteChanged();
        RefreshProcessesCommand.RaiseCanExecuteChanged();
        CreatePlanCommand.RaiseCanExecuteChanged();
        ApplyPlanCommand.RaiseCanExecuteChanged();
        RevertPlanCommand.RaiseCanExecuteChanged();
        DeletePlanCommand.RaiseCanExecuteChanged();
        CreateSceneCommand.RaiseCanExecuteChanged();
        ActivateSceneCommand.RaiseCanExecuteChanged();
        DeactivateSceneCommand.RaiseCanExecuteChanged();
        ToggleSceneEnabledCommand.RaiseCanExecuteChanged();
        DeleteSceneCommand.RaiseCanExecuteChanged();
        RefreshTuningCommand.RaiseCanExecuteChanged();
        ReleaseTakeoverCommand.RaiseCanExecuteChanged();
        RefreshModulesCommand.RaiseCanExecuteChanged();
        EnableModuleCommand.RaiseCanExecuteChanged();
        DisableModuleCommand.RaiseCanExecuteChanged();
        RefreshPowerCommand.RaiseCanExecuteChanged();
        ReadPowerLimitsCommand.RaiseCanExecuteChanged();
        ApplyPowerLimitsCommand.RaiseCanExecuteChanged();
        CreatePowerSchemeCommand.RaiseCanExecuteChanged();
        RenamePowerSchemeCommand.RaiseCanExecuteChanged();
        BackupPowerSchemeCommand.RaiseCanExecuteChanged();
        DeletePowerSchemeCommand.RaiseCanExecuteChanged();
        RestorePowerBackupCommand.RaiseCanExecuteChanged();
        RefreshLogCommand.RaiseCanExecuteChanged();
        RefreshDoctorCommand.RaiseCanExecuteChanged();
    }

    private int? ParseOptionalInt(string raw, string label)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new FormatException(T("error.positive_integer", label));
        }

        return value;
    }

    private static string FormatWatts(int? watts) => watts is { } w ? $"{w}W" : "—";

    private static string FormatAmps(int? amps) => amps is { } a ? $"{a}A" : "—";

    private static TelemetryHistoryRow BuildTelemetryHistoryRow(TelemetryDto sample)
    {
        var mem = sample.MemoryTotalBytes == 0
            ? "—"
            : $"{(1d - (double)sample.MemoryAvailableBytes / sample.MemoryTotalBytes) * 100d:0}%";
        var temp = sample.TemperatureCelsius is { } t ? $"{t:0.0}°C" : "—";
        var power = sample.PackagePowerWatts is { } p ? $"{p:0.0}W" : "—";
        return new TelemetryHistoryRow(
            sample.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            $"{sample.CpuTotalPercent:0}%",
            mem,
            sample.ProcessCount.ToString(CultureInfo.InvariantCulture),
            temp,
            power);
    }

    private static ModuleRow BuildModuleRow(ModuleDto module) => new(
        module.Id,
        module.Name,
        module.State,
        module.Optional,
        module.Enabled,
        string.Join(", ", module.Capabilities),
        module.Detail ?? string.Empty);

    private static PowerSchemeRow BuildPowerSchemeRow(PowerSchemeDto scheme) => new(
        scheme.Guid,
        scheme.Name,
        scheme.Active,
        scheme.BuiltIn,
        scheme.Visible);

    private static PowerBackupRow BuildPowerBackupRow(PowerSchemeBackupDto backup) => new(
        backup.Id,
        backup.Name,
        backup.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        backup.SettingCount);

    private static LogRow BuildLogRow(LogEntryDto entry) => new(
        entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        entry.Level,
        entry.Message);

    private PlanRow BuildPlanRow(Plan plan)
    {
        var state = plan.LastApply?.State switch
        {
            PlanApplyState.Applied => "applied",
            PlanApplyState.Partial => "partial",
            PlanApplyState.Failed => "failed",
            PlanApplyState.Unknown => "unknown",
            _ => "draft",
        };
        var detail = plan switch
        {
            CpuTuningPlan cpu => CurveOptimizerConfig.FormatPlanSummary(cpu),
            PowerPlan power => power.BaseSchemeGuid?.ToString("D") ?? T("plans.summary.settings_count", power.Settings.Count),
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
            CanRevert: plan.LastApply?.State is PlanApplyState.Applied or PlanApplyState.Partial or PlanApplyState.Unknown,
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

public sealed record ProcessRowView(
    int Pid,
    string Name,
    string Path,
    string Priority,
    string Affinity,
    string CpuSets,
    string CpuPercent,
    string Rule);

public sealed record TelemetryHistoryRow(string Time, string Cpu, string Memory, string Processes, string Temperature, string Power);

public sealed record ModuleRow(
    string Id,
    string Name,
    string State,
    bool Optional,
    bool Enabled,
    string Capabilities,
    string Detail);

public sealed record PowerSchemeRow(
    Guid Guid,
    string Name,
    bool Active,
    bool BuiltIn,
    bool Visible);

public sealed record PowerBackupRow(
    string Id,
    string Name,
    string CreatedAt,
    int SettingCount);

public sealed record LogRow(string Time, string Level, string Message);

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
