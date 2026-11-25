using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Zentwik.Platform.Windows;

/// <summary>单个逻辑处理器报告的拓扑：CPUID 0x8000001E 的核号与节点号。</summary>
internal readonly record struct AmdLogicalProcessorObservation(int Processor, int NodeId, int CoreId);

/// <summary>CPUID 与系统缓存拓扑的只读快照，构建映射时不再访问硬件。</summary>
internal sealed record AmdTopologySnapshot(
    IReadOnlyList<ulong> L3Masks,
    IReadOnlyList<AmdLogicalProcessorObservation> Processors);

/// <summary>
/// 无 APOB 时的核映射来源：CPUID 0x8000001E 给出硬件核号，系统 L3 缓存域给出 CCD 分组。
/// 两者都来自固件自报，且必须通过结构与数量校验；校验不过就不产出映射，不做兜底猜测。
/// </summary>
internal static class AmdWindowsTopology
{
    private const int ExtendedTopologyLeaf = unchecked((int)0x8000_001E);
    private const int MaxCoresPerComplex = 8;
    private const int MaxComplexesPerCcd = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    /// <summary>CACHE_RELATIONSHIP：关系头之后依次是 level、行大小、容量、类型，再 20 字节保留，最后是处理器组掩码。</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct CacheRelationship
    {
        [FieldOffset(0)]
        public int Relationship;

        [FieldOffset(4)]
        public int Size;

        [FieldOffset(8)]
        public byte Level;

        [FieldOffset(9)]
        public byte Associativity;

        [FieldOffset(10)]
        public ushort LineSize;

        [FieldOffset(12)]
        public int CacheSize;

        [FieldOffset(16)]
        public int Type;

        [FieldOffset(40)]
        public GroupAffinity Affinity;
    }

    public static AmdCoreMap Resolve(uint family, int physicalCoreCount, int processorCount, out string? error)
    {
        error = null;

        if (!X86Base.IsSupported)
        {
            error = "当前进程无法执行 CPUID，不能确定核的物理位置。";
            return AmdCoreMap.Empty;
        }

        if (processorCount is <= 0 or > 64)
        {
            error = $"逻辑处理器数 {processorCount} 不在单处理器组范围内，暂不能确定核映射。";
            return AmdCoreMap.Empty;
        }

        if (!TryReadL3Domains(processorCount, out var masks, out error))
        {
            return AmdCoreMap.Empty;
        }

        var observations = new List<AmdLogicalProcessorObservation>(processorCount);
        var thread = Native.GetCurrentThread();
        var original = Native.SetThreadAffinityMask(thread, 1);
        if (original == UIntPtr.Zero)
        {
            error = $"设置线程亲和性失败，Win32 错误码 {Marshal.GetLastWin32Error()}，不能逐个处理器读取 CPUID。";
            return AmdCoreMap.Empty;
        }

        try
        {
            for (var processor = 0; processor < processorCount; processor++)
            {
                if (Native.SetThreadAffinityMask(thread, (UIntPtr)(1UL << processor)) == UIntPtr.Zero)
                {
                    error = $"绑定到逻辑处理器 {processor} 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                    return AmdCoreMap.Empty;
                }

                var topology = X86Base.CpuId(ExtendedTopologyLeaf, 0);
                observations.Add(new AmdLogicalProcessorObservation(
                    processor,
                    topology.Ecx & 0xFF,
                    topology.Ebx & 0xFF));
            }
        }
        finally
        {
            _ = Native.SetThreadAffinityMask(thread, original);
        }

        return Build(new AmdTopologySnapshot(masks, observations), family, physicalCoreCount, processorCount, out error);
    }

    /// <summary>校验并构建映射；任一结构不符即返回不可用，不退化到 core/8 之类的推算。</summary>
    internal static AmdCoreMap Build(
        AmdTopologySnapshot snapshot,
        uint family,
        int physicalCoreCount,
        int processorCount,
        out string? error)
    {
        error = null;

        if (snapshot.L3Masks.Count == 0)
        {
            error = "系统未报告 L3 缓存拓扑，不能确定 CCD 分组。";
            return AmdCoreMap.Empty;
        }

        if (snapshot.Processors.Count != processorCount)
        {
            error = $"拓扑快照覆盖 {snapshot.Processors.Count} 个逻辑处理器，与系统报告的 {processorCount} 个不一致。";
            return AmdCoreMap.Empty;
        }

        var domainOf = new int[processorCount];
        Array.Fill(domainOf, -1);
        for (var processor = 0; processor < processorCount; processor++)
        {
            var found = -1;
            for (var domain = 0; domain < snapshot.L3Masks.Count; domain++)
            {
                if ((snapshot.L3Masks[domain] & (1UL << processor)) == 0)
                {
                    continue;
                }

                if (found >= 0)
                {
                    error = $"逻辑处理器 {processor} 同时属于多个 L3 缓存域，拓扑不可信。";
                    return AmdCoreMap.Empty;
                }

                found = domain;
            }

            if (found < 0)
            {
                error = $"逻辑处理器 {processor} 不属于任何 L3 缓存域，拓扑不可信。";
                return AmdCoreMap.Empty;
            }

            domainOf[processor] = found;
        }

        // 逻辑处理器组之外的域是别的处理器组或未启用核，不参与映射。
        var usedDomains = domainOf.Distinct().OrderBy(static domain => domain).ToArray();
        var slotOf = usedDomains.Select((domain, slot) => (domain, slot)).ToDictionary(static pair => pair.domain, static pair => pair.slot);

        var order = new List<(int Slot, int CoreId)>();
        var threads = new Dictionary<(int Slot, int CoreId), List<int>>();
        foreach (var observation in snapshot.Processors.OrderBy(static item => item.Processor))
        {
            if (observation.Processor < 0 || observation.Processor >= processorCount)
            {
                error = $"拓扑快照包含越界的逻辑处理器 {observation.Processor}。";
                return AmdCoreMap.Empty;
            }

            if (observation.CoreId > 0x0F)
            {
                error = $"逻辑处理器 {observation.Processor} 报告核号 {observation.CoreId}，超出 4 位字段范围。";
                return AmdCoreMap.Empty;
            }

            var key = (slotOf[domainOf[observation.Processor]], observation.CoreId);
            if (!threads.TryGetValue(key, out var list))
            {
                list = [];
                threads[key] = list;
                order.Add(key);
            }

            list.Add(observation.Processor);
        }

        var threadsPerCore = threads[order[0]].Count;
        if (threadsPerCore is < 1 or > 2)
        {
            error = $"检测到每核 {threadsPerCore} 个线程，超出 SMT 1–2 的范围。";
            return AmdCoreMap.Empty;
        }

        if (threads.Count != physicalCoreCount)
        {
            error = $"拓扑推出 {threads.Count} 个物理核，与系统报告的 {physicalCoreCount} 个不一致。";
            return AmdCoreMap.Empty;
        }

        foreach (var key in order)
        {
            if (threads[key].Count != threadsPerCore)
            {
                error = $"核（域 {key.Slot}，核号 {key.CoreId}）的线程数与其它核不一致。";
                return AmdCoreMap.Empty;
            }
        }

        var maxCoresPerDomain = family > 0x17 ? MaxCoresPerComplex : MaxCoresPerComplex / MaxComplexesPerCcd;
        var coreOf = new Dictionary<(int Slot, int CoreId), int>();
        var seen = new HashSet<(int Slot, int Core)>();
        foreach (var key in order)
        {
            var core = family > 0x17 ? key.CoreId & 0x7 : key.CoreId & 0x3;
            if (!seen.Add((key.Slot, core)))
            {
                error = $"L3 域 {key.Slot} 内有两个核落在同一个核内序号 {core}，硬件核号不是按 8 核分组。";
                return AmdCoreMap.Empty;
            }

            coreOf[key] = core;
        }

        foreach (var group in order.GroupBy(static key => key.Slot))
        {
            if (group.Count() > maxCoresPerDomain)
            {
                error = $"L3 域 {group.Key} 含 {group.Count()} 个核，超过每个复合体 {maxCoresPerDomain} 个核的上限。";
                return AmdCoreMap.Empty;
            }
        }

        // 核数不一致的域通常意味着同一 die 上有多个大小不同的 CCX（混合布局），
        // 此时"L3 域 = CCD"不成立，不能按域序号当 CCD 索引。
        var domainSizes = order.GroupBy(static key => key.Slot).Select(static group => group.Count()).Distinct().ToArray();
        if (domainSizes.Length > 1)
        {
            error = $"各 L3 域的核数不一致（{string.Join("/", domainSizes.Order())}），属异构布局，暂不能确定 CCD 索引。";
            return AmdCoreMap.Empty;
        }

        if (order.Count * threadsPerCore != processorCount)
        {
            error = $"拓扑共 {order.Count} 核 × {threadsPerCore} 线程，与 {processorCount} 个逻辑处理器不一致。";
            return AmdCoreMap.Empty;
        }

        var cores = new List<AmdCoreAddress>(order.Count);
        for (var index = 0; index < order.Count; index++)
        {
            var key = order[index];
            var ccd = family > 0x17 ? key.Slot : key.Slot / MaxComplexesPerCcd;
            var ccx = family > 0x17 ? 0 : key.Slot % MaxComplexesPerCcd;
            cores.Add(new AmdCoreAddress(index, ccd, ccx, coreOf[key], threadsPerCore));
        }

        var detail = $"Windows 拓扑：CPUID 0x8000001E 核号 + {order.Select(static key => key.Slot).Distinct().Count()} 个 L3 缓存域，"
                     + $"{cores.Count} 核 / {threadsPerCore} 线程每核，已与物理核数核对。";
        return AmdCoreMap.Create(cores, AmdCoreMapSource.WindowsTopology, detail);
    }

    private static bool TryReadL3Domains(int processorCount, out List<ulong> masks, out string? error)
    {
        masks = [];
        error = null;

        uint length = 0;
        _ = Native.GetLogicalProcessorInformationEx(Native.RelationCache, 0, ref length);
        if (length == 0)
        {
            error = $"读取缓存拓扑失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(Native.RelationCache, buffer, ref length))
            {
                error = $"读取缓存拓扑失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            var offset = 0;
            while (offset + 8 <= length)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0 || offset + size > length)
                {
                    break;
                }

                if (relationship == Native.RelationCache)
                {
                    var cache = Marshal.PtrToStructure<CacheRelationship>(buffer + offset);
                    if (cache.Level == 3)
                    {
                        if (cache.Affinity.Group != 0)
                        {
                            error = "L3 缓存跨处理器组，暂不支持核映射。";
                            return false;
                        }

                        masks.Add((ulong)cache.Affinity.Mask);
                    }
                }

                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (masks.Count == 0)
        {
            error = "系统未报告 L3 缓存拓扑，不能确定 CCD 分组。";
            return false;
        }

        var union = masks.Aggregate(0UL, static (all, mask) => all | mask);
        var expected = (1UL << processorCount) - 1;
        if ((union & expected) != expected)
        {
            error = "L3 缓存域未覆盖全部逻辑处理器，拓扑不可信。";
            return false;
        }

        return true;
    }
}
