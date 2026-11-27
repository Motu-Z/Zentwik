namespace Zentwik.Platform.Windows;

/// <summary>
/// SMN 温度与 RAPL 能量计数器的纯换算。口径见研究仓库
/// <c>knowledge/hardware/cpu/amd-power-limit.md</c> §8.3，与 LibreHardwareMonitor 同源。
/// </summary>
internal static class AmdSensorDecoder
{
    private const uint RangeSelectMask = 0x80000;
    private const uint TJunctionSelectMask = 0x30000;

    /// <summary>
    /// THM_TCON_CUR_TMP[31:21] 每单位 0.125 °C；RANGE_SEL 置位或 TJ_SEL=0b11 时读数整体高 49 °C。
    /// 返回的是 Tctl（k10temp 口径），老型号的 Tdie 修正未实现。
    /// </summary>
    public static double DecodeTemperatureCelsius(uint raw)
    {
        var celsius = (raw >> 21) * 0.125;
        var offset = (raw & RangeSelectMask) != 0 || (raw & TJunctionSelectMask) == TJunctionSelectMask;
        return offset ? celsius - 49d : celsius;
    }

    /// <summary>MSR_PWR_UNIT[12:8]：每计数 2^-ESU 焦耳。本机 ESU=16 → 15.3 μJ。</summary>
    public static int EnergyExponent(uint powerUnit) => (int)((powerUnit >> 8) & 0x1F);

    /// <summary>32 位累加器按无符号回绕求差（约百秒级回绕一次）。</summary>
    public static uint EnergyDelta(uint previous, uint current) => unchecked(current - previous);

    /// <summary>能量差 ÷ 经过时间 = 瓦。单位是焦耳不是微焦：按微焦算会放大 2^ESU 倍。</summary>
    public static double EnergyToWatts(uint delta, int exponent, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return 0d;
        }

        var joules = delta * Math.Pow(0.5, exponent);
        return joules / elapsed.TotalSeconds;
    }

    /// <summary>高于此值只可能是计数器回绕或跨核读数，不是真实频率。</summary>
    public const double MaxPlausibleMegahertz = 20000d;

    /// <summary>
    /// APERF 随核心实际时钟累加，差值 ÷ 经过时间 = 窗口内平均频率（MHz）。
    /// 这是唯一不依赖 P-state 读数的口径：P-state 会在读取瞬间被自己唤醒抬高，比值法实测经常 &gt;1。
    /// </summary>
    /// <param name="awake">窗口内 MPERF 是否增长。整窗睡眠的核两个计数器都不动，没有频率可报。</param>
    public static double? AverageMegahertz(ulong aperfDelta, bool awake, TimeSpan elapsed)
    {
        if (!awake || elapsed <= TimeSpan.Zero || aperfDelta == 0)
        {
            return null;
        }

        var megahertz = aperfDelta / (elapsed.TotalSeconds * 1_000_000d);
        return megahertz is > 0 and <= MaxPlausibleMegahertz ? megahertz : null;
    }
}
