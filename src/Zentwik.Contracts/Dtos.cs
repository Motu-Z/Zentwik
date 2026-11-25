namespace Zentwik.Contracts.Dto;

/// <summary>能力状态字符串常量。与 <c>Zentwik.Domain.Capabilities.CapabilityState</c> 一一对应。</summary>
public static class CapabilityStates
{
    public const string Unavailable = "unavailable";
    public const string Installed = "installed";
    public const string Enabled = "enabled";
    public const string Loaded = "loaded";
    public const string Running = "running";
    public const string Degraded = "degraded";
    public const string Failed = "failed";
}

public sealed record CapabilityDto(
    string Id,
    string State,
    string? Detail,
    IReadOnlyList<string> Requires);

public sealed record TelemetryDto(
    DateTimeOffset Timestamp,
    double CpuTotalPercent,
    ulong MemoryTotalBytes,
    ulong MemoryAvailableBytes,
    int ProcessCount,
    int ProcessorCount);

public sealed record AgentStatusDto(
    string Version,
    int ProtocolVersion,
    string MachineName,
    bool Elevated,
    int ProcessorCount,
    int ProcessorGroups,
    DateTimeOffset StartedAt,
    string DataDirectory,
    int PlanCount,
    int SceneCount);

public sealed record PlanSummaryDto(
    string Id,
    string Kind,
    string Name,
    int Revision,
    string State);

public sealed record SceneSummaryDto(
    string Id,
    string Name,
    int Priority,
    bool Enabled,
    IReadOnlyList<string> TargetPlanIds,
    string? LastOutcome);

public sealed record SettingsDto(
    string Language,
    string Theme,
    string Accent,
    double UiScale);

/// <summary>部分更新：null 字段表示不修改。</summary>
public sealed record SettingsPatch(
    string? Language,
    string? Theme,
    string? Accent,
    double? UiScale);

public sealed record SubscriptionRequest(int IntervalMs);

public sealed record SubscriptionAck(int IntervalMs);

public sealed record EmptyPayload;
