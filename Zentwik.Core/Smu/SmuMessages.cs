namespace Zentwik.Core.Smu;

internal enum SmuFamily
{
    Unsupported = 0,
    Zen2,
    Zen3,
    Zen4,
    Zen5,
}

/// <summary>
/// 每代平台的 RSMU 命令号。数值为硬件协议常量，与固件版本无关。
/// </summary>
internal readonly record struct SmuMessages(uint SetMargin, uint GetMargin, uint GetBoostLimit, uint SetBoostLimit)
{
    public static SmuMessages For(SmuFamily family) => family switch
    {
        SmuFamily.Zen2 => new(0x0A, 0x7C, 0x6E, 0x00),
        SmuFamily.Zen3 => new(0x0A, 0x7C, 0x6E, 0x00),
        SmuFamily.Zen4 => new(0x06, 0xD5, 0x6E, 0x70),
        SmuFamily.Zen5 => new(0x06, 0xD5, 0x6E, 0x70),
        _ => default,
    };
}
