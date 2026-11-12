namespace Zentwik.Core.Smu;

/// <summary>
/// CO 核心掩码：bit[31-28] CCD，bit[27-24] CCX，bit[23-20] 核内序号。
/// family 17h 起 CCX 恒为 0，改用 8 核粒度的核内序号。
/// </summary>
internal static class CoreMask
{
    public static uint Encode(uint core, uint ccd, uint ccx, uint familyId)
    {
        if (familyId > 0x17)
            return (ccd << 28) | ((core % 8) << 20);

        return (ccd << 28) | ((ccx % 2) << 24) | ((core % 4) << 20);
    }

    /// <summary>CO 参数低 16 位为偏移量，负数按补码下发。</summary>
    public static uint WithMargin(uint mask, int margin) => (mask & 0xFFF0_0000u) | (unchecked((uint)margin) & 0xFFFFu);
}
