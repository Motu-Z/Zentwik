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

    // Intel MSR 0x150 核心电压偏移（mV，负值为降压，仅当前会话有效）。
    public int? IntelVoltageOffsetMillivolts { get; init; }
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

/// <summary>
/// Curve Optimizer 分层范围定义（见 knowledge/hardware/cpu/amd-curve-optimizer.md §5）：
/// L1（固件/AGESA 实际范围）AMD 未公开，观测负向常达 -50、正向多为 +30，二者不对称；
/// L2（软件输入钳制）为 Zentwik 的不对称软限制，不冒充固件上限。
/// </summary>
public static class CurveOptimizerBounds
{
    public const int L2MinOffset = -50;
    public const int L2MaxOffset = 30;

    public static bool IsWithinL2(int offset) => offset is >= L2MinOffset and <= L2MaxOffset;

    public static string FormatL2Rejection(int offset) =>
        $"CO 值 {offset} 超出 L2 软件输入钳制范围 [{L2MinOffset}, +{L2MaxOffset}]（不对称软限制；AMD 未公开 L1 固件/AGESA 范围）。";
}

/// <summary>
/// Intel MSR 0x150 电压偏移输入范围（仅当前会话有效，重启由固件恢复）。
/// </summary>
public static class IntelVoltageOffsetBounds
{
    public const int MinMillivolts = -150;
    public const int MaxMillivolts = 100;

    public static bool IsWithinRange(int millivolts) => millivolts is >= MinMillivolts and <= MaxMillivolts;

    public static string FormatRejection(int millivolts) =>
        $"Intel 电压偏移 {millivolts} mV 超出允许范围 [{MinMillivolts}, +{MaxMillivolts}] mV。";
}
