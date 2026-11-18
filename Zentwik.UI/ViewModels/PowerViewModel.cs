using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>电源页视图模型：系统电源方案 + 处理器 / PCIe 策略的 AC / DC 两套值</summary>
public partial class PowerViewModel : ObservableObject
{
    private const int QueryTimeoutMs = 6_000;

    // Worker 写完要重新激活一次电源方案，低端机上这一步可能要几秒。
    private const int ApplyTimeoutMs = 30_000;

    /// <summary>诊断用：--screenshot 时不发 IPC</summary>
    public static bool SuppressWorkerInit { get; set; }

    /// <summary>一套策略值 + 每项自己的可读状态。</summary>
    private sealed class PowerSet
    {
        public int Epp;
        public bool EppSupported = true;
        public string EppError = string.Empty;

        public int BoostMode;
        public bool BoostModeSupported = true;
        public string BoostModeError = string.Empty;

        public int MaxProcessorState;
        public bool MaxProcessorStateSupported = true;
        public string MaxProcessorStateError = string.Empty;

        public int MinProcessorState;
        public bool MinProcessorStateSupported = true;
        public string MinProcessorStateError = string.Empty;

        public int CoolingPolicy;
        public bool CoolingPolicySupported = true;
        public string CoolingPolicyError = string.Empty;

        public int CoreParkingMin;
        public bool CoreParkingMinSupported = true;
        public string CoreParkingMinError = string.Empty;

        public bool IdleDisable;
        public bool IdleDisableSupported = true;
        public string IdleDisableError = string.Empty;

        public int Aspm;
        public bool AspmSupported = true;
        public string AspmError = string.Empty;

        public static PowerSet CreateDefault(bool ac) => new()
        {
            Epp = ac ? 50 : 80,
            BoostMode = 1,
            MaxProcessorState = ac ? 100 : 80,
            MinProcessorState = 5,
            CoolingPolicy = ac ? 1 : 0,
            CoreParkingMin = 100,
            IdleDisable = false,
            Aspm = ac ? 1 : 2,
        };

        /// <summary>连值带「支持 / 错误」一起拷。回读时两者都要换掉。</summary>
        public void CopyFrom(PowerSet other)
        {
            Epp = other.Epp;
            EppSupported = other.EppSupported;
            EppError = other.EppError;

            BoostMode = other.BoostMode;
            BoostModeSupported = other.BoostModeSupported;
            BoostModeError = other.BoostModeError;

            MaxProcessorState = other.MaxProcessorState;
            MaxProcessorStateSupported = other.MaxProcessorStateSupported;
            MaxProcessorStateError = other.MaxProcessorStateError;

            MinProcessorState = other.MinProcessorState;
            MinProcessorStateSupported = other.MinProcessorStateSupported;
            MinProcessorStateError = other.MinProcessorStateError;

            CoolingPolicy = other.CoolingPolicy;
            CoolingPolicySupported = other.CoolingPolicySupported;
            CoolingPolicyError = other.CoolingPolicyError;

            CoreParkingMin = other.CoreParkingMin;
            CoreParkingMinSupported = other.CoreParkingMinSupported;
            CoreParkingMinError = other.CoreParkingMinError;

            IdleDisable = other.IdleDisable;
            IdleDisableSupported = other.IdleDisableSupported;
            IdleDisableError = other.IdleDisableError;

            Aspm = other.Aspm;
            AspmSupported = other.AspmSupported;
            AspmError = other.AspmError;
        }

        /// <summary>只比五项值。Supported / Error 是环境事实，不参与「有没有改」。</summary>
        public static bool Same(PowerSet a, PowerSet b) =>
            a.Epp == b.Epp
            && a.BoostMode == b.BoostMode
            && a.MaxProcessorState == b.MaxProcessorState
            && a.MinProcessorState == b.MinProcessorState
            && a.CoolingPolicy == b.CoolingPolicy
            && a.CoreParkingMin == b.CoreParkingMin
            && a.IdleDisable == b.IdleDisable
            && a.Aspm == b.Aspm;

        public void ApplyDefaults(PowerPolicyDefaults defaults)
        {
            Epp = defaults.Epp;
            BoostMode = defaults.BoostMode;
            MaxProcessorState = defaults.MaxProcessorState;
            MinProcessorState = defaults.MinProcessorState;
            CoolingPolicy = defaults.CoolingPolicy;
            CoreParkingMin = defaults.CoreParkingMin;
            IdleDisable = defaults.IdleDisable;
            Aspm = defaults.Aspm;
        }
    }

    private readonly PowerSet _ac = PowerSet.CreateDefault(ac: true);
    private readonly PowerSet _dc = PowerSet.CreateDefault(ac: false);

    private readonly PowerSet _appliedAc = PowerSet.CreateDefault(ac: true);
    private readonly PowerSet _appliedDc = PowerSet.CreateDefault(ac: false);

    // 上一次从系统读回来的值。页面会被方案值覆盖，靠它才知道机器真值是什么。
    private readonly PowerSet _systemAc = PowerSet.CreateDefault(ac: true);
    private readonly PowerSet _systemDc = PowerSet.CreateDefault(ac: false);

    private bool _hasSystemSnapshot;

    private PowerSet Current => IsAcMode ? _ac : _dc;

    private bool _suppressDirty;

    /// <summary>程序性改动选中项时为 true，避免列表刷新触发一次方案切换。</summary>
    private bool _suppressSchemeSwitch;

    /// <summary>刷新方案面板期间为 true，期间不按方案快照覆盖页面值</summary>
    private bool _suppressPlanLoad;

    private Task? _refreshTask;

    private bool _initialized;

    // 系统电源方案

    public ObservableCollection<PowerScheme> Schemes { get; } = new();

    [ObservableProperty]
    private PowerScheme? _selectedScheme;

    partial void OnSelectedSchemeChanged(PowerScheme? value)
    {
        if (_suppressSchemeSwitch || value is null)
        {
            return;
        }

        // 点「＋ 新建方案」不是切换方案。选中项立刻退回当前活动方案，
        // 否则那一格会一直带着强调色，看起来像「已选中的是新建」。
        if (value.IsCreateNew)
        {
            SyncSelectedScheme();
            OpenCreateMenu();
            return;
        }

        if (value.Guid == ActiveSchemeGuid)
        {
            return;
        }

        // 切换会重读系统值，页面上还没应用的改动会被一并冲掉 —— 与面板换方案同一套口径：
        // 先确认再切，别让用户以为改动跟着过去了。
        if (IsDirty)
        {
            PowerScheme target = value;
            SyncSelectedScheme();

            List<string> consequences = new()
            {
                $"切换到「{target.Name}」将丢弃页面上的改动",
            };

            consequences.AddRange(DescribePendingChanges());
            consequences.Add("如需保留，请先保存并应用");

            ConfirmService.Instance.Request(
                "参数尚未应用",
                consequences,
                () => SwitchConfirmed(target),
                "丢弃并切换");

            return;
        }

        _ = SwitchSchemeAsync(value);
    }

    /// <summary>页面上改过、还没下发的项。切系统计划会被整组覆盖，弹窗里要逐条列出来。</summary>
    private IReadOnlyList<string> DescribePendingChanges()
    {
        const int shown = 4;

        List<string> items = new();

        CollectPending(items, "交流电", _ac, _appliedAc);
        CollectPending(items, "电池", _dc, _appliedDc);

        if (items.Count <= shown)
            return items;

        List<string> trimmed = items.GetRange(0, shown);
        trimmed.Add($"等 {items.Count} 项改动");
        return trimmed;
    }

    private static void CollectPending(List<string> items, string segment, PowerSet now, PowerSet applied)
    {
        AddPending(items, segment, "EPP", now.Epp, applied.Epp);
        AddPending(items, segment, "性能提升模式", now.BoostMode, applied.BoostMode);
        AddPending(items, segment, "最大处理器状态", now.MaxProcessorState, applied.MaxProcessorState);
        AddPending(items, segment, "最小处理器状态", now.MinProcessorState, applied.MinProcessorState);
        AddPending(items, segment, "系统散热方式", now.CoolingPolicy, applied.CoolingPolicy);
        AddPending(items, segment, "核心放置数量", now.CoreParkingMin, applied.CoreParkingMin);
        AddPending(items, segment, "链接电源管理", now.Aspm, applied.Aspm);
        AddPending(items, segment, "闲置禁用", now.IdleDisable, applied.IdleDisable);
    }

    private static void AddPending(List<string> items, string segment, string label, int now, int applied)
    {
        if (now != applied)
            items.Add($"{segment} · {label} {applied} → {now}");
    }

    private static void AddPending(List<string> items, string segment, string label, bool now, bool applied)
    {
        if (now != applied)
            items.Add($"{segment} · {label} {(applied ? "开" : "关")} → {(now ? "开" : "关")}");
    }

    /// <summary>确认丢弃改动之后真正切过去。先抑制一次选中回调，避免再弹一遍确认。</summary>
    private void SwitchConfirmed(PowerScheme scheme)
    {
        _suppressSchemeSwitch = true;

        try
        {
            SelectedScheme = scheme;
        }
        finally
        {
            _suppressSchemeSwitch = false;
        }

        _ = SwitchSchemeAsync(scheme);
    }

    [ObservableProperty]
    private Guid _activeSchemeGuid;

    [ObservableProperty]
    private string _activeSchemeName = PowerPolicySnapshot.UnknownSchemeName;

    // 供电状态

    /// <summary>本机有没有电池。false 时界面上不出现电池分段。</summary>
    [ObservableProperty]
    private bool _hasBattery;

    /// <summary>此刻是否在用电池。由快照的 <c>onAcPower</c> 推出。</summary>
    [ObservableProperty]
    private bool _isOnBattery;

    public string SupplyLabel => IsOnBattery ? "电池供电" : "交流电";

    partial void OnIsOnBatteryChanged(bool value) => OnPropertyChanged(nameof(SupplyLabel));

    // 分段

    [ObservableProperty]
    private bool _isAcMode = true;

    partial void OnIsAcModeChanged(bool value)
    {
        NotifyAllValues();
        OnPropertyChanged(nameof(IsBatteryMode));
        OnPropertyChanged(nameof(ModeTitle));
        OnPropertyChanged(nameof(MaxProcessorStateMinimum));
    }

    /// <summary>分段控件用：与 <see cref="IsAcMode"/> 互斥。</summary>
    public bool IsBatteryMode
    {
        get => !IsAcMode;
        set
        {
            if (value)
            {
                IsAcMode = false;
            }
        }
    }

    public string ModeTitle => IsAcMode ? "交流电策略" : "电池策略";

    /// <summary>最大处理器状态的滑块下限</summary>
    public int MaxProcessorStateMinimum => 50;

    // 策略项

    public int Epp
    {
        get => Current.Epp;
        set => SetValue(current => current.Epp, (current, v) => current.Epp = v, value, nameof(Epp));
    }

    public bool EppSupported => Current.EppSupported;

    public string EppError => Current.EppError;

    public int BoostMode
    {
        get => Current.BoostMode;
        set => SetValue(current => current.BoostMode, (current, v) => current.BoostMode = v, value, nameof(BoostMode));
    }

    public bool BoostModeSupported => Current.BoostModeSupported;

    public string BoostModeError => Current.BoostModeError;

    public int MaxProcessorState
    {
        get => Current.MaxProcessorState;
        set => SetValue(
            current => current.MaxProcessorState,
            (current, v) => current.MaxProcessorState = v,
            value,
            nameof(MaxProcessorState));
    }

    public bool MaxProcessorStateSupported => Current.MaxProcessorStateSupported;

    public string MaxProcessorStateError => Current.MaxProcessorStateError;

    public int MinProcessorState
    {
        get => Current.MinProcessorState;
        set => SetValue(
            current => current.MinProcessorState,
            (current, v) => current.MinProcessorState = v,
            value,
            nameof(MinProcessorState));
    }

    public bool MinProcessorStateSupported => Current.MinProcessorStateSupported;

    public string MinProcessorStateError => Current.MinProcessorStateError;

    public int CoolingPolicy
    {
        get => Current.CoolingPolicy;
        set => SetValue(
            current => current.CoolingPolicy,
            (current, v) => current.CoolingPolicy = v,
            value,
            nameof(CoolingPolicy));
    }

    public bool CoolingPolicySupported => Current.CoolingPolicySupported;

    public string CoolingPolicyError => Current.CoolingPolicyError;

    public int CoreParkingMin
    {
        get => Current.CoreParkingMin;
        set
        {
            int before = Current.CoreParkingMin;

            SetValue(
                current => current.CoreParkingMin,
                (current, v) => current.CoreParkingMin = v,
                value,
                nameof(CoreParkingMin));

            if (before != Current.CoreParkingMin)
            {
                OnPropertyChanged(nameof(CoreParkingWarns));
            }
        }
    }

    public bool CoreParkingMinSupported => Current.CoreParkingMinSupported;

    public string CoreParkingMinError => Current.CoreParkingMinError;

    /// <summary>停泊开启时被停泊的核不算可用，CPU Set 软约束会让线程跑出集合。</summary>
    public bool CoreParkingWarns => CoreParkingMinSupported && CoreParkingMin < 100;

    public bool IdleDisable
    {
        get => Current.IdleDisable;
        set => SetValue(
            current => current.IdleDisable,
            (current, v) => current.IdleDisable = v,
            value,
            nameof(IdleDisable));
    }

    public bool IdleDisableSupported => Current.IdleDisableSupported;

    public string IdleDisableError => Current.IdleDisableError;

    public int Aspm
    {
        get => Current.Aspm;
        set => SetValue(current => current.Aspm, (current, v) => current.Aspm = v, value, nameof(Aspm));
    }

    public bool AspmSupported => Current.AspmSupported;

    public string AspmError => Current.AspmError;

    // 状态

    [ObservableProperty]
    private bool _isDirty;

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();

        // 电源页的改动同样是「改了没下发」，方案面板据此贴未保存并拦住切换。
        // 方案条只看「与方案是否一致」；IsDirty 与方案无关，混进来会残留「未保存」。
        SyncPlanDirty();
    }

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveToPlan));
        PlanHub.Power.RefreshApplyState();
        SaveToCurrentPlanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>后台服务是否可写。false 时「保存并应用」禁用。</summary>
    [ObservableProperty]
    private bool _isWorkerAvailable = true;

    partial void OnIsWorkerAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveToPlan));
        PlanHub.Power.RefreshApplyState();
        SaveToCurrentPlanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>每行右侧「本机不支持」那行字</summary>
    [ObservableProperty]
    private string _unavailableText = "本机不支持";

    /// <summary>
    /// 「保存并应用」是否可点。没有方案时不看改动：此时点下去是把当前参数存成一份新方案，
    /// 系统本来就是这些值，语义成立。
    /// </summary>
    /// <summary>
    /// 「保存并应用」是否可点。没有方案时不看改动：此时点下去是把当前参数存成一份新方案。
    /// 「与系统不一致」时也要能点 —— 那一按是用机器当前值更新方案，不去动机器。
    /// </summary>
    public bool CanApply =>
        (IsDirty || DiffersFromSystem || (PlanHub.Power.Current is null && !PlanHub.SuppressWorkerInit))
        && !IsBusy
        && IsWorkerAvailable;

    /// <summary>方案条那条标记；顶栏按钮的可点状态与它同源，不各算一套。</summary>
    private bool DiffersFromSystem => PlanHub.Power.DiffersFromSystem;

    public bool CanOperate => !IsBusy;

    /// <summary>顶栏按钮的文案与可点状态都取自这一类方案的会话。</summary>
    public PlanScope Plans => PlanHub.Power;

    // 初始化

    public PowerViewModel()
    {
        foreach (PowerSchemePreset preset in PowerSchemePresets.All)
        {
            Presets.Add(new PresetOption(preset));
        }

        // 电源方案不能空白新建：记录里存的是「参数写到哪份 Windows 方案上」。
        // 顶栏的「新建」因此改走派生流程，与卡片区的模板新建殊途同归。
        PlanHub.Power.CreateHandler = CreatePlanAsync;

        // 面板底部按钮脏值时改成「保存至」：只写 plans.json，不碰系统。
        PlanHub.Power.CanSavePending = () => CanSaveToPlan;
        PlanHub.Power.SavePending = () => _ = SaveToCurrentPlanAsync();

        // 从方案面板应用电源方案，系统计划会被切走 —— 本页必须重读。
        System.Windows.WeakEventManager<PlanScope, EventArgs>.AddHandler(
            PlanHub.Power,
            nameof(PlanScope.Applied),
            OnPlanApplied);

        // 面板里换选一份方案 = 本页换一组值，与调校页、调度页同一套体验。
        // 「选中 ≠ 生效」不变：换的只是显示，真正切系统计划仍要按面板底部的应用。
        System.Windows.WeakEventManager<PlanScope, EventArgs>.AddHandler(
            PlanHub.Power,
            nameof(PlanScope.CurrentChanged),
            OnPlanSelectionChanged);

        if (SuppressWorkerInit)
        {
            // 截图模式按笔记本的形态画（有电池、交流电供电），
            // AC/DC 分段才在图上；真实运行一律以快照为准。
            HasBattery = true;
            SeedPlaceholderSchemes();
        }
    }

    /// <summary>读方案列表与当前策略</summary>
    public Task InitializeAsync()
    {
        if (SuppressWorkerInit || _initialized)
        {
            return Task.CompletedTask;
        }

        return _refreshTask ??= RunInitialLoadAsync();
    }

    private async Task RunInitialLoadAsync()
    {
        try
        {
            await RefreshCoreAsync(notifyOnFailure: false).ConfigureAwait(true);

            // 系统值只是兜底。方案列表若已就绪，界面显示选中方案存的那一套 ——
            // 与调校页一致：进来看到的是「我正在编辑的方案」，不是「机器现在的值」。
            LoadSelectedPlanValues();
        }
        finally
        {
            // 失败也算「已经尝试过」：再自动重试只会在 Worker 还没起来时
            // 反复堵住管道。用户点「回读」可以手动重来。
            _initialized = true;
            _refreshTask = null;
        }
    }

    /// <summary>方案面板应用了一份电源方案：系统计划与五项参数都变了，整页重读</summary>
    private void OnPlanApplied(object? sender, EventArgs e)
    {
        if (SuppressWorkerInit)
            return;

        _refreshTask = null;
        _ = RefreshCoreAsync(notifyOnFailure: false);
    }

    /// <summary>面板换选了一份电源方案：把它的五项参数摆到界面上。</summary>
    private void OnPlanSelectionChanged(object? sender, EventArgs e)
    {
        // 有/无方案直接决定顶栏按钮的文案与可点状态。
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveToPlan));
        PlanHub.Power.RefreshApplyState();
        SaveToCurrentPlanCommand.NotifyCanExecuteChanged();

        if (_suppressPlanLoad)
            return;

        LoadSelectedPlanValues();
    }

    /// <summary>把选中的那份方案存的五项参数搬到界面上（不动系统、不切计划、不标脏）</summary>
    private void LoadSelectedPlanValues()
    {
        if (SuppressWorkerInit)
            return;

        PlanItem? item = PlanHub.Power.Current;

        if (item is null || item.IsLocal || item.PowerPolicy is null)
            return;

        _suppressDirty = true;

        try
        {
            ApplyPolicyFields(item.PowerPolicy);

            // 换过来的是「这份方案的值」，不是用户改出来的草稿，
            // 基线一起搬过去 —— 界面不该一换方案就显示成「有未保存改动」。
            _appliedAc.CopyFrom(_ac);
            _appliedDc.CopyFrom(_dc);
        }
        finally
        {
            _suppressDirty = false;
        }

        NotifyAllValues();
        IsDirty = false;
        SyncPlanDirty();
    }

    /// <summary>按方案快照覆盖两项值；快照里为 null 的项保持现状（增量语义）。</summary>
    private void ApplyPolicyFields(ApplyPowerPolicyPayload policy)
    {
        if (policy.Epp is not null)
            _ac.Epp = policy.Epp.Value;

        if (policy.BoostMode is not null)
            _ac.BoostMode = policy.BoostMode.Value;

        if (policy.MaxProcessorState is not null)
            _ac.MaxProcessorState = policy.MaxProcessorState.Value;

        if (policy.MinProcessorState is not null)
            _ac.MinProcessorState = policy.MinProcessorState.Value;

        if (policy.CoolingPolicy is not null)
            _ac.CoolingPolicy = policy.CoolingPolicy.Value;

        if (policy.CoreParkingMin is not null)
            _ac.CoreParkingMin = policy.CoreParkingMin.Value;

        if (policy.Aspm is not null)
            _ac.Aspm = policy.Aspm.Value;

        if (policy.IdleDisable is not null)
            _ac.IdleDisable = policy.IdleDisable.Value;

        if (!HasBattery)
            return;

        if (policy.DcEpp is not null)
            _dc.Epp = policy.DcEpp.Value;

        if (policy.DcBoostMode is not null)
            _dc.BoostMode = policy.DcBoostMode.Value;

        if (policy.DcMaxProcessorState is not null)
            _dc.MaxProcessorState = policy.DcMaxProcessorState.Value;

        if (policy.DcMinProcessorState is not null)
            _dc.MinProcessorState = policy.DcMinProcessorState.Value;

        if (policy.DcCoolingPolicy is not null)
            _dc.CoolingPolicy = policy.DcCoolingPolicy.Value;

        if (policy.DcCoreParkingMin is not null)
            _dc.CoreParkingMin = policy.DcCoreParkingMin.Value;

        if (policy.DcAspm is not null)
            _dc.Aspm = policy.DcAspm.Value;

        if (policy.DcIdleDisable is not null)
            _dc.IdleDisable = policy.DcIdleDisable.Value;
    }

    /// <summary>首帧是否已经读过一次系统状态。</summary>
    public bool IsInitialized => _initialized;

    /// <summary>切回本页时对齐一次系统状态</summary>
    public Task ReloadOnShownAsync()
    {
        if (SuppressWorkerInit || !_initialized || IsDirty || IsBusy)
            return Task.CompletedTask;

        _refreshTask = null;
        return RefreshCoreAsync(notifyOnFailure: false);
    }

    /// <summary>「回读」：丢弃界面改动，重新读一次系统当前值。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _refreshTask = null;
        await RefreshCoreAsync(notifyOnFailure: true).ConfigureAwait(true);
    }

    private async Task RefreshCoreAsync(bool notifyOnFailure)
    {
        IsBusy = true;

        try
        {
            IpcOutcome<PowerSchemeListPayload> schemes = await WorkerBridge.Instance
                .QueryPowerSchemesAsync(QueryTimeoutMs)
                .ConfigureAwait(true);

            if (schemes.TransportFailed)
            {
                MarkWorkerUnreachable();
                return;
            }

            if (schemes.Success && schemes.Value is not null)
            {
                ApplySchemeList(schemes.Value);
            }

            IpcOutcome<PowerPolicySnapshot> policy = await WorkerBridge.Instance
                .QueryPowerPolicyAsync(QueryTimeoutMs)
                .ConfigureAwait(true);

            if (policy.TransportFailed)
            {
                MarkWorkerUnreachable();
                return;
            }

            IsWorkerAvailable = true;
            UnavailableText = "本机不支持";

            if (policy.Value is not null)
            {
                ApplySnapshot(policy.Value);
            }
            else
            {
                MarkAllUnsupported(policy.Message);
            }

            if (!policy.Success && notifyOnFailure)
            {
                ToastService.Instance.ShowError(policy.Message);
            }
        }
        catch (Exception ex)
        {
            MarkWorkerUnreachable();
            ToastService.Instance.ShowError($"读取电源策略失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 应用

    /// <summary>「保存并应用」：先把这套值写进当前方案，再只发跟上次生效值不同的项</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        // 一份方案都没有时不能闷声建：先把要建的名字摆给用户看。
        if (PlanHub.Power.Current is null && !PlanHub.SuppressWorkerInit)
        {
            string name = PlanHub.Power.NextNewName;

            ConfirmService.Instance.Request(
                "当前没有电源方案",
                new[] { $"是否新建「{name}」并应用当前参数" },
                () => AsyncRunner.Fire(() => ApplyCoreAsync(name)),
                "新建并应用");

            return;
        }

        await ApplyCoreAsync(null).ConfigureAwait(true);
    }

    /// <summary>真正执行：存进方案（必要时新建）再下发</summary>
    private async Task ApplyCoreAsync(string? newPlanName)
    {
        // 没动过任何值、方案却与机器对不上：这一按是把机器当前值更新进方案，不动机器。
        if (!IsDirty && PlanHub.Power.DiffersFromSystem)
        {
            if (await SaveToPlanBeforeApplyAsync(newPlanName, BuildFullSnapshot(_systemAc, _systemDc))
                    .ConfigureAwait(true))
            {
                SyncPlanDirty();
            }

            return;
        }

        ApplyPowerPolicyPayload patch = BuildPatch();

        if (patch.IsEmpty)
        {
            IsDirty = false;

            // 没有改动也能「新建并应用」：系统本来就是这些值，
            // 只需把当前状态存成一份方案，不必再写一遍。
            if (newPlanName is not null
                && await SaveToPlanBeforeApplyAsync(newPlanName).ConfigureAwait(true))
            {
                ToastService.Instance.Show($"已新建「{newPlanName}」");
            }

            return;
        }

        IsBusy = true;

        try
        {
            // 与调校页同一顺序：存不上方案就不下发，免得硬件已改、方案里还是旧值。
            if (!await SaveToPlanBeforeApplyAsync(newPlanName).ConfigureAwait(true))
                return;

            IpcOutcome<PowerPolicySnapshot> outcome = await WorkerBridge.Instance
                .ApplyPowerPolicyAsync(patch, ApplyTimeoutMs)
                .ConfigureAwait(true);

            if (outcome.TransportFailed)
            {
                MarkWorkerUnreachable();
                ToastService.Instance.ShowError("后台服务未连接，电源策略未写入");
                return;
            }

            if (outcome.Value is not null)
            {
                IsWorkerAvailable = true;
                UnavailableText = "本机不支持";
                ApplySnapshot(outcome.Value);
            }

            if (!outcome.Success)
            {
                // 原因里带着哪几项没写成（含 Win32 错误码），原样给用户看。
                ToastService.Instance.ShowError(outcome.Message);
            }
            else if (outcome.Value is null)
            {
                // 清脏依赖 ApplySnapshot。成功却没带回快照时保留脏标记，
                // 否则用户看到「点了没反应」，会反复点、反复下发同一份 patch。
                ToastService.Instance.ShowError("已写入，但未能回读电源策略，请刷新确认");
            }
            else
            {
                ToastService.Instance.Show($"已应用到「{CurrentPlanName()}」");
            }
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"写入电源策略失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>与上次生效值的差异</summary>
    private ApplyPowerPolicyPayload BuildPatch()
    {
        ApplyPowerPolicyPayload payload = new();

        if (_ac.Epp != _appliedAc.Epp) payload.Epp = _ac.Epp;
        if (_ac.BoostMode != _appliedAc.BoostMode) payload.BoostMode = _ac.BoostMode;
        if (_ac.MaxProcessorState != _appliedAc.MaxProcessorState) payload.MaxProcessorState = _ac.MaxProcessorState;
        if (_ac.MinProcessorState != _appliedAc.MinProcessorState) payload.MinProcessorState = _ac.MinProcessorState;
        if (_ac.CoolingPolicy != _appliedAc.CoolingPolicy) payload.CoolingPolicy = _ac.CoolingPolicy;
        if (_ac.CoreParkingMin != _appliedAc.CoreParkingMin) payload.CoreParkingMin = _ac.CoreParkingMin;
        if (_ac.IdleDisable != _appliedAc.IdleDisable) payload.IdleDisable = _ac.IdleDisable;
        if (_ac.Aspm != _appliedAc.Aspm) payload.Aspm = _ac.Aspm;

        if (!HasBattery)
        {
            return payload;
        }

        if (_dc.Epp != _appliedDc.Epp) payload.DcEpp = _dc.Epp;
        if (_dc.BoostMode != _appliedDc.BoostMode) payload.DcBoostMode = _dc.BoostMode;
        if (_dc.MaxProcessorState != _appliedDc.MaxProcessorState) payload.DcMaxProcessorState = _dc.MaxProcessorState;
        if (_dc.MinProcessorState != _appliedDc.MinProcessorState) payload.DcMinProcessorState = _dc.MinProcessorState;
        if (_dc.CoolingPolicy != _appliedDc.CoolingPolicy) payload.DcCoolingPolicy = _dc.CoolingPolicy;
        if (_dc.CoreParkingMin != _appliedDc.CoreParkingMin) payload.DcCoreParkingMin = _dc.CoreParkingMin;
        if (_dc.IdleDisable != _appliedDc.IdleDisable) payload.DcIdleDisable = _dc.IdleDisable;
        if (_dc.Aspm != _appliedDc.Aspm) payload.DcAspm = _dc.Aspm;

        return payload;
    }

    /// <summary>「恢复默认」：把当前分段填成一组折中值</summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        _suppressDirty = true;

        try
        {
            Current.ApplyDefaults(IsAcMode ? PowerPolicyDefaults.Ac : PowerPolicyDefaults.Dc);
        }
        finally
        {
            _suppressDirty = false;
        }

        NotifyAllValues();
        RefreshDirty();
    }

    /// <summary>丢弃界面改动，回到上次生效的值。</summary>
    [RelayCommand]
    private void Discard()
    {
        _ac.CopyFrom(_appliedAc);
        _dc.CopyFrom(_appliedDc);

        NotifyAllValues();
        IsDirty = false;
        SyncPlanDirty();
    }

    // 新建 / 改名 / 删除

    /// <summary>新建弹层里的四个内置模板：节能 / 平衡 / 高性能 / 卓越性能</summary>
    public ObservableCollection<PresetOption> Presets { get; } = new();

    [ObservableProperty]
    private bool _isCreateMenuOpen;

    [RelayCommand]
    private void OpenCreateMenu() => IsCreateMenuOpen = true;

    [RelayCommand]
    private void CloseCreateMenu() => IsCreateMenuOpen = false;

    /// <summary>以某个内置模板派生一份新方案，并立刻切过去</summary>
    [RelayCommand]
    private async Task CreateSchemeAsync(PresetOption? option)
    {
        if (option is null || IsBusy)
        {
            return;
        }

        IsCreateMenuOpen = false;
        IsBusy = true;

        try
        {
            IpcOutcome<PowerSchemeListPayload> outcome = await WorkerBridge.Instance
                .CreatePowerSchemeAsync(option.Preset.Guid, BuildUniqueSchemeName(option.Name), ApplyTimeoutMs)
                .ConfigureAwait(true);

            if (outcome.TransportFailed)
            {
                MarkWorkerUnreachable();
                ToastService.Instance.ShowError("后台服务未连接，无法新建电源方案");
                return;
            }

            if (!outcome.Success || outcome.Value is null)
            {
                ToastService.Instance.ShowError(outcome.Message);
                return;
            }

            ApplySchemeList(outcome.Value);

            // 新建出来的方案是空模板派生的一份，五项值跟原方案一样；
            // 但活动方案已经变了，界面上显示的值必须重读，不能沿用旧快照。
            _refreshTask = null;
            await RefreshCoreAsync(notifyOnFailure: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"新建电源方案失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>顶栏方案面板的「新建」：把当前五项参数存成一份方案，不动系统电源计划列表</summary>
    private async Task<string?> CreatePlanAsync(string requestedName)
    {
        if (IsBusy)
            return null;

        IsBusy = true;

        try
        {
            // 记下当前活动方案，纯粹为了让「应用」能连系统计划一起切回去。
            // 拿不到就留空：那时照样能把参数写到当前活动方案上。
            string? schemeGuid = ActiveSchemeGuid != Guid.Empty
                ? ActiveSchemeGuid.ToString("D")
                : null;

            IpcOutcome<string> saved = await WorkerBridge.Instance
                .SavePlanAsync(
                    PlanKind.Power,
                    null,
                    requestedName,
                    powerSchemeGuid: schemeGuid,
                    powerPolicy: BuildFullSnapshot(),
                    timeoutMs: ApplyTimeoutMs)
                .ConfigureAwait(true);

            if (!saved.Success || string.IsNullOrEmpty(saved.Value))
            {
                ToastService.Instance.ShowError(
                    string.IsNullOrEmpty(saved.Message) ? "新建电源方案失败" : saved.Message);
                return null;
            }

            // 对齐「生效中」：新建的就是当前这套参数，与系统现状一致。
            // 失败不另行提示，方案本身已经建好。
            await WorkerBridge.Instance
                .ApplyPlanAsync(PlanKind.Power, saved.Value, ApplyTimeoutMs)
                .ConfigureAwait(true);

            return saved.Value;
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"新建电源方案失败：{ex.Message}");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>存进方案的是完整一套值，不像 BuildPatch 只挑改动项：方案要能独立还原。</summary>
    private ApplyPowerPolicyPayload BuildFullSnapshot() => BuildFullSnapshot(_ac, _dc);

    /// <summary>按给定的一套值拼完整快照；「用机器值更新方案」时传系统那份。</summary>
    private ApplyPowerPolicyPayload BuildFullSnapshot(PowerSet ac, PowerSet dc)
    {
        ApplyPowerPolicyPayload payload = new();

        if (ac.EppSupported) payload.Epp = ac.Epp;
        if (ac.BoostModeSupported) payload.BoostMode = ac.BoostMode;
        if (ac.MaxProcessorStateSupported) payload.MaxProcessorState = ac.MaxProcessorState;
        if (ac.MinProcessorStateSupported) payload.MinProcessorState = ac.MinProcessorState;
        if (ac.CoolingPolicySupported) payload.CoolingPolicy = ac.CoolingPolicy;
        if (ac.CoreParkingMinSupported) payload.CoreParkingMin = ac.CoreParkingMin;
        if (ac.IdleDisableSupported) payload.IdleDisable = ac.IdleDisable;
        if (ac.AspmSupported) payload.Aspm = ac.Aspm;

        if (!HasBattery)
            return payload;

        if (dc.EppSupported) payload.DcEpp = dc.Epp;
        if (dc.BoostModeSupported) payload.DcBoostMode = dc.BoostMode;
        if (dc.MaxProcessorStateSupported) payload.DcMaxProcessorState = dc.MaxProcessorState;
        if (dc.MinProcessorStateSupported) payload.DcMinProcessorState = dc.MinProcessorState;
        if (dc.CoolingPolicySupported) payload.DcCoolingPolicy = dc.CoolingPolicy;
        if (dc.CoreParkingMinSupported) payload.DcCoreParkingMin = dc.CoreParkingMin;
        if (dc.IdleDisableSupported) payload.DcIdleDisable = dc.IdleDisable;
        if (dc.AspmSupported) payload.DcAspm = dc.Aspm;

        return payload;
    }

    /// <summary>下发前先把这套值写进当前方案；返回 false 时调用方不得继续下发</summary>
    private async Task<bool> SaveToPlanBeforeApplyAsync(
        string? newPlanName = null,
        ApplyPowerPolicyPayload? snapshot = null)
    {
        if (PlanHub.SuppressWorkerInit)
            return true;

        PlanItem? current = PlanHub.Power.Current;
        ApplyPowerPolicyPayload values = snapshot ?? BuildFullSnapshot();
        bool hasScheme = ActiveSchemeGuid != Guid.Empty;

        // 绑定只在新建时定。已有方案保存的是值，不该因为当前活动计划换了就改绑 ——
        // 那会让两份方案抢同一份 Windows 计划，「谁在生效」再也说不清。
        bool creating = current is null;
        string? schemeGuid = creating && hasScheme ? ActiveSchemeGuid.ToString("D") : null;

        // 没有选中方案时由 SaveAsync 新建一份：与调校页「保存并应用」同一条路径。
        // 新建的这份照旧认领当前活动的系统计划，否则之后「应用」就无从切回。
        PlanItem? saved = await PlanHub.Power
            .SaveAsync(
                name: newPlanName ?? current?.Name,
                powerSchemeGuid: schemeGuid,
                powerPolicy: values)
            .ConfigureAwait(true);

        if (saved is null)
            return false;

        saved.PowerPolicy = values;

        if (creating && hasScheme)
        {
            saved.PowerSchemeGuid = schemeGuid!;
            saved.PowerSchemeName = ActiveSchemeName;
        }

        PlanHub.Power.UpdateCurrentSummary();

        // 值已经进方案：方案条上的「未保存」必须立刻消失，否则用户会一直点。
        PlanHub.Power.MarkSynced();
        return true;
    }

    /// <summary>成功提示里的方案名：没有方案时用页面上的通用称呼</summary>
    private string CurrentPlanName() => PlanHub.Power.Current?.Name ?? "当前电源方案";

    /// <summary>「保存到当前方案」：把界面上这套参数存进选中的方案，并让它认领当前这份系统计划</summary>
    [RelayCommand(CanExecute = nameof(CanSaveToPlan))]
    private async Task SaveToCurrentPlanAsync()
    {
        PlanItem? current = PlanHub.Power.Current;

        if (IsBusy || current is null || current.IsLocal || PlanHub.SuppressWorkerInit)
            return;

        IsBusy = true;

        try
        {
            ApplyPowerPolicyPayload snapshot = BuildFullSnapshot();

            PlanItem? saved = await PlanHub.Power
                .SaveAsync(name: current.Name, powerPolicy: snapshot)
                .ConfigureAwait(true);

            if (saved is null)
                return;

            current.PowerPolicy = snapshot;

            PlanHub.Power.UpdateCurrentSummary();

            // 值已经进方案了：方案条的「未保存」必须立刻消失，否则用户会一直点。
            PlanHub.Power.MarkSynced();

            // 方案里是新值了，是否与机器对得上要重算一次。
            SyncPlanDirty();
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"保存到方案失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>有方案可写、后台在线、不忙时可保存；不要求有脏改动。</summary>
    public bool CanSaveToPlan =>
        !IsBusy
        && IsWorkerAvailable
        && !PlanHub.SuppressWorkerInit
        && PlanHub.Power.Current is { IsLocal: false };

    /// <summary>在模板名后面加序号，避让本机已有的同名方案</summary>
    internal string BuildUniqueSchemeName(string baseName)
    {
        bool Taken(string candidate) => Schemes.Any(
            s => !s.IsCreateNew && string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Taken(baseName))
            return baseName;

        for (int index = 2; index < 1000; index++)
        {
            string candidate = $"{baseName} {index}";
            if (!Taken(candidate))
                return candidate;
        }

        return $"{baseName} {DateTime.Now:HHmmss}";
    }

    /// <summary>把卡片切到改名态，并把当前名字填进输入框。</summary>
    [RelayCommand]
    private void BeginRename(PowerScheme? scheme)
    {
        if (scheme is null || scheme.IsCreateNew || IsBusy)
        {
            return;
        }

        scheme.IsConfirmingDelete = false;
        scheme.EditName = scheme.Name;
        scheme.IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename(PowerScheme? scheme)
    {
        if (scheme is not null)
        {
            scheme.IsRenaming = false;
        }
    }

    /// <summary>提交改名。名字没变就直接收起输入框，不发 IPC —— 一次什么都没改的写请求会让系统重新加载方案，切换时画面会闪一下</summary>
    [RelayCommand]
    private async Task CommitRenameAsync(PowerScheme? scheme)
    {
        if (scheme is null || !scheme.IsRenaming || IsBusy)
        {
            return;
        }

        string name = scheme.EditName.Trim();

        if (name.Length == 0 || name == scheme.Name)
        {
            scheme.IsRenaming = false;
            return;
        }

        IsBusy = true;

        try
        {
            IpcOutcome<PowerSchemeListPayload> outcome = await WorkerBridge.Instance
                .RenamePowerSchemeAsync(scheme.Guid, name)
                .ConfigureAwait(true);

            if (outcome.TransportFailed)
            {
                MarkWorkerUnreachable();
                ToastService.Instance.ShowError("后台服务未连接，无法重命名电源方案");
                return;
            }

            if (!outcome.Success || outcome.Value is null)
            {
                ToastService.Instance.ShowError(outcome.Message);
                return;
            }

            scheme.IsRenaming = false;
            ApplySchemeList(outcome.Value);
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"重命名失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>删除按钮第一次点击：切成「确认删除」两字，避免误触。</summary>
    [RelayCommand]
    private void RequestDelete(PowerScheme? scheme)
    {
        if (scheme is null || scheme.IsCreateNew || scheme.IsBuiltIn || IsBusy)
        {
            return;
        }

        scheme.IsRenaming = false;
        scheme.IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete(PowerScheme? scheme)
    {
        if (scheme is not null)
        {
            scheme.IsConfirmingDelete = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync(PowerScheme? scheme)
    {
        if (scheme is null || !scheme.IsConfirmingDelete || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            IpcOutcome<PowerSchemeListPayload> outcome = await WorkerBridge.Instance
                .DeletePowerSchemeAsync(scheme.Guid)
                .ConfigureAwait(true);

            if (outcome.TransportFailed)
            {
                MarkWorkerUnreachable();
                ToastService.Instance.ShowError("后台服务未连接，无法删除电源方案");
                return;
            }

            if (!outcome.Success || outcome.Value is null)
            {
                ToastService.Instance.ShowError(outcome.Message);
                return;
            }

            ApplySchemeList(outcome.Value);

            // 删掉的可能是活动方案，系统自行回落到了别的方案。
            // 活动 GUID 以返回的列表为准，同时五项值也要跟着重读。
            _refreshTask = null;
            await RefreshCoreAsync(notifyOnFailure: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"删除电源方案失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 方案切换

    private async Task SwitchSchemeAsync(PowerScheme scheme)
    {
        IsBusy = true;

        try
        {
            IpcOutcome<string> outcome = await WorkerBridge.Instance
                .SetActivePowerSchemeAsync(scheme.Guid)
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                // 切换没成，选中项必须退回真实的活动方案，否则界面会一直
                // 显示用户点的那一份，而系统里生效的是另一份。
                SyncSelectedScheme();
                ToastService.Instance.ShowError(
                    outcome.TransportFailed ? "后台服务未连接，无法切换电源方案" : outcome.Message);
                return;
            }

            ActiveSchemeGuid = scheme.Guid;
            ActiveSchemeName = scheme.Name;

            // 换了方案，五项的值全变了，必须重读，不能只改名字。
            _refreshTask = null;
            await RefreshCoreAsync(notifyOnFailure: false).ConfigureAwait(true);

            // 方案层的「生效中」也跟着变：后台已按这份计划反查归属并记账。
            _suppressPlanLoad = true;

            try
            {
                await PlanHub.Power.RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);
            }
            finally
            {
                _suppressPlanLoad = false;
            }
        }
        catch (Exception ex)
        {
            SyncSelectedScheme();
            ToastService.Instance.ShowError($"切换电源方案失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 快照 → 界面

    private void ApplySchemeList(PowerSchemeListPayload payload)
    {
        _suppressSchemeSwitch = true;

        try
        {
            Schemes.Clear();

            foreach (PowerSchemeEntry entry in payload.Schemes)
            {
                Schemes.Add(new PowerScheme(entry.Name, entry.Guid));
            }

            AppendCreateEntry();

            if (payload.ActiveGuid != Guid.Empty)
            {
                ActiveSchemeGuid = payload.ActiveGuid;
            }
        }
        finally
        {
            _suppressSchemeSwitch = false;
        }

        SyncSelectedScheme();
    }

    private void SyncSelectedScheme()
    {
        _suppressSchemeSwitch = true;

        try
        {
            SelectedScheme = Schemes.FirstOrDefault(s => s.Guid == ActiveSchemeGuid);
        }
        finally
        {
            _suppressSchemeSwitch = false;
        }
    }

    private void ApplySnapshot(PowerPolicySnapshot snapshot)
    {
        _suppressDirty = true;

        try
        {
            HasBattery = snapshot.HasBattery;
            IsOnBattery = snapshot.OnAcPower == false;

            _ac.Epp = snapshot.Epp ?? _ac.Epp;
            _ac.EppSupported = snapshot.EppSupported;
            _ac.EppError = snapshot.EppError ?? string.Empty;

            _ac.BoostMode = snapshot.BoostMode ?? _ac.BoostMode;
            _ac.BoostModeSupported = snapshot.BoostModeSupported;
            _ac.BoostModeError = snapshot.BoostModeError ?? string.Empty;

            _ac.MaxProcessorState = snapshot.MaxProcessorState ?? _ac.MaxProcessorState;
            _ac.MaxProcessorStateSupported = snapshot.MaxProcessorStateSupported;
            _ac.MaxProcessorStateError = snapshot.MaxProcessorStateError ?? string.Empty;

            _ac.MinProcessorState = snapshot.MinProcessorState ?? _ac.MinProcessorState;
            _ac.MinProcessorStateSupported = snapshot.MinProcessorStateSupported;
            _ac.MinProcessorStateError = snapshot.MinProcessorStateError ?? string.Empty;

            _ac.CoolingPolicy = snapshot.CoolingPolicy ?? _ac.CoolingPolicy;
            _ac.CoolingPolicySupported = snapshot.CoolingPolicySupported;
            _ac.CoolingPolicyError = snapshot.CoolingPolicyError ?? string.Empty;

            _ac.CoreParkingMin = snapshot.CoreParkingMin ?? _ac.CoreParkingMin;
            _ac.CoreParkingMinSupported = snapshot.CoreParkingMinSupported;
            _ac.CoreParkingMinError = snapshot.CoreParkingMinError ?? string.Empty;

            _ac.IdleDisable = snapshot.IdleDisable ?? _ac.IdleDisable;
            _ac.IdleDisableSupported = snapshot.IdleDisableSupported;
            _ac.IdleDisableError = snapshot.IdleDisableError ?? string.Empty;

            _ac.Aspm = snapshot.Aspm ?? _ac.Aspm;
            _ac.AspmSupported = snapshot.AspmSupported;
            _ac.AspmError = snapshot.AspmError ?? string.Empty;

            if (snapshot.HasBattery)
            {
                _dc.Epp = snapshot.DcEpp ?? _dc.Epp;
                _dc.EppSupported = snapshot.DcEppSupported;
                _dc.EppError = snapshot.DcEppError ?? string.Empty;

                _dc.BoostMode = snapshot.DcBoostMode ?? _dc.BoostMode;
                _dc.BoostModeSupported = snapshot.DcBoostModeSupported;
                _dc.BoostModeError = snapshot.DcBoostModeError ?? string.Empty;

                _dc.MaxProcessorState = snapshot.DcMaxProcessorState ?? _dc.MaxProcessorState;
                _dc.MaxProcessorStateSupported = snapshot.DcMaxProcessorStateSupported;
                _dc.MaxProcessorStateError = snapshot.DcMaxProcessorStateError ?? string.Empty;

                _dc.MinProcessorState = snapshot.DcMinProcessorState ?? _dc.MinProcessorState;
                _dc.MinProcessorStateSupported = snapshot.DcMinProcessorStateSupported;
                _dc.MinProcessorStateError = snapshot.DcMinProcessorStateError ?? string.Empty;

                _dc.CoolingPolicy = snapshot.DcCoolingPolicy ?? _dc.CoolingPolicy;
                _dc.CoolingPolicySupported = snapshot.DcCoolingPolicySupported;
                _dc.CoolingPolicyError = snapshot.DcCoolingPolicyError ?? string.Empty;

                _dc.CoreParkingMin = snapshot.DcCoreParkingMin ?? _dc.CoreParkingMin;
                _dc.CoreParkingMinSupported = snapshot.DcCoreParkingMinSupported;
                _dc.CoreParkingMinError = snapshot.DcCoreParkingMinError ?? string.Empty;

                _dc.IdleDisable = snapshot.DcIdleDisable ?? _dc.IdleDisable;
                _dc.IdleDisableSupported = snapshot.DcIdleDisableSupported;
                _dc.IdleDisableError = snapshot.DcIdleDisableError ?? string.Empty;

                _dc.Aspm = snapshot.DcAspm ?? _dc.Aspm;
                _dc.AspmSupported = snapshot.DcAspmSupported;
                _dc.AspmError = snapshot.DcAspmError ?? string.Empty;
            }

            _appliedAc.CopyFrom(_ac);
            _appliedDc.CopyFrom(_dc);

            _systemAc.CopyFrom(_ac);
            _systemDc.CopyFrom(_dc);
            _hasSystemSnapshot = true;

            ActiveSchemeGuid = snapshot.ActiveSchemeGuid;
            ActiveSchemeName = snapshot.ActiveSchemeName;

            // 进到这一页时可能还没拉到方案列表，靠快照里的名字也要能显示出来。
            if (Schemes.All(s => s.Guid != ActiveSchemeGuid))
            {
                Schemes.Add(new PowerScheme(snapshot.ActiveSchemeName, snapshot.ActiveSchemeGuid));
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        SyncSelectedScheme();
        NotifyAllValues();
        IsDirty = false;

        // 回读拿到系统真值，IsDirty 归零；方案条要按「与方案的差异」重新判定，
        // 否则值明明与方案不同、按钮却落回不可点的「应用」。
        SyncPlanDirty();
    }

    // 降级

    private void MarkAllUnsupported(string reason)
    {
        _suppressDirty = true;

        try
        {
            MarkSetUnsupported(_ac, reason);
            MarkSetUnsupported(_dc, reason);
        }
        finally
        {
            _suppressDirty = false;
        }

        NotifyAllValues();
        IsDirty = false;
        SyncPlanDirty();
    }

    private static void MarkSetUnsupported(PowerSet set, string reason)
    {
        set.EppSupported = false;
        set.EppError = reason;
        set.BoostModeSupported = false;
        set.BoostModeError = reason;
        set.MaxProcessorStateSupported = false;
        set.MaxProcessorStateError = reason;
        set.MinProcessorStateSupported = false;
        set.MinProcessorStateError = reason;
        set.CoolingPolicySupported = false;
        set.CoolingPolicyError = reason;
        set.CoreParkingMinSupported = false;
        set.CoreParkingMinError = reason;
        set.IdleDisableSupported = false;
        set.IdleDisableError = reason;
        set.AspmSupported = false;
        set.AspmError = reason;
    }

    private void MarkWorkerUnreachable()
    {
        IsWorkerAvailable = false;
        UnavailableText = "后台服务未连接";
        MarkAllUnsupported("后台服务未连接");
    }

    // 内部

    private void SetValue(
        Func<PowerSet, int> read,
        Action<PowerSet, int> write,
        int value,
        string propertyName)
    {
        if (read(Current) == value)
        {
            return;
        }

        write(Current, value);
        OnPropertyChanged(propertyName);
        RefreshDirty();
    }

    private void SetValue(
        Func<PowerSet, bool> read,
        Action<PowerSet, bool> write,
        bool value,
        string propertyName)
    {
        if (read(Current) == value)
        {
            return;
        }

        write(Current, value);
        OnPropertyChanged(propertyName);
        RefreshDirty();
    }

    /// <summary>把方案条的状态同步给面板；整体换值、换方案后都要调。</summary>
    /// <remarks>
    /// 三件事分开说，混成一句用户会以为自己改坏了：
    /// <c>IsDirty</c> 是「改了没保存」，<c>NotApplied</c> 是「这份方案绑的 Windows 计划不是当前活动的」，
    /// <c>DiffersFromSystem</c> 是「方案在生效，但机器上的值与方案不同」。
    /// </remarks>
    private void SyncPlanDirty()
    {
        bool edited = IsDirty;
        PlanItem? current = PlanHub.Power.Current;

        PlanHub.Power.SetUnsaved(edited);

        // 没有当前方案时后两条都谈不上，那是还没选方案。
        bool notApplied = current is not null && IsBoundToOtherScheme(current);
        bool wasDrift = PlanHub.Power.DiffersFromSystem;

        PlanHub.Power.SetNotApplied(notApplied);
        PlanHub.Power.SetDiffersFromSystem(
            !edited && !notApplied && current is not null && DiffersFromSnapshot(current));

        // 顶栏按钮的可点状态跟着「与系统不一致」走：那一按是把机器值存回方案。
        if (PlanHub.Power.DiffersFromSystem != wasDrift)
        {
            OnPropertyChanged(nameof(CanApply));
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>方案绑的 Windows 计划不是当前活动的那份：应用它会切走系统计划。</summary>
    private bool IsBoundToOtherScheme(PlanItem current)
    {
        string bound = current.PowerSchemeGuid ?? string.Empty;

        if (bound.Length == 0 || ActiveSchemeGuid == Guid.Empty)
            return false;

        return !string.Equals(bound, ActiveSchemeGuid.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>方案里存的值与机器上读回来的值对不上；方案缺该字段视为「不关心」。</summary>
    /// <remarks>
    /// 比的是上一次回读留下的系统值，不是页面上的值：页面随时会被方案值覆盖，
    /// 拿它比方案永远相等，「与系统不一致」就再也不会亮。
    /// </remarks>
    private bool DiffersFromSnapshot(PlanItem current)
    {
        ApplyPowerPolicyPayload? plan = current.PowerPolicy;

        if (plan is null || !_hasSystemSnapshot)
            return false;

        return Differs(_systemAc, plan, ac: true) || (HasBattery && Differs(_systemDc, plan, ac: false));
    }

    private static bool Differs(PowerSet set, ApplyPowerPolicyPayload plan, bool ac)
    {
        if (ac)
        {
            return (plan.Epp is int epp && epp != set.Epp)
                   || (plan.BoostMode is int boost && boost != set.BoostMode)
                   || (plan.MaxProcessorState is int max && max != set.MaxProcessorState)
                   || (plan.MinProcessorState is int min && min != set.MinProcessorState)
                   || (plan.CoolingPolicy is int cooling && cooling != set.CoolingPolicy)
                   || (plan.CoreParkingMin is int park && park != set.CoreParkingMin)
                   || (plan.Aspm is int aspm && aspm != set.Aspm)
                   || (plan.IdleDisable is bool idle && idle != set.IdleDisable);
        }

        return (plan.DcEpp is int dcEpp && dcEpp != set.Epp)
               || (plan.DcBoostMode is int dcBoost && dcBoost != set.BoostMode)
               || (plan.DcMaxProcessorState is int dcMax && dcMax != set.MaxProcessorState)
               || (plan.DcMinProcessorState is int dcMin && dcMin != set.MinProcessorState)
               || (plan.DcCoolingPolicy is int dcCooling && dcCooling != set.CoolingPolicy)
               || (plan.DcCoreParkingMin is int dcPark && dcPark != set.CoreParkingMin)
               || (plan.DcAspm is int dcAspm && dcAspm != set.Aspm)
               || (plan.DcIdleDisable is bool dcIdle && dcIdle != set.IdleDisable);
    }

    private void RefreshDirty()
    {
        if (_suppressDirty)
        {
            return;
        }

        IsDirty = !PowerSet.Same(_ac, _appliedAc) || !PowerSet.Same(_dc, _appliedDc);

        // 每次值变都同步方案条：保存到方案后它要能重新亮起「未保存」。
        // 回读会把 IsDirty 归零，但界面值可能仍与方案不同，方案条要给「保存至」。
        SyncPlanDirty();
    }

    private void NotifyAllValues()
    {
        OnPropertyChanged(nameof(Epp));
        OnPropertyChanged(nameof(EppSupported));
        OnPropertyChanged(nameof(EppError));

        OnPropertyChanged(nameof(BoostMode));
        OnPropertyChanged(nameof(BoostModeSupported));
        OnPropertyChanged(nameof(BoostModeError));

        OnPropertyChanged(nameof(MaxProcessorState));
        OnPropertyChanged(nameof(MaxProcessorStateSupported));
        OnPropertyChanged(nameof(MaxProcessorStateError));

        OnPropertyChanged(nameof(MinProcessorState));
        OnPropertyChanged(nameof(MinProcessorStateSupported));
        OnPropertyChanged(nameof(MinProcessorStateError));

        OnPropertyChanged(nameof(CoolingPolicy));
        OnPropertyChanged(nameof(CoolingPolicySupported));
        OnPropertyChanged(nameof(CoolingPolicyError));

        OnPropertyChanged(nameof(CoreParkingMin));
        OnPropertyChanged(nameof(CoreParkingMinSupported));
        OnPropertyChanged(nameof(CoreParkingMinError));
        OnPropertyChanged(nameof(CoreParkingWarns));

        OnPropertyChanged(nameof(IdleDisable));
        OnPropertyChanged(nameof(IdleDisableSupported));
        OnPropertyChanged(nameof(IdleDisableError));

        OnPropertyChanged(nameof(Aspm));
        OnPropertyChanged(nameof(AspmSupported));
        OnPropertyChanged(nameof(AspmError));
    }

    /// <summary>截图模式下的固定方案卡片</summary>
    private void SeedPlaceholderSchemes()
    {
        Schemes.Add(new PowerScheme("平衡", new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")));
        Schemes.Add(new PowerScheme("高性能", new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")));
        Schemes.Add(new PowerScheme("节能", new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")));
        Schemes.Add(new PowerScheme("卓越性能", new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")));
        AppendCreateEntry();

        _suppressSchemeSwitch = true;
        SelectedScheme = Schemes[0];
        ActiveSchemeGuid = Schemes[0].Guid;
        ActiveSchemeName = Schemes[0].Name;
        _suppressSchemeSwitch = false;
    }

    /// <summary>列表末尾补那条「＋ 新建方案」。</summary>
    private void AppendCreateEntry()
        => Schemes.Add(new PowerScheme(string.Empty, Guid.Empty, isCreateNew: true));
}

public partial class PowerScheme : ObservableObject
{
    public PowerScheme(string name, Guid guid, bool isCreateNew = false)
    {
        Name = name;
        Guid = guid;
        IsCreateNew = isCreateNew;
        EditName = name;
    }

    public string Name { get; }

    public Guid Guid { get; }

    /// <summary>列表末尾那条「＋ 新建方案」</summary>
    public bool IsCreateNew { get; }

    /// <summary>是不是 Windows 内置方案</summary>
    public bool IsBuiltIn => PowerSchemePresets.IsBuiltIn(Guid);

    /// <summary>改名输入框是否展开。内置方案也允许改名，名字只是显示层的。</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>改名输入框里的草稿。取消时丢弃，不写回 <see cref="Name"/>。</summary>
    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>删除按钮是否已进入二次确认态。</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;

    partial void OnIsRenamingChanged(bool value)
    {
        if (!value)
        {
            EditName = Name;
        }
    }

    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        if (value)
        {
            IsRenaming = false;
        }
    }

    public override string ToString() => Name;
}

/// <summary>新建弹层里的一项内置模板</summary>
public partial class PresetOption : ObservableObject
{
    public PresetOption(PowerSchemePreset preset)
    {
        Preset = preset;
    }

    public PowerSchemePreset Preset { get; }

    public string Name => Preset.Name;
}
