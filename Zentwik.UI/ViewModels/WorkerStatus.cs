using System.ComponentModel;

namespace Zentwik.UI.ViewModels;

/// <summary>外壳左下角的 Worker 连接状态</summary>
public sealed class WorkerStatus : ViewModelBase
{
    public static WorkerStatus Instance { get; } = new();

    private WorkerStatus()
    {
    }

    private bool _isConnected;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    private bool _isElevated;

    public bool IsElevated
    {
        get => _isElevated;
        set
        {
            if (SetProperty(ref _isElevated, value))
            {
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    public string StatusText => IsConnected ? "Worker 已连接" : "Worker 未连接";

    public string DetailText => IsConnected
        ? (IsElevated ? "提权进程运行中，具备 SMU 写入权限" : "后台服务在线，SMU 当前不可写")
        : "后台服务未响应，调校与调度都不会生效";

    public string ToolTipText => DetailText + "\n点击重新检测";
}
