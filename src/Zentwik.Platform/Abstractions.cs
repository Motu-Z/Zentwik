using Zentwik.Domain.Capabilities;

namespace Zentwik.Platform;

/// <summary>时间戳由采样源给出，接收端不得用本地时钟替代。</summary>
public sealed record TelemetrySample(
    DateTimeOffset Timestamp,
    double CpuTotalPercent,
    ulong MemoryTotalBytes,
    ulong MemoryAvailableBytes,
    int ProcessCount,
    int ProcessorCount);

public interface ITelemetrySource
{
    /// <summary>实现不得外泄异常；失败返回上一帧。</summary>
    TelemetrySample Sample();
}

public sealed record PowerSchemeInfo(Guid Guid, string FriendlyName);

public interface IPowerPolicyService
{
    PowerSchemeInfo? GetActiveScheme();

    IReadOnlyList<PowerSchemeInfo> ListSchemes();

    /// <summary>切换活动电源方案。需要管理员权限；失败返回 false 并给出原因。</summary>
    bool TrySetActiveScheme(Guid scheme, out string? error);

    bool TryReadValue(Guid scheme, Guid subGroup, Guid setting, bool ac, out uint value, out string? error);

    bool TryWriteValue(Guid scheme, Guid subGroup, Guid setting, bool ac, uint value, out string? error);
}

public enum HardwareAvailability
{
    Available,
    NotSupported,
    DriverUnavailable,
    ModuleMissing,
    PrivilegeMissing,
    ProbeFailed,

    /// <summary>通道可用，但平台拒绝了这项具体操作（例如固件锁定 Curve Optimizer）。</summary>
    Rejected,
}

public sealed record CpuTuningStatus(
    HardwareAvailability Availability,
    string? Detail,
    string? Vendor,
    string? Platform,
    bool PersistsAcrossReboot,
    bool Simulated);

public sealed record CoreOffset(int Core, int Millivolts);

public interface ICpuTuningProvider
{
    CpuTuningStatus Query();

    /// <summary>部分核读不到时仍返回已读到的核，失败明细走 error；全部失败才返回 false。</summary>
    bool TryReadOffsets(out IReadOnlyList<CoreOffset> offsets, out string? error);

    bool TryWriteOffset(int core, int millivolts, out string? error);
}

/// <summary>Intel 调校是会话级的：写入只改 MSR，重启后由固件恢复。</summary>
public sealed record IntelTuningState(
    int VoltageOffsetMillivolts,
    int TurboRatio1Core,
    int TurboRatio8Cores);

public interface IIntelTuningProvider
{
    CpuTuningStatus Query();

    bool TryRead(out IntelTuningState state, out string? error);

    /// <summary>写入核心电压偏移（负值为降压）。写入后必须回读一致。</summary>
    bool TryWriteVoltageOffset(int millivolts, out string? error);
}

public sealed record ProcessTarget(int ProcessId, string Name);public interface IProcessScheduler
{
    IReadOnlyList<ProcessTarget> Find(string name);

    bool TryRead(int processId, out string? cpuMask, out string? priority, out string? error);

    bool TryApply(int processId, string? cpuMask, string? priority, out string? error);
}

public interface IPowerStatus
{
    /// <summary>null 表示无法判定（例如台式机无电池且状态未知）。</summary>
    bool? OnAcPower { get; }
}

/// <summary>系统启动时刻。用来区分"服务重启"与"整机重启"：固件持久化的写入在后者之后仍然有效。</summary>
public interface IBootInfo
{
    DateTimeOffset BootTimeUtc { get; }
}

public interface ICapabilityProbe
{
    string CapabilityId { get; }

    CapabilityDescriptor Probe();
}

public interface ISystemInfo
{
    string MachineName { get; }

    bool IsElevated { get; }

    int ProcessorCount { get; }

    /// <summary>物理核数（不含 SMT 兄弟线程）。SMU 的 CO 与核掩码按物理核编号。</summary>
    int PhysicalCoreCount { get; }

    int ProcessorGroups { get; }

    string? CpuVendor { get; }

    string? CpuName { get; }

    /// <summary>形如 AMD64 Family 25 Model 97 Stepping 2，用于判定 SMU 命令族。</summary>
    string? CpuIdentifier { get; }
}
