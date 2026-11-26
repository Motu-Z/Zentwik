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
    int PhysicalCoreCount,
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

/// <summary>
/// 一个 Windows 电源方案。Guid 是系统标识，Name 是显示名。
/// Visible=false 表示方案存在且可读写，但不出现在枚举里（需激活才短暂可见）。
/// </summary>
public sealed record PowerSchemeDto(Guid Guid, string Name, bool Active, bool BuiltIn, bool Visible);

/// <summary>以模板派生新方案：Template 可为内置 GUID 或现有方案 GUID。</summary>
public sealed record PowerSchemeCreateRequest(Guid Template, string Name);

/// <summary>改名：Guid 定位方案，Name 是新显示名。</summary>
public sealed record PowerSchemeNameRequest(Guid Guid, string Name);

/// <summary>按 GUID 定位一个电源方案。</summary>
public sealed record PowerSchemeTarget(Guid Guid);

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

public sealed record CoreOffsetDto(int Core, int Offset);

public sealed record IntelTuningSnapshotDto(
    string Availability,
    string? Detail,
    string? Vendor,
    string? Platform,
    bool PersistsAcrossReboot,
    bool Simulated,
    int? VoltageOffsetMillivolts,
    int? TurboRatio1Core,
    int? TurboRatio8Cores);

public sealed record TuningSnapshotDto(
    string Availability,
    string? Detail,
    string? Vendor,
    string? Platform,
    bool PersistsAcrossReboot,
    bool Simulated,
    IReadOnlyList<CoreOffsetDto> Cores);
