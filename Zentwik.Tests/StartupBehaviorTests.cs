using System.Text.Json;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;
using Zentwik.Worker;

namespace Zentwik.Tests
{
    /// <summary>
    /// 启动 / 关闭行为的纯逻辑：命令行组装、关闭策略、Payload 解析。
    ///
    /// 它们的共同点是「写错了不会报错，只会表现成另一种行为」：
    ///   · 参数拼错 → 计划任务建成功，开机时却什么都不弹（或被当成未知参数忽略）；
    ///   · 关闭策略错 → 用户以为只是关了窗口，后台其实已经退了；
    ///   · Payload 解析错 → 开关点了没反应，或者把「只留托盘」静默降级成「开窗口」。
    /// 这些都不在编译期暴露，也不会有异常日志，只能靠断言钉住。
    /// </summary>
    public sealed class StartupBehaviorTests
    {
        // ==================== 命令行组装 ====================

        [Theory]
        [InlineData(AutoStartMode.WorkerWithUI, "--open-ui")]
        [InlineData(AutoStartMode.WorkerWithTray, "--tray")]
        [InlineData(AutoStartMode.WorkerOnly, "")]
        [InlineData(AutoStartMode.None, "")]
        public void BuildArguments_MapsModeToSwitch(AutoStartMode mode, string expected)
        {
            Assert.Equal(expected, AutoStartService.BuildArguments(mode));
        }

        /// <summary>
        /// None 与 WorkerOnly 的参数都是空串，但**只有 None 会被 Enable 拒绝**。
        /// 这个差别是「不要自启」与「自启但不带界面」的分界线，必须钉住。
        /// </summary>
        [Fact]
        public void Enable_WithNone_RefusedWithoutTouchingTaskScheduler()
        {
            AutoStartService service = new(new FakeLogService());

            // 返回 false 而不是抛异常：调用方（管道分派）只处理 bool。
            Assert.False(service.Enable(AutoStartMode.None));
        }

        [Fact]
        public void DescribeMode_IsDistinctForEveryMode()
        {
            string none = AutoStartService.DescribeMode(AutoStartMode.None);
            string worker = AutoStartService.DescribeMode(AutoStartMode.WorkerOnly);
            string tray = AutoStartService.DescribeMode(AutoStartMode.WorkerWithTray);
            string ui = AutoStartService.DescribeMode(AutoStartMode.WorkerWithUI);

            Assert.NotEqual(none, worker);
            Assert.NotEqual(worker, tray);
            Assert.NotEqual(tray, ui);
            Assert.NotEqual(worker, ui);
        }

        // ==================== 关闭策略 ====================

        /// <summary>Tray：窗口销毁，应用与 Worker 都继续跑。</summary>
        [Fact]
        public void ClosePolicy_Tray_KeepsProcessAlive()
        {
            CloseDecision decision = CloseBehaviorPolicy.Decide(CloseBehavior.Tray);

            Assert.False(decision.Cancel);
            Assert.False(decision.Shutdown);
            Assert.True(decision.ClearMainWindow);
        }

        /// <summary>Exit：窗口关掉、应用退出（OnExit 里再决定要不要关 Worker）。</summary>
        [Fact]
        public void ClosePolicy_Exit_ShutsDown()
        {
            CloseDecision decision = CloseBehaviorPolicy.Decide(CloseBehavior.Exit);

            Assert.False(decision.Cancel);
            Assert.True(decision.Shutdown);
            Assert.False(decision.ClearMainWindow);
        }

        /// <summary>WorkerOnly：同样是退出应用，但 OnExit 不发 shutdown。</summary>
        [Fact]
        public void ClosePolicy_WorkerOnly_ShutsDownUiButKeepsWorker()
        {
            CloseDecision decision = CloseBehaviorPolicy.Decide(CloseBehavior.WorkerOnly);

            Assert.False(decision.Cancel);
            Assert.True(decision.Shutdown);
            Assert.False(decision.ClearMainWindow);
        }

        /// <summary>
        /// Exit 与 WorkerOnly 的判定完全一致，日志必须不同 ——
        /// 否则用户排障时分不清「后台被我关了」和「后台还在跑」，
        /// 而这两者的区别正是这两个选项存在的唯一理由。
        /// </summary>
        [Fact]
        public void ClosePolicy_ExitAndWorkerOnly_LogDifferently()
        {
            string exit = CloseBehaviorPolicy.Decide(CloseBehavior.Exit).LogMessage;
            string workerOnly = CloseBehaviorPolicy.Decide(CloseBehavior.WorkerOnly).LogMessage;

            Assert.NotEqual(exit, workerOnly);
            Assert.Contains("[关闭行为]", exit, System.StringComparison.Ordinal);
            Assert.Contains("[关闭行为]", workerOnly, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// 三档的中文说明必须互不相同。
        /// 行为页下拉与日志共用 <c>Describe</c>，文案撞车会让「只留后台」和「完全退出」
        /// 在界面上看起来是同一件事 —— 而这两者的差别正是用户唯一关心的。
        /// </summary>
        [Fact]
        public void ClosePolicy_DescribeIsDistinctForEveryBehavior()
        {
            string exit = CloseBehaviorPolicy.Describe(CloseBehavior.Exit);
            string tray = CloseBehaviorPolicy.Describe(CloseBehavior.Tray);
            string workerOnly = CloseBehaviorPolicy.Describe(CloseBehavior.WorkerOnly);

            Assert.NotEqual(exit, tray);
            Assert.NotEqual(tray, workerOnly);
            Assert.NotEqual(exit, workerOnly);
            Assert.NotEqual(string.Empty, exit);
        }

        // ==================== register-autostart 的 Payload ====================

        [Theory]
        [InlineData("WorkerWithUI", AutoStartMode.WorkerWithUI)]
        [InlineData("workerwithtray", AutoStartMode.WorkerWithTray)]
        [InlineData(" WorkerOnly ", AutoStartMode.WorkerOnly)]
        [InlineData("None", AutoStartMode.None)]
        public void ParseAutoStartMode_AcceptsEnumNames(string payload, AutoStartMode expected)
        {
            Assert.True(PipeServerService.TryParseAutoStartMode(payload, out AutoStartMode mode));
            Assert.Equal(expected, mode);
        }

        /// <summary>旧版 UI 发空 Payload，语义固定为「拉服务 + 开窗口」。</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseAutoStartMode_EmptyMeansWorkerWithUi(string? payload)
        {
            Assert.True(PipeServerService.TryParseAutoStartMode(payload, out AutoStartMode mode));
            Assert.Equal(AutoStartMode.WorkerWithUI, mode);
        }

        /// <summary>
        /// 数值形态必须拒绝：Enum.TryParse 默认接受 "3"，
        /// 一旦静默接受，「传了 3」和「传了 WorkerWithUI」在日志里就长得一模一样。
        /// </summary>
        [Theory]
        [InlineData("3")]
        [InlineData("99")]
        [InlineData("WorkerWithEverything")]
        public void ParseAutoStartMode_RejectsNumbersAndUnknownNames(string payload)
        {
            Assert.False(PipeServerService.TryParseAutoStartMode(payload, out _));
        }

        // ==================== ui.json 的默认值 ====================

        /// <summary>
        /// 默认值必须是「不自启 + 关窗口就退」。
        /// 枚举的默认值恰好是 0，但这条产品决定不能靠「恰好」——
        /// 将来有人往枚举里插一个值而不改数字，默认行为就悄悄变了。
        /// </summary>
        [Fact]
        public void UiSettings_Defaults_AreConservative()
        {
            UiSettings settings = new();

            Assert.Equal(AutoStartMode.None, settings.AutoStartMode);
            Assert.Equal(CloseBehavior.Exit, settings.CloseBehavior);
        }

        /// <summary>两个枚举在 ui.json 里必须是名称而不是数字（可读、可手改）。</summary>
        [Fact]
        public void UiSettings_SerializesEnumsAsNames()
        {
            string json = JsonSerializer.Serialize(
                new UiSettings
                {
                    AutoStartMode = AutoStartMode.WorkerWithTray,
                    CloseBehavior = CloseBehavior.WorkerOnly
                },
                AppJsonContext.Default.UiSettings);

            Assert.Contains("\"WorkerWithTray\"", json, System.StringComparison.Ordinal);
            Assert.Contains("\"WorkerOnly\"", json, System.StringComparison.Ordinal);

            UiSettings? restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.UiSettings);
            Assert.NotNull(restored);
            Assert.Equal(AutoStartMode.WorkerWithTray, restored!.AutoStartMode);
            Assert.Equal(CloseBehavior.WorkerOnly, restored.CloseBehavior);
        }

        // ==================== 旧键一律忽略（不迁移） ====================

        /// <summary>
        /// 用临时目录跑一次真实的 Load：写入给定 ui.json 原文，返回读出来的设置。
        ///
        /// 这里要验证的是「读盘路径有没有偷偷做兼容」—— 只测 JsonSerializer
        /// 会漏掉 ReadFromDisk 里可能被加回来的旧键探测。
        /// </summary>
        private static UiSettings LoadFromJson(string json)
        {
            FakeAppPaths paths = new();
            try
            {
                paths.EnsureCreated();
                System.IO.File.WriteAllText(paths.File("ui.json"), json);

                UiSettingsStore store = new(paths);
                store.Load();
                return store.Settings;
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(paths.DataDirectory, recursive: true);
                }
                catch
                {
                    // 清理失败不影响断言
                }
            }
        }

        /// <summary>
        /// 旧版的 <c>"AutoStart": true</c> **不再迁移**。
        ///
        /// 本工程已裁定不背历史包袱：旧键读进来被静默 Skip（没有配置
        /// UnmappedMemberHandling.Disallow），自启形态回到默认的 None。
        /// 这条测试就是那条裁定的看门人 —— 谁哪天把迁移逻辑加回来，它会红。
        /// </summary>
        [Fact]
        public void LegacyAutoStartKey_IsIgnored()
        {
            UiSettings settings = LoadFromJson("""{"AutoStart": true}""");

            Assert.Equal(AutoStartMode.None, settings.AutoStartMode);
        }

        /// <summary>旧版强调色迁移用的两个字段已删除，残留键既不能读进来也不能让整份配置失败。</summary>
        [Fact]
        public void LegacyAccentMigrationFields_AreIgnored()
        {
            UiSettings settings = LoadFromJson(
                """{"AccentVersion": 0, "FollowSystemAccentExplicit": true, "FollowSystemAccent": false}""");

            Assert.False(settings.FollowSystemAccent);
        }

        /// <summary>未知键不能让整份配置读失败（默认 Skip）。</summary>
        [Fact]
        public void UnknownKeys_DoNotBreakLoad()
        {
            UiSettings settings = LoadFromJson(
                """{"SomeFutureField": 42, "CloseBehavior": "Tray"}""");

            Assert.Equal(CloseBehavior.Tray, settings.CloseBehavior);
        }
    }
}
