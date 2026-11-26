namespace Zentwik.Platform.Windows;

/// <summary>
/// Intel 调校用的两个 MSR 位域。0x150 是 OC Mailbox：高 32 位放命令字，bit63 为忙位，
/// 低 32 位 bit[31:21] 是 11 位有符号电压调整量（1/1024 V，负值降压）；
/// 0x1AD 每 8 位一个睿频倍频，bit[7:0] 为单核、bit[63:56] 为八核。
/// </summary>
public static class IntelMsrBits
{
    public const uint OcMailbox = 0x150;
    public const uint TurboRatioLimit = 0x1AD;

    /// <summary>邮箱命令字：0x10 读、0x11 写，低 8 位叠加电压平面位。</summary>
    public const uint ReadCommand = 0x8000_0010;
    public const uint WriteCommand = 0x8000_0011;

    public const int CorePlane = 0;
    public const int GraphicsPlane = 1 << 8;
    public const int CachePlane = 1 << 9;

    private const long BusyBit = long.MinValue;
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

    /// <summary>读命令：高 32 位为命令字与平面位，低 32 位由硬件写回数据。</summary>
    public static long BuildReadCommand(int planeBit = CorePlane) =>
        (long)(uint)(ReadCommand | (planeBit & 0x3FF)) << 32;

    /// <summary>写命令：命令字在高端，电压定点值放在低 32 位的 bit[31:21]。</summary>
    public static long BuildWriteCommand(int millivolts, int planeBit = CorePlane) =>
        ((long)(uint)(WriteCommand | (planeBit & 0x3FF)) << 32)
        | ((long)(uint)(OffsetCode(millivolts) & VoltageMask) << VoltageShift);

    public static bool IsBusy(long msrValue) => (msrValue & BusyBit) != 0;

    /// <summary>coreCount 取 1..8，对应 MSR 中从低到高的 8 个倍频字段。</summary>
    public static int DecodeTurboRatio(long msrValue, int coreCount)
    {
        var shift = (Math.Clamp(coreCount, 1, 8) - 1) * 8;
        return (int)((msrValue >> shift) & 0xFF);
    }

    /// <summary>毫伏换算成 1/1024 V 定点码，供写入与可表示性检查共用。</summary>
    public static int OffsetCode(int millivolts) =>
        (int)Math.Round(millivolts / MillivoltsPerStep, MidpointRounding.AwayFromZero);
}
