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
    bool RunningAsService,
    bool Simulated,
    int ProcessorCount,
    int ProcessorGroups,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    string DataDirectory,
    string? ActiveSceneId,
    int PlanCount,
    int SceneCount,
    int LedgerEntryCount);

public sealed record PlanSummaryDto(
    string Id,
    string Kind,
    string Name,
    int Revision,
    string State,
    DateTimeOffset? AppliedAt,
    bool PersistsAcrossReboot,
    string? Outcome);

public sealed record SceneSummaryDto(
    string Id,
    string Name,
    int Priority,
    bool Enabled,
    IReadOnlyList<string> TargetPlanIds,
    DateTimeOffset? LastActivatedAt,
    bool Active,
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

public sealed record IdRequest(string Id);

public sealed record LogRequest(int Limit);

public sealed record TargetOutcomeDto(
    string Target,
    string? Previous,
    string? Desired,
    string? Actual,
    string Status,
    string? Failure);

public sealed record PlanApplyResultDto(
    string PlanId,
    string State,
    DateTimeOffset? AppliedAt,
    bool PersistsAcrossReboot,
    string? Outcome,
    IReadOnlyList<TargetOutcomeDto> Targets);

public sealed record SceneActivationDto(
    string SceneId,
    bool Active,
    string Outcome,
    IReadOnlyList<string> PlanIds);

public sealed record ModuleDto(
    string Id,
    string Name,
    string State,
    string? Detail,
    IReadOnlyList<string> Capabilities);

public sealed record LogEntryDto(
    DateTimeOffset Timestamp,
    string Level,
    string Message);

public sealed record DoctorDto(
    string Version,
    int ProtocolVersion,
    string MachineName,
    bool Elevated,
    bool RunningAsService,
    bool Simulated,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    string DataDirectory,
    string StatePath,
    string LogDirectory,
    int PlanCount,
    int SceneCount,
    int LedgerEntryCount,
    string? ActiveSceneId,
    IReadOnlyList<CapabilityDto> Capabilities,
    IReadOnlyList<ModuleDto> Modules,
    IReadOnlyList<LogEntryDto> RecentErrors);

public sealed record CoreOffsetDto(int Core, int Millivolts);

public sealed record TuningSnapshotDto(
    string Availability,
    string? Detail,
    string? Vendor,
    string? Platform,
    bool PersistsAcrossReboot,
    bool Simulated,
    IReadOnlyList<CoreOffsetDto> Cores);
