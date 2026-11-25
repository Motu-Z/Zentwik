using System.Text.Json.Serialization;

namespace Zentwik.Domain.Plans;

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

    public abstract PlanKind Kind { get; }
}
