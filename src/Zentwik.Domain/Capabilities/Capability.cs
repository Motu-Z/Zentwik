namespace Zentwik.Domain.Capabilities;

/// <summary>不能用 bool 表达能力可用性：平台不支持、驱动缺失、探测失败是不同事实。</summary>
public enum CapabilityState
{
    Unavailable,
    Installed,
    Enabled,
    Loaded,
    Running,
    Degraded,
    Failed,
}

public sealed record CapabilityDescriptor
{
    public required string Id { get; init; }

    public CapabilityState State { get; init; }

    public string? Detail { get; init; }

    public IReadOnlyList<string> Requires { get; init; } = [];

    public bool IsUsable => State is CapabilityState.Running or CapabilityState.Degraded;
}

public static class CapabilityIds
{
    public const string AmdSmu = "hardware.amd.smu";
    public const string IntelMsr = "hardware.intel.msr";

    /// <summary>硬件功耗墙（PPT FAST/SLOW、TDC、EDC）。属可选组件，默认不启用。</summary>
    public const string AmdPowerLimit = "hardware.amd.power.limit";
    public const string PowerPolicy = "system.power.policy";
    public const string CpuSets = "system.scheduling.cpusets";
    public const string AffinityMask = "system.scheduling.affinity";
    public const string TelemetryBasic = "monitor.telemetry.system";

    /// <summary>CPU 封装温度与封装功耗。只读通道，不需要额外授权。</summary>
    public const string TelemetryCpuSensors = "monitor.telemetry.cpu.sensors";
    public const string SceneEngine = "automation.scene.engine";
}
