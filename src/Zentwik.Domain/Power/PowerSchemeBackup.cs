namespace Zentwik.Domain.Power;

/// <summary>
/// 备份里的一个设置项。某一侧读不出来时对应字段为 null：AC 与 DC 是两套独立取值，
/// 一侧读失败不该让另一侧也丢掉。
/// </summary>
public sealed record PowerSchemeSettingValue(Guid SubGroup, Guid Setting, uint? Ac, uint? Dc);

/// <summary>
/// 一个电源方案的快照。只保存 powrprof 能读到的内容。
/// GUID 与可见性记下来是为了说明来源，恢复时都还原不了：系统会给新方案分配新 GUID，
/// 可见性由系统按当前策略决定，不受备份文件影响。
/// </summary>
public sealed record PowerSchemeBackupDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid SourceGuid { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>备份时该方案是否出现在枚举结果里（false = 隐藏）。仅供说明，不参与恢复。</summary>
    public bool Visible { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string? MachineName { get; init; }

    public IReadOnlyList<PowerSchemeSettingValue> Settings { get; init; } = [];
}
