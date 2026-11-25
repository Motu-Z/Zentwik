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

    /// <summary>切换活动电源方案。需要管理员权限；失败返回 false 并给出原因。</summary>
    bool TrySetActiveScheme(Guid scheme, out string? error);
}

/// <summary>能力探测。探测只读，不产生写入副作用。</summary>
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

    int ProcessorGroups { get; }

    string? CpuVendor { get; }

    string? CpuName { get; }
}
