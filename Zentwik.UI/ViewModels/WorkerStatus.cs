using System.ComponentModel;
using Zentwik.Core.Services;

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

    private string? _vendor;

    /// <summary>Worker 报上来的处理器厂商；老版本不带该字段时为 null。</summary>
    public string? Vendor
    {
        get => _vendor;
        set
        {
            if (SetProperty(ref _vendor, value))
            {
                OnPropertyChanged(nameof(IsIntel));
                OnPropertyChanged(nameof(IsTuningSupported));
                OnPropertyChanged(nameof(IsTuningPlanSupported));
            }
        }
    }

    private bool _intelTuning;

    /// <summary>Intel MSR 调校通道是否就绪；老版本不带该字段时为 false。</summary>
    public bool IntelTuning
    {
        get => _intelTuning;
        set
        {
            if (SetProperty(ref _intelTuning, value))
            {
                OnPropertyChanged(nameof(IsTuningSupported));
            }
        }
    }

    public bool IsIntel => string.Equals(Vendor, CpuVendorNames.Intel, StringComparison.OrdinalIgnoreCase);

    /// <summary>AMD 走 SMU，Intel 要有 MSR 通道；厂商未知时照常显示，别把功能藏没了。</summary>
    public bool IsTuningSupported => !IsIntel || IntelTuning;

    /// <summary>调校方案、场景动作与启动异常拦截只对 SMU 生效；Intel 直接写 MSR，不经过方案。</summary>
    public bool IsTuningPlanSupported => !IsIntel;

    public string StatusText => IsConnected ? "Worker 已连接" : "Worker 未连接";

    public string DetailText => IsConnected
        ? (IsElevated ? "提权进程运行中，具备 SMU 写入权限" : "后台服务在线，SMU 当前不可写")
        : FailureDetail ?? "后台服务未响应，调校与调度都不会生效";

    /// <summary>连不上的原因不是「重新检测」能解决的，就别提示去点。</summary>
    public string ToolTipText => FailureDetail is null ? DetailText + "\n点击重新检测" : DetailText;
}
