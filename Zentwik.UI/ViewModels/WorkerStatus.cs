using System.Collections.Generic;
using System.ComponentModel;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Ipc;
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

    /// <summary>调校页在 AMD（SMU）与 Intel（MSR）上都存在；通道是否就绪交给页面内遮罩，没装驱动时也要能进页看到提示。</summary>
    public bool IsTuningSupported => true;

    private IReadOnlyList<CapabilityInfoPayload>? _capabilities;

    /// <summary>
    /// 应用能力快照。传 null 表示对端不支持能力查询（旧版 Worker），调用方退回厂商判断。
    /// </summary>
    public void ApplyCapabilities(IReadOnlyList<CapabilityInfoPayload>? capabilities)
    {
        _capabilities = capabilities;
        OnPropertyChanged(nameof(IsTuningPlanSupported));
    }

    /// <summary>
    /// 调校方案是否可用。以 Worker 的能力快照为准：Intel 模块不提供该能力。
    ///
    /// 只看「安装 / 启用 / 硬件支持」三项，不看运行可用性：驱动晚就绪时方案页不该闪一下再出现，
    /// 通道是否就绪由页面内的遮罩单独表达。
    /// </summary>
    public bool IsTuningPlanSupported
    {
        get
        {
            if (_capabilities is not null)
            {
                foreach (CapabilityInfoPayload capability in _capabilities)
                {
                    if (capability.Id != CapabilityIds.TuningPlan)
                        continue;

                    if (capability.States.Contains("installed")
                        && capability.States.Contains("enabled")
                        && capability.States.Contains("hardwareSupported"))
                    {
                        return true;
                    }
                }

                return false;
            }

            // 旧版 Worker 没有能力查询：Intel 不经过方案，其余按支持处理。
            return !IsIntel;
        }
    }

    public string StatusText => IsConnected ? "Worker 已连接" : "Worker 未连接";

    public string DetailText => IsConnected
        ? (IsIntel
            ? (IsElevated ? "提权进程运行中，具备 MSR 写入权限" : "后台服务在线，MSR 当前不可写")
            : (IsElevated ? "提权进程运行中，具备 SMU 写入权限" : "后台服务在线，SMU 当前不可写"))
        : FailureDetail ?? "后台服务未响应，调校与调度都不会生效";

    /// <summary>连不上的原因不是「重新检测」能解决的，就别提示去点。</summary>
    public string ToolTipText => FailureDetail is null ? DetailText + "\n点击重新检测" : DetailText;
}
