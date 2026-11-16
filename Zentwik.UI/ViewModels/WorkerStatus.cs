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

    private string? _failureDetail;

    /// <summary>对端回的具体原因（版本不一致等）；null 表示纯传输层连不上。</summary>
    public string? FailureDetail
    {
        get => _failureDetail;
        set
        {
            if (SetProperty(ref _failureDetail, value))
            {
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
        : FailureDetail ?? "后台服务未响应，调校与调度都不会生效";

    /// <summary>连不上的原因不是「重新检测」能解决的，就别提示去点。</summary>
    public string ToolTipText => FailureDetail is null ? DetailText + "\n点击重新检测" : DetailText;
}
