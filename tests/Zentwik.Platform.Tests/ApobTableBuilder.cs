namespace Zentwik.Platform.Tests;

/// <summary>按 APOB 结构合成原始表，用于覆盖启用核、禁用核与两种映射表。</summary>
internal static class ApobTableBuilder
{
    public const uint CcdMapType = 3;
    public const uint CcxMapType = 1;
    private const int HeaderSize = 0x50;
    private const int EntryHeaderSize = 48;

    public readonly record struct CoreSlot(int Ccx, int Core, bool[] Threads);

    public readonly record struct CcdSlot(int Slot, int CcdId, IReadOnlyList<CoreSlot> Cores);

    public static byte[] CcdMap(int ccdSlots, int complexSlots, int coreSlots, int threads, IReadOnlyList<CcdSlot> present)
    {
        var coreSize = 1 + threads;
        var complexSize = 1 + (coreSlots * coreSize);
        var ccdSize = 1 + (complexSlots * complexSize);
        var payload = new byte[ccdSlots * ccdSize];
        Array.Fill(payload, (byte)0xFF);

        foreach (var ccd in present)
        {
            var ccdOffset = ccd.Slot * ccdSize;
            payload[ccdOffset] = (byte)ccd.CcdId;
            foreach (var core in ccd.Cores)
            {
                var coreOffset = ccdOffset + 1 + (core.Ccx * complexSize) + 1 + (core.Core * coreSize);
                payload[ccdOffset + 1 + (core.Ccx * complexSize)] = (byte)core.Ccx;
                payload[coreOffset] = (byte)core.Core;
                for (var thread = 0; thread < threads; thread++)
                {
                    payload[coreOffset + 1 + thread] = (byte)(core.Threads[thread] ? 1 : 0);
                }
            }
        }

        return Wrap(CcdMapType, payload);
    }

    public static byte[] CcxMap(int complexSlots, int coreSlots, int threads, IReadOnlyList<CoreSlot> present)
    {
        var coreSize = 1 + threads;
        var complexSize = 1 + (coreSlots * coreSize);
        var payload = new byte[complexSlots * complexSize];
        Array.Fill(payload, (byte)0xFF);

        foreach (var core in present)
        {
            if (core.Ccx >= complexSlots)
            {
                payload[core.Ccx * complexSize] = (byte)core.Ccx;
            }

            var coreOffset = (core.Ccx * complexSize) + 1 + (core.Core * coreSize);
            payload[core.Ccx * complexSize] = (byte)core.Ccx;
            payload[coreOffset] = (byte)core.Core;
            for (var thread = 0; thread < threads; thread++)
            {
                payload[coreOffset + 1 + thread] = (byte)(core.Threads[thread] ? 1 : 0);
            }
        }

        return Wrap(CcxMapType, payload);
    }

    public static byte[] WithSignature(byte[] table, uint signature)
    {
        BitConverter.GetBytes(signature).CopyTo(table, 0);
        return table;
    }

    private static byte[] Wrap(uint dataType, byte[] payload)
    {
        var table = new byte[HeaderSize + EntryHeaderSize + payload.Length];
        BitConverter.GetBytes(0x424F_5041u).CopyTo(table, 0);
        BitConverter.GetBytes(1u).CopyTo(table, 4);
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 8);
        BitConverter.GetBytes((uint)HeaderSize).CopyTo(table, 0x0C);

        var entry = HeaderSize;
        BitConverter.GetBytes(3u).CopyTo(table, entry);
        BitConverter.GetBytes(dataType).CopyTo(table, entry + 4);
        BitConverter.GetBytes(0u).CopyTo(table, entry + 8);
        BitConverter.GetBytes((uint)(EntryHeaderSize + payload.Length)).CopyTo(table, entry + 12);
        payload.CopyTo(table, entry + EntryHeaderSize);
        return table;
    }
}
