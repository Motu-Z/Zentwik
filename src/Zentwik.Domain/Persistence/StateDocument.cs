using System.Text.Json;
using System.Text.Json.Serialization;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Domain.Settings;

namespace Zentwik.Domain.Persistence;

/// <summary>版本高于当前支持时拒绝加载并保留原文件，不做尽力解析。</summary>
public sealed record StateDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public UserSettings Settings { get; init; } = new();

    public IReadOnlyList<Plan> Plans { get; init; } = [];

    public IReadOnlyList<Scene> Scenes { get; init; } = [];

    /// <summary>状态账本快照。M1 只写不恢复；跨进程重启的恢复语义随自动化引擎落地。</summary>
    public IReadOnlyList<LedgerEntry> Ledger { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(StateDocument))]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(CpuTuningPlan))]
[JsonSerializable(typeof(PowerPlan))]
[JsonSerializable(typeof(SchedulingPlan))]
[JsonSerializable(typeof(Scene))]
[JsonSerializable(typeof(LedgerEntry))]
[JsonSerializable(typeof(LedgerEntry[]))]
public sealed partial class DomainJson : JsonSerializerContext;
