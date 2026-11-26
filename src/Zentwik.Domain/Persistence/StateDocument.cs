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

    public string? OwnerSid { get; init; }

    public UserSettings Settings { get; init; } = new();

    public IReadOnlyList<Plan> Plans { get; init; } = [];

    public IReadOnlyList<Scene> Scenes { get; init; } = [];

    public IReadOnlyList<LedgerEntry> Ledger { get; init; } = [];

    /// <summary>显式启用的可选模块。非可选模块不受此列表影响，永远视为启用。</summary>
    public IReadOnlyList<string> EnabledOptionalModules { get; init; } = [];

    public DateTimeOffset? LastBootTimeUtc { get; init; }
}
