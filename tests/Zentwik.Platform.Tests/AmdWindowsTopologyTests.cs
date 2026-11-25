using Xunit;
using System.Globalization;
using System.Runtime.Intrinsics.X86;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Tests;

public sealed class AmdWindowsTopologyTests
{
    [Fact]
    public void Two_l3_domains_produce_two_ccds()
    {
        var snapshot = new AmdTopologySnapshot(
            [0x0000_FFFFUL, 0xFFFF_0000UL],
            Enumerable.Range(0, 32)
                .Select(processor => new AmdLogicalProcessorObservation(processor, 0, processor / 2))
                .ToArray());

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 16, 32, out var error);

        Assert.True(map.IsUsable, error);
        Assert.Equal(AmdCoreMapSource.WindowsTopology, map.Source);
        Assert.Equal(16, map.Cores.Count);
        Assert.Equal((0, 0, 7), (map.Cores[7].Ccd, map.Cores[7].Ccx, map.Cores[7].Core));
        Assert.Equal((1, 0, 0), (map.Cores[8].Ccd, map.Cores[8].Ccx, map.Cores[8].Core));
        Assert.Equal(0x1000_0000u, AmdCoreMask.Encode(map.Cores[8], 0x1A));
        Assert.Equal(2, map.Cores[0].Threads);
    }

    [Fact]
    public void Disabled_cores_keep_hardware_numbering_within_a_ccd()
    {
        var coreIds = new[] { 0, 1, 2, 5, 6, 7 };
        var processors = new List<AmdLogicalProcessorObservation>();
        for (var index = 0; index < coreIds.Length; index++)
        {
            processors.Add(new AmdLogicalProcessorObservation(processors.Count, 0, coreIds[index]));
            processors.Add(new AmdLogicalProcessorObservation(processors.Count, 0, coreIds[index]));
        }

        var snapshot = new AmdTopologySnapshot([0x0000_0FFFUL], processors);

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 6, 12, out var error);

        Assert.True(map.IsUsable, error);
        Assert.Equal(6, map.Cores.Count);
        Assert.Equal(5, map.Cores[3].Core);
        Assert.Equal(0x0050_0000u, AmdCoreMask.Encode(map.Cores[3], 0x1A));
    }

    [Fact]
    public void Family17h_treats_l3_domains_as_ccxs()
    {
        var snapshot = new AmdTopologySnapshot(
            [0x0000_000FUL, 0x0000_00F0UL, 0x0000_0F00UL, 0x0000_F000UL],
            Enumerable.Range(0, 16)
                .Select(processor => new AmdLogicalProcessorObservation(processor, 0, processor % 4))
                .ToArray());

        var map = AmdWindowsTopology.Build(snapshot, 0x17, 16, 16, out var error);

        Assert.True(map.IsUsable, error);
        Assert.Equal((0, 0, 3), (map.Cores[3].Ccd, map.Cores[3].Ccx, map.Cores[3].Core));
        Assert.Equal((0, 1, 0), (map.Cores[4].Ccd, map.Cores[4].Ccx, map.Cores[4].Core));
        Assert.Equal((1, 0, 0), (map.Cores[8].Ccd, map.Cores[8].Ccx, map.Cores[8].Core));
        Assert.Equal(0x0110_0000u, AmdCoreMask.Encode(map.Cores[5], 0x17));
    }

    [Fact]
    public void Core_count_mismatch_is_rejected()
    {
        var snapshot = new AmdTopologySnapshot(
            [0x0000_0003UL],
            [
                new AmdLogicalProcessorObservation(0, 0, 0),
                new AmdLogicalProcessorObservation(1, 0, 0),
            ]);

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 2, 2, out var error);

        Assert.False(map.IsUsable);
        Assert.Contains("物理核", error);
    }

    [Fact]
    public void Missing_l3_domain_is_rejected()
    {
        var snapshot = new AmdTopologySnapshot(
            [0x0000_0001UL],
            [
                new AmdLogicalProcessorObservation(0, 0, 0),
                new AmdLogicalProcessorObservation(1, 0, 0),
            ]);

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 1, 2, out var error);

        Assert.False(map.IsUsable);
        Assert.Contains("L3", error);
    }

    [Fact]
    public void Overlapping_l3_domains_are_rejected()
    {
        var snapshot = new AmdTopologySnapshot(
            [0x0000_0003UL, 0x0000_0002UL],
            [
                new AmdLogicalProcessorObservation(0, 0, 0),
                new AmdLogicalProcessorObservation(1, 0, 0),
            ]);

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 1, 2, out var error);

        Assert.False(map.IsUsable);
        Assert.Contains("多个 L3", error);
    }

    [Fact]
    public void Colliding_core_numbers_within_a_ccd_are_rejected()
    {
        // 同一 CCD 内出现核号 0 与 8：4 位字段按 8 取模后撞号，说明核号不是按复合体分组的。
        var snapshot = new AmdTopologySnapshot(
            [0x0000_0003UL, 0x0000_000CUL],
            [
                new AmdLogicalProcessorObservation(0, 0, 0),
                new AmdLogicalProcessorObservation(1, 0, 8),
                new AmdLogicalProcessorObservation(2, 0, 1),
                new AmdLogicalProcessorObservation(3, 0, 9),
            ]);

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 4, 4, out var error);

        Assert.False(map.IsUsable);
        Assert.Contains("同一个核内序号", error);
    }

    [Fact]
    public void Heterogeneous_domain_sizes_are_rejected()
    {
        // 4 核域 + 8 核域：同一 die 上大小不同的两个 CCX，不能按域序号当 CCD 索引。
        var processors = new List<AmdLogicalProcessorObservation>();
        for (var processor = 0; processor < 4; processor++)
        {
            processors.Add(new AmdLogicalProcessorObservation(processor, 0, processor));
        }

        for (var processor = 4; processor < 12; processor++)
        {
            processors.Add(new AmdLogicalProcessorObservation(processor, 0, processor - 4));
        }

        var snapshot = new AmdTopologySnapshot([0x0000_000FUL, 0x0000_0FF0UL], processors);

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 12, 12, out var error);

        Assert.False(map.IsUsable);
        Assert.Contains("异构", error);
    }

    [Fact]
    public void Single_thread_cores_are_supported()
    {
        var snapshot = new AmdTopologySnapshot(
            [0x0000_00FFUL],
            Enumerable.Range(0, 8)
                .Select(processor => new AmdLogicalProcessorObservation(processor, 0, processor))
                .ToArray());

        var map = AmdWindowsTopology.Build(snapshot, 0x1A, 8, 8, out var error);

        Assert.True(map.IsUsable, error);
        Assert.All(map.Cores, core => Assert.Equal(1, core.Threads));
    }

    [Fact]
    public void Resolve_on_this_machine_matches_the_system_core_count()
    {
        if (!OperatingSystem.IsWindows() || !X86Base.IsSupported)
        {
            return;
        }

        var info = new WindowsSystemInfo();
        if (!string.Equals(info.CpuVendor, "AuthenticAMD", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var map = AmdWindowsTopology.Resolve(ReadFamily(info.CpuIdentifier), info.PhysicalCoreCount, info.ProcessorCount, out var error);

        if (!map.IsUsable)
        {
            // 无法确定时必须给出原因，不允许无提示地空映射。
            Assert.False(string.IsNullOrWhiteSpace(error));
            return;
        }

        Assert.Equal(info.PhysicalCoreCount, map.Cores.Count);
        Assert.Equal(info.ProcessorCount, map.Cores.Sum(static core => core.Threads));
        Assert.Equal(Enumerable.Range(0, map.Cores.Count), map.Cores.Select(static core => core.LogicalIndex));
        Assert.All(map.Cores, core => Assert.InRange(core.Core, 0, 7));
    }

    private static uint ReadFamily(string? identifier)
    {
        if (identifier is null)
        {
            return 0x1A;
        }

        var index = identifier.IndexOf("Family", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0x1A;
        }

        var start = index + "Family".Length;
        var end = start;
        while (end < identifier.Length && char.IsDigit(identifier[end]))
        {
            end++;
        }

        return uint.TryParse(identifier.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0x1A;
    }
}
