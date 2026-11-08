using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>管道契约：常量形状 + 版本协商只有「相等才通过」一条规则。</summary>
    public sealed class WorkerPipeContractTests
    {
        [Fact]
        public void Constants_match_contract()
        {
            // 9：方案拆成三类独立列表（调校 / 调度 / 电源），旧的 profile 五条命令下线。
            // 这里写死字面量是有意的 —— 改版本号必须同时改这条断言，
            // 否则「顺手把版本号从 8 改成 9」这种改动没人会注意到。
            Assert.Equal(10, WorkerPipeContract.ProtocolVersion);
            Assert.False(string.IsNullOrWhiteSpace(WorkerPipeContract.PipeName));
            Assert.StartsWith(@"Global\", WorkerPipeContract.WorkerMutexName);
        }

        [Fact]
        public void ProcessPageCommands_match_contract_strings()
        {
            Assert.Equal("query-processes", WorkerPipeContract.CommandQueryProcesses);
            Assert.Equal("query-scheduling-rules", WorkerPipeContract.CommandQuerySchedulingRules);
            Assert.Equal("save-scheduling-rule", WorkerPipeContract.CommandSaveSchedulingRule);
            Assert.Equal("delete-scheduling-rule", WorkerPipeContract.CommandDeleteSchedulingRule);
            Assert.Equal("clear-all-schedules", WorkerPipeContract.CommandClearAllSchedules);

            // 界面要靠这条拿到掩码 Id —— 保存规则时 targetValue 传的是 Id，不是掩码值。
            Assert.Equal("query-masks", WorkerPipeContract.CommandQueryMasks);
            Assert.Equal("save-mask", WorkerPipeContract.CommandSaveMask);
            Assert.Equal("delete-mask", WorkerPipeContract.CommandDeleteMask);
            Assert.Equal("query-cpu-topology", WorkerPipeContract.CommandQueryCpuTopology);
        }

        [Fact]
        public void PlanCommands_match_contract_strings()
        {
            // 三类方案各查各的：合成一条会让「切了调校方案，进程页也跟着变」。
            Assert.Equal("query-plans", WorkerPipeContract.CommandQueryPlans);
            Assert.Equal("save-plan", WorkerPipeContract.CommandSavePlan);
            Assert.Equal("delete-plan", WorkerPipeContract.CommandDeletePlan);
            Assert.Equal("rename-plan", WorkerPipeContract.CommandRenamePlan);
            Assert.Equal("apply-plan", WorkerPipeContract.CommandApplyPlan);
        }

        [Fact]
        public void PlanKind_wire_names_round_trip()
        {
            foreach (PlanKind kind in new[] { PlanKind.Tuning, PlanKind.Scheduling, PlanKind.Power })
            {
                string wire = PlanKinds.ToWire(kind);
                Assert.True(PlanKinds.TryParse(wire, out PlanKind back));
                Assert.Equal(kind, back);
            }

            // 写错必须失败，不能悄悄落成默认值 —— 那会把「电源方案」存进调校列表。
            Assert.False(PlanKinds.TryParse("nope", out _));
        }

        [Fact]
        public void ScenarioCommands_match_contract_strings()
        {
            Assert.Equal("query-scenario-rules", WorkerPipeContract.CommandQueryScenarioRules);
            Assert.Equal("save-scenario-rule", WorkerPipeContract.CommandSaveScenarioRule);
            Assert.Equal("delete-scenario-rule", WorkerPipeContract.CommandDeleteScenarioRule);
            Assert.Equal("query-scenario-state", WorkerPipeContract.CommandQueryScenarioState);
            Assert.Equal("set-scenario-enabled", WorkerPipeContract.CommandSetScenarioEnabled);
        }

        [Fact]
        public void LogCommands_match_contract_strings()
        {
            Assert.Equal("snapshot-logs", WorkerPipeContract.CommandSnapshotLogs);

            // 清空必须落到后台：只清界面那份缓存的话，下次刷新日志会全回来。
            Assert.Equal("clear-logs", WorkerPipeContract.CommandClearLogs);
        }

        [Fact]
        public void ValidateVersion_accepts_only_protocol_version()
        {
            Assert.True(WorkerPipeContract.ValidateVersion(WorkerPipeContract.ProtocolVersion, out string ok));
            Assert.Equal(string.Empty, ok);

            foreach (int bad in new[] { 0, WorkerPipeContract.ProtocolVersion - 1, WorkerPipeContract.ProtocolVersion + 1, -1 })
            {
                Assert.False(WorkerPipeContract.ValidateVersion(bad, out string error));
                Assert.Equal(WorkerPipeContract.VersionMismatchMessage, error);
            }
        }

        [Fact]
        public void VersionMismatchResponse_has_fixed_shape()
        {
            PipeResponse response = WorkerPipeContract.VersionMismatchResponse();
            Assert.Equal(WorkerPipeContract.ProtocolVersion, response.Version);
            Assert.False(response.Success);
            Assert.Equal(WorkerPipeContract.VersionMismatchMessage, response.Message);
            Assert.Null(response.Payload);
        }
    }
}
