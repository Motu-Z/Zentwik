using System.Linq;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>调度方案往返规则时，Id、掩码名、绑定方式都不能丢。</summary>
    public sealed class PlanScheduleRuleRoundTripTests
    {
        private static PlanService Create(out FakeSchedulingStore scheduling)
        {
            FakeLogService log = new();
            FakeTuningStore tuning = new();
            scheduling = new FakeSchedulingStore();
            TuningApplyService apply = new(log, new FakeUiSettingsStore(), tuning, new FakeSmuReader());

            PlanService plans = new(
                log, tuning, scheduling, apply, new FakeProcessWatcher(), new FakeAppPaths(), new FakePowerPolicyApi());

            plans.Load();
            return plans;
        }

        [Fact]
        public void Apply_keeps_rule_identity_and_bind_type()
        {
            PlanService plans = Create(out FakeSchedulingStore scheduling);

            plans.Save(new PlanEntry
            {
                Id = "s1",
                Kind = PlanKind.Scheduling,
                Name = "AM 方案",
                ScheduleRules =
                [
                    new ScheduleRule
                    {
                        Id = "rule-am",
                        Name = "game.exe",
                        Target = SchedulingTarget.TargetMask,
                        MaskId = "m1",
                        TargetMask = 0xFFUL,
                        MaskName = "纯大核",
                        BindType = MaskType.AffinityMask,
                        Priority = "High",
                        Enabled = true
                    }
                ]
            });

            PlanApplyResult result = plans.Apply(PlanKind.Scheduling, "s1");

            Assert.True(result.Success);

            ScheduleRule applied = scheduling.Profile.Rules.Single();
            Assert.Equal("rule-am", applied.Id);
            Assert.Equal(MaskType.AffinityMask, applied.BindType);
            Assert.Equal("纯大核", applied.MaskName);
            Assert.Equal("High", applied.Priority);
        }
    }
}
