using System.Collections.Generic;
using Xunit;
using Zentwik.Core.Ipc;

namespace Zentwik.Tests
{
    /// <summary>规则集指纹：顺序无关、内容敏感。UI 与 Worker 各算一遍靠它判脏。</summary>
    public sealed class ScheduleRuleDigestTests
    {
        private static ScheduleRulePayload Rule(
            string name,
            string type = "ccd",
            string value = "0",
            string priority = "Normal",
            bool enabled = true) => new()
        {
            ProcessName = name,
            TargetType = type,
            TargetValue = value,
            Priority = priority,
            Enabled = enabled
        };

        [Fact]
        public void SameRulesInDifferentOrderMatch()
        {
            List<ScheduleRulePayload> a = new() { Rule("a.exe"), Rule("b.exe", "mask", "65280") };
            List<ScheduleRulePayload> b = new() { Rule("b.exe", "mask", "65280"), Rule("a.exe") };

            Assert.Equal(ScheduleRuleDigest.Of(a), ScheduleRuleDigest.Of(b));
        }

        [Fact]
        public void NameCaseAndPaddingIgnored()
        {
            Assert.Equal(
                ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("Game.exe") }),
                ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("  game.exe  ") }));
        }

        [Fact]
        public void AnyFieldChangeChangesDigest()
        {
            string baseline = ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("a.exe") });

            Assert.NotEqual(baseline, ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("a.exe", "ccd", "1") }));
            Assert.NotEqual(baseline, ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("a.exe", "ccd", "0", "High") }));
            Assert.NotEqual(baseline, ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("a.exe", "ccd", "0", "Normal", false) }));
            Assert.NotEqual(baseline, ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("b.exe") }));
        }

        [Fact]
        public void EmptySetIsStable()
        {
            Assert.Equal(
                ScheduleRuleDigest.Of(new List<ScheduleRulePayload>()),
                ScheduleRuleDigest.Of(null));
        }

        /// <summary>名称里带分隔符不能让两条规则拼成同一个串。</summary>
        [Fact]
        public void SeparatorInNameStaysDistinct()
        {
            Assert.NotEqual(
                ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("a|b.exe") }),
                ScheduleRuleDigest.Of(new List<ScheduleRulePayload> { Rule("a.exe"), Rule("b.exe") }));
        }
    }
}
