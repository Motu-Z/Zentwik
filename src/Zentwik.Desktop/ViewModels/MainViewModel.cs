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
    private string _powerLimitsSummary = "—";
    private string _doctorSummary = "—";

    private double _cpuPercent;
    private double _memoryPercent;
    private int _processCount;
    private int _coreCount;
    private int _physicalCoreCount;

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
        ApplyPowerLimitsCommand = new AsyncRelayCommand(ApplyPowerLimitsAsync, () => IsConnected);
        CreatePowerSchemeCommand = new AsyncRelayCommand(CreatePowerSchemeAsync, () => IsConnected && SelectedPowerScheme is not null && !string.IsNullOrWhiteSpace(PowerSchemeNewName));
        RenamePowerSchemeCommand = new AsyncRelayCommand(RenameSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is { BuiltIn: false } && !string.IsNullOrWhiteSpace(PowerSchemeNewName));
        BackupPowerSchemeCommand = new AsyncRelayCommand(BackupSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is not null);
        DeletePowerSchemeCommand = new AsyncRelayCommand(DeleteSelectedPowerSchemeAsync, () => IsConnected && SelectedPowerScheme is { BuiltIn: false });
        RestorePowerBackupCommand = new AsyncRelayCommand(RestoreSelectedPowerBackupAsync, () => IsConnected && SelectedPowerBackup is not null);

        RefreshLogCommand = new AsyncRelayCommand(RefreshLogAsync, () => IsConnected);
        RefreshDoctorCommand = new AsyncRelayCommand(RefreshDoctorAsync, () => IsConnected);

        catalog.CultureChanged += OnCultureChanged;
    }

    /// <summary>采样历史更新。视图层据此重绘折线，视图模型不持有绘图对象。</summary>
    public event Action<double[]>? HistoryChanged;

    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    public ObservableCollection<TelemetryHistoryRow> TelemetryHistory { get; } = [];

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
        set => Set(ref _fastWattsInput, value);
    }

    public string SlowWattsInput
    {
        get => _slowWattsInput;
        set => Set(ref _slowWattsInput, value);
    }

    public string TdcAmpsInput
    {
        get => _tdcAmpsInput;
        set => Set(ref _tdcAmpsInput, value);
    }

    public string EdcAmpsInput
    {
        get => _edcAmpsInput;
        set => Set(ref _edcAmpsInput, value);
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
                ShowNotice($"已启用模块 {selected.Name}。", isError: false);
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
                ShowNotice($"已停用模块 {selected.Name}。", isError: false);
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
                    PowerLimitsSummary = $"unavailable ({limits.Error ?? "当前平台或模块不可用"})";
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
            var fast = ParseOptionalInt(FastWattsInput, "FAST (W)");
            var slow = ParseOptionalInt(SlowWattsInput, "SLOW (W)");
            var tdc = ParseOptionalInt(TdcAmpsInput, "TDC (A)");
            var edc = ParseOptionalInt(EdcAmpsInput, "EDC (A)");

            if (fast is null && slow is null && tdc is null && edc is null)
            {
                throw new ArgumentException("请至少填写一项功耗墙参数（FAST / SLOW / TDC / EDC）。");
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
                ShowNotice($"已派生电源方案 {created.Name}。", isError: false);
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
                ShowNotice($"已重命名电源方案为 {renamed.Name}。", isError: false);
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
            _ui.Post(_ => ShowNotice($"已备份电源方案 {selected.Name} → {backup.Id}。", isError: false), null);
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
            _ui.Post(_ => ShowNotice(result.Deleted ? $"已删除电源方案 {selected.Name}。" : $"未能删除电源方案 {selected.Name}。", isError: !result.Deleted), null);
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
            _ui.Post(_ => ShowNotice($"已还原电源方案 {result.Scheme.Name}（应用 {result.Applied} 项，失败 {result.Failed} 项）。", isError: result.Failed > 0), null);
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
            var summary =
                $"版本 {doc.Version} (协议 v{doc.ProtocolVersion}) · 运行 {doc.UptimeSeconds}s · 方案 {doc.PlanCount} / 场景 {doc.SceneCount} / 账本 {doc.LedgerEntryCount}" +
                $"{Environment.NewLine}数据目录: {doc.DataDirectory}" +
                $"{Environment.NewLine}状态文件: {doc.StatePath}" +
                $"{Environment.NewLine}日志目录: {doc.LogDirectory}";
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
            ShowNotice($"已保存方案 {saved.Name} (rev {saved.Revision})。", isError: false);
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
            throw new ArgumentException("方案 ID 和名称不能为空。");
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

                if (defaultCo is null && ccdOffsets.Count == 0 && cores.Count == 0)
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
            ShowNotice($"已删除方案 {selected.Id}。", isError: false);
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
            ShowNotice($"已删除场景 {selected.Id}。", isError: false);
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
            ShowNotice(released ? $"已释放 {selected.Target} 的用户接管。" : $"{selected.Target} 当前非用户接管。", isError: !released);
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

        Raise(nameof(CpuText));
        Raise(nameof(MemoryText));
        Raise(nameof(ProcessText));
        Raise(nameof(CoreText));

        _history.Enqueue(_cpuPercent);
        while (_history.Count > HistoryLength)
        {
            _history.Dequeue();
        }

        HistoryChanged?.Invoke([.. _history]);
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
        RefreshHistoryCommand.RaiseCanExecuteChanged();
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

    private static int? ParseOptionalInt(string raw, string label)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new FormatException($"{label} 必须为正整数。");
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
