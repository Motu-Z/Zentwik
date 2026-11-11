using System.Windows.Input;

namespace Zentwik.UI.ViewModels;

/// <summary>外壳布局状态（侧栏展开/收起）</summary>
public sealed class ShellState : ViewModelBase
{
    public static ShellState Instance { get; } = new();

    private ShellState()
    {
        ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        ExpandCommand = new RelayCommand(_ => IsExpanded = true);
        CollapseCommand = new RelayCommand(_ => IsExpanded = false);
    }

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(IsCollapsed));
                OnPropertyChanged(nameof(Width));
                OnPropertyChanged(nameof(ToggleTip));
            }
        }
    }

    public bool IsCollapsed => !_isExpanded;

    /// <summary>当前侧栏宽度：收缩 72 / 展开 220。</summary>
    public double Width => _isExpanded
        ? LayoutMetrics.SidebarExpandedWidth
        : LayoutMetrics.SidebarWidth;

    /// <summary>箭头按钮的提示语。</summary>
    public string ToggleTip => _isExpanded ? "收起侧栏" : "展开侧栏";

    public ICommand ToggleCommand { get; }

    public ICommand ExpandCommand { get; }

    public ICommand CollapseCommand { get; }
}
