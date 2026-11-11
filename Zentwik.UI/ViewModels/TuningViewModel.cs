using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>调校页视图模型：逐核 CO + FMax 的唯一状态与唯一出口</summary>
public partial class TuningViewModel : ObservableObject
{
    private const string SmuUnavailableHint = "请确保已安装 PawnIO 驱动并以管理员权限运行";

    // 状态查询只读内存字段；快照要真读一次硬件；
    // 下发放到 75 秒是为了盖住 Worker 等 PawnIO 的 60 秒。
    private const int StatusTimeoutMs = 2_000;
    private const int SnapshotTimeoutMs = 15_000;
    private const int ApplyTimeoutMs = 75_000;

    /// <summary>诊断用：<c>--cores=N</c> 改初始格子数。</summary>
    public static int SeedCoreCount { get; set; } = 16;

    /// <summary>诊断用：<c>--smu=off</c> 强制整页不可用。</summary>
    public static bool ForceSmuOffline { get; set; }

    /// <summary>诊断用：--screenshot 时不发 IPC</summary>
    public static bool SuppressWorkerInit { get; set; }

    /// <summary>处理器型号。与侧栏同源，只在关于页与诊断里用。</summary>
    [ObservableProperty]
    private string _cpuName = CpuInfo.Name;

    /// <summary>物理核心数 = 逐核 CO 的格子数。</summary>
    [ObservableProperty]
    private int _cpuCoreCount = SeedCoreCount;

    /// <summary>SMU 是否可写。false 时整页遮罩、应用参数禁用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _smuAvailable = !ForceSmuOffline;

    /// <summary>管道是否连通。与 <see cref="SmuAvailable"/> 分开：连上了也可能不可写。</summary>
    [ObservableProperty]
    private bool _workerConnected;

    /// <summary>遮罩标题。取值为「SMU 接口不可用」或「后台服务未连接」。</summary>
    [ObservableProperty]
    private string _blockingTitle = "SMU 接口不可用";

    /// <summary>遮罩说明。SMU 不可用时优先用 Worker 回的原因。</summary>
    [ObservableProperty]
    private string _blockingDetail = SmuUnavailableHint;

    /// <summary>逐核偏移项。长度 = <see cref="CpuCoreCount"/>，核数变了整段重建。</summary>
    public ObservableCollection<CoreMarginItem> CoreMargins { get; } = new();

    /// <summary>逐核 CO 的上下限</summary>
    public int CoreMarginMinimum => SmuReader.CoMin;

    public int CoreMarginMaximum => SmuReader.CoMax;

    public int CoreMarginStep => 1;

    /// <summary>频率上限（MHz）。</summary>
    [ObservableProperty]
    private int _fMaxMHz;

    /// <summary>FMax 输入框的文本</summary>
    public string FMaxText
    {
        get => _fMaxText;
        set
        {
            if (!SetProperty(ref _fMaxText, value))
            {
                return;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                // 空串 / 非数字：不接受，把框里的字纠回当前值。
                SyncFMaxText();
                return;
            }

            FMaxMHz = Math.Clamp(parsed, FMaxMinimum, FMaxMaximum);
            SyncFMaxText();
        }
    }

    private string _fMaxText = string.Empty;

    /// <summary>可调下限（MHz）</summary>
    [ObservableProperty]
    private int _fMaxMinimum = (int)SmuReader.FMaxMin;

    /// <summary>可调上限（MHz）。同上，快照给 0 时退到 <see cref="SmuReader.FMaxMax"/>。</summary>
    [ObservableProperty]
    private int _fMaxMaximum = (int)SmuReader.FMaxMax;

    public int FMaxStepMHz => 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isDirty;

    /// <summary>回读 / 取消是否可点：只受「正在通信」限制</summary>
    public bool CanOperate => !IsBusy;

    /// <summary>应用参数是否可点：有改动、SMU 可写、且当前没有正在跑的通信。</summary>
    public bool CanSave => !IsBusy && SmuAvailable && IsDirty;

    /// <summary>界面挂载期间冻结脏标记</summary>
    public bool FreezeDirty { get; set; }

    /// <summary>上一次「已生效」的一组值。「取消」回到这里，「回读」后同步更新。</summary>
    private int[] _baselineMargins = Array.Empty<int>();

    private int _baselineFMax;

    /// <summary>构造函数已跑完。为 false 时任何赋值都不算改动。</summary>
    private bool _ready;

    /// <summary>正在批量写入（读快照 / 取消 / 换方案），期间的赋值不算改动。</summary>
    private bool _suppressDirty;

    /// <summary>是否已经跟 Worker 对过一次数（初始化只做一次）。</summary>
    private bool _syncedWithWorker;

    /// <summary>上一次加载时用的方案 Id，用来判断「方案换人了没有」。</summary>
    private string? _loadedProfileId;

    public TuningViewModel()
    {
        int[] seed = BuildSeed(CpuCoreCount);

        for (int i = 0; i < seed.Length; i++)
        {
            CoreMargins.Add(CreateItem(i, seed[i]));
        }

        _fMaxMHz = 4750;
        _fMaxText = _fMaxMHz.ToString(CultureInfo.InvariantCulture);

        _baselineFMax = _fMaxMHz;
        _baselineMargins = Snapshot();

        _ready = true;

        // 方案值在内存里（列表是 query-profiles 一次带回来的）：
        // 有就先显示，别让界面先闪一遍占位值再被覆盖。
        LoadProfileValues();

        // 面板底部按钮脏值时改成「保存至」：只写 plans.json，不碰 SMU。
        PlanHub.Tuning.CanSavePending = () => !IsBusy && IsDirty;
        PlanHub.Tuning.SavePending = () => _ = SaveToPlanAsync();

        // 顶栏切方案 = 本页换一组值。用弱事件订阅：页面实例被外壳缓存着，
        // 但自检会造很多个，普通订阅会在单例上留下一串永不释放的强引用。
        System.Windows.WeakEventManager<PlanScope, EventArgs>.AddHandler(
            PlanHub.Tuning,
            nameof(PlanScope.CurrentChanged),
            OnCurrentProfileChanged);
    }

    /// <summary>与 Worker 对一次数：SMU 是否可写、硬件当前值、当前方案存的值</summary>
    public async Task InitializeAsync()
    {
        if (_syncedWithWorker)
        {
            return;
        }

        _syncedWithWorker = true;

        if (SuppressWorkerInit)
        {
            return;
        }

        // 方案列表可能还在路上（窗口启动时并行拉取）。
        // 这里是幂等的：已经同步过就立刻返回。
        await PlanHub.Tuning.InitializeAsync().ConfigureAwait(true);

        await RefreshFromWorkerAsync(notifyOnFailure: false).ConfigureAwait(true);
    }

    /// <summary>回读：从硬件重读 CO / FMax，并把它作为新基线。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ReadbackAsync()
        => await RefreshFromWorkerAsync(notifyOnFailure: true, reloadPlan: false).ConfigureAwait(true);

    /// <summary>应用参数：写进当前方案并立即下发</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ApplyAsync()
    {
        IsBusy = true;

        try
        {
            int[] margins = Snapshot();
            uint fMax = (uint)Math.Max(0, FMaxMHz);
            PlanItem? target = PlanHub.Tuning.Current;

            // 先写进方案，再应用：应用读的是 store，而 store 由「应用」这一步写入。
            // 两步分开之后，保存失败不会被当成下发失败，回执能说清是哪一步出的问题。
            IpcOutcome<string> saved = await WorkerBridge.Instance
                .SavePlanAsync(PlanKind.Tuning, target?.Id, target?.Name ?? "当前调校", margins, fMax)
                .ConfigureAwait(true);

            if (!saved.Success)
            {
                if (saved.TransportFailed)
                    MarkWorkerUnreachable("保存调校方案时与后台服务的连接中断。");

                ToastService.Instance.Show(
                    string.IsNullOrEmpty(saved.Message) ? "调校未保存" : saved.Message);
                return;
            }

            IpcOutcome<string> outcome = await WorkerBridge.Instance
                .ApplyPlanAsync(PlanKind.Tuning, saved.Value ?? target?.Id ?? string.Empty, ApplyTimeoutMs)
                .ConfigureAwait(true);

            if (outcome.TransportFailed)
            {
                MarkWorkerUnreachable("下发过程中与后台服务的连接中断，无法确认是否已生效。");
                ToastService.Instance.ShowError("后台服务未连接，调校未下发");
                return;
            }

            if (!outcome.Success)
            {
                ToastService.Instance.Show(
                    string.IsNullOrEmpty(outcome.Message) ? "调校未生效" : outcome.Message);
                return;
            }

            PlanHub.Tuning.RememberTuning(margins, fMax);
            AcceptCurrentAsBaseline();
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"下发调校失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>只把界面上的值写进当前方案，不下发硬件</summary>
    private async Task SaveToPlanAsync()
    {
        int[] margins = Snapshot();
        uint fMax = (uint)Math.Max(0, FMaxMHz);
        PlanItem? target = PlanHub.Tuning.Current;

        try
        {
            IpcOutcome<string> saved = await WorkerBridge.Instance
                .SavePlanAsync(PlanKind.Tuning, target?.Id, target?.Name ?? "当前调校", margins, fMax)
                .ConfigureAwait(true);

            if (!saved.Success)
            {
                ToastService.Instance.Show(
                    string.IsNullOrEmpty(saved.Message) ? "方案未保存" : saved.Message);
                return;
            }

            PlanHub.Tuning.RememberTuning(margins, fMax);
            await PlanHub.Tuning.RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);
            AcceptCurrentAsBaseline();
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError($"保存方案失败：{ex.Message}");
        }
    }

    /// <summary>取消：回到上一次生效的值。纯本地操作，不需要 SMU。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void Discard()
    {
        _suppressDirty = true;

        try
        {
            for (int i = 0; i < CoreMargins.Count && i < _baselineMargins.Length; i++)
            {
                CoreMargins[i].Restore(_baselineMargins[i]);
            }

            FMaxMHz = _baselineFMax;
            IsDirty = false;
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    [RelayCommand]
    private void StepFMaxUp() => FMaxMHz = Math.Min(FMaxMaximum, FMaxMHz + FMaxStepMHz);

    [RelayCommand]
    private void StepFMaxDown() => FMaxMHz = Math.Max(FMaxMinimum, FMaxMHz - FMaxStepMHz);

    /// <summary>与 Worker 对数，再把当前方案的值叠上去</summary>
    private async Task RefreshFromWorkerAsync(bool notifyOnFailure, bool reloadPlan = true)
    {
        // 初始化与「切方案」可能同时触发，共用同一次刷新，避免遮罩连闪两下。
        Task? existing = _refreshTask;
        if (existing is not null)
        {
            await existing.ConfigureAwait(true);

            // 复用的那次是静默刷新时，用户主动点的「回读」仍要补一次带提示的，
            // 否则点下去失败也毫无反应。
            if (notifyOnFailure)
                await RefreshFromWorkerCoreAsync(true, reloadPlan).ConfigureAwait(true);

            return;
        }

        Task task = RefreshFromWorkerCoreAsync(notifyOnFailure, reloadPlan);
        _refreshTask = task;

        try
        {
            await task.ConfigureAwait(true);
        }
        finally
        {
            _refreshTask = null;
        }
    }

    private Task? _refreshTask;

    private async Task RefreshFromWorkerCoreAsync(bool notifyOnFailure, bool reloadPlan)
    {
        IsBusy = true;

        try
        {
            IpcOutcome<SmuStatusPayload> status = await WorkerBridge.Instance
                .QuerySmuStatusAsync(StatusTimeoutMs)
                .ConfigureAwait(true);

            if (status.TransportFailed)
            {
                MarkWorkerUnreachable("无法读写硬件。后台服务未响应，点左下角状态灯可重新连接。");
                await FallbackToStoredValuesAsync().ConfigureAwait(true);
                NotifyIf(notifyOnFailure, "后台服务未连接，未能读取硬件当前值");
                return;
            }

            WorkerConnected = true;

            if (!status.Success || status.Value is null)
            {
                // 查不到状态：既不能说可写，也不能说不可写，按不可用处理并给出原文。
                MarkSmuUnavailable(string.IsNullOrEmpty(status.Message) ? SmuUnavailableHint : status.Message);
                await FallbackToStoredValuesAsync().ConfigureAwait(true);
                NotifyIf(notifyOnFailure, "无法获取 SMU 状态");
                return;
            }

            if (ForceSmuOffline || !status.Value.Available)
            {
                MarkSmuUnavailable(status.Value.Error);
                await FallbackToStoredValuesAsync().ConfigureAwait(true);
                return;
            }

            IpcOutcome<TuningSnapshotPayload> snapshot = await WorkerBridge.Instance
                .QueryTuningSnapshotAsync(SnapshotTimeoutMs)
                .ConfigureAwait(true);

            if (snapshot.TransportFailed)
            {
                MarkWorkerUnreachable("读取硬件当前值时连接中断。请确认后台服务仍在运行后重试。");
                await FallbackToStoredValuesAsync().ConfigureAwait(true);
                NotifyIf(notifyOnFailure, "读取硬件当前值时连接中断");
                return;
            }

            if (!snapshot.Success || snapshot.Value is null)
            {
                // 能查状态但读不到值：SMU 通道大概率坏了，原因用 Worker 的原文。
                MarkSmuUnavailable(string.IsNullOrEmpty(snapshot.Message) ? SmuUnavailableHint : snapshot.Message);
                await FallbackToStoredValuesAsync().ConfigureAwait(true);
                NotifyIf(notifyOnFailure, "读取 SMU 快照失败");
                return;
            }

            ApplySnapshot(snapshot.Value);

            // 主动回读要显示硬件真值；初始化 / 切方案才叠上当前方案的编辑值。
            if (reloadPlan)
                LoadProfileValues();
        }
        catch (Exception ex)
        {
            // 只可能是本地的意外（界面已卸载、任务被取消等）。同样折成状态，不向外抛。
            MarkWorkerUnreachable($"读取硬件当前值失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _refreshTask = null;
        }
    }

    /// <summary>把硬件快照写进界面，并作为基线。</summary>
    private void ApplySnapshot(TuningSnapshotPayload payload)
    {
        ClearBlocking();

        _suppressDirty = true;

        try
        {
            ApplyFMaxRange(payload.FMaxMinMHz, payload.FMaxMaxMHz);

            int[] margins = payload.CoreMargins ?? Array.Empty<int>();
            int coreCount = margins.Length > 0 ? margins.Length : CpuCoreCount;

            RebuildCoreMargins(coreCount, margins);

            if (payload.FMaxMHz > 0)
            {
                FMaxMHz = Math.Clamp((int)payload.FMaxMHz, FMaxMinimum, FMaxMaximum);
            }

            AcceptCurrentAsBaseline();
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    /// <summary>收下 Worker 给的可写区间</summary>
    private void ApplyFMaxRange(uint min, uint max)
    {
        int low = min > 0 ? (int)min : (int)SmuReader.FMaxMin;
        int high = max > 0 ? (int)max : (int)SmuReader.FMaxMax;

        if (high <= low)
        {
            return;
        }

        int current = FMaxMHz;

        if (current > 0)
        {
            low = Math.Min(low, current);
            high = Math.Max(high, current);
        }

        FMaxMinimum = low;
        FMaxMaximum = high;
    }

    /// <summary>把当前方案存的调校值叠到界面上</summary>
    private bool LoadProfileValues()
    {
        PlanItem? item = PlanHub.Tuning.Current;

        _loadedProfileId = item?.Id;

        if (item is null || (item.CoreMargins.Length == 0 && item.FMaxMHz == 0))
        {
            return false;
        }

        _suppressDirty = true;

        try
        {
            if (item.CoreMargins.Length > 0)
            {
                RebuildCoreMargins(item.CoreMargins.Length, item.CoreMargins);
            }

            if (item.FMaxMHz > 0)
            {
                FMaxMHz = Math.Clamp((int)item.FMaxMHz, FMaxMinimum, FMaxMaximum);
            }

            AcceptCurrentAsBaseline();
            return true;
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    /// <summary>读不到硬件时的兜底：先试当前方案的值，再退回盘上上一次保存的值</summary>
    private async Task FallbackToStoredValuesAsync()
    {
        if (LoadProfileValues())
        {
            return;
        }

        (int[] margins, uint fMax) = await Task.Run(TuningLocalStore.Load).ConfigureAwait(true);

        if (margins.Length == 0 && fMax == 0)
        {
            return;
        }

        _suppressDirty = true;

        try
        {
            if (margins.Length > 0)
            {
                RebuildCoreMargins(margins.Length, margins);
            }

            if (fMax > 0)
            {
                FMaxMHz = Math.Clamp((int)fMax, FMaxMinimum, FMaxMaximum);
            }

            AcceptCurrentAsBaseline();
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    /// <summary>顶栏换了方案：本页跟着换一组值，未保存的改动作废。</summary>
    private void OnCurrentProfileChanged(object? sender, EventArgs e)
    {
        if (SuppressWorkerInit)
            return;

        // 正忙着（回读 / 下发）时先记下，等忙完补做。直接丢弃会让
        // 页面一直显示旧方案的值，而顶栏已经切到新方案。
        if (IsBusy)
        {
            _recheckProfileOnIdle = true;
            return;
        }

        Fire(async () =>
        {
            if (PlanHub.Tuning.Current?.Id == _loadedProfileId)
                return;

            // 方案值是列表里带过来的，只有它没有值时才去问硬件。
            if (LoadProfileValues())
                return;

            await RefreshFromWorkerAsync(notifyOnFailure: false).ConfigureAwait(true);
        });
    }

    private bool _recheckProfileOnIdle;

    private static void Fire(Func<Task> action) => AsyncRunner.Fire(action);

    private void RebuildCoreMargins(int coreCount, IReadOnlyList<int> values)
    {
        CoreMargins.Clear();

        for (int i = 0; i < coreCount; i++)
        {
            int margin = i < values.Count ? NormalizeMargin(values[i]) : 0;
            CoreMargins.Add(CreateItem(i, margin));
        }

        CpuCoreCount = coreCount;
    }

    private CoreMarginItem CreateItem(int index, int margin)
    {
        CoreMarginItem item = new(
            index,
            NormalizeMargin(margin),
            CoreMarginMinimum,
            CoreMarginMaximum,
            CoreMarginStep);

        if (margin == int.MinValue)
        {
            item.MarkUnavailable();
        }

        // 任何一核的偏移变化都把页面标成「未保存」。
        // 订阅放在这里而不是 View 里 —— 页面不需要知道「脏」是怎么算出来的。
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CoreMarginItem.Margin))
            {
                MarkDirty();
            }
        };

        return item;
    }

    /// <summary>硬件回读里 int.MinValue 表示「该核读失败 / 禁用」，不是「偏移 0」</summary>
    private static int NormalizeMargin(int value)
        => value == int.MinValue ? 0 : Math.Clamp(value, SmuReader.CoMin, SmuReader.CoMax);

    private int[] Snapshot() => CoreMargins.Select(c => c.PayloadValue()).ToArray();

    private void AcceptCurrentAsBaseline()
    {
        _baselineMargins = Snapshot();
        _baselineFMax = FMaxMHz;
        IsDirty = false;
    }

    private void MarkDirty()
    {
        if (FreezeDirty || _suppressDirty || !_ready)
        {
            return;
        }

        IsDirty = FMaxMHz != _baselineFMax || !Snapshot().SequenceEqual(_baselineMargins);
    }

    private void ClearBlocking()
    {
        WorkerConnected = true;
        SmuAvailable = true;
        BlockingTitle = "SMU 接口不可用";
        BlockingDetail = SmuUnavailableHint;
    }

    private void MarkSmuUnavailable(string? reason)
    {
        WorkerConnected = true;
        SmuAvailable = false;
        BlockingTitle = "SMU 接口不可用";
        BlockingDetail = string.IsNullOrWhiteSpace(reason) ? SmuUnavailableHint : reason!;
    }

    private void MarkWorkerUnreachable(string detail)
    {
        WorkerConnected = false;
        SmuAvailable = false;
        BlockingTitle = "后台服务未连接";
        BlockingDetail = detail;
    }

    private static void NotifyIf(bool condition, string message)
    {
        if (condition)
        {
            ToastService.Instance.Show(message);
        }
    }

    /// <summary>把 FMax 输入框的文本纠回当前值（解析失败 / 被夹取时）。</summary>
    private void SyncFMaxText()
    {
        string text = FMaxMHz.ToString(CultureInfo.InvariantCulture);

        if (string.Equals(_fMaxText, text, StringComparison.Ordinal))
        {
            return;
        }

        _fMaxText = text;
        OnPropertyChanged(nameof(FMaxText));
    }

    partial void OnFMaxMHzChanged(int value)
    {
        SyncFMaxText();
        MarkDirty();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOperate));
        ReadbackCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();

        if (!value && _recheckProfileOnIdle)
        {
            _recheckProfileOnIdle = false;
            OnCurrentProfileChanged(this, EventArgs.Empty);
        }
    }

    partial void OnIsDirtyChanged(bool value)
    {
        ApplyCommand.NotifyCanExecuteChanged();

        // 方案面板靠它给当前行贴「未保存」并拦住切换。
        PlanHub.Tuning.SetUnsaved(value);
    }

    partial void OnSmuAvailableChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    /// <summary>没有存档时的占位值</summary>
    private static int[] BuildSeed(int coreCount)
    {
        int[] template = { -18, -22, -17, -25, -26, -24, -29, -33, -13, -20, -13, -20, -11, -17, -14, -17 };

        int[] seed = new int[Math.Max(0, coreCount)];

        for (int i = 0; i < seed.Length; i++)
        {
            seed[i] = template[i % template.Length];
        }

        return seed;
    }
}

/// <summary>单个核心的 CO 偏移项</summary>
public partial class CoreMarginItem : ObservableObject
{
    /// <summary>该核读不到值时的占位字符（与数字等宽）。</summary>
    private const string UnavailableText = "—";

    private readonly int _minimum;
    private readonly int _maximum;
    private readonly int _step;

    private int _margin;
    private string _editText;
    private bool _isUnavailable;

    public int Index { get; }

    public string DisplayName => $"核心 {Index}";

    /// <summary>该核的值读不到（读取失败 / 被禁用）</summary>
    public bool IsUnavailable
    {
        get => _isUnavailable;
        private set
        {
            if (!SetProperty(ref _isUnavailable, value))
            {
                return;
            }

            OnPropertyChanged(nameof(EditText));
            OnPropertyChanged(nameof(IsReadOnly));
        }
    }

    public bool IsReadOnly => IsUnavailable;

    /// <summary>该核的偏移值</summary>
    public int Margin
    {
        get => _margin;
        set
        {
            if (IsUnavailable)
            {
                return;
            }

            int clamped = Math.Clamp(value, _minimum, _maximum);

            if (!SetProperty(ref _margin, clamped))
            {
                return;
            }

            SyncEditText();
        }
    }

    /// <summary>输入框的文本</summary>
    public string EditText
    {
        get => IsUnavailable ? UnavailableText : _editText;
        set
        {
            if (IsUnavailable)
            {
                return;
            }

            if (!SetProperty(ref _editText, value))
            {
                return;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                // 中间态（空串 / 单个负号 / 乱敲）：不提交，等下一次输入。
                return;
            }

            Margin = parsed;
        }
    }

    public CoreMarginItem(int index, int margin, int minimum, int maximum, int step)
    {
        Index = index;
        _minimum = minimum;
        _maximum = maximum;
        _step = step;
        _margin = Math.Clamp(margin, minimum, maximum);
        _editText = _margin.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>标记该核读不到值。只有下一次回读到真实值才能恢复。</summary>
    public void MarkUnavailable() => IsUnavailable = true;

    /// <summary>按快照里的原始值恢复（<c>int.MinValue</c> 即不可用）。</summary>
    public void Restore(int value)
    {
        if (value == int.MinValue)
        {
            MarkUnavailable();
            return;
        }

        IsUnavailable = false;
        _margin = Math.Clamp(value, _minimum, _maximum);

        OnPropertyChanged(nameof(Margin));
        SyncEditText();
    }

    /// <summary>下发时用的值：不可用的核回 <c>int.MinValue</c>，Worker 会跳过它。</summary>
    public int PayloadValue() => IsUnavailable ? int.MinValue : _margin;

    [RelayCommand]
    private void Increment() => Margin += _step;

    [RelayCommand]
    private void Decrement() => Margin -= _step;

    /// <summary>值被外部改动（步进 / 取消 / 快照）时把框里的字跟上。</summary>
    private void SyncEditText()
    {
        string text = _margin.ToString(CultureInfo.InvariantCulture);

        if (string.Equals(_editText, text, StringComparison.Ordinal))
        {
            return;
        }

        _editText = text;
        OnPropertyChanged(nameof(EditText));
    }
}
