namespace Zentwik.Domain.Power;

/// <summary>
/// 一次功耗墙设置请求。null 表示该项不改。
/// 单位：功率 W、电流 A（写入前转为 SMU 要求的 mW / mA）。
/// </summary>
/// <remarks>
/// 不含 STAPM：该命令在 MP1 邮箱，当前通道只能发 RSMU，未配置的命令号不能当作硬件不支持。
/// </remarks>
public sealed record PowerLimitRequest(int? FastWatts, int? SlowWatts, int? TdcAmps, int? EdcAmps)
{
    public bool IsEmpty => FastWatts is null && SlowWatts is null && TdcAmps is null && EdcAmps is null;
}

/// <summary>
/// PM 表里的当前生效上限。null 表示读不到；读不到不是「没有上限」。
/// STAPM 只读：本通道写不了，但读数可用于对照厂商固件的持续功耗策略。
/// </summary>
public sealed record PowerLimitCurrent(int? StapmWatts, int? FastWatts, int? SlowWatts, string Detail)
{
    public bool HasAny => StapmWatts is not null || FastWatts is not null || SlowWatts is not null;

    public static PowerLimitCurrent Unreadable(string detail) => new(null, null, null, detail);
}

/// <summary>
/// 出厂熔断的功耗/电流上限（SMU fused 值）。它不是当前生效值——
/// 当前生效值在 PM 表，固件可能在 fused 之内再限一层。
/// </summary>
public sealed record FusedPowerLimits(int? FastWatts, int? SlowWatts, int? TdcAmps, int? EdcAmps, string Detail)
{
    public bool HasAny => FastWatts is not null || SlowWatts is not null || TdcAmps is not null || EdcAmps is not null;

    public static FusedPowerLimits None(string detail) => new(null, null, null, null, detail);
}

/// <summary>单项写入状态。Applied 必须回读一致；Accepted 仅表示 SMU 收下了请求。</summary>
public enum PowerLimitWriteState
{
    /// <summary>写入后回读与请求一致（在容差内）。</summary>
    Applied,

    /// <summary>SMU 收下且回读有变化，但与请求不一致——固件按自身策略折算，不能算成功也不能算失败。</summary>
    Diverged,

    /// <summary>SMU 未报错，但该项没有可用的回读通道，无法确认生效。</summary>
    Accepted,

    /// <summary>写入被拒，或回读完全没变化。</summary>
    Rejected,
}

/// <summary>
/// 单项写入结果。Requested 是请求值，Actual 是 PM 表回读到值；Actual 为 null 表示该项没有回读通道。
/// </summary>
public sealed record PowerLimitWrite(
    string Item,
    int Requested,
    int? Actual,
    PowerLimitWriteState State,
    string? Detail);

/// <summary>写入汇总。RejectedReason 非空表示通道层面就不可用，此时 Items 为空。</summary>
public sealed record PowerLimitApplyResult(
    IReadOnlyList<PowerLimitWrite> Items,
    PowerLimitCurrent? Before,
    PowerLimitCurrent? After,
    string? RejectedReason,
    bool Simulated)
{
    public bool AnyApplied => Items.Any(static item => item.State == PowerLimitWriteState.Applied);

    public bool HasDiverged => Items.Any(static item => item.State == PowerLimitWriteState.Diverged);

    public bool HasRejected => Items.Any(static item => item.State == PowerLimitWriteState.Rejected);
}

/// <summary>功耗墙的取值范围。超出即拒绝，不静默钳到边界——用户要知道自己越界了。</summary>
public static class PowerLimitBounds
{
    public const int MinWatts = 1;
    public const int MaxWatts = 1000;
    public const int MinAmps = 1;
    public const int MaxAmps = 500;

    /// <summary>PM 表按 1 W 取整，固件还会滞后刷新，容差覆盖两者。</summary>
    public const int ReadBackToleranceWatts = 2;

    public static string? Validate(PowerLimitRequest request)
    {
        if (request.IsEmpty)
        {
            return "没有给出任何要修改的项。";
        }

        if (request.FastWatts is int fast && (fast < MinWatts || fast > MaxWatts))
        {
            return $"FAST 上限 {fast} W 超出 [{MinWatts}, {MaxWatts}]。";
        }

        if (request.SlowWatts is int slow && (slow < MinWatts || slow > MaxWatts))
        {
            return $"SLOW 上限 {slow} W 超出 [{MinWatts}, {MaxWatts}]。";
        }

        if (request.TdcAmps is int tdc && (tdc < MinAmps || tdc > MaxAmps))
        {
            return $"TDC 上限 {tdc} A 超出 [{MinAmps}, {MaxAmps}]。";
        }

        if (request.EdcAmps is int edc && (edc < MinAmps || edc > MaxAmps))
        {
            return $"EDC 上限 {edc} A 超出 [{MinAmps}, {MaxAmps}]。";
        }

        if (request.FastWatts is int f && request.SlowWatts is int s && s > f)
        {
            return $"SLOW 上限 {s} W 高于 FAST 上限 {f} W：短时上限不应低于长时上限。";
        }

        return null;
    }
}

/// <summary>
/// 「回读没变」的成因。只用于把说明写准，不对外暴露出新的写入状态——
/// 对使用者来说 Repeated / ScaledToSame / Unexplained 都是「回读没变化」，区别在谁能解释它。
/// </summary>
public enum PowerLimitReadBackReason
{
    /// <summary>回读与请求在容差内一致。</summary>
    Applied,

    /// <summary>回读发生了变化（与请求不符即固件折算）。</summary>
    Changed,

    /// <summary>本次请求值与此前成功写入的同一项相同，属重复应用。</summary>
    Repeated,

    /// <summary>按本机实测折算比例计算，请求值正好落在当前生效值上。</summary>
    ScaledToSame,

    /// <summary>回读与写前逐字相同，且手上信息解释不了。</summary>
    Unexplained,
}

/// <summary>把写入请求与 PM 表回读按状态分类；不猜固件为什么折算，只如实区分结果。</summary>
public static class PowerLimitReadBack
{
    /// <summary>
    /// <paramref name="knownScale"/> 是本机实测过的折算比例（actual / requested），
    /// <paramref name="lastRequested"/> 是此前成功写入的同一项请求值。
    /// 两者都允许为空：缺信息时不下结论，宁可落回 Rejected 也不谎报成功。
    /// </summary>
    public static PowerLimitWriteState Classify(
        int requested,
        int? actual,
        int? previous,
        double? knownScale = null,
        int? lastRequested = null) =>
        ClassifyDetailed(requested, actual, previous, knownScale, lastRequested).State;

    public static (PowerLimitWriteState State, PowerLimitReadBackReason Reason) ClassifyDetailed(
        int requested,
        int? actual,
        int? previous,
        double? knownScale = null,
        int? lastRequested = null)
    {
        if (actual is null)
        {
            return (PowerLimitWriteState.Accepted, PowerLimitReadBackReason.Unexplained);
        }

        if (Math.Abs(actual.Value - requested) <= PowerLimitBounds.ReadBackToleranceWatts)
        {
            return (PowerLimitWriteState.Applied, PowerLimitReadBackReason.Applied);
        }

        if (!previous.HasValue || previous.Value != actual.Value)
        {
            return (PowerLimitWriteState.Diverged, PowerLimitReadBackReason.Changed);
        }

        // 回读与写前逐字相同。先看是不是「重复应用同一设置」——这是最常见的一种。
        if (lastRequested == requested)
        {
            return (PowerLimitWriteState.Diverged, PowerLimitReadBackReason.Repeated);
        }

        // 再看是不是「折算后恰好等于原值」：用实测比例预测，不写死任何机型常数。
        if (knownScale is double scale
            && scale > 0
            && Math.Abs((requested * scale) - previous.Value) <= PowerLimitBounds.ReadBackToleranceWatts)
        {
            return (PowerLimitWriteState.Diverged, PowerLimitReadBackReason.ScaledToSame);
        }

        return (PowerLimitWriteState.Rejected, PowerLimitReadBackReason.Unexplained);
    }

    /// <summary>
    /// 用这次观察到的比例解释「已改变但与请求不符」。只报实测，不假设机型常数；没有 Diverged 项时返回 null。
    /// </summary>
    public static string? DescribeFirmwareScaling(IReadOnlyList<PowerLimitWrite> items)
    {
        var ratios = new List<double>(items.Count);
        foreach (var item in items)
        {
            if (item.State == PowerLimitWriteState.Diverged && item.Actual is int actual && actual > 0)
            {
                ratios.Add((double)actual / item.Requested);
            }
        }

        if (ratios.Count == 0)
        {
            return null;
        }

        if (ratios.Max() - ratios.Min() > 0.02)
        {
            return "固件对各项按不同比例折算，没有统一系数；以回读值为准。";
        }

        var percent = Math.Round(ratios.Average() * 100);
        return $"固件把写入值折算到约 {percent:0}% 后作为 CPU 上限生效。推测写入值是封装总功耗，CPU 只占其中一部分；"
            + "比例由固件决定，本工具不代为换算——同型号也可能因固件版本而变，换算会掩盖真实上限。";
    }
}
