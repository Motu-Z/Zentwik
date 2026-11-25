namespace Zentwik.Platform.Windows;

/// <summary>
/// Intel 调校用的两个 MSR 位域。数值为 Intel SDM / XTU 的硬件约定：
/// 0x150 的 bit[31:21] 是 11 位有符号电压调整量（1/1024 V，负值降压）；
/// 0x1AD 每 8 位一个睿频倍频，bit[7:0] 为单核、bit[63:56] 为八核。
/// </summary>
public static class IntelMsrBits
{
    public const uint OcMailbox = 0x150;
    public const uint TurboRatioLimit = 0x1AD;

    private const int VoltageShift = 21;
    private const long VoltageMask = 0x7FF;
    private const double MillivoltsPerStep = 1000.0 / 1024.0;

    public static int DecodeVoltageOffset(long msrValue)
    {
        var raw = (int)((msrValue >> VoltageShift) & VoltageMask);
        if ((raw & 0x400) != 0)
        {
            raw -= 0x800;
        }

        return (int)Math.Round(raw * MillivoltsPerStep, MidpointRounding.AwayFromZero);
    }

    /// <summary>把电压偏移写回 MSR，其余位保持原样。</summary>
    public static long EncodeVoltageOffset(long msrValue, int millivolts)
    {
        var raw = (int)Math.Round(millivolts / MillivoltsPerStep, MidpointRounding.AwayFromZero);
        var field = (long)(raw & (int)VoltageMask) << VoltageShift;
        return (msrValue & ~(VoltageMask << VoltageShift)) | field;
    }

    /// <summary>coreCount 取 1..8，对应 MSR 中从低到高的 8 个倍频字段。</summary>
    public static int DecodeTurboRatio(long msrValue, int coreCount)
    {
        var shift = (Math.Clamp(coreCount, 1, 8) - 1) * 8;
        return (int)((msrValue >> shift) & 0xFF);
    }
}
