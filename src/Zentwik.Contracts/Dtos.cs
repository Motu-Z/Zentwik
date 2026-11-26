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

/// <summary>删除电源方案。NoBackup=true 表示不留备份（默认留）。</summary>
public sealed record PowerSchemeDeleteRequest(Guid Guid, bool NoBackup = false);

/// <summary>删除结果：Backup 为删除前自动生成的备份，--no-backup 时为 null。</summary>
public sealed record PowerSchemeDeleteResultDto(Guid Guid, bool Deleted, PowerSchemeBackupDto? Backup);

/// <summary>
/// 一份方案备份。Id 即备份文件名，用它恢复；PortablePath 是附带的 .pow 原件，可能为 null。
/// Visible 为 null 表示备份未记录原方案的可见性。
/// </summary>
public sealed record PowerSchemeBackupDto(
    string Id,
    string Path,
    Guid SourceGuid,
    string Name,
    bool? Visible,
    DateTimeOffset CreatedAt,
    int SettingCount,
    int UnreadableCount,
    string? PortablePath);

/// <summary>从备份恢复：Backup 可为备份 ID，也可为备份文件路径。</summary>
public sealed record PowerSchemeRestoreRequest(string Backup);

/// <summary>
/// 恢复结果：新建的方案与写入统计。Failures 只列前若干条原因。
/// SourceVisible 是备份记录的原方案可见性，Scheme.Visible 是恢复后实测值。
/// </summary>
public sealed record PowerSchemeRestoreResultDto(
    PowerSchemeDto Scheme,
    int Applied,
    int Failed,
    string[] Failures,
    bool? SourceVisible);

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
