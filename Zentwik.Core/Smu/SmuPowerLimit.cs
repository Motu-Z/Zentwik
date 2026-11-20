namespace Zentwik.Core.Smu;

/// <summary>
/// 功耗限制的 RSMU 命令组。按代号分两套：APU 与 Strix 系一组，Dragon Range / Fire Range 一组。
/// </summary>
internal readonly record struct SmuPowerLimitMessages(uint Stapm, uint Fast, uint Slow)
{
    private static readonly SmuPowerLimitMessages GroupApu = new(0x14, 0x15, 0x16);
    private static readonly SmuPowerLimitMessages GroupHx = new(0x4F, 0x3E, 0x5F);

    public bool IsSupported => Stapm != 0;

    public static SmuPowerLimitMessages For(uint familyId, uint modelId)
    {
        if (familyId == 0x19 && modelId == 0x61)
            return GroupHx;

        if (familyId == 0x1A && modelId == 0x44)
            return GroupHx;

        return (familyId, modelId) switch
        {
            (0x17, 0x60 or 0x68 or 0x90 or 0x91 or 0xA0) => GroupApu,
            (0x19, 0x40 or 0x44 or 0x50 or 0x74 or 0x75 or 0x78) => GroupApu,
            (0x1A, 0x20 or 0x24 or 0x60 or 0x70) => GroupApu,
            _ => default
        };
    }
}

/// <summary>PM 表读取结果。功率单位为毫瓦，null 表示读不到。</summary>
internal sealed class SmuPowerLimitSnapshot
{
    public bool Supported { get; set; }
    public bool Available { get; set; }
    public string? Error { get; set; }
    public int? StapmMilliWatts { get; set; }
    public int? FastMilliWatts { get; set; }
    public int? SlowMilliWatts { get; set; }

    public bool HasAny => StapmMilliWatts.HasValue || FastMilliWatts.HasValue || SlowMilliWatts.HasValue;
}

internal sealed class SmuPowerLimitResult
{
    public bool AllSucceeded { get; set; }
    public List<string> Failures { get; } = new();
    public Dictionary<string, int> Applied { get; } = new();
}

/// <summary>PM 表固定段：前六项在多个表版本间偏移不变。</summary>
internal static class SmuPowerLimitCodec
{
    /// <summary>寄存器步进换算：PM 表里是瓦，界面与命令用毫瓦。</summary>
    public const int MilliWattsPerWatt = 1000;

    public const int MinimumMilliWatts = 5000;
    public const int MaximumMilliWatts = 300000;

    /// <summary>回读容差，覆盖 PM 表按 1 W 取整的舍入。</summary>
    public const int ReadBackTolerance = 1500;

    /// <summary>STAPM 限制在 PM 表第一项的 float。</summary>
    public static int? FromWatts(long qword, int index)
    {
        int bits = (int)((qword >> (index * 32)) & 0xFFFFFFFF);
        float watts = BitConverter.Int32BitsToSingle(bits);
        if (float.IsNaN(watts) || watts <= 0 || watts > 1000)
            return null;

        return (int)Math.Round(watts * MilliWattsPerWatt);
    }
}
