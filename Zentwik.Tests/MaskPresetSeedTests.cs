using System.Linq;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;

namespace Zentwik.Tests
{
    /// <summary>核心组预置掩码的播种：铺一次、删了不重建、不覆盖用户自建。</summary>
    public sealed class MaskPresetSeedTests
    {
        private static readonly (string Key, string Name, ulong Mask)[] TwoGroups =
        {
            ("group:0xFFFF", "CCD0", 0xFFFFUL),
            ("group:0xFFFF0000", "CCD1", 0xFFFF0000UL)
        };

        private static MaskStore NewStore(FakeAppPaths paths)
        {
            MaskStore store = new(new FakeLogService(), paths);
            store.Load();
            return store;
        }

        [Fact]
        public void SeedPresets_AddsGroups_AndDoesNotRepeat()
        {
            FakeAppPaths paths = new();
            MaskStore store = NewStore(paths);

            Assert.Equal(2, store.SeedPresets(TwoGroups));
            Assert.Equal(2, store.Masks.Count);
            Assert.All(store.Masks, m => Assert.True(m.Preset));

            Assert.Equal(0, store.SeedPresets(TwoGroups));
            Assert.Equal(2, store.Masks.Count);
        }

        [Fact]
        public void SeedPresets_AfterDelete_DoesNotRecreate()
        {
            FakeAppPaths paths = new();
            MaskStore store = NewStore(paths);
            store.SeedPresets(TwoGroups);

            store.Remove(store.Masks[1].Id);

            // 重启：重新加载同一个目录，再铺一次
            MaskStore restarted = NewStore(paths);
            Assert.Equal(0, restarted.SeedPresets(TwoGroups));
            Assert.Single(restarted.Masks);
            Assert.Equal("CCD0", restarted.Masks[0].Name);
        }

        [Fact]
        public void SeedPresets_PresetDoesNotConsumeUserQuota()
        {
            FakeAppPaths paths = new();
            MaskStore store = NewStore(paths);

            store.SeedPresets(TwoGroups);

            // 预置项不占 10 套配额：还能再建满 10 条自建掩码
            for (int i = 0; i < MaskStore.MaxCount; i++)
            {
                Assert.True(
                    store.Upsert(new CustomMask { Name = $"自建{i}", Mask = 0xFUL }, out MaskStore.UpsertError error),
                    error.ToString());
            }

            Assert.True(store.IsFull);
            Assert.Equal(MaskStore.MaxCount + 2, store.Masks.Count);
        }

        [Fact]
        public void SeedPresets_SkipsNameTakenByUser()
        {
            FakeAppPaths paths = new();
            MaskStore store = NewStore(paths);

            CustomMask own = new() { Name = "CCD0", Mask = 0x1UL };
            Assert.True(store.Upsert(own, out _));

            Assert.Equal(1, store.SeedPresets(TwoGroups));
            Assert.Equal(2, store.Masks.Count);
            Assert.Equal(0x1UL, store.FindByName("CCD0")!.Mask);
        }

        [Fact]
        public void ResetToFactory_ClearsLedger_SoPresetsComeBack()
        {
            FakeAppPaths paths = new();
            MaskStore store = NewStore(paths);
            store.SeedPresets(TwoGroups);

            store.ResetToFactory();
            Assert.Empty(store.Masks);

            MaskStore restarted = NewStore(paths);
            Assert.Equal(2, restarted.SeedPresets(TwoGroups));
        }

        [Fact]
        public void GroupName_UsesVendorTerm_OnlyWhenMultipleGroups()
        {
            Assert.Equal("CCD0", CpuVendorProbe.GroupName(CpuVendor.Amd, 0, 2));
            Assert.Equal("CCD1", CpuVendorProbe.GroupName(CpuVendor.Amd, 1, 2));

            Assert.Equal("Tile0", CpuVendorProbe.GroupName(CpuVendor.Intel, 0, 2));
            Assert.Equal("分组0", CpuVendorProbe.GroupName(CpuVendor.Unknown, 0, 2));

            // 单组一律叫「全核心」：Intel 桌面只有一个 L3 域，说 Tile0 没有意义
            Assert.Equal("全核心", CpuVendorProbe.GroupName(CpuVendor.Intel, 0, 1));
            Assert.Equal("全核心", CpuVendorProbe.GroupName(CpuVendor.Amd, 0, 1));
        }

        [Fact]
        public void MaskFormatter_RejectsIdentifierLikeInput()
        {
            // "CCD0" 全是 hex 字符，不拦就被解析成 0xCCD0
            Assert.False(MaskFormatter.TryParse("CCD0", out _, out _));
            Assert.False(MaskFormatter.TryParse("Tile1", out _, out _));

            // 带前缀就按掩码走
            Assert.True(MaskFormatter.TryParse("0xCCD0", out ulong mask, out _));
            Assert.Equal(0xCCD0UL, mask);

            // 纯字母的 hex 不受影响
            Assert.True(MaskFormatter.TryParse("FFFF", out ulong ffff, out _));
            Assert.Equal(0xFFFFUL, ffff);
        }
    }
}
