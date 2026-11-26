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

    // 可选 CCD 编号（仅在无硬件拓扑映射时用于展示与回退解析）。
    public int? CcdIndex { get; init; }

    // 为 true 时表示显式保持自动/默认，不继承 DefaultOffset 或 CcdOffsets。
    public bool Auto { get; init; }

    // Curve Optimizer 值：有符号整数步进，负值降压，没有物理单位。null 表示未单独指定单核偏移。
    public int? Offset { get; init; }

    public int? MaxFrequencyMhz { get; init; }
}

public sealed record CpuTuningPlan : Plan
{
    public override PlanKind Kind => PlanKind.CpuTuning;

    // 空表示未绑定平台，应用时按探测结果解析。
    public string? PlatformId { get; init; }

    // 全核默认 CO 偏移（最低优先级，低于 CCD 覆盖与单核覆盖）。
    public int? DefaultOffset { get; init; }

    // 按 CCD 覆盖 CO 偏移（优先级高于 DefaultOffset，低于单核覆盖或 Auto）。
    public IReadOnlyDictionary<int, int>? CcdOffsets { get; init; }

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

    // CPU Sets 逻辑处理器集合（如 "0,1,2,3" 或 "0-3,8-11"）。null 表示不管理 CPU Sets。
    public string? CpuSets { get; init; }

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

public enum CoValueSource
{
    Unset,
    AutoOverride,
    Default,
    Ccd,
    PerCore,
}

public sealed record ResolvedCoreCo(
    int CoreIndex,
    int? CcdIndex,
    int? EffectiveOffset,
    CoValueSource Source);

public sealed record CoAsymmetrySummary(
    bool IsAsymmetric,
    int? MinOffset,
    int? MaxOffset,
    int ConfiguredCoreCount,
    int AutoOverrideCount,
    IReadOnlyList<int> DistinctOffsets);

/// <summary>
/// Curve Optimizer 分层解析与不对称识别：
/// 优先级从高到低为 单核 Auto > 单核 Offset > CCD 覆盖 > 全核 DefaultOffset > 未配置。
/// </summary>
public static class CurveOptimizerConfig
{
    public static bool HasAnyCo(CpuTuningPlan plan) =>
        plan.DefaultOffset is not null
        || (plan.CcdOffsets is { Count: > 0 })
        || plan.Cores.Any(static core => core.Offset is not null || core.Auto);

    public static IReadOnlyList<ResolvedCoreCo> Resolve(
        CpuTuningPlan plan,
        IReadOnlyList<int> coreIndices,
        Func<int, int?>? ccdLookup = null)
    {
        var byCore = plan.Cores.ToDictionary(static core => core.CoreIndex);
        var resolved = new List<ResolvedCoreCo>(coreIndices.Count);
        foreach (var coreIndex in coreIndices.Distinct().Order())
        {
            byCore.TryGetValue(coreIndex, out var entry);
            var ccd = ccdLookup?.Invoke(coreIndex) ?? entry?.CcdIndex;

            if (entry?.Auto == true)
            {
                resolved.Add(new ResolvedCoreCo(coreIndex, ccd, null, CoValueSource.AutoOverride));
                continue;
            }

            if (entry?.Offset is { } coreOffset)
            {
                resolved.Add(new ResolvedCoreCo(coreIndex, ccd, coreOffset, CoValueSource.PerCore));
                continue;
            }

            if (ccd is { } ccdIdx && plan.CcdOffsets is not null && plan.CcdOffsets.TryGetValue(ccdIdx, out var ccdOffset))
            {
                resolved.Add(new ResolvedCoreCo(coreIndex, ccdIdx, ccdOffset, CoValueSource.Ccd));
                continue;
            }

            if (plan.DefaultOffset is { } defaultOffset)
            {
                resolved.Add(new ResolvedCoreCo(coreIndex, ccd, defaultOffset, CoValueSource.Default));
                continue;
            }

            resolved.Add(new ResolvedCoreCo(coreIndex, ccd, null, CoValueSource.Unset));
        }

        return resolved;
    }

    public static CoAsymmetrySummary AnalyzeAsymmetry(
        CpuTuningPlan plan,
        IReadOnlyList<int>? coreIndices = null,
        Func<int, int?>? ccdLookup = null)
    {
        if (coreIndices is not null && coreIndices.Count > 0)
        {
            var resolved = Resolve(plan, coreIndices, ccdLookup);
            var activeValues = resolved
                .Where(static r => r.EffectiveOffset is not null)
                .Select(static r => r.EffectiveOffset!.Value)
                .ToArray();
            var distinct = activeValues.Distinct().Order().ToArray();
            var autoCount = resolved.Count(static r => r.Source == CoValueSource.AutoOverride);
            var hasMixedUnset = activeValues.Length > 0 && resolved.Any(static r => r.EffectiveOffset is null);
            var asymmetric = distinct.Length > 1 || (autoCount > 0 && activeValues.Length > 0) || hasMixedUnset;
            return new CoAsymmetrySummary(
                asymmetric,
                distinct.Length > 0 ? distinct[0] : null,
                distinct.Length > 0 ? distinct[^1] : null,
                activeValues.Length,
                autoCount,
                distinct);
        }

        var values = new List<int>();
        if (plan.DefaultOffset is { } def)
        {
            values.Add(def);
        }

        if (plan.CcdOffsets is not null)
        {
            values.AddRange(plan.CcdOffsets.Values);
        }

        var autoOverrides = 0;
        var perCoreValues = 0;
        foreach (var core in plan.Cores)
        {
            if (core.Auto)
            {
                autoOverrides++;
            }
            else if (core.Offset is { } offset)
            {
                values.Add(offset);
                perCoreValues++;
            }
        }

        var distinctStatic = values.Distinct().Order().ToArray();
        var isAsym = distinctStatic.Length > 1
            || (autoOverrides > 0 && distinctStatic.Length > 0)
            || (plan.CcdOffsets is { Count: > 0 } && (plan.DefaultOffset is not null || perCoreValues > 0 || plan.CcdOffsets.Count > 1));

        return new CoAsymmetrySummary(
            isAsym,
            distinctStatic.Length > 0 ? distinctStatic[0] : null,
            distinctStatic.Length > 0 ? distinctStatic[^1] : null,
            perCoreValues,
            autoOverrides,
            distinctStatic);
    }

    public static string FormatPlanSummary(CpuTuningPlan plan)
    {
        var parts = new List<string>();
        if (plan.DefaultOffset is { } def)
        {
            parts.Add($"default={def}");
        }

        if (plan.CcdOffsets is { Count: > 0 })
        {
            foreach (var pair in plan.CcdOffsets.OrderBy(static p => p.Key))
            {
                parts.Add($"ccd{pair.Key}={pair.Value}");
            }
        }

        foreach (var core in plan.Cores.OrderBy(static c => c.CoreIndex))
        {
            if (core.Auto)
            {
                parts.Add($"C{core.CoreIndex}:auto");
            }
            else if (core.Offset is { } offset)
            {
                parts.Add($"C{core.CoreIndex}:{offset}");
            }
        }

        var asym = AnalyzeAsymmetry(plan);
        if (asym.IsAsymmetric && asym.MinOffset is not null && asym.MaxOffset is not null)
        {
            parts.Add($"[asymmetric {asym.MinOffset}..{asym.MaxOffset}]");
        }

        if (plan.FMaxLimitMhz is { } fmax)
        {
            parts.Add($"fmax={fmax}MHz");
        }

        if (plan.IntelVoltageOffsetMillivolts is { } mv)
        {
            parts.Add($"intel={mv}mV");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// CPU Sets 规范解析、规范化与逻辑处理器映射校验（如 "0-3,8,10-11"）。
/// </summary>
public static class CpuSetSpec
{
    public static bool TryParse(string? spec, out IReadOnlyList<int> logicalIndices, out string? error)
    {
        logicalIndices = [];
        error = null;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "CPU Sets 规范不能为空。";
            return false;
        }

        var result = new SortedSet<int>();
        foreach (var rawToken in spec.Split(',', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                error = $"CPU Sets 包含空项：\"{spec}\"。";
                return false;
            }

            var dash = rawToken.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                if (!int.TryParse(rawToken, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var single) || single < 0)
                {
                    error = $"无法解析 CPU Sets 索引 \"{rawToken}\"。";
                    return false;
                }

                result.Add(single);
                continue;
            }

            var left = rawToken[..dash].Trim();
            var right = rawToken[(dash + 1)..].Trim();
            if (!int.TryParse(left, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var start)
                || !int.TryParse(right, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var end)
                || start < 0
                || end < start)
            {
                error = $"无法解析 CPU Sets 范围 \"{rawToken}\"。";
                return false;
            }

            for (var i = start; i <= end; i++)
            {
                result.Add(i);
            }
        }

        if (result.Count == 0)
        {
            error = "CPU Sets 未包含任何逻辑处理器。";
            return false;
        }

        logicalIndices = [.. result];
        return true;
    }

    public static string Format(IEnumerable<int> logicalIndices) =>
        string.Join(",", logicalIndices.Distinct().Order());

    public static bool TryNormalize(string? spec, out string normalized, out string? error)
    {
        if (!TryParse(spec, out var indices, out error))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = Format(indices);
        return true;
    }

    public static bool ValidateAndResolve(
        IReadOnlyList<int> requestedLogicalIndices,
        IReadOnlyDictionary<int, uint> logicalToCpuSetId,
        out uint[] cpuSetIds,
        out string? error)
    {
        cpuSetIds = [];
        error = null;

        if (requestedLogicalIndices.Count == 0)
        {
            error = "CPU Sets 未指定任何逻辑处理器。";
            return false;
        }

        var missing = requestedLogicalIndices.Where(idx => !logicalToCpuSetId.ContainsKey(idx)).ToArray();
        if (missing.Length > 0)
        {
            error = $"逻辑处理器 {string.Join(",", missing)} 不在系统 CPU Sets 拓扑中。";
            return false;
        }

        cpuSetIds = requestedLogicalIndices.Select(idx => logicalToCpuSetId[idx]).Distinct().ToArray();
        return true;
    }
}