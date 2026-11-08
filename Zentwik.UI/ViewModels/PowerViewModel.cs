using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>
/// 电源页视图模型：系统电源方案 + 五项处理器 / PCIe 策略的 AC / DC 两套值。
/// </summary>
/// <remarks>
/// 【AC / DC 是两套值，不是一个值加个开关】
/// 系统里每一项都存成 AC、DC 两个独立的值，笔记本上通常不同。
/// 分段切换只换「在看哪一套」，两套都在内存里，来回切不会丢改动。
///
/// 【写入是增量的】
/// <c>apply-power-policy</c> 只改 Payload 里出现的项。这里只把「跟上次生效值不同」
/// 的项发出去：全量重发等于每点一次应用就把五项刷一遍，其中一项本机不支持
/// 就会回一句失败 —— 用户只动了一个滑块，却被告知五项里有一项失败。
///
/// 【「读不到」必须与「读到 0」分开】
/// 每项自带 Supported / Error。折成一个值时，界面会画出 EPP = 0 的滑块，
/// 拖一下才发现写不进去。
/// </remarks>
public partial class PowerViewModel : ObservableObject
{
    private const int QueryTimeoutMs = 6_000;

    // Worker 写完要重新激活一次电源方案，低端机上这一步可能要几秒。
    private const int ApplyTimeoutMs = 30_000;

    /// <summary>
    /// 诊断用：<c>--screenshot</c> 时不发 IPC。
    /// </summary>
    /// <remarks>
    /// 离屏截图是一帧定格，异步查询返回的时机不确定，同一个命令两次会拍出不同的图。
    /// 截图模式只验视觉，方案卡片与五项值一律用占位数据。
    /// </remarks>
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
            && a.IdleDisable == b.IdleDisable
            && a.Aspm == b.Aspm;

        public void ApplyDefaults(PowerPolicyDefaults defaults)
        {
            Epp = defaults.Epp;
            BoostMode = defaults.BoostMode;
            MaxProcessorState = defaults.MaxProcessorState;
            IdleDisable = defaults.IdleDisable;
            Aspm = defaults.Aspm;
        }
    }

    private readonly PowerSet _ac = PowerSet.CreateDefault(ac: true);
    private readonly PowerSet _dc = PowerSet.CreateDefault(ac: false);

    private readonly PowerSet _appliedAc = PowerSet.CreateDefault(ac: true);
    private readonly PowerSet _appliedDc = PowerSet.CreateDefault(ac: false);

    private PowerSet Current => IsAcMode ? _ac : _dc;

    private bool _suppressDirty;

    /// <summary>程序性改动选中项时为 true，避免列表刷新触发一次方案切换。</summary>
    private bool _suppressSchemeSwitch;

    private Task? _refreshTask;

    private bool _initialized;

    // ==================== 系统电源方案 ====================

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

        _ = SwitchSchemeAsync(value);
    }

    [ObservableProperty]
    private Guid _activeSchemeGuid;

    [ObservableProperty]
    private string _activeSchemeName = PowerPolicySnapshot.UnknownSchemeName;

    // ==================== 供电状态 ====================

    /// <summary>本机有没有电池。false 时界面上不出现电池分段。</summary>
    [ObservableProperty]
    private bool _hasBattery;

    /// <summary>此刻是否在用电池。由快照的 <c>onAcPower</c> 推出。</summary>
    [ObservableProperty]
    private bool _isOnBattery;

    public string SupplyLabel => IsOnBattery ? "电池供电" : "交流电";

    partial void OnIsOnBatteryChanged(bool value) => OnPropertyChanged(nameof(SupplyLabel));

    // ==================== 分段 ====================

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

    /// <summary>
    /// 最大处理器状态的滑块下限。
    /// </summary>
    /// <remarks>
    /// 系统允许 0，Worker 侧固定拒绝 50 以下的写入（见 <c>PowerPolicyService</c>）。
    /// 这里是同一个 50，滑块拖不到写不进去的位置。
    /// </remarks>
    public int MaxProcessorStateMinimum => 50;

    // ==================== 五项 ====================

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

    // ==================== 状态 ====================

    [ObservableProperty]
    private bool _isDirty;

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();

        // 电源页的改动同样是「改了没下发」，方案面板据此贴未保存并拦住切换。
        PlanHub.Power.SetUnsaved(value);
    }

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>后台服务是否可写。false 时应用参数禁用。</summary>
    [ObservableProperty]
    private bool _isWorkerAvailable = true;

    partial void OnIsWorkerAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 每行右侧「本机不支持」那行字。
    /// </summary>
    /// <remarks>
    /// 后台服务没连上时也叫「本机不支持」，用户会去查主板手册 ——
    /// 而真正要做的是把服务拉起来。
    /// </remarks>
    [ObservableProperty]
    private string _unavailableText = "本机不支持";

    public bool CanApply => IsDirty && !IsBusy && IsWorkerAvailable;

    // ==================== 初始化 ====================

    public PowerViewModel()
    {
        foreach (PowerSchemePreset preset in PowerSchemePresets.All)
        {
            Presets.Add(new PresetOption(preset));
        }

        if (SuppressWorkerInit)
        {
            // 截图模式按笔记本的形态画（有电池、交流电供电），
            // AC/DC 分段才在图上；真实运行一律以快照为准。
            HasBattery = true;
            SeedPlaceholderSchemes();
        }
    }

    /// <summary>
    /// 读方案列表与当前策略。失败不弹提示，界面以「本机不支持」表达。
    /// </summary>
    /// <remarks>
    /// 启动流程与页面 Loaded 会同时调到这里，共用同一个 Task：
    /// 并发发起两次查询会在管道上排两条命令，第二条等第一条的超时。
    /// </remarks>
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
        }
        finally
        {
            // 失败也算「已经尝试过」：再自动重试只会在 Worker 还没起来时
            // 反复堵住管道。用户点「回读」可以手动重来。
            _initialized = true;
            _refreshTask = null;
        }
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

    // ==================== 应用 ====================

    /// <summary>
    /// 「应用参数」：只发跟上次生效值不同的项。
    /// </summary>
    /// <remarks>
    /// 部分失败时 Worker 仍会回一份写入后的快照，这里照单全收 ——
    /// 已经有项写进系统了，界面必须立刻对齐，否则下次点应用会把它们再写一遍。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        ApplyPowerPolicyPayload patch = BuildPatch();

        if (patch.IsEmpty)
        {
            IsDirty = false;
            return;
        }

        IsBusy = true;

        try
        {
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

    /// <summary>
    /// 与上次生效值的差异。
    /// </summary>
    /// <remarks>
    /// 电池那五项只在确实有电池时参与：台式机上发 <c>dcEpp</c> 之类的字段，
    /// 系统会照收（存在方案里），但那是一项用户永远看不到的值。
    /// </remarks>
    private ApplyPowerPolicyPayload BuildPatch()
    {
        ApplyPowerPolicyPayload payload = new();

        if (_ac.Epp != _appliedAc.Epp) payload.Epp = _ac.Epp;
        if (_ac.BoostMode != _appliedAc.BoostMode) payload.BoostMode = _ac.BoostMode;
        if (_ac.MaxProcessorState != _appliedAc.MaxProcessorState) payload.MaxProcessorState = _ac.MaxProcessorState;
        if (_ac.IdleDisable != _appliedAc.IdleDisable) payload.IdleDisable = _ac.IdleDisable;
        if (_ac.Aspm != _appliedAc.Aspm) payload.Aspm = _ac.Aspm;

        if (!HasBattery)
        {
            return payload;
        }

        if (_dc.Epp != _appliedDc.Epp) payload.DcEpp = _dc.Epp;
        if (_dc.BoostMode != _appliedDc.BoostMode) payload.DcBoostMode = _dc.BoostMode;
        if (_dc.MaxProcessorState != _appliedDc.MaxProcessorState) payload.DcMaxProcessorState = _dc.MaxProcessorState;
        if (_dc.IdleDisable != _appliedDc.IdleDisable) payload.DcIdleDisable = _dc.IdleDisable;
        if (_dc.Aspm != _appliedDc.Aspm) payload.DcAspm = _dc.Aspm;

        return payload;
    }

    /// <summary>
    /// 「恢复默认」：把当前分段填成一组折中值。不写系统，要再点应用参数。
    /// </summary>
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
    }

    // ==================== 新建 / 改名 / 删除 ====================

    /// <summary>
    /// 新建弹层里的四个内置模板：节能 / 平衡 / 高性能 / 卓越性能。
    /// </summary>
    public ObservableCollection<PresetOption> Presets { get; } = new();

    [ObservableProperty]
    private bool _isCreateMenuOpen;

    [RelayCommand]
    private void OpenCreateMenu() => IsCreateMenuOpen = true;

    [RelayCommand]
    private void CloseCreateMenu() => IsCreateMenuOpen = false;

    /// <summary>
    /// 以某个内置模板派生一份新方案，并立刻切过去。
    /// </summary>
    /// <remarks>
    /// 派生源固定为微软模板：本页只管五项设置，凭空造方案会让其余几十项落回系统默认值。
    /// </remarks>
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

    /// <summary>
    /// 在模板名后面加序号，避让本机已有的同名方案。
    /// </summary>
    /// <remarks>
    /// 模板名是「高性能」这类通用词，本机往往已经有一份，直接用会被后台的重名校验挡回来。
    /// </remarks>
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

    /// <summary>
    /// 提交改名。名字没变就直接收起输入框，不发 IPC ——
    /// 一次什么都没改的写请求会让系统重新加载方案，切换时画面会闪一下。
    /// </summary>
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

    // ==================== 方案切换 ====================

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

    // ==================== 快照 → 界面 ====================

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

                _dc.IdleDisable = snapshot.DcIdleDisable ?? _dc.IdleDisable;
                _dc.IdleDisableSupported = snapshot.DcIdleDisableSupported;
                _dc.IdleDisableError = snapshot.DcIdleDisableError ?? string.Empty;

                _dc.Aspm = snapshot.DcAspm ?? _dc.Aspm;
                _dc.AspmSupported = snapshot.DcAspmSupported;
                _dc.AspmError = snapshot.DcAspmError ?? string.Empty;
            }

            _appliedAc.CopyFrom(_ac);
            _appliedDc.CopyFrom(_dc);

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
    }

    // ==================== 降级 ====================

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
    }

    private static void MarkSetUnsupported(PowerSet set, string reason)
    {
        set.EppSupported = false;
        set.EppError = reason;
        set.BoostModeSupported = false;
        set.BoostModeError = reason;
        set.MaxProcessorStateSupported = false;
        set.MaxProcessorStateError = reason;
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

    // ==================== 内部 ====================

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

    private void RefreshDirty()
    {
        if (_suppressDirty)
        {
            return;
        }

        IsDirty = !PowerSet.Same(_ac, _appliedAc) || !PowerSet.Same(_dc, _appliedDc);
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

        OnPropertyChanged(nameof(IdleDisable));
        OnPropertyChanged(nameof(IdleDisableSupported));
        OnPropertyChanged(nameof(IdleDisableError));

        OnPropertyChanged(nameof(Aspm));
        OnPropertyChanged(nameof(AspmSupported));
        OnPropertyChanged(nameof(AspmError));
    }

    /// <summary>
    /// 截图模式下的固定方案卡片。
    /// </summary>
    /// <remarks>
    /// 四个 GUID 是 Windows 内置方案，不查系统也能列出来，仅用于离屏截图
    /// （截图模式不连 Worker）。真实运行一律以 query-power-schemes 的结果为准。
    /// </remarks>
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

    /// <summary>
    /// 列表末尾那条「＋ 新建方案」。它不对应系统里的任何方案，
    /// 只是方案行里的一个固定位置 —— 单独放到 ListBox 外面的话，
    /// 方案多了会换行，加号就跑到第二行开头去了。
    /// </summary>
    public bool IsCreateNew { get; }

    /// <summary>
    /// 是不是 Windows 内置方案。内置方案不能删：删掉之后系统里
    /// 再也没有对应模式的模板，只能 powercfg -restoredefaultschemes 重置全部电源设置。
    /// </summary>
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

/// <summary>
/// 新建弹层里的一项内置模板。
/// </summary>
public partial class PresetOption : ObservableObject
{
    public PresetOption(PowerSchemePreset preset)
    {
        Preset = preset;
    }

    public PowerSchemePreset Preset { get; }

    public string Name => Preset.Name;
}
