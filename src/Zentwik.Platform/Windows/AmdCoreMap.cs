namespace Zentwik.Platform.Windows;

/// <summary>逻辑核到物理位置的映射来源。</summary>
public enum AmdCoreMapSource
{
    /// <summary>映射不可用，任何按核操作都不得进行。</summary>
    None,

    /// <summary>APOB 中固件留下的逻辑到物理映射。</summary>
    Apob,

    /// <summary>由 CPUID 0x8000001E 核号与系统 L3 缓存域推导，并按物理核数校验。</summary>
    WindowsTopology,
}

/// <summary>
/// 一个逻辑核的物理位置。<see cref="Ccd"/>、<see cref="Ccx"/>、<see cref="Core"/>
/// 是 SMU 核掩码的三个字段，不是操作系统枚举序号。
/// </summary>
public sealed record AmdCoreAddress(int LogicalIndex, int Ccd, int Ccx, int Core, int Threads);

/// <summary>
/// 逻辑核索引到物理位置的映射。逻辑索引与操作系统枚举顺序一致，作为方案与账本的核标识；
/// 发送给 SMU 的始终是由物理位置编码出的掩码。
/// </summary>
public sealed class AmdCoreMap
{
    private readonly AmdCoreAddress[] _cores;
    private readonly Dictionary<int, AmdCoreAddress> _byLogicalIndex;

    private AmdCoreMap(AmdCoreAddress[] cores, AmdCoreMapSource source, string detail)
    {
        _cores = cores;
        _byLogicalIndex = new Dictionary<int, AmdCoreAddress>(cores.Length);
        foreach (var core in cores)
        {
            _byLogicalIndex[core.LogicalIndex] = core;
        }

        Source = source;
        Detail = detail;
    }

    public static AmdCoreMap Empty { get; } = new([], AmdCoreMapSource.None, "核映射不可用。");

    public static AmdCoreMap Create(IEnumerable<AmdCoreAddress> cores, AmdCoreMapSource source, string detail)
    {
        ArgumentNullException.ThrowIfNull(cores);
        var ordered = cores.OrderBy(static core => core.LogicalIndex).ToArray();
        return ordered.Length == 0 ? Empty : new AmdCoreMap(ordered, source, detail);
    }

    public IReadOnlyList<AmdCoreAddress> Cores => _cores;

    public AmdCoreMapSource Source { get; }

    /// <summary>映射来源与校验结论，用于向用户说明读数依据。</summary>
    public string Detail { get; }

    /// <summary>false 时不得按核读写：掩码无法确定，读到的值也不可信。</summary>
    public bool IsUsable => Source != AmdCoreMapSource.None && _cores.Length > 0;

    public bool TryGet(int logicalIndex, out AmdCoreAddress address) =>
        _byLogicalIndex.TryGetValue(logicalIndex, out address!);
}

/// <summary>
/// SMU 核掩码编码，权威定义见研究仓库 knowledge/hardware/cpu/amd-smu.md §3.1：
/// [31:28] CCD、[27:24] CCX、[23:20] 核内序号（数值字段，不是独热位）。
/// </summary>
public static class AmdCoreMask
{
    public static uint Encode(AmdCoreAddress address, uint family)
    {
        ArgumentNullException.ThrowIfNull(address);

        return family > 0x17
            ? ((uint)address.Ccd << 28) | ((uint)(address.Core & 0x7) << 20)
            : ((uint)address.Ccd << 28) | ((uint)(address.Ccx & 0x1) << 24) | ((uint)(address.Core & 0x3) << 20);
    }
}
