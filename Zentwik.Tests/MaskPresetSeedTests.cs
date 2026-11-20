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
        private static readonly (string Key, string Name, CoreMask Mask)[] TwoGroups =
        {
            ("group:0xFFFF", "CCD0", new CoreMask(0xFFFFUL)),
            ("group:0xFFFF0000", "CCD1", new CoreMask(0xFFFF0000UL))
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
                    store.Upsert(new CustomMask { Name = $"自建{i}", Mask = new CoreMask(0xFUL) }, out MaskStore.UpsertError error),
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

            CustomMask own = new() { Name = "CCD0", Mask = new CoreMask(0x1UL) };
            Assert.True(store.Upsert(own, out _));

            Assert.Equal(1, store.SeedPresets(TwoGroups));
            Assert.Equal(2, store.Masks.Count);
            Assert.Equal(0x1UL, store.FindByName("CCD0")!.Mask.Low);
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
            Assert.Equal("CCD 0", CpuVendorProbe.GroupName(CpuVendor.Amd, 0, 2));
            Assert.Equal("CCD 1", CpuVendorProbe.GroupName(CpuVendor.Amd, 1, 2));

            // 非 AMD 一律用系统实际划分单位，不再出现 Tile / 分组这类自造词
            Assert.Equal("Group 0", CpuVendorProbe.GroupName(CpuVendor.Intel, 0, 2));
            Assert.Equal("Group 0", CpuVendorProbe.GroupName(CpuVendor.Unknown, 0, 2));

            // 单组一律叫 All Cores：只有一个 L3 域时说序号没有意义
            Assert.Equal("All Cores", CpuVendorProbe.GroupName(CpuVendor.Intel, 0, 1));
            Assert.Equal("All Cores", CpuVendorProbe.GroupName(CpuVendor.Amd, 0, 1));
        }

        [Fact]
        public void RenamePresets_TouchesOnlyUntouchedPresetNames()
        {
            FakeAppPaths paths = new();
            MaskStore store = NewStore(paths);
            store.SeedPresets(new (string, string, CoreMask)[] { ("group:0xFF", "CCD0", new CoreMask(0xFFUL)) });

            CustomMask userNamed = new() { Name = "前八个核", Mask = new CoreMask(0xFFUL), Preset = true };
            Assert.True(store.Upsert(userNamed, out _));

            int renamed = store.RenamePresets(new (string, string)[] { ("CCD0", "CCD 0") });

            Assert.Equal(1, renamed);
            Assert.Contains(store.Masks, m => m.Name == "CCD 0");
            Assert.Contains(store.Masks, m => m.Name == "前八个核");
        }

        [Fact]
        public void MaskFormatter_RejectsIdentifierLikeInput()
        {
            // "CCD0" 全是 hex 字符，不拦就被解析成 0xCCD0
            Assert.False(MaskFormatter.TryParse("CCD0", out _, out _));
            Assert.False(MaskFormatter.TryParse("Tile1", out _, out _));

            // 带前缀就按掩码走
            Assert.True(MaskFormatter.TryParse("0xCCD0", out CoreMask mask, out _));
            Assert.Equal(0xCCD0UL, mask.Low);

            // 纯字母的 hex 不受影响
            Assert.True(MaskFormatter.TryParse("FFFF", out CoreMask ffff, out _));
            Assert.Equal(0xFFFFUL, ffff.Low);
        }
    }
}
