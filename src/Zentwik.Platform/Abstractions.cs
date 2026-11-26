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

/// <summary>
/// 一个 Windows 电源方案及其是否可见。Visible=false 表示方案键存在、可读写，
/// 但不出现在 <see cref="IPowerPolicyService.ListSchemes"/> 的枚举结果里（系统模板/被隐藏的方案）。
/// </summary>
public sealed record PowerSchemeEntry(Guid Guid, string FriendlyName, bool Visible);

public interface IPowerPolicyService
{
    PowerSchemeInfo? GetActiveScheme();

    IReadOnlyList<PowerSchemeInfo> ListSchemes();

    /// <summary>
    /// 列出全部方案，含不出现在枚举里的（系统模板、被激活过滤掉的方案）。
    /// 枚举结果只回答「此刻是否可见」，不能拿它判断方案是否存在——隐藏方案同样可读写。
    /// </summary>
    IReadOnlyList<PowerSchemeEntry> ListAllSchemes();

    /// <summary>切换活动电源方案。需要管理员权限；失败返回 false 并给出原因。</summary>
    bool TrySetActiveScheme(Guid scheme, out string? error);

    bool TryReadValue(Guid scheme, Guid subGroup, Guid setting, bool ac, out uint value, out string? error);

    bool TryWriteValue(Guid scheme, Guid subGroup, Guid setting, bool ac, uint value, out string? error);

    /// <summary>以现有方案为模板派生新方案，返回新 GUID；失败返回 null 并给出原因。</summary>
    Guid? DuplicateScheme(Guid template, out string? error);

    /// <summary>改方案显示名。缓冲区按 UTF-16 字节计且含结尾符。</summary>
    bool RenameScheme(Guid scheme, string name, out string? error);

    /// <summary>删除方案。活动方案须由上层先切走；内置方案由上层拒绝。</summary>
    bool DeleteScheme(Guid scheme, out string? error);

    /// <summary>枚举方案下的子组。用于把模板的设置项搬到派生品。</summary>
    IReadOnlyList<Guid> EnumerateSubgroups(Guid scheme);

    /// <summary>枚举某个子组下的设置项。</summary>
    IReadOnlyList<Guid> EnumerateSettings(Guid scheme, Guid subGroup);

    /// <summary>读方案说明。返回原始字符串（可能是 <c>@dll,-id,文本</c> 形式的间接引用）。</summary>
    string? ReadDescription(Guid scheme);

    /// <summary>写方案说明。缓冲区按 UTF-16 字节计且含结尾符。</summary>
    bool TryWriteDescription(Guid scheme, string description, out string? error);
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

/// <summary>AMD Curve Optimizer 值：有符号整数步进，负值降压，没有物理单位。</summary>
public sealed record CoreOffset(int Core, int Offset);

public interface ICpuTuningProvider
{
    CpuTuningStatus Query();

    /// <summary>部分核读不到时仍返回已读到的核，失败明细走 error；全部失败才返回 false。</summary>
    bool TryReadOffsets(out IReadOnlyList<CoreOffset> offsets, out string? error);

    bool TryWriteOffset(int core, int offset, out string? error);
}

/// <summary>可选 CCD 拓扑查询接口，用于按 CCD 分层解析 Curve Optimizer。</summary>
public interface ICpuCcdTopologyProvider
{
    bool TryGetCoreCcd(int coreIndex, out int ccdIndex);
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

/// <summary>进程创建时间（UTC ticks）用于区分 PID 复用：同一 PID 的新进程不是账本记录的那个。</summary>
public sealed record ProcessTarget(int ProcessId, string Name, long StartedAtTicks = 0);

public interface IProcessScheduler
{
    IReadOnlyList<ProcessTarget> Find(string name);

    bool TryRead(int processId, out string? cpuMask, out string? priority, out string? error);

    bool TryApply(int processId, string? cpuMask, string? priority, out string? error);

    /// <summary>读取进程创建时间；进程不存在或已被复用为其他进程时返回 false。</summary>
    bool TryGetStartTicks(int processId, out long ticks);
}

/// <summary>可选 CPU Sets 调度接口（Get/SetProcessDefaultCpuSets）。</summary>
public interface IProcessCpuSetScheduler
{
    bool IsCpuSetsSupported(out string? detail);

    bool TryReadCpuSets(int processId, out string? cpuSets, out string? error);

    bool TryApplyCpuSets(int processId, string? cpuSets, out string? error);
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