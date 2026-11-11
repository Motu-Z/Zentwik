using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Zentwik.Core.Ipc;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>自定义掩码管理器</summary>
public sealed class MaskManagerViewModel : ViewModelBase
{
    private const int DemoCores = 16;
    private const int DemoCoresPerCcd = 8;
    private const int DemoThreadsPerCore = 2;

    private readonly ProcessViewModel _owner;

    public MaskManagerViewModel(ProcessViewModel owner)
    {
        _owner = owner;

        BuildGrid(DemoCores, DemoCoresPerCcd, DemoThreadsPerCore);

        // 诊断模式没有 Worker，构造时就种一份，截图与自检拿到的不是空弹层。
        if (ProcessViewModel.SuppressWorkerInit)
            SeedDemo();

        NewMaskCommand = new RelayCommand(_ => NewMask());
        DeleteMaskCommand = new RelayCommand(p => DeleteMask(p));
        OpenEditorCommand = new RelayCommand(p =>
        {
            if (p is SavedMaskItem item)
                BeginEdit(item);
        });
        BackCommand = new RelayCommand(_ => ReturnToList());

        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        SelectPhysicalOnlyCommand = new RelayCommand(_ => SelectPhysicalOnly());
        InvertSelectionCommand = new RelayCommand(_ => InvertSelection());
        ClearSelectionCommand = new RelayCommand(_ => SetAll(false));
        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => ReturnToList());
    }

    private bool _isEditing;

    /// <summary>true = 编辑器视图；false = 已保存掩码列表。滑入动画在 code-behind。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
                OnPropertyChanged(nameof(IsListVisible));
        }
    }

    public bool IsListVisible => !_isEditing;

    private bool _isNewMask;

    /// <summary>新建时隐藏「删除」，标题改成「新建掩码」。</summary>
    public bool IsNewMask
    {
        get => _isNewMask;
        private set
        {
            if (SetProperty(ref _isNewMask, value))
                OnPropertyChanged(nameof(EditorTitle));
        }
    }

    public string EditorTitle => _isNewMask ? "新建掩码" : "编辑掩码";

    /// <summary>编辑器滑入 / 滑出的方向由视图层消费。</summary>
    public event Action<bool>? EditingChanged;

    /// <summary>直接进编辑器编辑当前选中项</summary>
    public void EditSelected()
    {
        if (SelectedMask is not null)
            BeginEdit(SelectedMask);
    }

    public ObservableCollection<CcdGroup> CcdGroups { get; } = new();

    public ObservableCollection<SavedMaskItem> SavedMasks { get; } = new();

    private SavedMaskItem? _selectedMask;

    /// <summary>当前选中的掩码</summary>
    public SavedMaskItem? SelectedMask
    {
        get => _selectedMask;
        set
        {
            if (!SetProperty(ref _selectedMask, value))
                return;

            foreach (SavedMaskItem item in SavedMasks)
                item.IsSelected = ReferenceEquals(item, value);
        }
    }

    private string _maskName = string.Empty;

    public string MaskName
    {
        get => _maskName;
        set
        {
            if (SetProperty(ref _maskName, value))
                NameError = string.Empty;
        }
    }

    private string _nameError = string.Empty;

    /// <summary>名称输入框的内联错误文案，空串表示无错；只驱动红边与 ToolTip。</summary>
    public string NameError
    {
        get => _nameError;
        private set => SetProperty(ref _nameError, value ?? string.Empty);
    }

    private int _selectedCoreCount;

    public int SelectedCoreCount
    {
        get => _selectedCoreCount;
        private set
        {
            if (SetProperty(ref _selectedCoreCount, value))
                OnPropertyChanged(nameof(SelectedCoreCountText));
        }
    }

    public string SelectedCoreCountText => $"已选 {SelectedCoreCount} 个逻辑处理器";

    public RelayCommand NewMaskCommand { get; }

    public RelayCommand DeleteMaskCommand { get; }

    public RelayCommand OpenEditorCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectPhysicalOnlyCommand { get; }

    public RelayCommand InvertSelectionCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>打开管理器时拉一次拓扑与掩码表。</summary>
    public async Task InitializeAsync()
    {
        // 诊断种子在构造函数里已铺好，重复铺会清掉刚勾的核。
        if (ProcessViewModel.SuppressWorkerInit || IsEditing || _loading)
            return;

        _loading = true;

        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            _loading = false;
        }
    }

    private bool _loading;

    private async Task LoadAsync()
    {
        IpcOutcome<CpuTopologyPayload> topology =
            await WorkerBridge.Instance.QueryCpuTopologyAsync().ConfigureAwait(true);

        if (topology is { Success: true, Value: not null })
        {
            BuildGrid(topology.Value.Cores, topology.Value.CoresPerCcd, topology.Value.ThreadsPerCore);
        }
        else
        {
            ToastError(topology.Message);
        }

        IpcOutcome<MaskListPayload> masks =
            await WorkerBridge.Instance.QueryMasksAsync().ConfigureAwait(true);

        if (masks is { Success: true, Value: not null })
        {
            LoadMasks(masks.Value);
        }
        else
        {
            ToastError(masks.Message);
        }
    }

    private void SeedDemo()
    {
        LoadMasks(new MaskListPayload
        {
            Limit = 10,
            Masks =
            {
                new MaskEntryPayload { Id = "demo-mask", Name = "0-7,16-23", Mask = 16711935, CoreCount = 16 }
            }
        });

        SelectedMask = SavedMasks.FirstOrDefault();
    }

    /// <summary>按后台返回的列表重建左栏，尽量保持原选中项。</summary>
    private void LoadMasks(MaskListPayload payload)
    {
        string? selectedId = SelectedMask?.Id;

        SavedMasks.Clear();

        foreach (MaskEntryPayload mask in payload.Masks)
            SavedMasks.Add(new SavedMaskItem(mask.Id, mask.Name, mask.Mask));

        if (selectedId is null)
        {
            SelectedMask = null;
            return;
        }

        SavedMaskItem? restored = SavedMasks.FirstOrDefault(m => m.Id == selectedId);
        SelectedMask = restored;

        if (restored is not null)
        {
            MaskName = restored.Name;
            ApplyMaskToGrid(restored.Mask);
            UpdateCount();
            return;
        }

        // 选中的那套已被删掉：网格停在当前勾选会让人以为它还存着。
        SetAll(false);
    }

    private void SetEditing(bool editing)
    {
        IsEditing = editing;

        if (editing)
            UpdateCount();

        EditingChanged?.Invoke(editing);
    }

    private void BeginEdit(SavedMaskItem item)
    {
        SelectedMask = item;
        MaskName = item.Name;
        ApplyMaskToGrid(item.Mask);
        IsNewMask = false;
        SetEditing(true);
    }

    private void ReturnToList() => SetEditing(false);

    private void NewMask()
    {
        SelectedMask = null;
        MaskName = string.Empty;
        SetAll(false);
        IsNewMask = true;
        SetEditing(true);
    }

    private void DeleteMask(object? parameter)
    {
        if (parameter is not SavedMaskItem item)
            return;

        if (ProcessViewModel.SuppressWorkerInit)
        {
            SavedMasks.Remove(item);

            if (ReferenceEquals(SelectedMask, item))
            {
                SelectedMask = null;
                SetAll(false);
            }

            ReturnToList();
            return;
        }

        Fire(async () =>
        {
            IpcOutcome<MaskListPayload> outcome = await WorkerBridge.Instance
                .DeleteMaskAsync(item.Id).ConfigureAwait(true);

            if (!outcome.Success || outcome.Value is null)
            {
                ToastError(outcome.Message);
                return;
            }

            SelectedMask = null;
            LoadMasks(outcome.Value);
            _owner.ApplyMasks(outcome.Value);

            // 规则里存的是掩码名快照：改名 / 删掉之后规则卡上的标签会停在旧样子。
            await _owner.RefreshRulesAsync().ConfigureAwait(true);

            ReturnToList();
        });
    }

    private void Save()
    {
        ulong mask = BuildMask();

        if (mask == 0)
        {
            ToastError("至少选一个核心");
            return;
        }

        string name = (MaskName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            NameError = "掩码名称不能为空";
            ToastError(NameError);
            return;
        }

        NameError = string.Empty;

        if (ProcessViewModel.SuppressWorkerInit)
        {
            ReturnToList();
            return;
        }

        Fire(async () =>
        {
            IpcOutcome<MaskListPayload> outcome = await WorkerBridge.Instance
                .SaveMaskAsync(SelectedMask?.Id, name, mask).ConfigureAwait(true);

            if (!outcome.Success || outcome.Value is null)
            {
                NameError = string.IsNullOrEmpty(outcome.Message) ? "保存失败" : outcome.Message;
                ToastError(NameError);
                return;
            }

            LoadMasks(outcome.Value);
            _owner.ApplyMasks(outcome.Value);
            await _owner.RefreshRulesAsync().ConfigureAwait(true);
            ReturnToList();
        });
    }

    private void SetAll(bool selected)
    {
        BulkApply(() =>
        {
            foreach (CcdGroup group in CcdGroups)
            foreach (CoreSlot core in group.Cores)
            foreach (ThreadSlot thread in core.Threads)
                thread.IsSelected = selected;
        });
    }

    private void SelectPhysicalOnly()
    {
        BulkApply(() =>
        {
            foreach (CcdGroup group in CcdGroups)
            foreach (CoreSlot core in group.Cores)
            {
                for (int i = 0; i < core.Threads.Count; i++)
                    core.Threads[i].IsSelected = i == 0;
            }
        });
    }

    private void InvertSelection()
    {
        BulkApply(() =>
        {
            foreach (CcdGroup group in CcdGroups)
            foreach (CoreSlot core in group.Cores)
            foreach (ThreadSlot thread in core.Threads)
                thread.IsSelected = !thread.IsSelected;
        });
    }

    private void ApplyMaskToGrid(ulong mask)
    {
        BulkApply(() =>
        {
            foreach (CcdGroup group in CcdGroups)
            foreach (CoreSlot core in group.Cores)
            foreach (ThreadSlot thread in core.Threads)
                thread.IsSelected = (mask & BitAt(thread.LogicalIndex)) != 0;
        });
    }

    /// <summary>成批改勾选时不逐格重算，末尾统一刷一次。</summary>
    private void BulkApply(Action action)
    {
        _bulkSelect = true;

        try
        {
            action();
        }
        finally
        {
            _bulkSelect = false;
        }

        UpdateCount();
    }

    private bool _bulkSelect;

    /// <summary>单个格子被点：计数要立刻跟上，否则「已选 N」停在打开时那一刻。</summary>
    private void OnThreadSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThreadSlot.IsSelected) && !_bulkSelect)
            UpdateCount();
    }

    private ulong BuildMask()
    {
        ulong mask = 0;

        foreach (CcdGroup group in CcdGroups)
        foreach (CoreSlot core in group.Cores)
        foreach (ThreadSlot thread in core.Threads)
        {
            if (thread.IsSelected)
                mask |= BitAt(thread.LogicalIndex);
        }

        return mask;
    }

    /// <summary>C# 对移位计数取模，>= 64 会绕回低位；超出位图宽度的核直接忽略。</summary>
    private static ulong BitAt(int index)
        => index is >= 0 and < 64 ? 1UL << index : 0UL;

    private void UpdateCount()
    {
        int count = 0;

        foreach (CcdGroup group in CcdGroups)
        foreach (CoreSlot core in group.Cores)
        foreach (ThreadSlot thread in core.Threads)
        {
            if (thread.IsSelected)
                count++;
        }

        SelectedCoreCount = count;
    }

    /// <summary>按后台拓扑重建网格</summary>
    private void BuildGrid(int cores, int coresPerCcd, int threadsPerCore)
    {
        cores = Math.Max(1, cores);
        coresPerCcd = coresPerCcd <= 0 ? cores : Math.Min(coresPerCcd, cores);
        int threads = Math.Max(1, threadsPerCore);

        // 拓扑是异步到达的，用户可能已经先勾了几个核。重建前留一份位图。
        ulong previous = BuildMask();

        CcdGroups.Clear();

        int ccdCount = (cores + coresPerCcd - 1) / coresPerCcd;

        for (int ccd = 0; ccd < ccdCount; ccd++)
        {
            CcdGroup group = new(ccd);
            int firstCore = ccd * coresPerCcd;
            int coreCount = Math.Min(coresPerCcd, cores - firstCore);

            for (int core = 0; core < coreCount; core++)
            {
                // 核编号一路排下去：CCD1 的第一个核是 Core 8，不是又从 Core 0 起。
                int globalCore = firstCore + core;
                CoreSlot slot = new(globalCore, globalCore * threads);

                for (int thread = 0; thread < threads; thread++)
                {
                    ThreadSlot item = new((globalCore * threads) + thread, thread);
                    item.PropertyChanged += OnThreadSelectionChanged;
                    slot.Threads.Add(item);
                }

                group.Cores.Add(slot);
            }

            CcdGroups.Add(group);
        }

        if (previous != 0)
            ApplyMaskToGrid(previous);

        UpdateCount();
    }

    private static void ToastError(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            ToastService.Instance.ShowError(message);
    }

    private static void Fire(Func<Task> action) => AsyncRunner.Fire(action);
}

public sealed class SavedMaskItem : ViewModelBase
{
    public SavedMaskItem(string id, string name, ulong mask)
    {
        Id = id;
        Name = name;
        Mask = mask;
    }

    public string Id { get; }

    public string Name { get; }

    public ulong Mask { get; }

    public string RangeText => MaskText.ToRangeText(Mask);

    public string CountText =>
        BitOperations.PopCount(Mask).ToString(CultureInfo.InvariantCulture) + " 核";

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class CcdGroup
{
    public CcdGroup(int index) => Index = index;

    public int Index { get; }

    public string DisplayName => $"CCD {Index}";

    public ObservableCollection<CoreSlot> Cores { get; } = new();
}

public sealed class CoreSlot
{
    public CoreSlot(int coreIndex, int logicalIndex)
    {
        CoreIndex = coreIndex;
        LogicalIndex = logicalIndex;
    }

    /// <summary>全局物理核序号，跨 CCD 连续。</summary>
    public int CoreIndex { get; }

    /// <summary>该核第一个逻辑处理器的序号（掩码位起点）。</summary>
    public int LogicalIndex { get; }

    public string DisplayName => $"Core {CoreIndex}";

    public ObservableCollection<ThreadSlot> Threads { get; } = new();
}

public sealed class ThreadSlot : ViewModelBase
{
    public ThreadSlot(int logicalIndex, int threadIndex)
    {
        LogicalIndex = logicalIndex;
        ThreadIndex = threadIndex;
    }

    public int LogicalIndex { get; }

    public int ThreadIndex { get; }

    public string DisplayName => $"T{ThreadIndex}";

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
