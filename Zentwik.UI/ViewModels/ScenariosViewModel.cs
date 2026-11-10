using System.Collections.ObjectModel;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>
/// 场景页：一条列表维护「触发源 → 三个动作」的规则，列表自上而下的顺序就是仲裁优先级。
/// 一条规则可以只动其中一块，留空的那块表示保持原样。
/// </summary>
public sealed class ScenariosViewModel : ViewModelBase
{
    /// <summary>诊断模式（截图 / 自检）：不连 Worker，用示例规则。</summary>
    public static bool SuppressWorkerInit { get; set; }

    /// <summary>诊断用：以「自动化已关闭」启动，核对规则卡置灰的外观。</summary>
    public static bool StartWithAutoSwitchOff { get; set; }

    /// <summary>诊断用：截图时直接展开新建表单（截图没有鼠标，点不出这一步）。</summary>
    public static bool StartWithEditorOpen { get; set; }

    /// <summary>诊断用：截图时把进程下拉撑开（离屏截图不驱动 Popup，只能验数据）。</summary>
    public static bool StartWithProcessPickerOpen { get; set; }

    /// <summary>诊断用：表单以「进程退出」触发打开，核对调度块置灰的样子。</summary>
    public static bool StartWithExitTrigger { get; set; }

    private const int PriorityStep = 10;

    private const string KeepActionLabel = "保持原样";

    private bool _sceneAutoSwitch = !StartWithAutoSwitchOff;

    public ScenariosViewModel()
    {
        NewRuleCommand = new RelayCommand(_ => OpenEditor(null));
        EditRuleCommand = new RelayCommand(p => { if (p is ScenarioRuleItem item) OpenEditor(item); });
        DeleteRuleCommand = new RelayCommand(p => { if (p is ScenarioRuleItem item) Delete(item); });
        ToggleRuleCommand = new RelayCommand(p => { if (p is ScenarioRuleItem item) Toggle(item); });
        MoveRuleUpCommand = new RelayCommand(p =>
        {
            if (p is ScenarioRuleItem item)
                MoveRuleTo(item, Rules.IndexOf(item) - 1);
        });
        MoveRuleDownCommand = new RelayCommand(p =>
        {
            if (p is ScenarioRuleItem item)
                MoveRuleTo(item, Rules.IndexOf(item) + 1);
        });

        SaveEditorCommand = new RelayCommand(_ => SaveEditor());
        CancelEditorCommand = new RelayCommand(_ => IsEditorOpen = false);

        OpenProcessPickerCommand = new RelayCommand(_ => Fire(OpenProcessPickerAsync));
        PickProcessCommand = new RelayCommand(p => { if (p is ScenarioProcessOption option) PickProcess(option); });

        // 诊断模式在启动早期就置好了开关：种子里直接放规则，截图拿到的不是空列表。
        // 不能等 InitializeAsync —— 它有 await，continuation 会排在渲染后面。
        if (SuppressWorkerInit)
            SeedDemo();
    }

    public ObservableCollection<ScenarioRuleItem> Rules { get; } = new();

    public bool HasRules => Rules.Count > 0;

    public bool SceneAutoSwitch
    {
        get => _sceneAutoSwitch;
        set
        {
            if (!SetProperty(ref _sceneAutoSwitch, value))
                return;

            if (SuppressWorkerInit)
                return;

            int sequence = System.Threading.Interlocked.Increment(ref _switchRequestSeq);

            Fire(async () =>
            {
                IpcOutcome<ScenarioStatePayload> outcome = await WorkerBridge.Instance
                    .SetScenarioEnabledAsync(value).ConfigureAwait(true);

                // 快速连点只认最后一次请求的回值：早到的过期响应不能把开关拨回去。
                if (sequence != System.Threading.Volatile.Read(ref _switchRequestSeq))
                    return;

                if (!outcome.Success || outcome.Value is null)
                {
                    Toast(outcome.Message);
                    SetAutoSwitchSilently(!value);
                    return;
                }

                // 以服务端回值为准：写盘失败时它保留原值，界面拿回显值会显示成「已经开了」。
                SetAutoSwitchSilently(outcome.Value.Enabled);
            });
        }
    }

    private int _switchRequestSeq;

    private void SetAutoSwitchSilently(bool enabled)
    {
        if (_sceneAutoSwitch == enabled)
            return;

        _sceneAutoSwitch = enabled;
        OnPropertyChanged(nameof(SceneAutoSwitch));
    }

    private bool _isEditorOpen;

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        set => SetProperty(ref _isEditorOpen, value);
    }

    public string EditorTitle => _editingId is null ? "添加自动化联动" : "编辑自动化联动";

    // ---- 触发源：两级下拉（大类 → 状态）----
    //
    // 界面写成一句人话「当 进程 启动时 <cs2.exe>」，所以要有两级：
    // 大类决定联动谁，状态决定是它的哪个瞬间。两级各自的下拉项在字段里缓存成
    // 固定实例，切换大类时只换 ItemsSource —— 每次重建集合会让 ComboBox
    // 把 SelectedItem 回写成 null，选中的那项凭空消失。

    private static readonly ScenarioKindOption ProcessKind = new(ScenarioTrigger.ProcessRunning, "进程");
    private static readonly ScenarioKindOption PowerKind = new(ScenarioTrigger.OnBattery, "系统电源");

    public IReadOnlyList<ScenarioKindOption> TriggerKinds { get; } = new[] { ProcessKind, PowerKind };

    private static readonly ScenarioStateOption[] ProcessStates =
    {
        new(ScenarioTrigger.ProcessRunning, "启动时"),
        new(ScenarioTrigger.ProcessExited, "退出时")
    };

    private static readonly ScenarioStateOption[] PowerStates =
    {
        new(ScenarioTrigger.OnBattery, "拔下电源时"),
        new(ScenarioTrigger.OnAcPower, "接通电源时")
    };

    private ScenarioKindOption _selectedKind = ProcessKind;

    public ScenarioKindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            // 切大类时 WPF 会先回写一次 null（旧选中项已不在新列表里），忽略即可。
            if (value is null || ReferenceEquals(value, _selectedKind))
                return;

            _selectedKind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TriggerStates));
            OnPropertyChanged(nameof(IsProcessKind));

            // 状态必须落在新列表里：留在「启动时」而大类已切到电源，
            // 下拉会显示空白，保存下去就是一条参数对不上的规则。
            SelectedState = TriggerStates[0];
        }
    }

    public IReadOnlyList<ScenarioStateOption> TriggerStates
        => ReferenceEquals(_selectedKind, PowerKind) ? PowerStates : ProcessStates;

    /// <summary>当前大类是进程 —— 决定要不要显示进程选择框。</summary>
    public bool IsProcessKind => ReferenceEquals(_selectedKind, ProcessKind);

    private ScenarioStateOption _selectedState = ProcessStates[0];

    public ScenarioStateOption SelectedState
    {
        get => _selectedState;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedState))
                return;

            _selectedState = value;
            OnPropertyChanged();
        }
    }

    /// <summary>真正下发的触发源，由两级下拉合成。</summary>
    public ScenarioTrigger EditorTrigger => _selectedState.Trigger;

    // ---- 进程搜索 ----

    /// <summary>最近一次拉到的进程表（去重后），过滤只在这份上做，不重复采样。</summary>
    private readonly List<ScenarioProcessOption> _allProcesses = new();

    public ObservableCollection<ScenarioProcessOption> ProcessSuggestions { get; } = new();

    private string _editorProcessName = string.Empty;

    public string EditorProcessName
    {
        get => _editorProcessName;
        set
        {
            // 绑定回写可能给 null（TextBox 清空、ComboBox 重建候选），统一收成空串，
            // 否则下面所有 .Length 判断都会抛 NullReferenceException。
            if (!SetProperty(ref _editorProcessName, value ?? string.Empty))
                return;

            if (!string.IsNullOrEmpty(_processNameError))
                SetProcessNameError(string.Empty);

            OnPropertyChanged(nameof(ShowProcessPlaceholder));
            FilterProcesses();
        }
    }

    /// <summary>输入框为空时显示水印。WPF 的 TextBox 没有占位符属性，只能叠一层文字。</summary>
    public bool ShowProcessPlaceholder => string.IsNullOrEmpty(_editorProcessName);

    private string _processNameError = string.Empty;

    public string ProcessNameError => _processNameError;

    public bool HasProcessNameError => !string.IsNullOrEmpty(_processNameError);

    private void SetProcessNameError(string text)
    {
        _processNameError = text;
        OnPropertyChanged(nameof(ProcessNameError));
        OnPropertyChanged(nameof(HasProcessNameError));
    }

    /// <summary>进程名必须能当文件名用且带 .exe；命中候选时规范成真实大小写。</summary>
    private string NormalizeTriggerProcess(string raw)
    {
        string name = raw.Trim();

        if (name.Length == 0 || name.Length > 128)
            return string.Empty;

        if (name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            return string.Empty;

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return ProcessSuggestions.FirstOrDefault(
                   p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
               ?? name;
    }

    private bool _isProcessPickerOpen;

    public bool IsProcessPickerOpen
    {
        get => _isProcessPickerOpen;
        set => SetProperty(ref _isProcessPickerOpen, value);
    }

    /// <summary>三个动作的候选：调校 / 调度 / 电源各自的方案列表，首项 Id 为空串。</summary>
    public ObservableCollection<ScenarioActionOption> EditorProfiles { get; } = new();

    public ObservableCollection<ScenarioActionOption> EditorMasks { get; } = new();

    public ObservableCollection<ScenarioActionOption> EditorPowerSchemes { get; } = new();

    private string _editorProfileId = string.Empty;

    public string EditorProfileId
    {
        get => _editorProfileId;
        set
        {
            // ComboBox 用 SelectedValue 做 TwoWay 绑定：候选集合 Clear 再重建时，
            // 它会把找不到的选中项回写成 null。不归一化就会让 HasEditorProfile
            // 在读取 .Length 时抛 NRE，整块动作区渲染直接断掉。
            if (SetProperty(ref _editorProfileId, value ?? string.Empty))
                OnPropertyChanged(nameof(HasEditorProfile));
        }
    }

    private string _editorMaskId = string.Empty;

    public string EditorMaskId
    {
        get => _editorMaskId;
        set
        {
            if (SetProperty(ref _editorMaskId, value ?? string.Empty))
                OnPropertyChanged(nameof(HasEditorMask));
        }
    }

    private string _editorPowerSchemeId = string.Empty;

    public string EditorPowerSchemeId
    {
        get => _editorPowerSchemeId;
        set
        {
            if (SetProperty(ref _editorPowerSchemeId, value ?? string.Empty))
                OnPropertyChanged(nameof(HasEditorPowerScheme));
        }
    }

    /// <summary>动作下拉是否是空值 —— 空值时下拉画虚线框，表示「这一块不动」。</summary>
    public bool HasEditorProfile => !string.IsNullOrEmpty(_editorProfileId);

    public bool HasEditorMask => !string.IsNullOrEmpty(_editorMaskId);

    public bool HasEditorPowerScheme => !string.IsNullOrEmpty(_editorPowerSchemeId);

    private string? _editingId;

    private int _editingPriority;

    private bool _editingEnabled = true;

    public RelayCommand NewRuleCommand { get; }
    public RelayCommand EditRuleCommand { get; }
    public RelayCommand DeleteRuleCommand { get; }
    public RelayCommand ToggleRuleCommand { get; }

    public RelayCommand MoveRuleUpCommand { get; }

    public RelayCommand MoveRuleDownCommand { get; }
    public RelayCommand SaveEditorCommand { get; }
    public RelayCommand CancelEditorCommand { get; }
    public RelayCommand OpenProcessPickerCommand { get; }
    public RelayCommand PickProcessCommand { get; }

    /// <summary>页面首次显示时拉一次总闸、规则与动作候选。</summary>
    public async Task InitializeAsync()
    {
        // 诊断种子在构造函数里已铺好，这里只补占位方案，重复铺会清掉编辑器状态。
        if (SuppressWorkerInit)
        {
            await PlanHub.Tuning.InitializeAsync().ConfigureAwait(true);
            return;
        }

        WorkerBridge bridge = WorkerBridge.Instance;

        IpcOutcome<ScenarioStatePayload> state = await bridge
            .QueryScenarioStateAsync().ConfigureAwait(true);

        if (state is { Success: true, Value: not null })
            SetAutoSwitchSilently(state.Value.Enabled);
        else
            Toast(state.Message);

        await RefreshActionOptionsAsync().ConfigureAwait(true);

        IpcOutcome<List<ScenarioRulePayload>> rules = await bridge
            .QueryScenarioRulesAsync().ConfigureAwait(true);

        if (rules is { Success: true, Value: not null })
            ApplyRules(rules.Value);
        else
            Toast(rules.Message);
    }

    /// <summary>
    /// 重拉三个动作候选。页面实例被主窗口缓存，切回来时不会再跑 InitializeAsync，
    /// 所以候选必须单独刷一次 —— 否则三大页新建 / 改名 / 删除的方案在场景页看不到，
    /// 首次进入时后台还没就绪更会只剩「不修改」。
    /// </summary>
    /// <summary>三个动作各认三大页自己的方案列表；查失败保留旧候选。</summary>
    public async Task RefreshActionOptionsAsync()
    {
        // 页面重新可见时先重绑一次，不依赖候选是否真的变了。
        RebindActionSelection();

        if (SuppressWorkerInit)
        {
            RefreshRuleDisplayNames();
            return;
        }

        WorkerBridge bridge = WorkerBridge.Instance;

        await FillActionOptionsAsync(bridge, PlanKind.Tuning, EditorProfiles).ConfigureAwait(true);
        await FillActionOptionsAsync(bridge, PlanKind.Scheduling, EditorMasks).ConfigureAwait(true);
        await FillActionOptionsAsync(bridge, PlanKind.Power, EditorPowerSchemes).ConfigureAwait(true);

        if (!IsKnownOption(EditorProfiles, EditorProfileId))
            EditorProfileId = string.Empty;
        if (!IsKnownOption(EditorMasks, EditorMaskId))
            EditorMaskId = string.Empty;
        if (!IsKnownOption(EditorPowerSchemes, EditorPowerSchemeId))
            EditorPowerSchemeId = string.Empty;

        RebindActionSelection();
        RefreshRuleDisplayNames();
    }

    /// <summary>
    /// 候选集合重建后强制通知一次三个动作 Id。
    /// <para>
    /// ComboBox 用 SelectedValue 做 TwoWay 绑定，集合 <c>Clear()</c> 时它会把选中项丢掉并
    /// 回写 null；setter 归一化成空串后与旧值相同，<c>SetProperty</c> 不认为有变化，于是
    /// 不再通知。结果就是切页回来下拉框空着、点开却能看到选项。强制通知让 ComboBox
    /// 重新按当前值找一次项。
    /// </para>
    /// </summary>
    internal void RebindActionSelection()
    {
        OnPropertyChanged(nameof(EditorProfileId));
        OnPropertyChanged(nameof(EditorMaskId));
        OnPropertyChanged(nameof(EditorPowerSchemeId));
    }

    private static async Task FillActionOptionsAsync(
        WorkerBridge bridge,
        PlanKind kind,
        ObservableCollection<ScenarioActionOption> target)
    {
        IpcOutcome<PlanListPayload> plans = await bridge.QueryPlansAsync(kind).ConfigureAwait(true);

        if (plans is not { Success: true, Value: not null })
        {
            if (target.Count == 0)
                target.Add(new ScenarioActionOption(string.Empty, KeepActionLabel));

            return;
        }

        target.Clear();
        target.Add(new ScenarioActionOption(string.Empty, KeepActionLabel));

        foreach (PlanEntryPayload entry in plans.Value.Plans ?? new List<PlanEntryPayload>())
            target.Add(new ScenarioActionOption(entry.Id, entry.Name));
    }

    private static bool IsKnownOption(ObservableCollection<ScenarioActionOption> options, string id)
        => string.IsNullOrEmpty(id) || options.Any(o => string.Equals(o.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 候选刷新后把已列出规则卡上的名字按 Id 重解析一遍。
    /// 只改显示字段：重拉规则会把用户正在编辑的表单一起冲掉。
    /// </summary>
    private void RefreshRuleDisplayNames()
    {
        foreach (ScenarioRuleItem rule in Rules)
        {
            rule.ProfileName = ProfileNameOf(rule.ProfileId);
            rule.MaskName = MaskNameOf(rule.MaskId);
            rule.PowerSchemeName = PowerNameOf(rule.PowerSchemeId);
        }
    }

    /// <summary>拉一次进程表。Worker 侧每次调用都现采，不走缓存。</summary>
    private async Task LoadProcessesAsync()
    {
        if (SuppressWorkerInit)
        {
            SeedDemoProcesses();
            FilterProcesses();
            return;
        }

        IpcOutcome<List<ProcessEntryPayload>> outcome = await WorkerBridge.Instance
            .QueryProcessesAsync().ConfigureAwait(true);

        if (outcome is not { Success: true, Value: not null })
            return;

        _allProcesses.Clear();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProcessEntryPayload process in outcome.Value
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(process.Name) || !seen.Add(process.Name))
                continue;

            _allProcesses.Add(new ScenarioProcessOption(process.Name, process.Pid));
        }

        FilterProcesses();
    }

    private void FilterProcesses()
    {
        string keyword = _editorProcessName.Trim();

        ProcessSuggestions.Clear();

        foreach (ScenarioProcessOption option in _allProcesses
                     .Where(p => keyword.Length == 0
                              || p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                     .Take(40))
        {
            ProcessSuggestions.Add(option);
        }
    }

    private async Task OpenProcessPickerAsync()
    {
        await LoadProcessesAsync().ConfigureAwait(true);
        FilterProcesses();
        IsProcessPickerOpen = ProcessSuggestions.Count > 0;
    }

    private void PickProcess(ScenarioProcessOption option)
    {
        // 先关下拉再改文本：改文本会重新过滤，正被引用的那一项会从集合里消失。
        IsProcessPickerOpen = false;
        EditorProcessName = option.Name;
    }

    private void SeedDemo()
    {
        Rules.Clear();

        Rules.Add(new ScenarioRuleItem(
            "demo-1", ScenarioTrigger.ProcessRunning, "cs2.exe",
            "local-placeholder", "mask-demo", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            10, true, "极限游戏", "游戏调度", "卓越性能"));

        Rules.Add(new ScenarioRuleItem(
            "demo-2", ScenarioTrigger.ProcessExited, "cs2.exe",
            "local-daily", string.Empty, "381b4222-f694-41f0-9685-ff5bb260df2e",
            20, true, "日常办公", string.Empty, "节能"));

        Rules.Add(new ScenarioRuleItem(
            "demo-3", ScenarioTrigger.OnBattery, string.Empty,
            string.Empty, string.Empty, "381b4222-f694-41f0-9685-ff5bb260df2e",
            30, false, string.Empty, string.Empty, "节能"));

        Rules.Add(new ScenarioRuleItem(
            "demo-4", ScenarioTrigger.OnAcPower, string.Empty,
            "local-placeholder", "mask-demo", string.Empty,
            40, true, "极限游戏", "游戏专属核", string.Empty));

        EditorProfiles.Clear();
        EditorProfiles.Add(new ScenarioActionOption(string.Empty, KeepActionLabel));
        EditorProfiles.Add(new ScenarioActionOption("local-placeholder", "极限游戏"));
        EditorProfiles.Add(new ScenarioActionOption("local-daily", "日常办公"));

        EditorMasks.Clear();
        EditorMasks.Add(new ScenarioActionOption(string.Empty, KeepActionLabel));
        EditorMasks.Add(new ScenarioActionOption("mask-demo", "游戏调度"));
        EditorMasks.Add(new ScenarioActionOption("mask-office", "日常调度"));

        EditorPowerSchemes.Clear();
        EditorPowerSchemes.Add(new ScenarioActionOption(string.Empty, KeepActionLabel));
        EditorPowerSchemes.Add(new ScenarioActionOption(
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "卓越性能"));
        EditorPowerSchemes.Add(new ScenarioActionOption(
            "381b4222-f694-41f0-9685-ff5bb260df2e", "节能"));

        SeedDemoProcesses();
        FilterProcesses();

        OnPropertyChanged(nameof(HasRules));

        if (StartWithEditorOpen)
        {
            OpenEditor(null);

            if (StartWithExitTrigger)
                SelectedState = TriggerStates[1];
        }

        if (StartWithProcessPickerOpen && IsEditorOpen)
            IsProcessPickerOpen = true;
    }

    private void SeedDemoProcesses()
    {
        _allProcesses.Clear();
        _allProcesses.Add(new ScenarioProcessOption("cs2.exe", 1042));
        _allProcesses.Add(new ScenarioProcessOption("obs64.exe", 3890));
        _allProcesses.Add(new ScenarioProcessOption("chrome.exe", 2050));
        _allProcesses.Add(new ScenarioProcessOption("valorant.exe", 8876));
    }

    /// <summary>按后台返回的规则重建列表，顺序与 Priority 一致。</summary>
    private void ApplyRules(List<ScenarioRulePayload> rules)
    {
        Rules.Clear();

        foreach (ScenarioRulePayload rule in rules
                     .OrderBy(r => r.Priority)
                     .ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            Rules.Add(new ScenarioRuleItem(
                rule.Id,
                rule.Trigger,
                rule.Argument,
                rule.ProfileId,
                rule.MaskId,
                rule.PowerSchemeId,
                rule.Priority,
                rule.Enabled,
                ProfileNameOf(rule.ProfileId),
                MaskNameOf(rule.MaskId),
                PowerNameOf(rule.PowerSchemeId)));
        }

        OnPropertyChanged(nameof(HasRules));
    }

    private string ProfileNameOf(string profileId)
    {
        // 三个动作统一以候选集合为准：它们由 RefreshActionOptionsAsync 直接从
        // 后台拉取，是场景页自己的权威列表。过去调校方案查 PlanHub.Tuning.Items
        // （只在调校页刷新）、掩码与电源方案另存一份字典，三套状态各走各的，
        // 于是三大页改名后规则卡停在旧名，诊断模式下字典空还会显示「已删除」。
        if (string.IsNullOrEmpty(profileId))
            return string.Empty;

        return NameOf(EditorProfiles, profileId, "（调校方案已删除）");
    }

    private static string NameOf(
        ObservableCollection<ScenarioActionOption> options,
        string id,
        string missing)
        => options.FirstOrDefault(
               o => string.Equals(o.Id, id, StringComparison.Ordinal))?.Name ?? missing;

    private string MaskNameOf(string maskId)
        => string.IsNullOrEmpty(maskId) ? string.Empty : NameOf(EditorMasks, maskId, "（调度方案已删除）");

    private string PowerNameOf(string powerSchemeId)
        => string.IsNullOrEmpty(powerSchemeId)
            ? string.Empty
            : NameOf(EditorPowerSchemes, powerSchemeId, "（电源方案已删除）");

    /// <summary>打开表单。<paramref name="existing"/> 为空 = 新建。</summary>
    private void OpenEditor(ScenarioRuleItem? existing)
    {
        _editingId = existing?.Id;

        // 先放大类再放状态：SelectedKind 的 setter 会把状态重置成新列表的第一项。
        bool isPowerTrigger = existing is not null
                           && (existing.Trigger == ScenarioTrigger.OnBattery || existing.Trigger == ScenarioTrigger.OnAcPower);

        _selectedKind = isPowerTrigger ? PowerKind : ProcessKind;
        OnPropertyChanged(nameof(SelectedKind));
        OnPropertyChanged(nameof(TriggerStates));
        OnPropertyChanged(nameof(IsProcessKind));

        ScenarioTrigger trigger = existing?.Trigger ?? ScenarioTrigger.ProcessRunning;
        _selectedState = TriggerStates.FirstOrDefault(s => s.Trigger == trigger) ?? TriggerStates[0];
        OnPropertyChanged(nameof(SelectedState));
        OnPropertyChanged(nameof(EditorTrigger));

        EditorProcessName = existing?.Argument ?? string.Empty;

        EditorProfileId = existing?.ProfileId ?? string.Empty;
        EditorMaskId = existing?.MaskId ?? string.Empty;
        EditorPowerSchemeId = existing?.PowerSchemeId ?? string.Empty;

        if (existing is null)
        {
            // 新建排在列表末尾：优先级比现有所有规则都大，不会莫名抢走仲裁。
            _editingPriority = Rules.Count == 0 ? PriorityStep : Rules.Max(r => r.Priority) + PriorityStep;
            _editingEnabled = true;
        }
        else
        {
            _editingPriority = existing.Priority;
            _editingEnabled = existing.Enabled;
        }

        IsProcessPickerOpen = false;

        OnPropertyChanged(nameof(EditorTitle));
        IsEditorOpen = true;

        // 进程表每次打开都重拉：上一次打开可能是十几分钟前，用户要选的程序
        // 很可能就是这期间刚启动的。
        if (IsProcessKind)
            Fire(LoadProcessesAsync);
    }

    private void SaveEditor()
    {
        string processName = string.Empty;

        if (IsProcessKind)
        {
            processName = NormalizeTriggerProcess(EditorProcessName);

            if (processName.Length == 0)
            {
                string reason = string.IsNullOrWhiteSpace(EditorProcessName)
                    ? "填写要监听的进程名"
                    : "进程名应形如 cs2.exe";

                SetProcessNameError(reason);
                Toast(reason);
                return;
            }
        }

        SetProcessNameError(string.Empty);

        // 三个动作全空的规则命中后什么也不会发生，后台同样会拒。
        if (EditorProfileId.Length == 0 && EditorMaskId.Length == 0 && EditorPowerSchemeId.Length == 0)
        {
            Toast("至少要选一个执行动作");
            return;
        }

        ScenarioRulePayload payload = new()
        {
            Id = _editingId ?? string.Empty,
            Trigger = EditorTrigger,
            Argument = processName,
            ProfileId = EditorProfileId,
            MaskId = EditorMaskId,
            PowerSchemeId = EditorPowerSchemeId,
            Priority = _editingPriority,
            Enabled = _editingEnabled
        };

        if (SuppressWorkerInit)
        {
            ApplyLocalSave(payload);
            IsEditorOpen = false;
            return;
        }

        Fire(async () =>
        {
            IpcOutcome<string> saved = await WorkerBridge.Instance
                .SaveScenarioRuleAsync(payload).ConfigureAwait(true);

            if (!saved.Success)
            {
                Toast(saved.Message);
                return;
            }

            // 提前收表单：表单关掉后用户看到列表里多了一条，就是最直接的「成功了」。
            IsEditorOpen = false;

            IpcOutcome<List<ScenarioRulePayload>> rules = await WorkerBridge.Instance
                .QueryScenarioRulesAsync().ConfigureAwait(true);

            if (rules is { Success: true, Value: not null })
                ApplyRules(rules.Value);
        });
    }

    /// <summary>诊断模式下的本地保存：只改界面这一份，形状与后台一致。</summary>
    private void ApplyLocalSave(ScenarioRulePayload payload)
    {
        string id = string.IsNullOrEmpty(payload.Id)
            ? "local-" + Guid.NewGuid().ToString("N")[..8]
            : payload.Id;

        ScenarioRuleItem item = new(
            id,
            payload.Trigger,
            payload.Argument,
            payload.ProfileId,
            payload.MaskId,
            payload.PowerSchemeId,
            payload.Priority,
            payload.Enabled,
            ProfileNameOf(payload.ProfileId),
            MaskNameOf(payload.MaskId),
            PowerNameOf(payload.PowerSchemeId));

        int index = Rules.ToList().FindIndex(r => r.Id == id);

        if (index >= 0)
            Rules[index] = item;
        else
            Rules.Add(item);

        OnPropertyChanged(nameof(HasRules));
    }

    private void Delete(ScenarioRuleItem item)
    {
        int index = Rules.IndexOf(item);
        Rules.Remove(item);
        OnPropertyChanged(nameof(HasRules));

        if (SuppressWorkerInit)
            return;

        Fire(async () =>
        {
            IpcOutcome<string> removed = await WorkerBridge.Instance
                .DeleteScenarioRuleAsync(item.Id).ConfigureAwait(true);

            if (removed.Success)
                return;

            // 后台那条还在，插回原位，别让界面长期少一条规则。
            Rules.Insert(Math.Clamp(index, 0, Rules.Count), item);
            OnPropertyChanged(nameof(HasRules));
            Toast(removed.Message);
        });
    }

    private void Toggle(ScenarioRuleItem item)
    {
        item.Enabled = !item.Enabled;

        if (SuppressWorkerInit)
            return;

        ScenarioRulePayload payload = item.ToPayload();

        Fire(async () =>
        {
            IpcOutcome<string> saved = await WorkerBridge.Instance
                .SaveScenarioRuleAsync(payload).ConfigureAwait(true);

            if (saved.Success)
                return;

            item.Enabled = !item.Enabled;
            Toast(saved.Message);
        });
    }

    /// <summary>按 Priority 整段重新编号后写回，否则后台下次刷新会把顺序打回去。</summary>
    public void MoveRuleTo(ScenarioRuleItem item, int targetIndex)
    {
        int index = Rules.IndexOf(item);

        if (index < 0 || targetIndex < 0 || targetIndex >= Rules.Count || index == targetIndex)
            return;

        Rules.Move(index, targetIndex);

        List<ScenarioRulePayload> changed = new();

        for (int i = 0; i < Rules.Count; i++)
        {
            ScenarioRuleItem row = Rules[i];
            int next = (i + 1) * PriorityStep;

            if (row.Priority == next)
                continue;

            row.Priority = next;
            changed.Add(row.ToPayload());
        }

        if (SuppressWorkerInit || changed.Count == 0)
            return;

        Fire(async () =>
        {
            foreach (ScenarioRulePayload payload in changed)
            {
                IpcOutcome<string> saved = await WorkerBridge.Instance
                    .SaveScenarioRuleAsync(payload).ConfigureAwait(true);

                if (saved.Success)
                    continue;

                Toast(saved.Message);
                break;
            }

            // 中途失败会留下半套顺序，整表重拉一次以后台为准。
            IpcOutcome<List<ScenarioRulePayload>> rules = await WorkerBridge.Instance
                .QueryScenarioRulesAsync().ConfigureAwait(true);

            if (rules is { Success: true, Value: not null })
                ApplyRules(rules.Value);
        });
    }

    private static void Toast(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            ToastService.Instance.Show(message);
    }

    private static void Fire(Func<Task> action) => AsyncRunner.Fire(action);
}

/// <summary>触发源的大类（第一级下拉）。</summary>
public sealed record ScenarioKindOption(ScenarioTrigger Representative, string Label);

/// <summary>大类的具体状态（第二级下拉）。<see cref="Trigger"/> 才是真正下发的值。</summary>
public sealed record ScenarioStateOption(ScenarioTrigger Trigger, string Label);

/// <summary>下拉里的一项动作目标；Id 为空串表示「不修改」。</summary>
public sealed record ScenarioActionOption(string Id, string Name);

/// <summary>进程搜索下拉的一行。</summary>
public sealed record ScenarioProcessOption(string Name, int Pid);

/// <summary>一条场景规则在界面上的形态。</summary>
public sealed class ScenarioRuleItem : ViewModelBase
{
    public ScenarioRuleItem(
        string id,
        ScenarioTrigger trigger,
        string argument,
        string profileId,
        string maskId,
        string powerSchemeId,
        int priority,
        bool enabled,
        string profileName,
        string maskName,
        string powerSchemeName)
    {
        Id = id;
        Trigger = trigger;
        Argument = argument;
        ProfileId = profileId;
        MaskId = maskId;
        PowerSchemeId = powerSchemeId;
        Priority = priority;
        Enabled = enabled;
        ProfileName = profileName;
        MaskName = maskName;
        PowerSchemeName = powerSchemeName;
    }

    public string Id { get; }

    public ScenarioTrigger Trigger { get; }

    public string Argument { get; }

    public string ProfileId { get; }

    public string MaskId { get; }

    public string PowerSchemeId { get; }

    public int Priority { get; set; }

    // 三个显示名要能按 Id 重解析：三大页改名 / 新建后，候选刷新得让已列出的
    // 规则卡跟着变，而不是只剩「（方案已删除）」或停在旧名字上。
    private string _profileName = string.Empty;
    private string _maskName = string.Empty;
    private string _powerSchemeName = string.Empty;

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (SetProperty(ref _profileName, value))
                OnPropertyChanged(nameof(ProfileText));
        }
    }

    public string MaskName
    {
        get => _maskName;
        set
        {
            if (SetProperty(ref _maskName, value))
                OnPropertyChanged(nameof(MaskText));
        }
    }

    public string PowerSchemeName
    {
        get => _powerSchemeName;
        set
        {
            if (SetProperty(ref _powerSchemeName, value))
                OnPropertyChanged(nameof(PowerSchemeText));
        }
    }

    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
                OnPropertyChanged(nameof(Dimmed));
        }
    }

    /// <summary>停用的规则整行变淡，但内容不变 —— 用户还要能看清它配了什么。</summary>
    public bool Dimmed => !Enabled;

    /// <summary>IF 块的标题：触发源类型。</summary>
    public string TriggerLabel => Trigger switch
    {
        ScenarioTrigger.ProcessRunning => "IF 进程启动",
        ScenarioTrigger.ProcessExited => "IF 进程退出",
        ScenarioTrigger.OnBattery => "IF 使用电池",
        _ => "IF 接入电源"
    };

    /// <summary>IF 块的值：真正被监听的东西，不与标题重复。</summary>
    public string TriggerValue => Trigger switch
    {
        ScenarioTrigger.ProcessRunning => Argument,
        ScenarioTrigger.ProcessExited => Argument,
        ScenarioTrigger.OnBattery => "拔掉电源适配器",
        _ => "接入电源适配器"
    };

    public bool HasProfile => !string.IsNullOrEmpty(ProfileId);

    public bool HasMask => !string.IsNullOrEmpty(MaskId);

    public bool HasPowerScheme => !string.IsNullOrEmpty(PowerSchemeId);

    public string ProfileText => HasProfile ? ProfileName : "保持原样";

    public string MaskText => HasMask ? MaskName : "保持原样";

    public string PowerSchemeText => HasPowerScheme ? PowerSchemeName : "保持原样";

    public string ToggleLabel => Enabled ? "停用这条规则" : "启用这条规则";

    public ScenarioRulePayload ToPayload() => new()
    {
        Id = Id,
        Trigger = Trigger,
        Argument = Argument,
        ProfileId = ProfileId,
        MaskId = MaskId,
        PowerSchemeId = PowerSchemeId,
        Priority = Priority,
        Enabled = Enabled
    };
}
