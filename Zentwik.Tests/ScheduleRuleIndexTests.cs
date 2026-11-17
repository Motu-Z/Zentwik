using System.Collections.Generic;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Tests
{
    /// <summary>恢复默认的规则（Target=None）不参与匹配，也不再下发绑定。</summary>
    public sealed class ScheduleRuleIndexTests
    {
        private static ScheduleRule Rule(string name, SchedulingTarget target, bool enabled = true) => new()
        {
            Id = "r-" + name,
            Name = name,
            Target = target,
            TargetMask = target == SchedulingTarget.None ? 0UL : 0xFFUL,
            MaskId = target == SchedulingTarget.None ? string.Empty : "m1",
            Enabled = enabled
        };

        [Fact]
        public void None_target_is_skipped()
        {
            Dictionary<string, ScheduleRule> index = ProcessWatcher.BuildRuleIndex(new List<ScheduleRule>
            {
                Rule("kept.exe", SchedulingTarget.TargetMask),
                Rule("cleared.exe", SchedulingTarget.None)
            });

            Assert.True(index.ContainsKey("kept.exe"));
            Assert.False(index.ContainsKey("cleared.exe"));
        }

        [Fact]
        public void None_target_does_not_match_processes()
        {
            Assert.False(ProcessWatcher.Matches(Rule("cleared.exe", SchedulingTarget.None), "cleared.exe"));
            Assert.True(ProcessWatcher.Matches(Rule("kept.exe", SchedulingTarget.TargetMask), "kept.exe"));
        }

        [Fact]
        public void Disabled_rule_is_skipped()
        {
            Dictionary<string, ScheduleRule> index = ProcessWatcher.BuildRuleIndex(new List<ScheduleRule>
            {
                Rule("off.exe", SchedulingTarget.TargetMask, enabled: false)
            });

            Assert.Empty(index);
        }
    }
}
