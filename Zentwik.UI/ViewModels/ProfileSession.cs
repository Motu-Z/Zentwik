using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>
/// 三类方案各自的会话状态：列表、当前选中、增删改名、应用。
/// </summary>
/// <remarks>
/// 用户的抱怨是「改一个方案，三页全跟着变」。一个全局「当前方案」必然导致这种耦合：
/// 切调校方案就等于换了进程页的方案条。三类各自持有一份列表与选中项之后，
/// 三页互不相干，场景规则引用的调校方案 Id 也不受另外两类影响。
///
/// UI 这份只是展示缓存。任何增删改都走 IPC，成功后重拉该类的列表 ——
/// 本地先改再等回包，会出现「界面已经改名，盘上还是旧名」的中间态。
/// </remarks>
public sealed class PlanScope : ViewModelBase
{
    private const int ListTimeoutMs = 3_000;

    private PlanItem? _current;

    private bool _isCreating;
    private string _newName = string.Empty;

    internal PlanScope(PlanKind kind)
    {
        Kind = kind;
        (Label, EmptyText) = kind switch
        {
            PlanKind.Tuning => ("调校方案", "未选择调校方案"),
            PlanKind.Scheduling => ("调度方案", "未选择调度方案"),
            _ => ("电源方案", "未选择电源方案"),
        };

        OpenOverlayCommand = new RelayCommand(_ => OpenOverlay());
        CloseOverlayCommand = new RelayCommand(_ => { IsOverlayOpen = false; });
        SelectCommand = new RelayCommand(p => { if (p is PlanItem item) Select(item); });
        BeginCreateCommand = new RelayCommand(_ => BeginCreate());
        CommitCreateCommand = new RelayCommand(_ => Fire(CommitCreateAsync));
        CancelCreateCommand = new RelayCommand(_ => CancelCreate());
        StartRenameCommand = new RelayCommand(p => BeginRename(p as PlanItem));
        CommitRenameCommand = new RelayCommand(p => { if (p is PlanItem item) Fire(() => CommitRenameAsync(item)); });
        CancelRenameCommand = new RelayCommand(p => { if (p is PlanItem item) item.IsRenaming = false; });
        AskDeleteCommand = new RelayCommand(p => AskDelete(p as PlanItem));
        ConfirmDeleteCommand = new RelayCommand(p => { if (p is PlanItem item) Fire(() => DeleteAsync(item)); });
        CancelDeleteCommand = new RelayCommand(p => { if (p is PlanItem item) item.IsConfirmingDelete = false; });
        ApplyCommand = new RelayCommand(p => Fire(() => ApplyAsync(p as PlanItem ?? Current)));
    }

    public PlanKind Kind { get; }

    /// <summary>「调校方案」/「调度方案」/「电源方案」。界面文案统一从这里取。</summary>
    public string Label { get; }

    /// <summary>没有任何方案可选时顶栏显示的字样。</summary>
    public string EmptyText { get; }

    public ObservableCollection<PlanItem> Items { get; } = new();

    /// <summary>当前选中的方案。换人时触发 <see cref="CurrentChanged"/>。</summary>
    public PlanItem? Current
    {
        get => _current;
        private set
        {
            if (ReferenceEquals(_current, value))
                return;

            if (_current is not null)
            {
                _current.PropertyChanged -= OnCurrentItemChanged;
                _current.IsCurrent = false;
            }

            if (!SetProperty(ref _current, value))
                return;

            if (value is not null)
            {
                value.PropertyChanged += OnCurrentItemChanged;
                value.IsCurrent = true;
            }

            OnPropertyChanged(nameof(CurrentName));
            OnPropertyChanged(nameof(HasCurrent));
            OnPropertyChanged(nameof(ApplyLabel));
            OnPropertyChanged(nameof(CanApplyCurrent));
            CurrentChanged?.Invoke(this, EventArgs.Empty);

            // 记住「用户看的是哪一份」：重启后要恢复的是这个选择，而不是把选中
            // 强行拉回生效方案 —— 那在用户眼里就是「我的设置被改了」。
            if (value is not null && !value.IsLocal && !PlanHub.SuppressWorkerInit)
                UiStateStore.SetSelectedPlanId(KindKey, value.Id);
        }
    }

    private string KindKey => Kind.ToString();

    /// <summary>当前方案的属性变了（改名 / 生效标记 / 摘要）：底部按钮与摘要要跟着走。</summary>
    private void OnCurrentItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanItem.Name))
            OnPropertyChanged(nameof(ApplyLabel));

        if (e.PropertyName == nameof(PlanItem.IsActive))
            OnPropertyChanged(nameof(CanApplyCurrent));
    }

    /// <summary>当前方案换人了。调校页靠它把界面切到那一份。</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>
    /// 方案应用成功（系统状态已变）。电源页靠它重读系统方案与五项参数。
    /// </summary>
    /// <remarks>
    /// 不能用 <see cref="CurrentChanged"/> 顶替：列表重拉时选中项会被复用，
    /// 应用一份方案并不换人，那个事件根本不会触发 —— 系统计划已经切走了，
    /// 界面却还高亮着原来那张卡。
    /// </remarks>
    public event EventHandler? Applied;

    public string CurrentName => Current?.Name ?? EmptyText;

    public bool HasCurrent => Current is not null;

    private bool _hasUnsavedChanges;

    /// <summary>
    /// 页面上有改动还没落盘。
    /// </summary>
    /// <remarks>
    /// 由页面把各自的脏标记推过来（调校 / 电源页各有一个）。方案面板据此
    /// 给当前行贴「未保存」，并在用户点别的方案时拦住切换 ——
    /// 否则切换会按新方案重载页面值，那些没保存的改动就无声消失了。
    /// 调度页是即时写盘的，永远是 false。
    /// </remarks>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    /// <summary>页面同步脏标记。</summary>
    public void SetUnsaved(bool value) => HasUnsavedChanges = value;

    /// <summary>面板底部应用按钮的文案，带上方案名 —— 点哪个、应用哪个，一眼对上。</summary>
    public string ApplyLabel => Current is null ? "应用" : $"应用「{Current.Name}」";

    /// <summary>当前方案正在生效时，应用按钮没有意义。</summary>
    public bool CanApplyCurrent => Current is not null && !Current.IsActive;

    /// <summary>列表是否已与 Worker 对齐。false 时可能只有本地占位。</summary>
    public bool IsSynced { get; private set; }

    // ==================== 面板 / 内联新建 ====================

    private bool _isOverlayOpen;

    public bool IsOverlayOpen
    {
        get => _isOverlayOpen;
        set
        {
            bool wasOpen = _isOverlayOpen;

            if (SetProperty(ref _isOverlayOpen, value) || (wasOpen && !value))
            {
                foreach (PlanItem item in Items)
                {
                    item.IsRenaming = false;
                    item.IsConfirmingDelete = false;
                }

                // 面板关掉时把没提交的新建输入行一起收掉：
                // 留着它，下次打开会看到一个上回没打完的名字。
                IsCreating = false;
            }
        }
    }

    /// <summary>
    /// 正在内联输入新方案名。
    /// </summary>
    /// <remarks>
    /// 不用弹窗：改名只需要一个字段，为它打断整个界面不值。
    /// 新建走同一套 —— 点「+ 新建」就地长出一行输入框，回车即建。
    /// </remarks>
    public bool IsCreating
    {
        get => _isCreating;
        set
        {
            if (!SetProperty(ref _isCreating, value))
                return;

            if (!value)
                NewName = string.Empty;
        }
    }

    public string NewName
    {
        get => _newName;
        set
        {
            if (SetProperty(ref _newName, value))
                InputError = string.Empty;
        }
    }

    private string _inputError = string.Empty;

    /// <summary>
    /// 命名输入框的内联错误文案，空串表示无错。
    /// 只驱动红边与 ToolTip，不参与保存/校验的数据流；Toast 仍照旧弹。
    /// </summary>
    public string InputError
    {
        get => _inputError;
        private set => SetProperty(ref _inputError, value ?? string.Empty);
    }

    public RelayCommand OpenOverlayCommand { get; }
    public RelayCommand CloseOverlayCommand { get; }
    public RelayCommand SelectCommand { get; }
    public RelayCommand BeginCreateCommand { get; }
    public RelayCommand CommitCreateCommand { get; }
    public RelayCommand CancelCreateCommand { get; }
    public RelayCommand StartRenameCommand { get; }
    public RelayCommand CommitRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand AskDeleteCommand { get; }
    public RelayCommand ConfirmDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public RelayCommand ApplyCommand { get; }

    /// <summary>
    /// 由页面自己完成的新建，返回新方案 Id；null 表示失败（已给过回执）。
    /// </summary>
    /// <remarks>
    /// 电源方案不能空白新建：它存的是「参数要写到哪份 Windows 方案上」，
    /// 没有真实方案可挂时后台会直接拒掉。电源页据此把「新建」变成
    /// 「派生一份系统方案 + 登记成方案」，其余两页不需要这个钩子。
    /// </remarks>
    public Func<string, Task<string?>>? CreateHandler { get; set; }

    // ==================== 与 Worker 对齐 ====================

    /// <summary>拉这一类方案的列表。失败保留当前内容，不弹提示。</summary>
    public async Task InitializeAsync()
    {
        EnsurePlaceholder();

        if (IsSynced)
            return;

        await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);
    }

    /// <summary>
    /// 诊断模式下的占位方案。
    /// </summary>
    /// <remarks>
    /// 截图与自检不打 IPC，列表会是空的 —— 顶栏显示「未选择调校方案」，
    /// 页面上一片空白，既不反映真实布局也拍不出问题。
    /// 占位只在本类里造，不写盘、不影响真实运行。
    /// </remarks>
    public void EnsurePlaceholder()
    {
        if (!PlanHub.SuppressWorkerInit || Items.Count > 0)
            return;

        string name = Kind switch
        {
            PlanKind.Tuning => "默认调校",
            PlanKind.Scheduling => "默认调度",
            _ => "当前电源方案",
        };

        PlanItem item = new("local-placeholder", name, true) { IsLocal = true };

        switch (Kind)
        {
            case PlanKind.Tuning:
                item.CoreMargins = new[] { -18, -22, -17, -25, -26, -24, -29, -33, -13, -20, -13, -20, -11, -17, -14, -17 };
                item.FMaxMHz = 4750;
                break;

            case PlanKind.Scheduling:
                item.RuleCount = 3;
                break;

            default:
                item.PowerSchemeName = "平衡";
                break;
        }

        Items.Add(item);
        item.Summary = DescribePlan(Kind, item);
        Current = item;
    }

    /// <summary>重拉列表。任何增删改成功后都要调一次，以 Worker 的回值为准。</summary>
    public async Task RefreshAsync(bool notifyOnFailure = true)
    {
        if (PlanHub.SuppressWorkerInit)
        {
            IsSynced = true;
            return;
        }

        try
        {
            IpcOutcome<PlanListPayload> outcome = await WorkerBridge.Instance
                .QueryPlansAsync(Kind, ListTimeoutMs)
                .ConfigureAwait(true);

            if (outcome.TransportFailed)
            {
                Notify(notifyOnFailure, "后台服务未连接，方案列表未能更新");
                return;
            }

            if (!outcome.Success || outcome.Value is null)
            {
                Notify(notifyOnFailure, string.IsNullOrEmpty(outcome.Message) ? "读取方案列表失败" : outcome.Message);
                return;
            }

            ApplyPayload(outcome.Value);
            IsSynced = true;
        }
        catch (Exception ex)
        {
            Notify(notifyOnFailure, ex.Message);
        }
    }

    private void ApplyPayload(PlanListPayload payload)
    {
        PlanItem? keep = Current;

        Items.Clear();

        foreach (PlanEntryPayload entry in payload.Plans ?? new List<PlanEntryPayload>())
        {
            // 选中项还在就复用原实例。重建一个「同 Id 的新对象」会让 Current 的
            // setter 触发 CurrentChanged，调校页据此重新加载值 —— 用户还没保存的
            // 逐核负压会被一次「打开浮层刷列表」冲掉。场景页同理才绕开了共享集合。
            PlanItem item = keep is not null
                            && string.Equals(keep.Id, entry.Id, StringComparison.Ordinal)
                ? keep
                : new PlanItem(entry.Id, entry.Name, entry.IsActive);

            item.Name = entry.Name;
            item.IsActive = entry.IsActive;
            item.CoreMargins = entry.CoreMargins ?? Array.Empty<int>();
            item.FMaxMHz = entry.FMaxMHz;
            item.RuleCount = entry.RuleCount;
            item.PowerSchemeGuid = entry.PowerSchemeGuid;
            item.PowerSchemeName = entry.PowerSchemeName;
            item.PowerPolicy = entry.PowerPolicy;
            item.Summary = DescribePlan(Kind, item);

            Items.Add(item);
        }

        // 优先保持用户当前看的那一份；接着是上次选的那份（重启后恢复界面选择）；
        // 都没有才退到后台的生效方案，最后才是列表第一项。
        string? remembered = PlanHub.SuppressWorkerInit ? null : UiStateStore.SelectedPlanId(KindKey);

        PlanItem? next = Items.FirstOrDefault(p => p.Id == keep?.Id)
                         ?? Items.FirstOrDefault(p => p.Id == remembered)
                         ?? Items.FirstOrDefault(p => p.Id == payload.ActiveId)
                         ?? Items.FirstOrDefault(p => p.IsActive)
                         ?? Items.FirstOrDefault();

        if (!ReferenceEquals(Current, next))
            Current = next;
        else
            OnPropertyChanged(nameof(CanApplyCurrent));

        OnPropertyChanged(nameof(ApplyLabel));
    }

    /// <summary>
    /// 一行方案的参数摘要。
    /// </summary>
    /// <remarks>
    /// 列表里只有名字时，两个「新调校」在用户眼里完全一样 —— 摘要就是
    /// 让人不点进去也知道这份方案调了多少。数值都来自平台已带回的字段，
    /// 不额外打 IPC。
    /// </remarks>
    private static string DescribePlan(PlanKind kind, PlanItem item)
    {
        switch (kind)
        {
            case PlanKind.Tuning:
            {
                // int.MinValue 表示该核读失败 / 被禁用，不参与区间，也不算进核数。
                int[] usable = item.CoreMargins.Where(v => v != int.MinValue).ToArray();
                string fMax = $"{item.FMaxMHz} MHz";

                if (usable.Length == 0)
                    return $"未设置负压 · {fMax}";

                int min = usable.Min();
                int max = usable.Max();
                string co = min == max ? $"{min}" : $"{min}~{max}";
                return $"{usable.Length} 核 · CO {co} · {fMax}";
            }

            case PlanKind.Scheduling:
                return item.RuleCount > 0 ? $"{item.RuleCount} 条规则" : "未设置规则";

            default:
            {
                string scheme = string.IsNullOrWhiteSpace(item.PowerSchemeName)
                    ? "未指定系统方案"
                    : item.PowerSchemeName;

                // 方案现在自带参数快照，摘要要能看出「这份存了参数」而不只是
                // 指向哪份系统计划 —— 同一个计划下可以有多套参数。
                int count = CountPowerValues(item.PowerPolicy);
                return count > 0 ? $"{scheme} · {count} 项参数" : scheme;
            }
        }
    }

    /// <summary>只数 AC 那五项：界面摘要表达的是「这套策略调了几项」，DC 不重复计。</summary>
    private static int CountPowerValues(ApplyPowerPolicyPayload? policy)
    {
        if (policy is null)
            return 0;

        int count = 0;
        if (policy.Epp is not null) count++;
        if (policy.BoostMode is not null) count++;
        if (policy.MaxProcessorState is not null) count++;
        if (policy.Aspm is not null) count++;
        if (policy.IdleDisable is not null) count++;
        return count;
    }

    /// <summary>
    /// 不重拉列表，只按本地最新值重算当前行的摘要。
    /// 页面应用完参数后调它：重拉列表会 <c>Items.Clear()</c>，把编辑状态冲掉。
    /// </summary>
    public void UpdateCurrentSummary()
    {
        if (Current is not null)
            Current.Summary = DescribePlan(Kind, Current);
    }

    // ==================== 增删改 ====================

    private void OpenOverlay()
    {
        IsOverlayOpen = true;

        // 每次打开都刷一次：别的页刚存过方案时，这里得马上看到。
        Fire(() => RefreshAsync(notifyOnFailure: false));
    }

    private void Select(PlanItem item)
    {
        if (ReferenceEquals(Current, item))
            return;

        // 有没落盘的改动时不能直接换人：换方案会按新方案重载页面值，那些改动
        // 一条都不会提示就没了。
        //
        // 早先这里只弹一句「请先应用或放弃」就 return。Toast 一闪而过，用户以为
        // 已经切过去了，接着点「应用参数」—— 结果值全落进原来那份方案里。
        // 表现出来就是「我明明在调校 1 上改，重启后却是调校 2」。
        if (HasUnsavedChanges)
        {
            PlanItem? previous = Current;

            // 方案面板是 Popup，属于独立窗口，永远盖在窗口级叠层之上。
            // 不先收起，用户只会看到「点了没反应」。
            IsOverlayOpen = false;

            ConfirmService.Instance.Request(
                $"「{previous?.Name}」有未保存的改动",
                new[]
                {
                    $"切到「{item.Name}」会丢弃这些改动",
                    "想保留就先点「应用参数」，再回来切",
                },
                () => Current = item,
                "丢弃并切换");

            return;
        }

        Current = item;

        // 选中只换「编辑对象」，不碰硬件 —— 下发必须显式点「应用」。
        // 用户抱怨过「一改全改」：选中就下发会让切列表的动作顺手改了机器状态。
    }

    private void BeginCreate()
    {
        foreach (PlanItem item in Items)
        {
            item.IsRenaming = false;
            item.IsConfirmingDelete = false;
        }

        IsCreating = true;
        NewName = NextDefaultName();
    }

    private void CancelCreate() => IsCreating = false;

    /// <summary>默认名带类别：三页都叫「新方案 1」时，用户分不清刚建的是哪一类。</summary>
    private string NextDefaultName()
    {
        string baseName = Kind switch
        {
            PlanKind.Tuning => "新调校",
            PlanKind.Scheduling => "新调度",
            _ => "新电源",
        };

        int index = 1;

        while (Items.Any(p => string.Equals(p.Name, $"{baseName} {index}", StringComparison.Ordinal)))
            index++;

        return $"{baseName} {index}";
    }

    private async Task CommitCreateAsync()
    {
        string name = NewName.Trim();

        if (name.Length == 0)
            return;

        if (Items.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            InputError = $"已存在同名{Label}：{name}";
            ToastService.Instance.Show(InputError);
            return;
        }

        IsCreating = false;

        if (PlanHub.SuppressWorkerInit)
        {
            AddLocal(name);
            return;
        }

        // 有派生要求的类（电源）先在系统里造出载体，再回来登记方案。
        if (CreateHandler is not null)
        {
            string? createdId = await RunCreateHandlerAsync(name).ConfigureAwait(true);

            if (createdId is null)
                return;

            await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);

            PlanItem? newItem = Items.FirstOrDefault(p => p.Id == createdId)
                                ?? Items.FirstOrDefault(p => p.Name == name);

            if (newItem is not null)
                Current = newItem;

            return;
        }

        try
        {
            IpcOutcome<string> outcome = await WorkerBridge.Instance
                .SavePlanAsync(Kind, null, name)
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                // 把输入还给用户：名字已经打好了，后台失败不该让他重打一遍。
                // 校验通过后先收起输入行是为了防住 IPC 期间的重复提交。
                IsCreating = true;
                NewName = name;
                InputError = string.IsNullOrEmpty(outcome.Message) ? "新建失败" : outcome.Message;

                if (!outcome.TransportFailed)
                    ToastService.Instance.Show(InputError);

                return;
            }

            await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);

            PlanItem? created = Items.FirstOrDefault(p => p.Id == outcome.Value)
                                 ?? Items.FirstOrDefault(p => p.Name == name);

            if (created is not null)
                Current = created;
        }
        catch (Exception ex)
        {
            IsCreating = true;
            NewName = name;
            InputError = ex.Message;
            ToastService.Instance.Show(ex.Message);
        }
    }

    /// <summary>页面的新建钩子：失败把名字还给输入行，异常也当失败。</summary>
    private async Task<string?> RunCreateHandlerAsync(string name)
    {
        try
        {
            string? createdId = await CreateHandler!(name).ConfigureAwait(true);

            if (createdId is not null)
                return createdId;

            IsCreating = true;
            NewName = name;
            return null;
        }
        catch (Exception ex)
        {
            IsCreating = true;
            NewName = name;
            InputError = ex.Message;
            ToastService.Instance.Show(ex.Message);
            return null;
        }
    }

    private void AddLocal(string name)
    {
        PlanItem item = new("local-" + Guid.NewGuid().ToString("N")[..8], name, false) { IsLocal = true };
        Items.Add(item);
        Current = item;
    }

    private void BeginRename(PlanItem? item)
    {
        if (item is null)
            return;

        foreach (PlanItem other in Items)
            other.IsRenaming = false;

        item.RenameText = item.Name;
        item.IsRenaming = true;
    }

    private async Task CommitRenameAsync(PlanItem item)
    {
        // 内联输入框绑的是 RenameText（TwoWay），Name 在提交前仍是旧名。
        // 从 Name 取值会让「改名」变成「把旧名再写一遍」，用户看到没反应。
        string name = item.RenameText;

        if (string.IsNullOrWhiteSpace(name))
        {
            // 空名直接当取消：留一个没有名字的方案在列表里毫无意义。
            item.IsRenaming = false;
            return;
        }

        name = name.Trim();

        // 与新建同一道校验（排除自己）。Worker 也会拒，但等回包再回滚会让名字
        // 先跳一下再跳回来，不如就地挡住、输入框原样留着让用户改一个字。
        if (Items.Any(p => !ReferenceEquals(p, item)
                        && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            InputError = $"已存在同名{Label}：{name}";
            ToastService.Instance.Show(InputError);
            return;
        }

        InputError = string.Empty;
        item.Name = name;
        item.IsRenaming = false;

        if (PlanHub.SuppressWorkerInit || item.IsLocal)
            return;

        try
        {
            IpcOutcome<string> outcome = await WorkerBridge.Instance
                .RenamePlanAsync(Kind, item.Id, item.Name)
                .ConfigureAwait(true);

            if (outcome.Success)
                return;

            // 失败要把名字改回去：界面显示的和盘上不一致比改不动更糟。
            await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);

            if (!outcome.TransportFailed)
            {
                RestoreRenameState(item.Id, name);
                InputError = string.IsNullOrEmpty(outcome.Message) ? "改名未生效" : outcome.Message;
                ToastService.Instance.Show(InputError);
            }
        }
        catch (Exception ex)
        {
            await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);
            RestoreRenameState(item.Id, name);
            InputError = ex.Message;
            ToastService.Instance.Show(ex.Message);
        }
    }

    /// <summary>改名没落盘时，把输入框按 Id 找回来（列表可能刚被重拉过）。</summary>
    private void RestoreRenameState(string id, string typedName)
    {
        PlanItem? restored = Items.FirstOrDefault(
            p => string.Equals(p.Id, id, StringComparison.Ordinal));

        if (restored is null)
            return;

        restored.RenameText = typedName;
        restored.IsRenaming = true;
    }

    private void AskDelete(PlanItem? item)
    {
        if (item is null)
            return;

        foreach (PlanItem other in Items)
        {
            other.IsRenaming = false;
            other.IsConfirmingDelete = false;
        }

        item.IsConfirmingDelete = true;
    }

    private async Task DeleteAsync(PlanItem item)
    {
        item.IsConfirmingDelete = false;

        if (PlanHub.SuppressWorkerInit || item.IsLocal)
        {
            Items.Remove(item);

            if (ReferenceEquals(Current, item))
                Current = Items.FirstOrDefault();

            return;
        }

        try
        {
            IpcOutcome<string> outcome = await WorkerBridge.Instance
                .DeletePlanAsync(Kind, item.Id)
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                if (!outcome.TransportFailed)
                    ToastService.Instance.ShowError(string.IsNullOrEmpty(outcome.Message) ? "删除失败" : outcome.Message);

                return;
            }

            // 删除会顺带解除场景规则里对它的引用，而场景页不在当前页面：
            // 不提示就等于悄悄改了用户没看见的那部分数据。
            if (!string.IsNullOrEmpty(outcome.Message)
                && outcome.Message.Contains("解除引用", StringComparison.Ordinal))
            {
                ToastService.Instance.Show(outcome.Message);
            }

            await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show(ex.Message);
        }
    }

    // ==================== 应用 / 保存 ====================

    /// <summary>
    /// 应用一份方案：只下发它自己那一块。
    /// </summary>
    /// <remarks>
    /// 这是三页顶栏「应用」按钮的唯一出口。失败一定给回执 ——
    /// 应用是不可见动作，点下去没反应用户会以为按钮坏了。
    /// </remarks>
    public async Task ApplyAsync(PlanItem? item)
    {
        if (item is null)
            return;

        try
        {
            IpcOutcome<string> outcome = await WorkerBridge.Instance
                .ApplyPlanAsync(Kind, item.Id)
                .ConfigureAwait(true);

            if (outcome.TransportFailed)
            {
                ToastService.Instance.ShowError("后台服务未连接，未能应用");
                return;
            }

            if (!outcome.Success)
            {
                ToastService.Instance.ShowError(string.IsNullOrEmpty(outcome.Message) ? "应用失败" : outcome.Message);
                return;
            }

            // 生效标记以 Worker 为准，重拉列表。
            await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);

            // 列表刷新只改「谁生效」，页面上那份系统状态还是旧的。
            // 电源方案会顺手切走系统计划，必须让页面重读一次，否则卡片高亮
            // 与五项参数全停在上一份上，看起来像「点了应用什么都没发生」。
            Applied?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show(ex.Message);
        }
    }

    /// <summary>
    /// 把界面上正在编辑的值存进当前方案（没有当前方案时新建一份）。
    /// </summary>
    /// <returns>保存成功后的方案；失败返回 null。</returns>
    /// <remarks>
    /// 「保存」与「应用」是两件事：保存只写盘，应用才下发。
    /// 调校页把两者合成一个动作（用户点「应用参数」就是要它生效），
    /// 但调度与电源页必须先保存成方案再应用 —— 否则下次切回来就丢了。
    /// </remarks>
    public async Task<PlanItem?> SaveAsync(
        int[]? coreMargins = null,
        uint? fMaxMHz = null,
        string? powerSchemeGuid = null,
        string? name = null)
    {
        if (PlanHub.SuppressWorkerInit)
            return Current;

        PlanItem? target = Current;
        string planName = string.IsNullOrWhiteSpace(name) ? target?.Name ?? NextDefaultName() : name.Trim();

        try
        {
            IpcOutcome<string> outcome = await WorkerBridge.Instance
                .SavePlanAsync(Kind, target?.Id, planName, coreMargins, fMaxMHz, powerSchemeGuid)
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                if (!outcome.TransportFailed)
                    ToastService.Instance.ShowError(string.IsNullOrEmpty(outcome.Message) ? "保存失败" : outcome.Message);

                return null;
            }

            await RefreshAsync(notifyOnFailure: false).ConfigureAwait(true);

            return Items.FirstOrDefault(p => p.Id == outcome.Value) ?? Current;
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show(ex.Message);
            return null;
        }
    }

    /// <summary>把刚下发的调校值写回内存里的那一份，免得切走再切回来看到旧值。</summary>
    public void RememberTuning(int[] margins, uint fMaxMHz)
    {
        if (Current is null)
            return;

        Current.CoreMargins = margins;
        Current.FMaxMHz = fMaxMHz;

        // 摘要读的就是这两个字段，不刷新会一直显示旧的核数与区间。
        Current.Summary = DescribePlan(Kind, Current);
    }

    private static void Notify(bool condition, string message)
    {
        if (condition)
            ToastService.Instance.Show(message);
    }

    private static void Fire(Func<Task> action) => AsyncRunner.Fire(action);
}

/// <summary>三类方案的登记处。页面按类型取自己那一个。</summary>
public static class PlanHub
{
    public static PlanScope Tuning { get; } = new(PlanKind.Tuning);

    public static PlanScope Scheduling { get; } = new(PlanKind.Scheduling);

    public static PlanScope Power { get; } = new(PlanKind.Power);

    /// <summary>诊断模式（截图 / 自检）下不打 IPC，只摆占位数据。</summary>
    public static bool SuppressWorkerInit { get; set; }

    public static PlanScope Of(PlanKind kind) => kind switch
    {
        PlanKind.Tuning => Tuning,
        PlanKind.Scheduling => Scheduling,
        _ => Power
    };

    public static async Task InitializeAllAsync()
    {
        if (SuppressWorkerInit)
            return;

        await Task.WhenAll(
            Tuning.InitializeAsync(),
            Scheduling.InitializeAsync(),
            Power.InitializeAsync()).ConfigureAwait(true);
    }
}

/// <summary>列表里的一份方案。</summary>
public sealed class PlanItem : ViewModelBase
{
    private string _name;
    private bool _isActive;
    private bool _isRenaming;
    private bool _isConfirmingDelete;
    private string _renameText = string.Empty;

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    private bool _isCurrent;

    /// <summary>这一行是面板里当前选中（正在查看 / 编辑）的那份，不等于已生效。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    private string _summary = string.Empty;

    /// <summary>行内的参数摘要：核数 / 负压区间 / FMax、规则条数、系统电源方案。</summary>
    public string Summary
    {
        get => _summary;
        internal set => SetProperty(ref _summary, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (SetProperty(ref _isRenaming, value))
                OnPropertyChanged(nameof(IsEditing));
        }
    }

    public string RenameText
    {
        get => _renameText;
        set => SetProperty(ref _renameText, value);
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set
        {
            if (SetProperty(ref _isConfirmingDelete, value))
                OnPropertyChanged(nameof(IsEditing));
        }
    }

    public bool IsEditing => IsRenaming || IsConfirmingDelete;

    /// <summary>本地占位方案：Worker 里没有这一份，Id 不可用于写盘。</summary>
    public bool IsLocal { get; init; }

    /// <summary>这份方案存的逐核 CO；空数组 = 不碰 CO。</summary>
    public int[] CoreMargins { get; set; } = Array.Empty<int>();

    /// <summary>这份方案存的 FMax；0 = 不碰 FMax。</summary>
    public uint FMaxMHz { get; set; }

    /// <summary>调度方案的规则条数。</summary>
    public int RuleCount { get; set; }

    public string PowerSchemeGuid { get; set; } = string.Empty;

    public string PowerSchemeName { get; set; } = string.Empty;

    /// <summary>电源方案的五项参数快照；null = 这份方案只引用系统计划。</summary>
    public ApplyPowerPolicyPayload? PowerPolicy { get; set; }

    public PlanItem(string id, string name, bool isActive)
    {
        Id = id;
        _name = name;
        _isActive = isActive;
    }
}
