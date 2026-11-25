namespace Zentwik.Domain.Plans;

public enum PlanKind
{
    CpuTuning,
    Power,
    Scheduling,
}

public sealed record CoreTuning
{
    public required int CoreIndex { get; init; }

    // Curve Optimizer 值：有符号整数步进，负值降压，没有物理单位。null 表示该核不参与本次应用。
    public int? Offset { get; init; }

    public int? MaxFrequencyMhz { get; init; }
}

public sealed record CpuTuningPlan : Plan
{
    public override PlanKind Kind => PlanKind.CpuTuning;

    // 空表示未绑定平台，应用时按探测结果解析。
    public string? PlatformId { get; init; }

    public IReadOnlyList<CoreTuning> Cores { get; init; } = [];

    public int? FMaxLimitMhz { get; init; }
}

public sealed record PowerSettingEntry
{
    public required string SettingId { get; init; }

    public string? SubGroupId { get; init; }

    public string? AcValue { get; init; }

    public string? DcValue { get; init; }
}

public sealed record PowerPlan : Plan
{
    public override PlanKind Kind => PlanKind.Power;

    // null 表示只覆盖参数、不切换方案。
    public Guid? BaseSchemeGuid { get; init; }

    public IReadOnlyList<PowerSettingEntry> Settings { get; init; } = [];
}

public sealed record SchedulingRule
{
    public required string ProcessMatch { get; init; }

    public string? CpuMask { get; init; }

    public string? Priority { get; init; }

    // 进程自行改回设置时是否重新施加。
    public bool Enforce { get; init; } = true;
}

public sealed record SchedulingPlan : Plan
{
    public override PlanKind Kind => PlanKind.Scheduling;

    public IReadOnlyList<SchedulingRule> Rules { get; init; } = [];
}
