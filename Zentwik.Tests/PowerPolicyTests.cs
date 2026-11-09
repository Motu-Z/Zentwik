using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;
using Zentwik.Worker;

namespace Zentwik.Tests
{
    /// <summary>
    /// 电源策略的两条 IPC：读快照与增量写入。
    ///
    /// 这些测试会在开发机上跑几十次，而每一条写入路径都会改当前电源方案 ——
    /// 一个跑失败的测试可能把机器留在「最大处理器状态 50%」的状态里，
    /// 后续所有构建都跟着变慢，而且没人会想到是测试干的。
    /// 假实现把「改系统」这一步完全摘掉，同时保留「写进去再读回来」的因果。
    ///
    ///   1. 读不到某一项时，快照里是 Supported=false + 原因，而不是一个 0；
    ///   2. 批量写入只激活一次（CalledGuids.Count == 1），不是每项激活一次；
    ///   3. 失败文案分得清「没写成」与「写成了但没生效」—— 用户要做的事完全不同
    ///      （重试 vs 重启）。
    /// </summary>
    public sealed class PowerPolicyTests
    {
        /// <summary>造一个五项都读得出来的假 API。</summary>
        private static FakePowerPolicyApi BuildApi()
        {
            FakePowerPolicyApi api = new();
            api.AcValues[PowerPolicyService.SettingEpp] = 50;
            api.AcValues[PowerPolicyService.SettingBoostMode] = 2;
            api.AcValues[PowerPolicyService.SettingMaxProcessorState] = 100;
            api.AcValues[PowerPolicyService.SettingAspm] = 1;
            api.AcValues[PowerPolicyService.SettingIdleDisable] = 0;
            return api;
        }

        private static PowerPolicySnapshot ReadPayload(PipeResponse response)
        {
            Assert.NotNull(response.Payload);
            PowerPolicySnapshot? snapshot = JsonSerializer.Deserialize(
                response.Payload!, IpcJsonContext.Default.PowerPolicySnapshot);

            Assert.NotNull(snapshot);
            return snapshot!;
        }

        // ==================== 读 ====================

        [Fact]
        public void QueryPowerPolicy_Normal_ReturnsEveryValue()
        {
            FakePowerPolicyApi api = BuildApi();

            PipeResponse response = PipeServerService.RunQueryPowerPolicy(new PowerPolicyService(api));

            Assert.True(response.Success);
            Assert.Equal(WorkerPipeContract.ProtocolVersion, response.Version);

            PowerPolicySnapshot snapshot = ReadPayload(response);
            Assert.Equal(FakePowerPolicyApi.BalancedScheme, snapshot.ActiveSchemeGuid);
            Assert.Equal("平衡", snapshot.ActiveSchemeName);

            Assert.Equal(50, snapshot.Epp);
            Assert.Equal(2, snapshot.BoostMode);
            Assert.Equal(100, snapshot.MaxProcessorState);
            Assert.Equal(1, snapshot.Aspm);
            Assert.False(snapshot.IdleDisable);

            Assert.True(snapshot.EppSupported);
            Assert.True(snapshot.BoostModeSupported);
            Assert.True(snapshot.MaxProcessorStateSupported);
            Assert.True(snapshot.AspmSupported);
            Assert.True(snapshot.IdleDisableSupported);

            Assert.Null(snapshot.EppError);
            Assert.Null(snapshot.BoostModeError);
        }

        [Fact]
        public void QueryPowerPolicy_PartiallyUnsupported_ReportsPerItemErrors()
        {
            // 台式机上 ASPM 与 C-State 经常整项不存在 —— 这正是「读到 0」与
            // 「读不到」必须分开的那一类情况：两者的值都是 0。
            FakePowerPolicyApi api = BuildApi();
            api.Unsupported.Add(PowerPolicyService.SettingAspm);
            api.Unsupported.Add(PowerPolicyService.SettingIdleDisable);

            PipeResponse response = PipeServerService.RunQueryPowerPolicy(new PowerPolicyService(api));

            Assert.True(response.Success);

            PowerPolicySnapshot snapshot = ReadPayload(response);

            // 支持的那几项照常回值。
            Assert.True(snapshot.EppSupported);
            Assert.Equal(50, snapshot.Epp);

            // 不支持的两项：值为 null（不是 0），并带上错误码。
            Assert.False(snapshot.AspmSupported);
            Assert.Null(snapshot.Aspm);
            Assert.Equal($"{PowerPolicyService.LabelAspm}读取失败（错误 87）", snapshot.AspmError);

            Assert.False(snapshot.IdleDisableSupported);
            Assert.Null(snapshot.IdleDisable);
            Assert.Equal(
                $"{PowerPolicyService.LabelIdleDisable}读取失败（错误 87）",
                snapshot.IdleDisableError);
        }

        [Fact]
        public void QueryPowerPolicy_NoActiveScheme_DegradesButStillReturnsSnapshot()
        {
            FakePowerPolicyApi api = BuildApi();
            api.ActiveScheme = null;

            PipeResponse response = PipeServerService.RunQueryPowerPolicy(new PowerPolicyService(api));

            // 查询本身成功了：读不到方案是快照的内容，不是查询失败。
            Assert.True(response.Success);
            Assert.Equal(PowerPolicyService.NoActiveSchemeForSnapshotMessage, response.Message);

            PowerPolicySnapshot snapshot = ReadPayload(response);

            Assert.Equal(Guid.Empty, snapshot.ActiveSchemeGuid);
            Assert.Equal(PowerPolicySnapshot.UnknownSchemeName, snapshot.ActiveSchemeName);

            Assert.False(snapshot.EppSupported);
            Assert.False(snapshot.BoostModeSupported);
            Assert.False(snapshot.MaxProcessorStateSupported);
            Assert.False(snapshot.AspmSupported);
            Assert.False(snapshot.IdleDisableSupported);

            // 五项共用同一句原因：这个状态下故障只有一个。
            Assert.Equal(PowerPolicyService.NoActiveSchemeForSnapshotMessage, snapshot.EppError);
            Assert.Equal(PowerPolicyService.NoActiveSchemeForSnapshotMessage, snapshot.AspmError);

            // 值本身保持 null —— 0 会被界面画成一个合法的滑块位置。
            Assert.Null(snapshot.Epp);
            Assert.Null(snapshot.IdleDisable);
        }

        [Fact]
        public void QueryPowerPolicy_NoSchemeName_FallsBackToUnknown()
        {
            FakePowerPolicyApi api = BuildApi();
            api.SchemeName = null;

            PowerPolicySnapshot snapshot = ReadPayload(
                PipeServerService.RunQueryPowerPolicy(new PowerPolicyService(api)));

            // 方案读得到、名字读不到：GUID 照常给，名字用占位。
            Assert.Equal(FakePowerPolicyApi.BalancedScheme, snapshot.ActiveSchemeGuid);
            Assert.Equal(PowerPolicySnapshot.UnknownSchemeName, snapshot.ActiveSchemeName);
            Assert.True(snapshot.EppSupported);
        }

        [Fact]
        public void ReadSnapshot_NeverReturnsNull()
        {
            FakePowerPolicyApi api = new() { ActiveScheme = null };

            PowerPolicySnapshot snapshot = new PowerPolicyService(api).ReadSnapshot();

            Assert.NotNull(snapshot);
        }

        // ==================== 写：拦截 ====================

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{}")]
        public void ApplyPowerPolicy_EmptyPayload_IsRejected(string? payload)
        {
            FakePowerPolicyApi api = BuildApi();

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), payload);

            Assert.False(response.Success);
            Assert.Equal(PowerPolicyService.EmptyPayloadMessage, response.Message);
            Assert.Null(response.Payload);

            // 一个字段都没改：不该有任何写入，也不该激活。
            Assert.Empty(api.Writes);
            Assert.Empty(api.CalledGuids);
        }

        [Fact]
        public void ApplyPowerPolicy_MalformedJson_IsRejected()
        {
            FakePowerPolicyApi api = BuildApi();

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":");

            // 两者都是 Success = false，但一个是请求坏了、一个是请求空。
            // 混成一句会让排障的人去查 UI 表单，而真正的问题在序列化层。
            Assert.False(response.Success);
            Assert.StartsWith("请求格式错误：", response.Message);
            Assert.NotEqual(PowerPolicyService.EmptyPayloadMessage, response.Message);
            Assert.Null(response.Payload);
            Assert.Empty(api.Writes);
        }

        [Theory]
        [InlineData("{\"epp\":101}", PowerPolicyService.LabelEpp, 0, 100)]
        [InlineData("{\"epp\":-1}", PowerPolicyService.LabelEpp, 0, 100)]
        [InlineData("{\"maxProcessorState\":49}", PowerPolicyService.LabelMaxProcessorState, 50, 100)]
        [InlineData("{\"maxProcessorState\":101}", PowerPolicyService.LabelMaxProcessorState, 50, 100)]
        [InlineData("{\"aspm\":3}", PowerPolicyService.LabelAspm, 0, 2)]
        [InlineData("{\"boostMode\":5}", PowerPolicyService.LabelBoostMode, 0, 4)]
        [InlineData("{\"boostMode\":-1}", PowerPolicyService.LabelBoostMode, 0, 4)]
        public void ApplyPowerPolicy_OutOfRange_DoesNotTouchSystem(
            string payload, string label, int min, int max)
        {
            FakePowerPolicyApi api = BuildApi();

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), payload);

            Assert.False(response.Success);

            // 越界值在本地就被拦下：既没写系统，也没激活方案。
            // 这条断言比消息文案更重要 —— 「写了再回滚」在电源策略上没有回滚一说。
            Assert.Empty(api.Writes);
            Assert.Empty(api.CalledGuids);

            // 【文案不能套用 Win32 格式】本地拦截的失败根本没发生过系统调用，
            // 写成「（错误 87）」会让排障的人去查错误码表。
            Assert.Equal($"{label}超出允许范围（{min}-{max}）", response.Message);
            Assert.DoesNotContain("错误", response.Message);
        }

        // ==================== 写：正常 ====================

        [Fact]
        public void ApplyPowerPolicy_MultipleFields_ActivatesExactlyOnce()
        {
            FakePowerPolicyApi api = BuildApi();

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api),
                "{\"epp\":40,\"boostMode\":1,\"maxProcessorState\":90,\"aspm\":2,\"idleDisable\":true}");

            Assert.True(response.Success);
            Assert.Equal(WorkerPipeContract.ProtocolVersion, response.Version);

            // 五项都写进去了。
            Assert.Equal(5, api.Writes.Count);

            // 但只激活一次。每项激活一次会让电源管理器被反复拉起，
            // 在低端机上表现为「点应用后整机卡两秒」。
            Assert.Single(api.CalledGuids);
            Assert.Equal(FakePowerPolicyApi.BalancedScheme, api.CalledGuids[0]);

            // 返回的是写入后的快照，UI 不必再查一次。
            PowerPolicySnapshot snapshot = ReadPayload(response);
            Assert.Equal(40, snapshot.Epp);
            Assert.Equal(1, snapshot.BoostMode);
            Assert.Equal(90, snapshot.MaxProcessorState);
            Assert.Equal(2, snapshot.Aspm);
            Assert.True(snapshot.IdleDisable);
        }

        [Fact]
        public void ApplyPowerPolicy_OnlyTouchesFieldsThatWereProvided()
        {
            FakePowerPolicyApi api = BuildApi();
            uint before = api.AcValues[PowerPolicyService.SettingBoostMode];

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":30}");

            Assert.True(response.Success);
            Assert.Single(api.Writes);

            // 没提到的字段保持原值 —— 增量语义，不是「全量覆盖」。
            Assert.Equal(before, api.AcValues[PowerPolicyService.SettingBoostMode]);
            Assert.Equal(30u, api.AcValues[PowerPolicyService.SettingEpp]);
        }

        [Theory]
        [InlineData(true, 1u)]
        [InlineData(false, 0u)]
        public void ApplyPowerPolicy_IdleDisable_MapsBooleanToOneZero(bool disabled, uint expected)
        {
            FakePowerPolicyApi api = BuildApi();

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api),
                $"{{\"idleDisable\":{(disabled ? "true" : "false")}}}");

            Assert.True(response.Success);
            Assert.Single(api.Writes);
            Assert.Equal(PowerPolicyService.SettingIdleDisable, api.Writes[0].Setting);
            Assert.Equal(expected, api.Writes[0].Value);
        }

        // ==================== 写：失败语义 ====================

        [Fact]
        public void ApplyPowerPolicy_NoActiveScheme_ReportsReadFailure()
        {
            FakePowerPolicyApi api = BuildApi();
            api.ActiveScheme = null;

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":40,\"boostMode\":1,\"aspm\":2}");

            Assert.False(response.Success);
            Assert.Equal(PowerPolicyService.NoActiveSchemeMessage, response.Message);

            // 三项写不进去时原因是同一句，去重后只出现一次 —— 不能刷屏三遍。
            // 内部分隔符是逗号，所以这里连逗号也不该出现。
            Assert.DoesNotContain("；", response.Message);
            Assert.DoesNotContain("，", response.Message);

            // 系统里什么都没变 → 没有快照可回。
            Assert.Null(response.Payload);

            Assert.Empty(api.Writes);
            Assert.Empty(api.CalledGuids);
        }

        [Fact]
        public void ApplyPowerPolicy_ActivationFails_ReportsWrittenButNotActive()
        {
            FakePowerPolicyApi api = BuildApi();
            api.SetActiveSchemeSucceeds = false;

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":40}");

            Assert.False(response.Success);

            // 值确实写进去了 —— 这句话必须出现，否则用户会以为「什么都没发生」而反复重试。
            Assert.Equal(
                $"配置已写入但{PowerPolicyService.ActivateFailedMessage}",
                response.Message);

            Assert.Single(api.Writes);
            Assert.Single(api.CalledGuids);

            // 值已经落盘，界面必须能刷新成真实状态，否则用户看到「提示失败、
            // 滑块还停在旧位置」，下一次点应用又会把已生效的项重写一遍。
            Assert.NotNull(response.Payload);
        }

        [Fact]
        public void ApplyPowerPolicy_ApiRejectsValue_PassesErrorCodeThrough()
        {
            // 主板只认 Boost 0/1 的情况：范围校验放行（0-4 是我们的口径），
            // 但 API 返回 87。错误码必须原样带出去，否则用户看到的是
            // 「部分字段写入失败：升频模式（Boost）写入失败（）」这种没法排查的句子。
            FakePowerPolicyApi api = BuildApi();
            api.Unsupported.Add(PowerPolicyService.SettingBoostMode);

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"boostMode\":4}");

            Assert.False(response.Success);
            Assert.Equal(
                $"{PowerPolicyService.LabelBoostMode}写入失败（错误 87）",
                response.Message);

            // 一项都没写成 → 不激活、也没有快照可回。
            Assert.Empty(api.CalledGuids);
            Assert.Null(response.Payload);
        }

        [Fact]
        public void ApplyPowerPolicy_PartialFailure_StillActivatesButReportsFailure()
        {
            FakePowerPolicyApi api = BuildApi();
            api.Unsupported.Add(PowerPolicyService.SettingAspm);

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":40,\"aspm\":2}");

            // 有一项没写成 → 整体回失败：用户要求的事没有全部发生，
            // 回成功会让界面把失败那项也画成「已应用」。
            Assert.False(response.Success);
            Assert.Contains("部分字段写入失败", response.Message);

            // 但写成的那一项仍然要激活，否则它会一直躺在方案里不生效。
            Assert.Single(api.Writes);
            Assert.Single(api.CalledGuids);

            // 【失败也要回快照】系统里的值已经变了一半，界面必须能对齐真实状态。
            // 没有 Payload 的话，UI 只能继续显示旧值，用户下一次点应用
            // 会把已生效的那项再写一遍。
            Assert.NotNull(response.Payload);

            PowerPolicySnapshot snapshot = ReadPayload(response);
            Assert.Equal(40, snapshot.Epp);
        }

        [Fact]
        public void ApplyPowerPolicy_MultipleErrors_AreJoinedByCommaThenSemicolon()
        {
            // 两项被主板拒绝 + 激活也失败：一条消息里同时有两类问题。
            FakePowerPolicyApi api = BuildApi();
            api.Unsupported.Add(PowerPolicyService.SettingEpp);
            api.Unsupported.Add(PowerPolicyService.SettingBoostMode);
            api.SetActiveSchemeSucceeds = false;

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":40,\"boostMode\":2,\"aspm\":1}");

            Assert.False(response.Success);

            string expectedSummary =
                $"{PowerPolicyService.LabelEpp}写入失败（错误 87），" +
                $"{PowerPolicyService.LabelBoostMode}写入失败（错误 87）";

            // 逐项错误之间是逗号，两类信息之间是分号 ——
            // 共用一种分隔符的话，读的人分不清「后面那句是第三项错误还是整体失败」。
            Assert.Equal(
                $"{expectedSummary}；配置已写入但{PowerPolicyService.ActivateFailedMessage}",
                response.Message);

            Assert.Contains("，", response.Message);
            Assert.Contains("；", response.Message);

            // ASPM 那一项写成功了，所以整体仍然激活过一次，也仍然回快照。
            Assert.Single(api.CalledGuids);
            Assert.NotNull(response.Payload);
        }

        [Fact]
        public void ApplyPowerPolicy_PartialFailureWithoutActivationError_UsesPrefixOnly()
        {
            // 只失败一项、激活成功：消息不该出现分号（没有第二类问题）。
            FakePowerPolicyApi api = BuildApi();
            api.Unsupported.Add(PowerPolicyService.SettingAspm);

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":40,\"aspm\":2}");

            Assert.Equal(
                $"部分字段写入失败：{PowerPolicyService.LabelAspm}写入失败（错误 87）",
                response.Message);
            Assert.DoesNotContain("；", response.Message);
        }

        [Fact]
        public void ApplyPowerPolicy_PartialFailureAndSnapshotUnreadable_PayloadFallsBackToNull()
        {
            // 写入与激活都成功，但重读时方案已经不可用（例如用户在别处切换了方案）。
            // 此时回一份降级快照等于告诉界面「五项全不支持」，
            // 而系统里的值其实已经改了 —— 回 null 更诚实。
            FakePowerPolicyApi api = BuildApi();
            api.Unsupported.Add(PowerPolicyService.SettingAspm);

            // 调用次序：写 EPP(1) → 写 ASPM(2) → 激活(3) → 重读快照(4)。
            api.SchemeAvailableCalls = 3;

            PipeResponse response = PipeServerService.RunApplyPowerPolicy(
                new PowerPolicyService(api), "{\"epp\":40,\"aspm\":2}");

            Assert.False(response.Success);
            Assert.Contains("部分字段写入失败", response.Message);
            Assert.Null(response.Payload);
        }

        // ==================== ReadFriendlyName 的编码解析 ====================

        /// <summary>
        /// <see cref="PowerPolicyApi.ParseFriendlyName"/> 的纯逻辑测试。
        ///
        /// 【为什么能测】这一步只做「非托管 UTF-16 → string」，不碰 powrprof.dll。
        /// 抽出来之后，测试可以自己造缓冲区，不必去改真实电源方案的名字
        /// （那需要管理员权限，而且改完还得改回来）。
        /// </summary>
        [Theory]
        [InlineData("平衡")]
        [InlineData("High performance")]
        [InlineData("高性能")]
        public void ParseFriendlyName_DecodesUtf16AndStopsAtTerminator(string name)
        {
            // 模拟 API 的行为：长度是**含**结尾符的字节数。
            uint byteCount = (uint)((name.Length + 1) * sizeof(char));
            IntPtr buffer = Marshal.StringToHGlobalUni(name);

            try
            {
                Assert.Equal(name, PowerPolicyApi.ParseFriendlyName(buffer, byteCount));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void ParseFriendlyName_BoundsReadByBufferSize()
        {
            // 缓冲区里有 4 个字符，但只声明 2 个的长度。
            // 不带长度限制的解析会越过声明范围继续读，表现为「名字后面挂了随机字符」。
            IntPtr buffer = Marshal.AllocHGlobal(4 * sizeof(char));

            try
            {
                Marshal.WriteInt16(buffer, 0, (short)'A');
                Marshal.WriteInt16(buffer, sizeof(char), (short)'B');
                Marshal.WriteInt16(buffer, 2 * sizeof(char), (short)'X');
                Marshal.WriteInt16(buffer, 3 * sizeof(char), (short)'Y');

                Assert.Equal("AB", PowerPolicyApi.ParseFriendlyName(buffer, 2 * sizeof(char)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        public void ParseFriendlyName_RejectsEmptyOrTruncatedBuffer(uint byteCount)
        {
            IntPtr buffer = Marshal.StringToHGlobalUni("平衡");

            try
            {
                // 不足一个字符的字节数：没有任何内容可读。
                Assert.Null(PowerPolicyApi.ParseFriendlyName(buffer, byteCount));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void ParseFriendlyName_NullPointer_ReturnsNull()
        {
            Assert.Null(PowerPolicyApi.ParseFriendlyName(IntPtr.Zero, 64));
        }

        [Fact]
        public void ParseFriendlyName_WhitespaceOnly_ReturnsNull()
        {
            // 全空白等同于没名字：界面上显示一串空格比显示「未知」更糟。
            IntPtr buffer = Marshal.StringToHGlobalUni("   ");

            try
            {
                Assert.Null(PowerPolicyApi.ParseFriendlyName(buffer, 4 * sizeof(char)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // ==================== 契约字面量 ====================

        [Fact]
        public void PowerPolicyCommands_MatchContractStrings()
        {
            Assert.Equal("query-power-policy", WorkerPipeContract.CommandQueryPowerPolicy);
            Assert.Equal("apply-power-policy", WorkerPipeContract.CommandApplyPowerPolicy);
            Assert.Equal("create-power-scheme", WorkerPipeContract.CommandCreatePowerScheme);
            Assert.Equal("rename-power-scheme", WorkerPipeContract.CommandRenamePowerScheme);
            Assert.Equal("delete-power-scheme", WorkerPipeContract.CommandDeletePowerScheme);
        }

        // ==================== 新建方案 ====================

        [Fact]
        public void CreateScheme_DerivesFromBuiltInPreset()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool created, Guid guid, string? error) =
                service.CreateScheme(PowerSchemePresets.HighPerformance.Guid, "高性能");

            Assert.True(created);
            Assert.Null(error);
            Assert.NotEqual(Guid.Empty, guid);

            // 派生源固定是平衡（隐藏标记问题，见 CreateScheme_DerivesFromBalanced_SoSchemeStaysListed），
            // 模板的差异靠逐项搬运实现 —— 所以这里验证的也是「设置真的搬过来了」。
            Assert.Equal(PowerSchemePresets.Balanced.Guid, api.LastDuplicateSource);
            Assert.Equal(guid, api.LastRenameTarget);
            Assert.Contains(api.Schemes, s => s.Guid == guid && s.Name == "高性能");
        }

        [Fact]
        public void CreateScheme_CopiesTemplateDescription()
        {
            FakePowerPolicyApi api = BuildApi();
            api.Descriptions[PowerSchemePresets.UltimatePerformance.Guid] =
                "@C:\\WINDOWS\\system32\\powrprof.dll,-18,Ultimate Performance";

            PowerPolicyService service = new(api);

            (bool created, Guid guid, _) =
                service.CreateScheme(PowerSchemePresets.UltimatePerformance.Guid, "卓越性能");

            Assert.True(created);

            // 说明文字跟着派生源走。不搬的话新方案下面写着「自动平衡功耗与性能」，
            // 与「卓越性能」这个名字自相矛盾。
            Assert.Equal(
                "@C:\\WINDOWS\\system32\\powrprof.dll,-18,Ultimate Performance",
                api.Descriptions[guid]);
        }

        [Fact]
        public void CreateScheme_ToleratesDescriptionWriteFailure()
        {
            FakePowerPolicyApi api = BuildApi();
            api.Descriptions[PowerSchemePresets.HighPerformance.Guid] = "高性能";
            api.WriteDescriptionSucceeds = false;

            PowerPolicyService service = new(api);

            // 写说明要动 HKLM，非提权进程一定失败。方案本身建成的事实不能因此丢掉。
            (bool created, _, string? error) =
                service.CreateScheme(PowerSchemePresets.HighPerformance.Guid, "高性能");

            Assert.True(created);
            Assert.Null(error);
        }

        [Fact]
        public void CreateScheme_UltimateLayersOnHighPerformance()
        {
            FakePowerPolicyApi api = BuildApi();
            Guid processor = new("54533251-82be-4824-96c1-47b60b740d00");
            Guid minState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
            Guid disk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
            Guid diskIdle = new("6738e2c4-e8a5-4a42-b16a-e040e769756e");

            // 高性能的差异在处理器子组；卓越性能模板在本机只有硬盘与 PCIe 两项。
            api.SchemeTrees[PowerSchemePresets.HighPerformance.Guid] =
                new() { [processor] = new() { minState } };
            api.SchemeValues[(PowerSchemePresets.HighPerformance.Guid, minState)] = (100, 100);

            api.SchemeTrees[PowerSchemePresets.UltimatePerformance.Guid] =
                new() { [disk] = new() { diskIdle } };
            api.SchemeValues[(PowerSchemePresets.UltimatePerformance.Guid, diskIdle)] = (0, 0);

            PowerPolicyService service = new(api);

            (bool created, Guid guid, _) =
                service.CreateScheme(PowerSchemePresets.UltimatePerformance.Guid, "卓越性能");

            Assert.True(created);

            // 两层的取值都要落到新方案上：只搬卓越性能模板 = 复制平衡。
            Assert.Equal((100u, 100u), api.SchemeValues[(guid, minState)]);
            Assert.Equal((0u, 0u), api.SchemeValues[(guid, diskIdle)]);
        }

        [Fact]
        public void CreateScheme_RejectsDuplicateName()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool created, _, string? error) = service.CreateScheme(PowerSchemePresets.HighPerformance.Guid, "平衡");

            Assert.False(created);
            Assert.Equal(PowerPolicyService.SchemeNameExistsMessage, error);

            // 名字冲突必须在复制之前拦掉：先复制再改名失败再回滚，
            // 过程中系统里会短暂存在一份同名方案，资源管理器面板里能刷出来。
            Assert.Equal(Guid.Empty, api.LastDuplicateSource);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void CreateScheme_RejectsEmptyName(string name)
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool created, _, string? error) = service.CreateScheme(PowerSchemePresets.Balanced.Guid, name);

            Assert.False(created);
            Assert.Equal(PowerPolicyService.EmptySchemeNameMessage, error);
        }

        [Fact]
        public void CreateScheme_RejectsOverlongName()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            string name = new('字', PowerPolicyService.MaxSchemeNameLength + 1);

            (bool created, _, string? error) = service.CreateScheme(PowerSchemePresets.Balanced.Guid, name);

            Assert.False(created);
            Assert.Equal(PowerPolicyService.SchemeNameTooLongMessage, error);
        }

        [Fact]
        public void CreateScheme_RollsBackWhenRenameFails()
        {
            FakePowerPolicyApi api = BuildApi();
            api.RenameSchemeSucceeds = false;
            PowerPolicyService service = new(api);

            int before = api.Schemes.Count;

            (bool created, _, string? error) = service.CreateScheme(PowerSchemePresets.HighPerformance.Guid, "高性能");

            Assert.False(created);
            Assert.Equal(PowerPolicyService.RenameSchemeFailedMessage, error);

            // 复制出来的方案继承源名。改名失败却把它留下，用户会看到两张「高性能」，
            // 而且再点一次「新建」只会再多一张 —— 所以必须删掉。
            Assert.Equal(before, api.Schemes.Count);
        }

        [Fact]
        public void CreateScheme_ReportsDuplicateFailure()
        {
            FakePowerPolicyApi api = BuildApi();
            api.DuplicateSchemeFails = true;
            PowerPolicyService service = new(api);

            (bool created, _, string? error) = service.CreateScheme(PowerSchemePresets.HighPerformance.Guid, "高性能");

            Assert.False(created);
            Assert.Equal(PowerPolicyService.CreateSchemeFailedMessage, error);
        }

        [Fact]
        public void CreateScheme_RejectsUnknownTemplate()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool created, _, string? error) =
                service.CreateScheme(new Guid("11111111-2222-3333-4444-555555555555"), "随便");

            Assert.False(created);
            Assert.Equal(PowerPolicyService.TemplateNotFoundMessage, error);
        }

        [Fact]
        public void CreateScheme_AcceptsExistingSchemeAsTemplate()
        {
            FakePowerPolicyApi api = BuildApi();
            Guid custom = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            api.Schemes.Add((custom, "我的方案"));
            PowerPolicyService service = new(api);

            (bool created, Guid guid, _) = service.CreateScheme(custom, "我的方案副本");

            Assert.True(created);
            Assert.Equal(PowerSchemePresets.Balanced.Guid, api.LastDuplicateSource);
            Assert.NotEqual(custom, guid);
        }

        // ==================== 删除方案 ====================

        [Fact]
        public void DeleteScheme_RejectsBuiltInScheme()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool deleted, string? error) = service.DeleteScheme(PowerSchemePresets.Balanced.Guid);

            Assert.False(deleted);
            Assert.Equal(PowerPolicyService.BuiltInSchemeProtectedMessage, error);

            // 判定只看 GUID。用户把「平衡」改成「我的方案」之后它仍然是内置的那一份，
            // 删掉就等于永久失去平衡模式的模板（只能 powercfg -restoredefaultschemes 重置）。
            Assert.Equal(Guid.Empty, api.LastDeleteTarget);
        }

        [Fact]
        public void DeleteScheme_RemovesCustomScheme()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (_, Guid created, _) = service.CreateScheme(PowerSchemePresets.HighPerformance.Guid, "高性能");
            (bool deleted, string? error) = service.DeleteScheme(created);

            Assert.True(deleted);
            Assert.Null(error);
            Assert.DoesNotContain(api.Schemes, s => s.Guid == created);
        }

        [Fact]
        public void DeleteScheme_RejectsUnknownScheme()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool deleted, string? error) = service.DeleteScheme(new Guid("11111111-2222-3333-4444-555555555555"));

            Assert.False(deleted);
            Assert.Equal(PowerPolicyService.SchemeNotFoundMessage, error);
        }

        /// <summary>
        /// 活动方案删不掉：系统直接拒绝。用户刚新建完一个方案想删掉时，
        /// 它恰好就是活动的 —— 这条路径必然踩到，所以必须先切走再删。
        /// </summary>
        [Fact]
        public void DeleteScheme_SwitchesAwayWhenTargetIsActive()
        {
            FakePowerPolicyApi api = BuildApi();
            Guid custom = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            api.Schemes.Add((custom, "临时"));
            api.ActiveScheme = custom;
            PowerPolicyService service = new(api);

            (bool deleted, string? error) = service.DeleteScheme(custom);

            Assert.True(deleted);
            Assert.Null(error);

            // 切走的目标必须是「不是它自己的那一个」。
            Assert.Contains(api.CalledGuids, g => g == FakePowerPolicyApi.BalancedScheme);
        }

        // ==================== 改名 ====================

        [Fact]
        public void RenameScheme_RenamesInPlace()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool renamed, string? error) = service.RenameScheme(
                FakePowerPolicyApi.BalancedScheme, "  我的平衡  ");

            Assert.True(renamed);
            Assert.Null(error);

            // 首尾空白要去掉：它在界面上看不出来，却会让「我的平衡」与「我的平衡 」
            // 被判成两个不同的名字，重名检查随之失效。
            Assert.Contains(api.Schemes, s => s.Guid == FakePowerPolicyApi.BalancedScheme && s.Name == "我的平衡");
        }

        [Fact]
        public void RenameScheme_AllowsKeepingOwnName()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool renamed, string? error) = service.RenameScheme(FakePowerPolicyApi.BalancedScheme, "平衡");

            // 改成自己原来的名字不算冲突。否则用户点开改名、什么都没动、
            // 按回车却被告知「已存在同名方案」—— 而那个同名方案就是它自己。
            Assert.True(renamed);
            Assert.Null(error);
        }

        [Fact]
        public void RenameScheme_RejectsNameTakenByAnother()
        {
            FakePowerPolicyApi api = BuildApi();
            api.Schemes.Add((new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "高性能"));
            PowerPolicyService service = new(api);

            (bool renamed, string? error) = service.RenameScheme(FakePowerPolicyApi.BalancedScheme, "高性能");

            Assert.False(renamed);
            Assert.Equal(PowerPolicyService.SchemeNameExistsMessage, error);
        }

        [Fact]
        public void RenameScheme_RejectsUnknownScheme()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            (bool renamed, string? error) = service.RenameScheme(new Guid("11111111-2222-3333-4444-555555555555"), "x");

            Assert.False(renamed);
            Assert.Equal(PowerPolicyService.SchemeNotFoundMessage, error);
        }

        // ==================== Worker 侧三条命令 ====================

        [Fact]
        public void RunCreatePowerScheme_CreatesAndActivates()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            string payload = JsonSerializer.Serialize(
                new CreatePowerSchemePayload
                {
                    TemplateGuid = PowerSchemePresets.PowerSaver.Guid,
                    Name = "节能"
                },
                IpcJsonContext.Default.CreatePowerSchemePayload);

            PipeResponse response = PipeServerService.RunCreatePowerScheme(service, payload);

            Assert.True(response.Success);

            // 新建完不切过去，用户接着拖滑块会落到新建的那份里、界面上却还显示旧方案 ——
            // 表现为「改了半天没效果」。
            Assert.Contains(api.CalledGuids, g => g != Guid.Empty);

            PowerSchemeListPayload? list = JsonSerializer.Deserialize(
                response.Payload!, IpcJsonContext.Default.PowerSchemeListPayload);

            Assert.NotNull(list);
            Assert.Equal(2, list!.Schemes.Count);
            Assert.Contains(list.Schemes, s => s.Name == "节能");
        }

        /// <summary>
        /// 新建的方案必须派生自「平衡」，不论用户选的是哪个模板。
        ///
        /// 从节能 / 高性能 / 卓越性能派生出的方案会继承模板的隐藏标记：
        /// 它不在 <c>PowerEnumerate</c> 里，只在被激活那一刻临时可见，
        /// 一旦切到别的方案就从列表中消失 —— 用户看到的是「新建的方案被删了」。
        /// 隐藏标记由 SYSTEM 独占写入，管理员也改不掉，唯一可行的做法是
        /// 用「平衡」当底、再把模板的设置搬过去。
        /// </summary>
        [Fact]
        public void CreateScheme_DerivesFromBalanced_SoSchemeStaysListed()
        {
            FakePowerPolicyApi api = BuildApi();
            api.HideDuplicatedScheme = true;
            PowerPolicyService service = new(api);

            (bool created, Guid guid, _) = service.CreateScheme(PowerSchemePresets.UltimatePerformance.Guid, "卓越");

            Assert.True(created);
            Assert.Equal(PowerSchemePresets.Balanced.Guid, api.LastDuplicateSource);
            Assert.Contains(api.Schemes, s => s.Guid == guid);
            Assert.Contains(api.Schemes, s => s.Guid == guid && s.Name == "卓越");
        }

        /// <summary>
        /// 模板的设置项要整份搬到新方案上 —— 只改名不搬设置的话，
        /// 从「高性能」建出来的方案其实是一份换了名字的平衡。
        /// </summary>
        [Fact]
        public void CreateScheme_CopiesTemplateSettings()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            Guid subgroup = new("54533251-82be-4824-96c1-47b60b740d00");
            Guid epp = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
            Guid boost = new("be337238-0d82-4146-a960-4f3749d470c7");

            api.SettingTree[subgroup] = new List<Guid> { epp, boost };
            api.AcValues[epp] = 0;
            api.AcValues[boost] = 2;
            api.DcValues[epp] = 0;
            api.DcValues[boost] = 2;

            (bool created, _, _) = service.CreateScheme(
                PowerSchemePresets.HighPerformance.Guid, "高性能");

            Assert.True(created);
            Assert.Contains((epp, 0u), api.Writes);
            Assert.Contains((boost, 2u), api.Writes);
        }

        [Fact]
        public void RunCreatePowerScheme_ActivatesCreatedScheme()
        {
            FakePowerPolicyApi api = BuildApi();
            api.HideDuplicatedScheme = true;
            PowerPolicyService service = new(api);

            string payload = JsonSerializer.Serialize(
                new CreatePowerSchemePayload
                {
                    TemplateGuid = PowerSchemePresets.HighPerformance.Guid,
                    Name = "高性能"
                },
                IpcJsonContext.Default.CreatePowerSchemePayload);

            PipeResponse response = PipeServerService.RunCreatePowerScheme(service, payload);

            Assert.True(response.Success);
            Assert.Equal("已创建并切换电源方案", response.Message);
        }

        [Fact]
        public void RunCreatePowerScheme_RejectsBrokenJson()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            PipeResponse response = PipeServerService.RunCreatePowerScheme(service, "{ not json");

            Assert.False(response.Success);
            Assert.Contains("请求格式错误", response.Message);
        }

        [Fact]
        public void RunRenamePowerScheme_ReturnsRefreshedList()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            string payload = JsonSerializer.Serialize(
                new RenamePowerSchemePayload
                {
                    Guid = FakePowerPolicyApi.BalancedScheme,
                    Name = "日常"
                },
                IpcJsonContext.Default.RenamePowerSchemePayload);

            PipeResponse response = PipeServerService.RunRenamePowerScheme(service, payload);

            Assert.True(response.Success);

            PowerSchemeListPayload? list = JsonSerializer.Deserialize(
                response.Payload!, IpcJsonContext.Default.PowerSchemeListPayload);

            Assert.Contains(list!.Schemes, s => s.Name == "日常");
        }

        [Fact]
        public void RunDeletePowerScheme_UsesBareGuidPayload()
        {
            FakePowerPolicyApi api = BuildApi();
            Guid custom = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            api.Schemes.Add((custom, "临时"));
            PowerPolicyService service = new(api);

            PipeResponse response = PipeServerService.RunDeletePowerScheme(service, custom.ToString("D"));

            Assert.True(response.Success);
            Assert.DoesNotContain(api.Schemes, s => s.Guid == custom);
        }

        [Fact]
        public void RunDeletePowerScheme_RejectsMalformedGuid()
        {
            FakePowerPolicyApi api = BuildApi();
            PowerPolicyService service = new(api);

            PipeResponse response = PipeServerService.RunDeletePowerScheme(service, "not-a-guid");

            Assert.False(response.Success);
            Assert.Equal("方案 Id 格式错误", response.Message);
        }
    }
}
