using System.Text.Json;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>掩码被删除后规则仍按原绑定方式下发。</summary>
    public sealed class ScheduleBindTypeTests
    {
        private static ScheduleRule MaskRule(MaskType snapshot) => new()
        {
            Name = "game.exe",
            Target = SchedulingTarget.TargetMask,
            MaskId = "mask-1",
            TargetMask = 0xF0,
            BindType = snapshot,
        };

        private static CustomMask LiveMask(MaskType type) => new()
        {
            Id = "mask-1",
            Name = "大核",
            Mask = 0xF0,
            Type = type,
        };

        [Fact]
        public void Live_mask_type_wins_over_snapshot()
        {
            // 用户把掩码从 Affinity Mask 改成 CPU Sets，规则里的快照过期了也得跟实时值走。
            Assert.False(ProcessWatcher.UseAffinityMask(MaskRule(MaskType.AffinityMask), LiveMask(MaskType.CpuSets)));
            Assert.True(ProcessWatcher.UseAffinityMask(MaskRule(MaskType.CpuSets), LiveMask(MaskType.AffinityMask)));
        }

        [Fact]
        public void Deleted_mask_falls_back_to_snapshot()
        {
            Assert.True(ProcessWatcher.UseAffinityMask(MaskRule(MaskType.AffinityMask), null));
            Assert.False(ProcessWatcher.UseAffinityMask(MaskRule(MaskType.CpuSets), null));
        }

        [Fact]
        public void Legacy_ccd_rule_never_uses_affinity_mask()
        {
            ScheduleRule legacy = new() { Name = "old.exe", Target = (SchedulingTarget)1 };

            Assert.True(legacy.IsLegacyCcd);
            Assert.False(ProcessWatcher.UseAffinityMask(legacy, LiveMask(MaskType.AffinityMask)));
        }

        [Fact]
        public void Saved_rule_keeps_bind_type()
        {
            SchedulingProfile profile = new();
            profile.Rules.Add(MaskRule(MaskType.AffinityMask));

            string json = JsonSerializer.Serialize(profile, AppJsonContext.Default.SchedulingProfile);
            SchedulingProfile? back = JsonSerializer.Deserialize(json, AppJsonContext.Default.SchedulingProfile);

            Assert.NotNull(back);
            Assert.Equal(MaskType.AffinityMask, back!.Rules[0].BindType);
        }

        [Fact]
        public void Rule_without_bind_type_reads_as_cpu_sets()
        {
            const string legacy = """{"Rules":[{"Name":"game.exe","Target":3,"TargetMask":255}]}""";
            SchedulingProfile? profile = JsonSerializer.Deserialize(legacy, AppJsonContext.Default.SchedulingProfile);

            Assert.NotNull(profile);
            Assert.Equal(MaskType.CpuSets, profile!.Rules[0].BindType);
        }
    }
}
