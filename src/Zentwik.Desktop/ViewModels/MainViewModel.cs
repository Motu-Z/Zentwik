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

    private string _selectedLanguage;
    private string _selectedTheme;
    private string _selectedAccent;
    private double _uiScale = 1d;

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

        ToggleCreateSceneCommand = new RelayCommand(() => IsCreateSceneOpen = !IsCreateSceneOpen);
        CreateSceneCommand = new AsyncRelayCommand(CreateSceneAsync, () => IsConnected);
        ActivateSceneCommand = new AsyncRelayCommand(ActivateSelectedSceneAsync, () => IsConnected && SelectedScene is not null);
        DeactivateSceneCommand = new AsyncRelayCommand(DeactivateSceneAsync, () => IsConnected && !string.IsNullOrEmpty(_activeSceneId));
        ToggleSceneEnabledCommand = new AsyncRelayCommand(ToggleSelectedSceneAsync, () => IsConnected && SelectedScene is not null);
        DeleteSceneCommand = new AsyncRelayCommand(DeleteSelectedSceneAsync, () => IsConnected && SelectedScene is not null);

        RefreshTuningCommand = new AsyncRelayCommand(RefreshTuningAndLedgerAsync, () => IsConnected);
        ReleaseTakeoverCommand = new AsyncRelayCommand(ReleaseSelectedTakeoverAsync, () => IsConnected && SelectedLedger is { IsUserOwned: true });

        catalog.CultureChanged += OnCultureChanged;
    }

    /// <summary>采样历史更新。视图层据此重绘折线，视图模型不持有绘图对象。</summary>
    public event Action<double[]>? HistoryChanged;

    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    public ObservableCollection<PlanRow> Plans { get; } = [];

    public ObservableCollection<SceneRow> Scenes { get; } = [];

    public ObservableCollection<CoreOffsetRow> CoreOffsets { get; } = [];

    public ObservableCollection<LedgerRow> LedgerEntries { get; } = [];

    public IReadOnlyList<string> Languages => LocalizationCatalog.SupportedCultures;

    public IReadOnlyList<string> Themes { get; } = ["dark", "light"];

    public IReadOnlyList<string> Accents { get; } = ["blue", "violet", "teal", "amber"];

    public IReadOnlyList<double> ScaleOptions { get; } = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

    public IReadOnlyList<string> PlanKinds { get; } = ["cpuTuning", "power", "scheduling"];

    public IReadOnlyList<string> TriggerKinds { get; } =
    [
        SceneTriggerKinds.Manual,
        SceneTriggerKinds.Process,
        SceneTriggerKinds.PowerSource,
    ];

    public AsyncRelayCommand RefreshCommand { get; }
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
                await connection.SubscribeTelemetryAsync(SampleIntervalMs, _cts.Token).ConfigureAwait(false);

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
            ShowNotice(ex.Message, isError: true);
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
                    throw new FormatException("调度方案参数请使用 进程名=掩码:优先级（如 game=0xF:High）。");
                }

                var parts = assignment[1].Split(':', 2);
                var mask = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0].Trim();
                var priority = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;
                if (mask is null && priority is null)
                {
                    throw new ArgumentException("调度规则至少需要掩码或优先级。");
                }

                return new SchedulingPlan
                {
                    Id = id,
                    Name = name,
                    Rules = [new SchedulingRule { ProcessMatch = assignment[0].Trim(), CpuMask = mask, Priority = priority }],
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
                Actions = [new SceneAction { PlanId = planId, PlanRevision = 0 }],
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
            SelectedLanguage = settings.Language;
            SelectedTheme = settings.Theme;
            SelectedAccent = settings.Accent;
            UiScale = settings.UiScale;
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
            SchedulingPlan sched => string.Join(", ", sched.Rules.Select(static r => $"{r.ProcessMatch}({r.CpuMask ?? "-"}:{r.Priority ?? "-"})")),
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