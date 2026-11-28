using System.Text.Json.Serialization;

namespace Zentwik.Domain.Plans;

public enum PlanApplyState
{
    NotApplied,
    Applied,
    Partial,
    Failed,

    /// <summary>写入了但无法确认实际值（如无回读通道），不得当作成功或失败。</summary>
    Unknown,
}

public sealed record PlanApplyRecord
{
    public PlanApplyState State { get; init; }

    public DateTimeOffset? AppliedAt { get; init; }

    // 由提供者能力决定：AMD CO 写入固件持久，Intel MSR 仅当前会话有效。
    public bool PersistsAcrossReboot { get; init; } = true;

    public string? Outcome { get; init; }
}

// 判别字段必须叫 planType：kind 已被 Kind 占用，同名会让序列化在首次写盘时抛元数据冲突。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "planType")]
[JsonDerivedType(typeof(CpuTuningPlan), "cpuTuning")]
[JsonDerivedType(typeof(PowerPlan), "power")]
[JsonDerivedType(typeof(SchedulingPlan), "scheduling")]
public abstract record Plan
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public int Revision { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public PlanApplyRecord? LastApply { get; init; }

    public abstract PlanKind Kind { get; }
}
