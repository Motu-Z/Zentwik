using System.Collections.ObjectModel;
using System.Globalization;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

public class ProcessViewModel : ViewModelBase
{
    /// <summary>
    /// 诊断模式（截图 / 自检 / 集成测试）下不联网后台服务。
    ///
    /// 这些模式会被反复拉起，每次都去拉一次进程表既有延迟又不稳定；
    /// 截图更要求「没有后台也能把页面画出来」。此时改用页内种子数据。
    /// </summary>
    public static bool SuppressWorkerInit { get; set; }

    public static int BulkSeedCount { get; set; }

    private bool UseWorker => !SuppressWorkerInit;

    /// <summary>每 CCD 物理核数，来自 <c>query-cpu-topology</c>；诊断模式下沿用开发机形状。</summary>
    private int _coresPerCcd = 8;

    public ObservableCollection<RunningProcessItem> RunningProcesses { get; } = new();
    public ObservableCollection<RunningProcessItem> FilteredProcesses { get; } = new();
    public ObservableCollection<ScheduleRuleItem> SavedRules { get; } = new();
    public ObservableCollection<MaskOption> MaskOptions { get; } = new();
    public ObservableCollection<string> PriorityOptions { get; } = new();

    /// <summary>抽屉「自定义掩码」页签里展示的条目：可用的核心组合一览。</summary>
    public ObservableCollection<MaskPreviewItem> DrawerMasks { get; } = new();

    // ==================== 右侧抽屉 ====================

    private bool _isDrawerOpen = true;

    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set
        {
            if (!SetProperty(ref _isDrawerOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsDrawerClosed));
            DrawerStateChanged?.Invoke(value);
        }
    }

    public bool IsDrawerClosed => !_isDrawerOpen;

    /// <summary>抽屉收放时通知视图做宽度动画。动画必须走 code-behind，触发器 Storyboard 会被截图拍到中间帧。</summary>
    public event Action<bool>? DrawerStateChanged;

    public RelayCommand ToggleDrawerCommand { get; }

    private string _drawerTab = "rules";

    public bool IsRulesTab
    {
        get => _drawerTab == "rules";
        set
        {
            if (!value || _drawerTab == "rules")
            {
                return;
            }

            _drawerTab = "rules";
            OnPropertyChanged(nameof(IsRulesTab));
            OnPropertyChanged(nameof(IsMasksTab));
        }
    }

    public bool IsMasksTab
    {
        get => _drawerTab == "masks";
        set
        {
            if (!value || _drawerTab == "masks")
            {
                return;
            }

            _drawerTab = "masks";
            OnPropertyChanged(nameof(IsRulesTab));
            OnPropertyChanged(nameof(IsMasksTab));
        }
    }


    private string _sortKey = "cpu";

    public string SortKey
    {
        get => _sortKey;
        private set
        {
            if (SetProperty(ref _sortKey, value))
            {
                OnPropertyChanged(nameof(SortDescending));
            }
        }
    }

    private bool _sortDescending = true;

    public bool SortDescending
    {
        get => _sortDescending;
        private set
        {
            if (SetProperty(ref _sortDescending, value))
            {
                OnPropertyChanged(nameof(SortDescending));
            }
        }
    }

    public RelayCommand SortCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyRuleCommand { get; }
    public RelayCommand ManageMaskCommand { get; }
    public RelayCommand ToggleRuleCommand { get; }
    public RelayCommand DeleteRuleCommand { get; }
    public RelayCommand ClearAllRulesCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ReleaseAllCommand { get; }

    private bool _showSystemProcesses;

    public bool ShowSystemProcesses
    {
        get => _showSystemProcesses;
        set
        {
            if (SetProperty(ref _showSystemProcesses, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    private int _selectedCount;

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetProperty(ref _selectedCount, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionText));
                ResetBatchPickers();
            }
        }
    }

    /// <summary>
    /// 批量下拉是一次性动作。换一批进程后必须回到空值，
    /// 否则再选同一个值 ComboBox 不会触发 setter，界面显示着旧值却什么也没下发。
    /// </summary>
    private void ResetBatchPickers()
    {
        if (_batchMaskKey is not null)
        {
            _batchMaskKey = null;
            OnPropertyChanged(nameof(BatchMaskKey));
        }

        if (_batchPriority is not null)
        {
            _batchPriority = null;
            OnPropertyChanged(nameof(BatchPriority));
        }
    }

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => "已选 " + SelectedCount.ToString(CultureInfo.InvariantCulture) + " 项";

    private string? _batchMaskKey;

    public string? BatchMaskKey
    {
        get => _batchMaskKey;
        set
        {
            if (!SetProperty(ref _batchMaskKey, value) || value is null)
            {
                return;
            }

            ApplyBatchToSelected(p => p.MaskKey = value);
        }
    }

    private string? _batchPriority;

    public string? BatchPriority
    {
        get => _batchPriority;
        set
        {
            if (!SetProperty(ref _batchPriority, value) || value is null)
            {
                return;
            }

            ApplyBatchToSelected(p => p.Priority = value);
        }
    }

    public ProcessViewModel()
    {
        // 档位文案由 Core 统一给：那边是「英文常量 ⇄ 中文文案」的唯一出处，
        // 界面自己再写一份，两边迟早对不上（表现为选了「高」却下发 Normal）。
        foreach (string option in ProcessPriority.Options)
        {
            PriorityOptions.Add(ProcessPriority.DisplayOf(option));
        }

        BuildBaseMaskOptions();

        SortCommand = new RelayCommand(p =>
        {
            if (p is not string key) return;

            if (string.Equals(SortKey, key, StringComparison.Ordinal))
            {
                SortDescending = !SortDescending;
            }
            else
            {
                SortKey = key;
                SortDescending = key == "cpu";
            }

            ApplyFilterAndSort();
        });

        RefreshCommand = new RelayCommand(_ =>
        {
            if (UseWorker)
            {
                Fire(RefreshAsync);
                return;
            }

            ApplyFilterAndSort();
        });
        ApplyRuleCommand = new RelayCommand(_ => AutoApply());
        ManageMaskCommand = new RelayCommand(_ => OpenMaskManager());
        ToggleDrawerCommand = new RelayCommand(_ => IsDrawerOpen = !IsDrawerOpen);

        // 「自定义掩码」页签是抽屉的常驻内容，管理器必须一开始就存在，
        // 否则页签绑定到 null、切过去是一片空白。数据等打开页签时再拉。
        MaskManager = new MaskManagerViewModel(this);

        ToggleRuleCommand = new RelayCommand(p =>
        {
            if (p is not ScheduleRuleItem rule)
            {
                return;
            }

            // PropertyChanged → OnTargetChanged → AutoApply 是唯一写入口。
            // 这里再 Fire 一次会让同一条规则被并发写两遍。
            rule.Enabled = !rule.Enabled;
        });

        DeleteRuleCommand = new RelayCommand(p =>
        {
            if (p is not ScheduleRuleItem rule)
            {
                return;
            }

            if (!UseWorker)
            {
                SavedRules.Remove(rule);
                return;
            }

            Fire(async () =>
            {
                IpcOutcome<string> outcome = await WorkerBridge.Instance
                    .DeleteScheduleRuleAsync(rule.Id).ConfigureAwait(true);

                if (!outcome.Success)
                {
                    ToastService.Instance.ShowError(outcome.Message);
                }

                await RefreshAsync().ConfigureAwait(true);
            });
        });

        ClearAllRulesCommand = new RelayCommand(_ =>
        {
            if (SavedRules.Count == 0)
            {
                return;
            }

            int count = SavedRules.Count;

            ConfirmService.Instance.Request(
                "清空所有规则",
                new[]
                {
                    $"删除当前 {count} 条已保存的调度规则",
                    "正在运行的进程不会因此改变",
                },
                () =>
                {
                    if (!UseWorker)
                    {
                        SavedRules.Clear();
                        return;
                    }

                    Fire(async () =>
                    {
                        // 后台没有「清空规则」这一条命令：clear-all-schedules 的语义是
                        // 解除 CPU Set 绑定，不是删规则。这里只能逐条删。
                        List<string> ids = SavedRules.Select(r => r.Id).ToList();

                        foreach (string id in ids)
                        {
                            await WorkerBridge.Instance.DeleteScheduleRuleAsync(id).ConfigureAwait(true);
                        }

                        await RefreshAsync().ConfigureAwait(true);
                    });
                },
                "清空");
        });

        SelectAllCommand = new RelayCommand(_ =>
        {
            bool select = !AreAllSelected;

            foreach (var item in FilteredProcesses)
            {
                item.IsSelected = select;
            }

            RefreshSelectionState();
        });

        ReleaseAllCommand = new RelayCommand(_ =>
        {
            int bound = RunningProcesses.Count(p => p.TargetType != "none");
            int activeRules = SavedRules.Count(r => r.Enabled);

            if (bound == 0 && activeRules == 0)
            {
                return;
            }

            ConfirmService.Instance.Request(
                "解除全部绑核",
                new[]
                {
                    $"把 {bound} 个已被限制的进程恢复成系统默认调度",
                    $"停用当前 {activeRules} 条启用的调度规则",
                    "调校与电源设置不受影响",
                },
                () =>
                {
                    if (!UseWorker)
                    {
                        ReleaseAllLocal();
                        return;
                    }

                    Fire(async () =>
                    {
                        // 两步缺一不可：clear-all-schedules 只把已经绑上的 CPU Set 恢复成
                        // 系统默认，规则还在盘上，后台下一轮重扫会立刻把它们重新绑回去。
                        IpcOutcome<string> cleared = await WorkerBridge.Instance
                            .ClearAllSchedulesAsync().ConfigureAwait(true);

                        _suppressSync = true;

                        try
                        {
                            foreach (ScheduleRuleItem rule in SavedRules.Where(r => r.Enabled).ToList())
                            {
                                // 后台不存「不调度」规则，那种目标类型本来就没有规则可停用。
                                if (rule.TargetType is not ("ccd" or "mask"))
                                {
                                    continue;
                                }

                                string? value = rule.TargetType == "ccd"
                                    ? "CCD" + rule.TargetValue
                                    : ResolveMaskId(rule);

                                if (value is null)
                                {
                                    continue;
                                }

                                rule.Enabled = false;

                                await WorkerBridge.Instance.SaveScheduleRuleAsync(
                                    new SaveScheduleRulePayload
                                    {
                                        Id = rule.Id,
                                        ProcessName = rule.ProcessName,
                                        TargetType = rule.TargetType,
                                        TargetValue = value,

                                        // 空串 = 不碰优先级，规则里原来的档位原样保留。
                                        Priority = string.Empty,
                                        Enabled = false
                                    }).ConfigureAwait(true);
                            }
                        }
                        finally
                        {
                            _suppressSync = false;
                        }

                        await RefreshAsync().ConfigureAwait(true);

                        if (!cleared.Success)
                        {
                            ToastService.Instance.ShowError(cleared.Message);
                        }
                    });
                },
                "解除");
        });

        // 诊断模式没有后台服务，用页内种子数据把版式撑起来；
        // 真实运行时一律等 InitializeAsync 拉回来的真数据。
        if (SuppressWorkerInit)
        {
            SeedMasks();
            SeedRunning();
            SeedRules();
        }
    }

    /// <summary>
    /// 诊断模式专用的自定义掩码。真实运行时这一条来自后台的 <c>masks.json</c>，
    /// 占位数据必须自己也有一条，否则引用它的规则会显示成空白下拉。
    /// </summary>
    private void SeedMasks()
    {
        const string value = "16711935";

        MaskOptions.Add(new MaskOption("0-7,16-23", "mask", value, maskId: "demo-mask"));
        DrawerMasks.Add(new MaskPreviewItem("0-7,16-23", MaskText.ToRangeText(16711935)));
    }

    /// <summary>诊断模式下的「解除全部绑核」：只改内存状态。</summary>
    private void ReleaseAllLocal()
    {
        _suppressSync = true;

        try
        {
            foreach (var proc in RunningProcesses)
            {
                proc.ResetAffinity();
                proc.IsDirty = false;
            }

            foreach (var rule in SavedRules)
            {
                rule.Enabled = false;
            }
        }
        finally
        {
            _suppressSync = false;
        }

        ApplyFilterAndSort();
    }

    /// <summary>页面首次显示时拉一次真实数据。</summary>
    public async Task InitializeAsync()
    {
        if (!UseWorker)
        {
            return;
        }

        // 拓扑先于掩码：下拉里的 CCD 条数与掩码网格的格子数都由它决定，
        // 顺序反了就会先铺一份 16 核的形状，再被真实值覆盖一次。
        IpcOutcome<CpuTopologyPayload> topology = await WorkerBridge.Instance
            .QueryCpuTopologyAsync().ConfigureAwait(true);

        BuildBaseMaskOptions(topology is { Success: true, Value: not null } ? topology.Value : null);

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 重新拉一份进程表、规则表与掩码表。
    /// </summary>
    /// <remarks>
    /// 三份数据必须一起刷新：进程行显示的掩码来自规则，规则里的掩码名又来自掩码表。
    /// 只刷其中一份，界面上就会出现「进程指向一条不存在的规则」这种自相矛盾的行。
    /// </remarks>
    public async Task RefreshAsync()
    {
        if (!UseWorker)
        {
            return;
        }

        WorkerBridge bridge = WorkerBridge.Instance;

        IpcOutcome<List<ProcessEntryPayload>> processes = await bridge
            .QueryProcessesAsync().ConfigureAwait(true);

        IpcOutcome<List<ScheduleRulePayload>> rules = await bridge
            .QuerySchedulingRulesAsync().ConfigureAwait(true);

        IpcOutcome<MaskListPayload> masks = await bridge
            .QueryMasksAsync().ConfigureAwait(true);

        if (masks.Success && masks.Value is not null)
        {
            ApplyMasks(masks.Value);
        }

        if (rules.Success && rules.Value is not null)
        {
            ApplyRules(rules.Value);
        }

        if (!processes.Success || processes.Value is null)
        {
            ToastService.Instance.ShowError(
                processes.Message.Length > 0 ? processes.Message : "取不到进程列表");
            return;
        }

        ApplyProcesses(processes.Value);
    }

    /// <summary>
    /// 把后台的掩码表铺进下拉：CCD / 不调度 是固定的，自定义掩码整段重建。
    /// </summary>
    /// <remarks>
    /// 必须整段重建而不是追加去重：掩码在管理器里被删掉后，旧选项还留在下拉里，
    /// 选中它保存就会被后台拒绝（「掩码不存在」），而界面上根本看不出它已经没了。
    /// </remarks>
    internal void ApplyMasks(MaskListPayload payload)
    {
        for (int i = MaskOptions.Count - 1; i >= 0; i--)
        {
            if (MaskOptions[i].TargetType == "mask")
                MaskOptions.RemoveAt(i);
        }

        foreach (MaskEntryPayload mask in payload.Masks)
        {
            MaskOptions.Add(new MaskOption(
                mask.Name,
                "mask",
                mask.Mask.ToString(CultureInfo.InvariantCulture),
                maskId: mask.Id));
        }

        RebuildDrawerMasks();
    }

    private void ApplyRules(List<ScheduleRulePayload> payload)
    {
        _suppressSync = true;

        try
        {
            SavedRules.Clear();

            foreach (ScheduleRulePayload rule in payload)
            {
                ScheduleRuleItem item = new(
                    rule.Id,
                    rule.ProcessName,
                    rule.Path,
                    MaskLabelOf(rule),
                    ProcessPriority.DisplayOf(rule.Priority),
                    rule.Enabled);

                item.ApplyMask(
                    MaskLabelOf(rule),
                    rule.TargetType,
                    RuleMaskValue(rule));

                item.PropertyChanged += (_, e) => OnTargetChanged(item, e.PropertyName ?? string.Empty);
                SavedRules.Add(item);
            }
        }
        finally
        {
            _suppressSync = false;
        }
    }

    /// <summary>
    /// 规则的显示名：CCD 给 <c>CCD0</c>、掩码给用户起的名字、none 给「不调度」。
    ///
    /// 掩码名优先按 **值** 反查，查不到才退回规则里快照的名字 —— 掩码被删掉之后
    /// 规则仍然按快照生效，此时界面上必须还能说清它绑到哪，而不是显示一串数字。
    /// </summary>
    private string MaskLabelOf(ScheduleRulePayload rule)
    {
        switch (rule.TargetType)
        {
            case "ccd":
                return "CCD" + rule.TargetValue;

            case "mask":
                foreach (MaskOption option in MaskOptions)
                {
                    if (option.TargetType == "mask"
                        && string.Equals(option.TargetValue, rule.TargetValue, StringComparison.Ordinal))
                    {
                        return option.Label;
                    }
                }

                return string.IsNullOrWhiteSpace(rule.TargetValue) ? "不调度" : rule.TargetValue;

            default:
                return "不调度";
        }
    }

    private static string RuleMaskValue(ScheduleRulePayload rule)
        => rule.TargetType == "none" ? string.Empty : rule.TargetValue;

    private void ApplyProcesses(List<ProcessEntryPayload> payload)
    {
        // 规则先落位：进程行要按命中规则显示掩码与优先级，
        // 否则同一个进程在上下两块里会显示成两种状态。
        Dictionary<string, ScheduleRuleItem> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (ScheduleRuleItem rule in SavedRules)
        {
            byName[rule.ProcessName] = rule;
        }

        // 刷新会重建整张表，勾选按 PID 还原 —— 用户勾了几个进程再点刷新，
        // 勾选不该被清空，否则「批量设掩码」这个操作永远做不完。
        HashSet<int> selectedPids = new();

        foreach (RunningProcessItem existing in RunningProcesses)
        {
            if (existing.IsSelected)
            {
                selectedPids.Add(existing.Pid);
            }
        }

        _suppressSync = true;

        try
        {
            RunningProcesses.Clear();

            foreach (ProcessEntryPayload entry in payload)
            {
                RunningProcessItem item = new(
                    entry.Pid,
                    entry.Name,
                    entry.Path,
                    entry.Cpu,
                    ProcessPriority.DisplayOf(ProcessPriority.FromBasePriority(entry.Priority)),
                    IsSystemProcess(entry));

                if (byName.TryGetValue(entry.Name, out ScheduleRuleItem? matched))
                {
                    item.ApplyMask(matched.MaskLabel ?? "不调度", matched.TargetType, matched.TargetValue);
                    item.Priority = matched.Priority;
                }

                item.IsSelected = selectedPids.Contains(item.Pid);

                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RunningProcessItem.IsSelected))
                    {
                        SelectedCount = FilteredProcesses.Count(p => p.IsSelected);
                        OnPropertyChanged(nameof(AreAllSelected));
                        return;
                    }

                    OnTargetChanged(item, e.PropertyName ?? string.Empty);
                };

                RunningProcesses.Add(item);
            }
        }
        finally
        {
            _suppressSync = false;
        }

        ApplyFilterAndSort();
    }

    /// <summary>
    /// 是不是系统进程。后台只给 pid / 路径，没给这个标记，只能按路径判断。
    ///
    /// 判据取两条硬事实：pid 4 是内核，路径落在 Windows 目录下的基本都是系统组件。
    /// 第三方软件装进 Windows 目录的情况极少，误判的代价只是「要打开开关才看得到」。
    /// </summary>
    private static bool IsSystemProcess(ProcessEntryPayload entry)
    {
        if (entry.Pid is 0 or 4)
        {
            return true;
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return entry.Path.Length > 0
               && entry.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 基础目标：每个 CCD 一条 + 不调度。自定义掩码由 <see cref="ApplyMasks"/> 追加在后。
    /// 不提供「全部核心」：它只能以 targetType=mask 发出，却没有掩码 Id，保存必被拒。
    /// </summary>
    private void BuildBaseMaskOptions(CpuTopologyPayload? topology = null)
    {
        int cores = topology is { Cores: > 0 } ? topology.Cores : 16;
        int coresPerCcd = topology is { CoresPerCcd: > 0 } ? topology.CoresPerCcd : 8;

        if (coresPerCcd > cores)
            coresPerCcd = cores;

        _coresPerCcd = coresPerCcd;

        MaskOptions.Clear();

        int ccdCount = (cores + coresPerCcd - 1) / coresPerCcd;

        for (int ccd = 0; ccd < ccdCount; ccd++)
            MaskOptions.Add(new MaskOption($"CCD{ccd}", "ccd", ccd.ToString(CultureInfo.InvariantCulture)));

        MaskOptions.Add(new MaskOption("不调度", "none", string.Empty));

        RebuildDrawerMasks();
    }

    /// <summary>
    /// 抽屉里的目标预览。整表重建而不是增量：删一套掩码之后，
    /// 增量删除要按「哪条是掩码」逐项判断，判断依据又只有 label —— 同名就删错。
    /// </summary>
    private void RebuildDrawerMasks()
    {
        MaskOption.RebuildRegistry(MaskOptions);
        DrawerMasks.Clear();

        foreach (MaskOption option in MaskOptions)
        {
            string topology = option.TargetType switch
            {
                "ccd" => CcdRangeText(option),
                "mask" => MaskText.ToRangeText(ParseMask(option.TargetValue)),
                _ => string.Empty
            };

            if (topology.Length > 0)
                DrawerMasks.Add(new MaskPreviewItem(option.Label, topology));
        }
    }

    private static ulong ParseMask(string value) =>
        ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong bits) ? bits : 0UL;

    /// <summary>把一个 CCD 选项的 TargetValue（CCD 序号）换算成物理核序号区间，如 CCD0 → 0-7。</summary>
    private string CcdRangeText(MaskOption option)
    {
        if (!int.TryParse(option.TargetValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ccd))
            return string.Empty;

        int start = ccd * _coresPerCcd;
        return start.ToString(CultureInfo.InvariantCulture)
               + "-"
               + (start + _coresPerCcd - 1).ToString(CultureInfo.InvariantCulture);
    }

    public bool AreAllSelected => FilteredProcesses.Count > 0 && FilteredProcesses.All(p => p.IsSelected);

    private void RefreshSelectionState()
    {
        SelectedCount = FilteredProcesses.Count(p => p.IsSelected);
        OnPropertyChanged(nameof(AreAllSelected));
    }

    private void ApplyBatchToSelected(Action<RunningProcessItem> apply)
    {
        var targets = RunningProcesses.Where(p => p.IsSelected).ToList();

        if (targets.Count == 0)
        {
            return;
        }

        _suppressSync = true;

        try
        {
            foreach (var item in targets)
            {
                apply(item);
                item.IsDirty = true;

                foreach (var sibling in RunningProcesses)
                {
                    if (!ReferenceEquals(sibling, item)
                        && string.Equals(sibling.Name, item.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        sibling.CopyAffinityFrom(item);
                    }
                }

                foreach (var rule in SavedRules)
                {
                    if (string.Equals(rule.ProcessName, item.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        rule.CopyAffinityFrom(item);
                    }
                }
            }
        }
        finally
        {
            _suppressSync = false;
        }

        AutoApply();
    }

    private bool _suppressSync;

    private void OnTargetChanged(AffinityTarget target, string propertyName)
    {
        if (_suppressSync) return;

        if (propertyName != nameof(AffinityTarget.MaskKey)
            && propertyName != nameof(AffinityTarget.Priority)
            && propertyName != nameof(ScheduleRuleItem.Enabled))
        {
            return;
        }

        target.IsDirty = true;

        _suppressSync = true;

        try
        {
            foreach (var proc in RunningProcesses)
            {
                if (!ReferenceEquals(proc, target)
                    && string.Equals(proc.Name, target.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    proc.CopyAffinityFrom(target);
                }
            }

            foreach (var rule in SavedRules)
            {
                if (!ReferenceEquals(rule, target)
                    && string.Equals(rule.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    rule.CopyAffinityFrom(target);
                }
            }
        }
        finally
        {
            _suppressSync = false;
        }

        AutoApply();
    }

    private void AutoApply()
    {
        List<AffinityTarget> dirty = RunningProcesses
            .Where(p => p.IsDirty)
            .Cast<AffinityTarget>()
            .Concat(SavedRules.Where(r => r.IsDirty))
            .ToList();

        _suppressSync = true;

        try
        {
            foreach (var proc in RunningProcesses)
            {
                proc.IsDirty = false;
            }

            foreach (var rule in SavedRules)
            {
                rule.IsDirty = false;
            }
        }
        finally
        {
            _suppressSync = false;
        }

        if (dirty.Count == 0)
        {
            return;
        }

        if (!UseWorker)
        {
            // 诊断模式只改内存，不碰后台服务。
            _suppressSync = true;

            try
            {
                foreach (var proc in RunningProcesses.Where(p => dirty.Contains(p)))
                {
                    UpsertRule(proc);
                }
            }
            finally
            {
                _suppressSync = false;
            }

            return;
        }

        Fire(() => CommitAsync(dirty));
    }

    /// <summary>
    /// 把界面上改动过的一批目标写进后台。
    /// </summary>
    /// <remarks>
    /// 【为什么整批写完才刷新一次】
    /// 每次保存都会触发后台重扫并写一次盘。逐个刷新会让列表在一次操作里重建多次，
    /// 用户看到的是勾选与滚动位置反复跳动。
    ///
    /// 【为什么失败要继续往下走】
    /// 批量改十个进程时，其中有一个是受保护进程很常见。中途 return 会让后面的
    /// 目标全都不生效，而用户以为自己改的都是同一批。
    /// </remarks>
    private async Task CommitAsync(List<AffinityTarget> targets)
    {
        List<string> failures = new();

        foreach (AffinityTarget target in targets)
        {
            (bool ok, string? error) = await ApplyTargetAsync(target).ConfigureAwait(true);

            if (!ok)
            {
                failures.Add($"{target.ProcessName}：{error}");
            }
        }

        await RefreshRulesAsync().ConfigureAwait(true);

        if (failures.Count > 0)
        {
            ToastService.Instance.ShowError(failures[0]);
        }
    }

    /// <summary>
    /// 只重新拉规则与掩码，不重建进程表。
    ///
    /// 【为什么不复用 RefreshAsync】
    /// 用户刚在某一行的下拉里选了值，重建整张表会让那一行连同焦点一起消失 ——
    /// 表现是「选完立刻弹回顶部」。进程行的显示已经在改动发生时就同步过了，
    /// 这里只需要把规则区对齐。
    /// </summary>
    private async Task RefreshRulesAsync()
    {
        WorkerBridge bridge = WorkerBridge.Instance;

        IpcOutcome<List<ScheduleRulePayload>> rules = await bridge
            .QuerySchedulingRulesAsync().ConfigureAwait(true);

        if (!rules.Success || rules.Value is null)
        {
            ToastService.Instance.ShowError(rules.Message.Length > 0 ? rules.Message : "取不到调度规则");
            return;
        }

        ApplyRules(rules.Value);
    }

    /// <summary>把一个目标落成后台的一条规则（「不调度」等于删除这条规则）。</summary>
    private async Task<(bool Ok, string? Error)> ApplyTargetAsync(AffinityTarget target)
    {
        ScheduleRuleItem? existing = FindRule(target.ProcessName);

        if (target.TargetType == "none")
        {
            if (existing is null)
            {
                return (true, null);
            }

            IpcOutcome<string> removed = await WorkerBridge.Instance
                .DeleteScheduleRuleAsync(existing.Id).ConfigureAwait(true);

            return removed.Success ? (true, null) : (false, removed.Message);
        }

        string? maskId = target.TargetType == "mask" ? ResolveMaskId(target) : null;

        if (target.TargetType == "mask" && maskId is null)
        {
            // 掩码在界面打开之后被删掉了。此时传上去的 Id 一定匹配不到，
            // 后台会回「掩码不存在」—— 提前拦下来，提示用户重选。
            return (false, "选中的掩码已不存在，请重新选一次");
        }

        SaveScheduleRulePayload payload = new()
        {
            Id = existing?.Id ?? string.Empty,
            ProcessName = target.ProcessName,
            TargetType = target.TargetType,
            TargetValue = target.TargetType == "ccd" ? "CCD" + target.TargetValue : maskId!,
            Priority = PriorityConstantOf(target.Priority),
            Enabled = existing?.Enabled ?? true
        };

        IpcOutcome<string> saved = await WorkerBridge.Instance
            .SaveScheduleRuleAsync(payload).ConfigureAwait(true);

        return saved.Success ? (true, null) : (false, saved.Message);
    }

    private ScheduleRuleItem? FindRule(string processName)
        => SavedRules.FirstOrDefault(
            r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>规则引用的掩码 Id。按掩码 **值** 找回选项，再取它的 Id。</summary>
    private string? ResolveMaskId(AffinityTarget target)
    {
        foreach (MaskOption option in MaskOptions)
        {
            if (option.TargetType == "mask"
                && string.Equals(option.TargetValue, target.TargetValue, StringComparison.Ordinal))
            {
                return option.MaskId;
            }
        }

        return null;
    }

    /// <summary>中文档位 → 后台常量。认不出的按「标准」处理，绝不返回空串去代表标准。</summary>
    private static string PriorityConstantOf(string label)
    {
        foreach (string option in ProcessPriority.Options)
        {
            if (string.Equals(ProcessPriority.DisplayOf(option), label, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return ProcessPriority.Normal;
    }

    private void UpsertRule(AffinityTarget target)
    {
        var existing = SavedRules.FirstOrDefault(
            r => string.Equals(r.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.CopyAffinityFrom(target);
            existing.IsDirty = false;
            return;
        }

        var rule = new ScheduleRuleItem(
            "r-" + Guid.NewGuid().ToString("N")[..8],
            target.ProcessName,
            target.Path,
            target.MaskLabel ?? "不调度",
            target.Priority,
            enabled: true);

        rule.CopyAffinityFrom(target);
        rule.PropertyChanged += (_, e) => OnTargetChanged(rule, e.PropertyName ?? string.Empty);

        SavedRules.Add(rule);
    }

    public void ShowToast(string message) => ToastService.Instance.Show(message);

    /// <summary>
    /// 把异步动作塞进同步命令里。
    ///
    /// 【为什么不能直接写成 async void 的事件处理器】
    /// <see cref="RelayCommand"/> 收的是 <c>Action</c>，写 <c>async</c> 进去就是一个
    /// async void —— 里面任何异常都会直接抛到 UI 线程上，进程随即崩掉。
    /// 这里统一收口：异常转成 Toast，至少用户能看到发生了什么。
    /// </summary>
    private static void Fire(Func<Task> action) => _ = RunSafeAsync(action);

    private static async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show(ex.Message);
        }
    }

    private bool _isMaskDialogOpen;

    /// <summary>掩码管理器是否已激活。激活后右栏抽屉停在「自定义掩码」页签。</summary>
    public bool IsMaskDialogOpen
    {
        get => _isMaskDialogOpen;
        set => SetProperty(ref _isMaskDialogOpen, value);
    }

    private MaskManagerViewModel? _maskManager;

    public MaskManagerViewModel? MaskManager
    {
        get => _maskManager;
        private set => SetProperty(ref _maskManager, value);
    }

    public void OpenMaskManager()
    {
        IsMaskDialogOpen = true;

        // 抽屉自身在掩码页签上，掩码表才有地方铺。
        IsMasksTab = true;

        if (!IsDrawerOpen)
            IsDrawerOpen = true;

        // 拓扑与掩码表在打开时才拉：它们是掩码页签自己的数据，
        // 跟着进程页每次刷新一起拉一遍只是白做。
        if (MaskManager is not null)
            Fire(MaskManager.InitializeAsync);
    }

    private void ApplyFilterAndSort()
    {
        var list = RunningProcesses.Where(Matches).ToList();

        list = SortKey switch
        {
            "name" => list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "path" => list.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            "cpu" => list.OrderBy(p => p.Cpu).ToList(),
            "mask" => list.OrderBy(p => p.MaskLabel ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList(),
            "priority" => list.OrderBy(p => PriorityRank(p.Priority)).ToList(),
            _ => list,
        };

        if (SortDescending)
        {
            list.Reverse();
        }

        FilteredProcesses.Clear();

        foreach (var item in list)
        {
            FilteredProcesses.Add(item);
        }

        RefreshSelectionState();
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "高" => 4,
        "高于标准" => 3,
        "标准" => 2,
        "低于标准" => 1,
        "低" => 0,
        _ => 2,
    };

    private bool Matches(RunningProcessItem item)
    {
        if (!ShowSystemProcesses && item.IsSystem)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Pid.ToString(CultureInfo.InvariantCulture).Contains(SearchText, StringComparison.Ordinal);
    }

    private string _searchText = string.Empty;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    private void SeedRunning()
    {
        AddProcess(8124, "cs2.exe", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", 4.2, "高", "CCD0");
        AddProcess(4210, "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", 0.8, "标准", null);
        AddProcess(4211, "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", 0.2, "标准", null);
        AddProcess(6332, "obs64.exe", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", 2.1, "高于标准", null);
        AddProcess(1908, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 6.4, "标准", null);
        AddProcess(7745, "agent.exe", @"C:\ProgramData\Zentwik\worker\agent.exe", 0.3, "低于标准", null);

        AddProcess(4, "System", @"N/A", 0.1, "标准", null, isSystem: true);
        AddProcess(612, "csrss.exe", @"C:\Windows\System32\csrss.exe", 0.0, "标准", null, isSystem: true);
        AddProcess(728, "dwm.exe", @"C:\Windows\System32\dwm.exe", 1.1, "标准", null, isSystem: true);
        AddProcess(1184, "svchost.exe", @"C:\Windows\System32\svchost.exe", 0.4, "标准", null, isSystem: true);
        AddProcess(1201, "svchost.exe", @"C:\Windows\System32\svchost.exe", 0.2, "标准", null, isSystem: true);
        AddProcess(1268, "lsass.exe", @"C:\Windows\System32\lsass.exe", 0.0, "标准", null, isSystem: true);
        AddProcess(3052, "explorer.exe", @"C:\Windows\explorer.exe", 0.6, "标准", null, isSystem: true);

        for (int i = 0; i < BulkSeedCount; i++)
        {
            AddProcess(
                9000 + i,
                $"worker-{i:D4}.exe",
                $@"C:\ProgramData\Zentwik\bulk\worker-{i:D4}.exe",
                i % 40 * 0.25,
                "标准",
                null);
        }

        ApplyFilterAndSort();
    }

    private void AddProcess(int pid, string name, string path, double cpu, string priority, string? maskLabel, bool isSystem = false)
    {
        var item = new RunningProcessItem(pid, name, path, cpu, priority, isSystem);

        if (maskLabel is not null)
        {
            item.ApplyMask(maskLabel, "ccd", "0");
        }

        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunningProcessItem.IsSelected))
            {
                SelectedCount = FilteredProcesses.Count(p => p.IsSelected);
                OnPropertyChanged(nameof(AreAllSelected));
                return;
            }

            OnTargetChanged(item, e.PropertyName ?? string.Empty);
        };

        RunningProcesses.Add(item);
    }

    private void SeedRules()
    {
        var r1 = new ScheduleRuleItem("r-1", "cs2.exe", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive", "CCD0", "高", enabled: true);
        r1.ApplyMask("CCD0", "ccd", "0");

        var r2 = new ScheduleRuleItem("r-2", "obs64.exe", @"C:\Program Files\obs-studio", "0-7,16-23", "高于标准", enabled: true);
        r2.ApplyMask("0-7,16-23", "mask", "16711935");

        var r3 = new ScheduleRuleItem("r-3", "steam.exe", @"C:\Program Files (x86)\Steam", "不调度", "低于标准", enabled: false);
        r3.ApplyMask("不调度", "none", "");

        foreach (var rule in new[] { r1, r2, r3 })
        {
            rule.PropertyChanged += (_, e) => OnTargetChanged(rule, e.PropertyName ?? string.Empty);
            SavedRules.Add(rule);
        }

        // 命中了规则的进程，运行表里显示的就该是规则的值。
        // 否则同一个 obs64.exe 上面写「不调度」、下面写「0-7,16-23」。
        foreach (var rule in SavedRules)
        {
            foreach (var process in RunningProcesses)
            {
                if (string.Equals(process.Name, rule.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    process.CopyAffinityFrom(rule);
                }
            }
        }
    }
}

public abstract class AffinityTarget : ViewModelBase
{
    // 默认就是「不调度」：它和下拉里的那一项是同一个状态（targetType=none、无掩码），
    // 所以必须同一个外观。曾经默认留空、选中后显示「不调度」，
    // 同一件事在表里和规则区有两种长相。
    private string? _maskLabel = "不调度";
    private string _targetType = "none";
    private string _targetValue = string.Empty;
    private string _priority = "标准";
    private bool _isDirty;
    private string? _maskKey = MaskOption.MakeKey("none", string.Empty);

    public abstract string ProcessName { get; }
    public abstract string Path { get; }

    public string? MaskLabel
    {
        get => _maskLabel;
        set
        {
            if (SetProperty(ref _maskLabel, value))
            {
                OnPropertyChanged(nameof(MaskKey));
            }
        }
    }

    public string TargetType
    {
        get => _targetType;
        set => SetProperty(ref _targetType, value);
    }

    public string TargetValue
    {
        get => _targetValue;
        set => SetProperty(ref _targetValue, value);
    }

    public string? MaskKey
    {
        get => _maskKey;
        set
        {
            MaskOption? option = MaskOption.Resolve(value);

            if (option is null)
            {
                SetProperty(ref _maskKey, value);
                OnPropertyChanged(nameof(HasMaskKey));
                return;
            }

            WriteMask(option.Label, option.TargetType, option.TargetValue);
            NotifyMaskChanged();
        }
    }

    public bool HasMaskKey => MaskKey is not null;

    public string Priority
    {
        get => _priority;
        set => SetProperty(ref _priority, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    public void ApplyMask(string label, string targetType, string targetValue)
    {
        WriteMask(label, targetType, targetValue);
        NotifyMaskChanged();
    }

    private void WriteMask(string label, string targetType, string targetValue)
    {
        _maskLabel = label;
        _targetType = targetType;
        _targetValue = targetValue;
        _maskKey = MaskOption.MakeKey(targetType, targetValue);
    }

    private void NotifyMaskChanged()
    {
        OnPropertyChanged(nameof(MaskLabel));
        OnPropertyChanged(nameof(TargetType));
        OnPropertyChanged(nameof(TargetValue));
        OnPropertyChanged(nameof(MaskKey));
        OnPropertyChanged(nameof(HasMaskKey));
    }

    public void CopyAffinityFrom(AffinityTarget other)
    {
        WriteMask(other._maskLabel ?? "不调度", other._targetType, other._targetValue);
        _priority = other._priority;

        NotifyMaskChanged();
        OnPropertyChanged(nameof(Priority));
    }

    public void ResetAffinity()
    {
        WriteMask("不调度", "none", string.Empty);
        _priority = "标准";

        NotifyMaskChanged();
        OnPropertyChanged(nameof(Priority));
    }
}

public class RunningProcessItem : AffinityTarget
{
    public int Pid { get; }
    public string Name { get; }
    public string CpuText => Cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    public double Cpu { get; }
    public override string ProcessName => Name;
    public override string Path { get; }

    public bool IsSystem { get; }

    public string NameTooltip => Name + " · PID " + Pid.ToString(CultureInfo.InvariantCulture);

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RunningProcessItem(int pid, string name, string path, double cpu, string priority, bool isSystem = false)
    {
        Pid = pid;
        Name = name;
        Path = path;
        Cpu = cpu;
        IsSystem = isSystem;
        Priority = priority;
    }
}

public class ScheduleRuleItem : AffinityTarget
{
    private bool _enabled;

    public string Id { get; }
    public override string Path { get; }
    public override string ProcessName { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    public string ToggleLabel => Enabled ? "禁用" : "启用";

    public ScheduleRuleItem(
        string id,
        string processName,
        string path,
        string maskLabel,
        string priority,
        bool enabled)
    {
        Id = id;
        ProcessName = processName;
        Path = path;
        MaskLabel = maskLabel;
        Priority = priority;
        _enabled = enabled;
    }
}

/// <summary>抽屉掩码页签里的只读条目：名称 + 核心拓扑。</summary>
public sealed class MaskPreviewItem
{
    public string Label { get; }

    public string Topology { get; }

    public string LabelTooltip => Label + " · " + Topology;

    public MaskPreviewItem(string label, string topology)
    {
        Label = label;
        Topology = topology;
    }
}

public sealed class MaskOption : ViewModelBase
{
    private static readonly Dictionary<string, MaskOption> Registry = new();

    public string Label { get; }
    public string TargetType { get; }
    public string TargetValue { get; }
    public string Key => MakeKey(TargetType, TargetValue);

    /// <summary>
    /// 自定义掩码的 Id，只有 <see cref="TargetType"/> = <c>mask</c> 且来自后台时才有值。
    ///
    /// 【为什么单独存一份，不直接拿 TargetValue 当 Id】
    /// 查询给的 TargetValue 是掩码 **值**（位图），保存规则要的却是掩码 **Id**。
    /// 两者不能互换：值会重复（两套掩码选了同样的核），Id 才是身份。
    /// 界面按值显示与匹配，下发时换成 Id。
    /// </summary>
    public string? MaskId { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public MaskOption(string label, string targetType, string targetValue, string? maskId = null)
    {
        Label = label;
        TargetType = targetType;
        TargetValue = targetValue;
        MaskId = maskId;
    }

    /// <summary>
    /// 用当前选项集重建查找表。由选项集变化处统一调用，
    /// 构造时注册会让每次刷新都往静态表里塞新对象、永不释放。
    /// </summary>
    public static void RebuildRegistry(IEnumerable<MaskOption> options)
    {
        Registry.Clear();

        foreach (MaskOption option in options)
            Registry[option.Key] = option;
    }

    public static MaskOption? Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return Registry.TryGetValue(key, out var option) ? option : null;
    }

    public static string MakeKey(string targetType, string targetValue) => targetType + "|" + targetValue;

    public override string ToString() => Label;
}

public static class MaskText
{
    public static bool TryParse(string text, out ulong mask)
    {
        mask = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string seg = part.Trim();

            if (seg.Length == 0)
            {
                return false;
            }

            int dash = seg.IndexOf('-');

            if (dash < 0)
            {
                if (!int.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int single))
                {
                    return false;
                }

                if (single is < 0 or > 63)
                {
                    return false;
                }

                mask |= 1UL << single;
                continue;
            }

            string left = seg[..dash].Trim();
            string right = seg[(dash + 1)..].Trim();

            if (!int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ||
                !int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
            {
                return false;
            }

            if (start is < 0 or > 63 || end is < 0 or > 63 || end < start)
            {
                return false;
            }

            for (int i = start; i <= end; i++)
            {
                mask |= 1UL << i;
            }
        }

        return mask != 0;
    }

    public static string ToRangeText(ulong mask)
    {
        if (mask == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        int start = -1;

        for (int i = 0; i <= 64; i++)
        {
            bool on = i < 64 && (mask & (1UL << i)) != 0;

            if (on && start < 0)
            {
                start = i;
            }
            else if (!on && start >= 0)
            {
                parts.Add(start == i - 1 ? start.ToString(CultureInfo.InvariantCulture)
                                        : $"{start}-{i - 1}");
                start = -1;
            }
        }

        return string.Join(",", parts);
    }
}
