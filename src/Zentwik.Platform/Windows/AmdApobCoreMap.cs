namespace Zentwik.Platform.Windows;

/// <summary>
/// APOB（AGESA PSP Output Block）中 group APOB_CCX 的逻辑到物理核映射。
/// 结构与解析规则来自 irusanov/ZenStates-Core 的 ApobCoreMapParser；本实现只保留核映射所需的路径。
/// 启用核按逻辑顺序排列，被熔断或禁用的 CCD / 核不进入结果。
/// </summary>
internal static class AmdApobCoreMap
{
    private const uint ApobSignature = 0x424F_5041; // "APOB"
    private const uint ApobCcxGroup = 3;
    private const uint CcxMapType = 1;
    private const uint CcdMapType = 3;
    private const byte NotPresent = 0xFF;

    private const int TypeHeaderSize = 16 + 32;
    private const int ConfigListStart = 0x30;
    private const int HashSize = 32;
    private const int MaxEntries = 4096;
    private const int MaxCcdSlots = 16;

    private static readonly int[] ThreadCandidates = [2, 1];
    private static readonly int[] CoreCandidates = [8, 16, 4];
    private static readonly int[] ComplexCandidates = [1, 2];

    private readonly record struct Entry(uint Offset, uint GroupId, uint DataTypeId, uint InstanceId, uint Size);

    private sealed class DecodedMap
    {
        public uint DataTypeId;
        public uint InstanceId;
        public int CcdSlots;
        public int ComplexSlots;
        public int CoreSlots;
        public int ThreadSlots;
        public List<(int Ccd, int Ccx, int Core, int Threads)> Cores = [];
        public int EnabledThreads;
    }

    public static bool TryParse(byte[] table, uint family, int expectedThreads, out AmdCoreMap map, out string? error)
    {
        map = AmdCoreMap.Empty;
        error = null;

        if (table.Length < TypeHeaderSize || ReadUInt32(table, 0) != ApobSignature)
        {
            error = "APOB 表不以 APOB 签名开头。";
            return false;
        }

        var tableSize = ReadUInt32(table, 8);
        var headerSize = ReadUInt32(table, 0x0C);
        if (headerSize < 16 || headerSize > table.Length)
        {
            error = $"APOB 头长度 {headerSize} 无效。";
            return false;
        }

        var length = tableSize > 0 && tableSize <= table.Length ? (int)tableSize : table.Length;
        var entries = EnumerateEntries(table, headerSize, (uint)length);        if (entries.Count == 0)
        {
            error = "APOB 表中没有可解析的条目。";
            return false;
        }

        DecodedMap? decoded = null;
        foreach (var type in (ReadOnlySpan<uint>)[CcdMapType, CcxMapType])
        {
            foreach (var entry in entries)
            {
                if (entry.GroupId != ApobCcxGroup || entry.DataTypeId != type)
                {
                    continue;
                }

                var candidate = Decode(table, entry, expectedThreads);
                if (candidate is null)
                {
                    continue;
                }

                if (decoded is null || candidate.InstanceId < decoded.InstanceId)
                {
                    decoded = candidate;
                }
            }

            if (decoded is not null)
            {
                break;
            }
        }

        if (decoded is null)
        {
            error = "APOB 中没有通过校验的逻辑到物理核映射条目。";
            return false;
        }

        if (expectedThreads > 0 && decoded.EnabledThreads != expectedThreads)
        {
            error = $"APOB 映射含 {decoded.EnabledThreads} 个线程，与系统的 {expectedThreads} 个不一致。";
            return false;
        }

        var maxCoresPerComplex = family > 0x17 ? 8 : 4;
        var cores = new List<AmdCoreAddress>(decoded.Cores.Count);
        var seen = new HashSet<(int, int, int)>();
        foreach (var core in decoded.Cores)
        {
            if (core.Ccd >= MaxCcdSlots || core.Ccx >= 4 || core.Core >= maxCoresPerComplex)
            {
                error = $"APOB 映射出现越界的物理位置（CCD {core.Ccd} / CCX {core.Ccx} / 核 {core.Core}）。";
                return false;
            }

            if (!seen.Add((core.Ccd, core.Ccx, core.Core)))
            {
                error = $"APOB 映射重复出现同一物理核（CCD {core.Ccd} / CCX {core.Ccx} / 核 {core.Core}）。";
                return false;
            }

            cores.Add(new AmdCoreAddress(cores.Count, core.Ccd, core.Ccx, core.Core, core.Threads));
        }

        if (cores.Count == 0)
        {
            error = "APOB 映射没有启用核。";
            return false;
        }

        var kind = decoded.DataTypeId == CcdMapType ? "CCD" : "CCX";
        var detail = $"APOB 固件映射（{kind} 表，实例 {decoded.InstanceId}，布局 "
                     + $"{decoded.CcdSlots} CCD × {decoded.ComplexSlots} CCX × {decoded.CoreSlots} 核 × {decoded.ThreadSlots} 线程）。";
        map = AmdCoreMap.Create(cores, AmdCoreMapSource.Apob, detail);
        return true;
    }

    private static List<Entry> EnumerateEntries(byte[] table, uint firstEntry, uint tableSize)
    {
        var entries = new List<Entry>();
        var seen = new HashSet<uint>();

        WalkChain(table, firstEntry, tableSize, entries, seen);

        // 头部配置区列出额外条目起点：链接断裂时仍能找到核映射。
        // 区域为 [0x30, HeaderSize - 32)，末尾 32 字节是哈希，不能当作偏移扫描。
        if (firstEntry > ConfigListStart + HashSize)
        {
            var regionEnd = firstEntry - HashSize;
            for (var offset = (uint)ConfigListStart; offset + 4 <= regionEnd; offset += 4)
            {
                var start = ReadUInt32(table, offset);
                if (start != 0 && (ulong)start + 16 <= tableSize)
                {
                    WalkChain(table, start, tableSize, entries, seen);
                }
            }
        }

        entries.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        return entries;
    }

    private static void WalkChain(byte[] table, uint offset, uint end, List<Entry> entries, HashSet<uint> seen)
    {
        for (var count = 0; count < MaxEntries && offset != 0 && (ulong)offset + 16 <= end; count++)
        {
            if (!seen.Add(offset))
            {
                return;
            }

            var size = ReadUInt32(table, offset + 12);
            if (size < 16 || (ulong)offset + size > end)
            {
                seen.Remove(offset);
                return;
            }

            entries.Add(new Entry(
                offset,
                ReadUInt32(table, offset),
                ReadUInt32(table, offset + 4),
                ReadUInt32(table, offset + 8),
                size));

            offset += size;
        }
    }

    private static DecodedMap? Decode(byte[] table, Entry entry, int expectedThreads)
    {
        if (entry.Size <= TypeHeaderSize)
        {
            return null;
        }

        var payloadOffset = (int)entry.Offset + TypeHeaderSize;
        var payloadSize = (int)entry.Size - TypeHeaderSize;
        var ccdMap = entry.DataTypeId == CcdMapType;

        DecodedMap? best = null;
        var bestScore = int.MinValue;
        var order = 0;

        foreach (var threads in ThreadCandidates)
        {
            foreach (var coreSlots in CoreCandidates)
            {
                foreach (var complexes in ComplexCandidates)
                {
                    var maxCcds = ccdMap ? MaxCcdSlots : 1;
                    for (var ccds = 1; ccds <= maxCcds; ccds++)
                    {
                        var coreSize = 1 + threads;
                        var complexSize = 1 + (coreSlots * coreSize);
                        var ccdSize = 1 + (complexes * complexSize);
                        var size = ccdMap ? ccds * ccdSize : complexes * complexSize;
                        order++;

                        // 结构按 4 字节对齐，最多 3 字节填充
                        if (size > payloadSize || payloadSize - size >= 4)
                        {
                            continue;
                        }

                        var map = TryDecode(table, payloadOffset, ccds, complexes, coreSlots, threads, ccdMap);
                        if (map is null)
                        {
                            continue;
                        }

                        var score = -((payloadSize - size) * 16) - order;
                        if (expectedThreads > 0 && map.EnabledThreads == expectedThreads)
                        {
                            score += 1 << 20;
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = map;
                            best.CcdSlots = ccds;
                            best.ComplexSlots = complexes;
                            best.CoreSlots = coreSlots;
                            best.ThreadSlots = threads;
                        }
                    }
                }
            }
        }

        if (best is null)
        {
            return null;
        }

        best.DataTypeId = entry.DataTypeId;
        best.InstanceId = entry.InstanceId;
        return best;
    }

    private static DecodedMap? TryDecode(
        byte[] table,
        int offset,
        int ccdSlots,
        int complexSlots,
        int coreSlots,
        int threadSlots,
        bool ccdMap)
    {
        var map = new DecodedMap();
        var coreSize = 1 + threadSlots;
        var complexSize = 1 + (coreSlots * coreSize);
        var ccdSize = 1 + (complexSlots * complexSize);
        var physicalCcdSeen = new bool[256];

        for (var ccd = 0; ccd < ccdSlots; ccd++)
        {
            var ccdOffset = offset + (ccd * ccdSize);
            var physicalCcd = 0;
            var complexOffset = ccdOffset;

            if (ccdMap)
            {
                var value = table[ccdOffset];
                complexOffset = ccdOffset + 1;
                if (value == NotPresent)
                {
                    if (!IsEmptyCcd(table, complexOffset, complexSlots, coreSlots, threadSlots, coreSize, complexSize))
                    {
                        return null;
                    }

                    continue;
                }

                if (value >= MaxCcdSlots || physicalCcdSeen[value])
                {
                    return null;
                }

                physicalCcdSeen[value] = true;
                physicalCcd = value;
            }

            var physicalComplexSeen = new bool[256];
            for (var ccx = 0; ccx < complexSlots; ccx++)
            {
                var ccxOffset = complexOffset + (ccx * complexSize);
                var physicalCcx = table[ccxOffset];
                if (physicalCcx == NotPresent)
                {
                    if (!AreAbsentCores(table, ccxOffset + 1, coreSlots, threadSlots, coreSize))
                    {
                        return null;
                    }

                    continue;
                }

                if (physicalCcx >= 4 || physicalComplexSeen[physicalCcx])
                {
                    return null;
                }

                physicalComplexSeen[physicalCcx] = true;

                var physicalCoreSeen = new bool[256];
                for (var core = 0; core < coreSlots; core++)
                {
                    var coreOffset = ccxOffset + 1 + (core * coreSize);
                    var physicalCore = table[coreOffset];
                    if (physicalCore == NotPresent)
                    {
                        if (!IsAbsentCore(table, coreOffset, threadSlots))
                        {
                            return null;
                        }

                        continue;
                    }

                    if (physicalCore >= coreSlots || physicalCoreSeen[physicalCore])
                    {
                        return null;
                    }

                    physicalCoreSeen[physicalCore] = true;

                    var enabled = 0;
                    for (var thread = 0; thread < threadSlots; thread++)
                    {
                        var flag = table[coreOffset + 1 + thread];
                        if (flag > 1)
                        {
                            return null;
                        }

                        if (flag == 1)
                        {
                            enabled++;
                        }
                    }

                    if (enabled == 0)
                    {
                        continue;
                    }

                    map.Cores.Add((physicalCcd, physicalCcx, physicalCore, enabled));
                    map.EnabledThreads += enabled;
                }
            }
        }

        return map.Cores.Count > 0 && map.EnabledThreads > 0 ? map : null;
    }

    private static bool IsAbsentCore(byte[] table, int coreOffset, int threadSlots)
    {
        for (var thread = 0; thread < threadSlots; thread++)
        {
            var value = table[coreOffset + 1 + thread];
            if (value != 0 && value != NotPresent)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreAbsentCores(byte[] table, int firstCoreOffset, int coreSlots, int threadSlots, int coreSize)
    {
        for (var core = 0; core < coreSlots; core++)
        {
            if (!IsAbsentCore(table, firstCoreOffset + (core * coreSize), threadSlots))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmptyCcd(
        byte[] table,
        int complexOffset,
        int complexSlots,
        int coreSlots,
        int threadSlots,
        int coreSize,
        int complexSize)
    {
        for (var ccx = 0; ccx < complexSlots; ccx++)
        {
            var ccxOffset = complexOffset + (ccx * complexSize);
            for (var core = 0; core < coreSlots; core++)
            {
                var coreOffset = ccxOffset + 1 + (core * coreSize);
                for (var thread = 0; thread < threadSlots; thread++)
                {
                    if (table[coreOffset + 1 + thread] == 1)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static uint ReadUInt32(byte[] table, uint offset) =>
        BitConverter.ToUInt32(table, (int)offset);
}
