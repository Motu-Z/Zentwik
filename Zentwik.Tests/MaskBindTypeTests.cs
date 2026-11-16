using System;
using System.IO;
using System.Linq;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Tests.Fakes;

namespace Zentwik.Tests
{
    /// <summary>
    /// 掩码下发方式：线上写法、落盘往返、老数据兼容。
    ///
    /// 这一项改变的是「用哪套 API 绑进程」，一旦把 CpuSets 和 AffinityMask 读反，
    /// 界面看起来一切正常，只有绑定效果不对 —— 所以三层各锁一条。
    /// </summary>
    public sealed class MaskBindTypeTests
    {
        [Theory]
        [InlineData("cpuSets", MaskType.CpuSets)]
        [InlineData("affinityMask", MaskType.AffinityMask)]
        [InlineData("AFFINITYMASK", MaskType.AffinityMask)]
        [InlineData("", MaskType.CpuSets)]
        [InlineData(null, MaskType.CpuSets)]
        [InlineData("以后才有的值", MaskType.CpuSets)]
        public void Parse_FallsBackToCpuSets(string? value, MaskType expected)
            => Assert.Equal(expected, MaskTypeNames.Parse(value));

        [Fact]
        public void ToName_RoundTripsThroughParse()
        {
            foreach (MaskType type in Enum.GetValues<MaskType>())
                Assert.Equal(type, MaskTypeNames.Parse(MaskTypeNames.ToName(type)));
        }

        [Fact]
        public void Upsert_KeepsBindType_AcrossReload()
        {
            FakeAppPaths paths = new();
            MaskStore store = new(new FakeLogService(), paths);
            store.Load();

            Assert.True(
                store.Upsert(
                    new CustomMask { Name = "大核亲和", Mask = 0xF, Type = MaskType.AffinityMask },
                    out MaskStore.UpsertError error),
                MaskStore.Describe(error));

            Assert.Equal(MaskType.AffinityMask, store.Masks.Single().Type);

            // 落盘后再读：masks.json 里存的是枚举数字，重载必须还原成同一项。
            MaskStore reloaded = new(new FakeLogService(), paths);
            reloaded.Load();

            Assert.Equal(MaskType.AffinityMask, reloaded.Masks.Single().Type);
        }

        [Fact]
        public void Load_LegacyFileWithoutType_DefaultsToCpuSets()
        {
            FakeAppPaths paths = new();
            Directory.CreateDirectory(paths.DataDirectory);

            // 加 Type 字段之前的 masks.json：没有这个键。
            File.WriteAllText(
                paths.File("masks.json"),
                "[{\"Id\":\"legacy\",\"Name\":\"旧掩码\",\"Mask\":255,\"Preset\":false}]");

            MaskStore store = new(new FakeLogService(), paths);
            store.Load();

            Assert.Equal(MaskType.CpuSets, store.Masks.Single().Type);
        }
    }
}
