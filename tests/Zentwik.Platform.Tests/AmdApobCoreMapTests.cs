using Xunit;
using Zentwik.Platform.Windows;
using static Zentwik.Platform.Tests.ApobTableBuilder;

namespace Zentwik.Platform.Tests;

public sealed class AmdApobCoreMapTests
{
    private const int PayloadStart = 0x50 + 48;

    [Fact]
    public void Two_ccds_with_eight_cores_map_to_numbered_masks()
    {
        var cores = Enumerable.Range(0, 8).Select(core => new CoreSlot(0, core, [true, true])).ToArray();
        var table = CcdMap(2, 1, 8, 2, [new CcdSlot(0, 0, cores), new CcdSlot(1, 1, cores)]);

        Assert.True(AmdApobCoreMap.TryParse(table, 0x1A, 32, out var map, out var error), error);
        Assert.Equal(AmdCoreMapSource.Apob, map.Source);
        Assert.Equal(16, map.Cores.Count);
        Assert.Equal((0, 0, 0), (map.Cores[0].Ccd, map.Cores[0].Ccx, map.Cores[0].Core));
        Assert.Equal(0x1000_0000u, AmdCoreMask.Encode(map.Cores[8], 0x1A));
        Assert.Equal(0x1070_0000u, AmdCoreMask.Encode(map.Cores[15], 0x1A));
    }

    [Fact]
    public void Disabled_core_is_skipped_and_hardware_numbering_is_kept()
    {
        var cores = Enumerable.Range(0, 8)
            .Where(core => core != 3)
            .Select(core => new CoreSlot(0, core, [true, true]))
            .ToArray();
        var all = Enumerable.Range(0, 8).Select(core => new CoreSlot(0, core, [true, true])).ToArray();
        var table = CcdMap(2, 1, 8, 2, [new CcdSlot(0, 0, cores), new CcdSlot(1, 1, all)]);

        Assert.True(AmdApobCoreMap.TryParse(table, 0x1A, 30, out var map, out var error), error);
        Assert.Equal(15, map.Cores.Count);
        Assert.Equal(4, map.Cores[3].Core);
        Assert.Equal(0x0040_0000u, AmdCoreMask.Encode(map.Cores[3], 0x1A));
    }

    [Fact]
    public void Absent_ccd_contributes_no_cores()
    {
        var cores = Enumerable.Range(0, 8).Select(core => new CoreSlot(0, core, [true, false])).ToArray();
        var table = CcdMap(2, 1, 8, 2, [new CcdSlot(0, 0, cores)]);

        Assert.True(AmdApobCoreMap.TryParse(table, 0x1A, 8, out var map, out var error), error);
        Assert.Equal(8, map.Cores.Count);
        Assert.All(map.Cores, core => Assert.Equal(0, core.Ccd));
        Assert.All(map.Cores, core => Assert.Equal(1, core.Threads));
    }

    [Fact]
    public void Ccx_map_is_used_when_no_ccd_map_exists()
    {
        var cores = new List<CoreSlot>();
        for (var ccx = 0; ccx < 2; ccx++)
        {
            for (var core = 0; core < 4; core++)
            {
                cores.Add(new CoreSlot(ccx, core, [true]));
            }
        }

        var table = CcxMap(2, 4, 1, cores);

        Assert.True(AmdApobCoreMap.TryParse(table, 0x17, 8, out var map, out var error), error);
        Assert.Equal(8, map.Cores.Count);
        Assert.Equal((0, 1, 1), (map.Cores[5].Ccd, map.Cores[5].Ccx, map.Cores[5].Core));
        Assert.Equal(0x0110_0000u, AmdCoreMask.Encode(map.Cores[5], 0x17));
    }

    [Fact]
    public void Missing_signature_is_rejected()
    {
        var table = WithSignature(BuildSingleCcd(), 0x424F_5042u);

        Assert.False(AmdApobCoreMap.TryParse(table, 0x1A, 16, out var map, out var error));
        Assert.False(map.IsUsable);
        Assert.Contains("签名", error);
    }

    [Fact]
    public void Thread_count_mismatch_is_rejected()
    {
        Assert.False(AmdApobCoreMap.TryParse(BuildSingleCcd(), 0x1A, 15, out var map, out var error));
        Assert.False(map.IsUsable);
        Assert.Contains("不一致", error);
    }

    [Fact]
    public void Duplicate_physical_core_is_rejected()
    {
        var table = BuildSingleCcd();
        // 单个 CCD：1 字节 CCD + 1 字节 CCX + 8 核 × (核号 + 2 线程标志)
        const int coreSize = 3;
        var second = PayloadStart + 1 + 1 + coreSize;
        table[second] = 0;

        Assert.False(AmdApobCoreMap.TryParse(table, 0x1A, 16, out var map, out var error));
        Assert.False(map.IsUsable);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Invalid_thread_flag_is_rejected()
    {
        var table = BuildSingleCcd();
        table[PayloadStart + 1 + 1 + 1] = 2;

        Assert.False(AmdApobCoreMap.TryParse(table, 0x1A, 16, out var map, out _));
        Assert.False(map.IsUsable);
    }

    [Fact]
    public void Truncated_table_is_rejected()
    {
        var table = BuildSingleCcd()[..64];

        Assert.False(AmdApobCoreMap.TryParse(table, 0x1A, 16, out var map, out var error));
        Assert.False(map.IsUsable);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static byte[] BuildSingleCcd()
    {
        var cores = Enumerable.Range(0, 8).Select(core => new CoreSlot(0, core, [true, true])).ToArray();
        return CcdMap(1, 1, 8, 2, [new CcdSlot(0, 0, cores)]);
    }
}
