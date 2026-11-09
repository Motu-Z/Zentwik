using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.Tests.Fakes;

namespace Zentwik.Tests
{
    /// <summary>
    /// 托盘生命周期与「规则 Id」这两块纯逻辑。
    ///
    /// 真正的资源是 WinForms 的 NotifyIcon，只有 UI 进程能持有；但「什么时候该有」
    /// 与「有了之后该不该撤」都是纯函数，全部在 Core 的 <see cref="TrayPolicy"/> 里。
    /// 这里用一个假的存在位模拟资源槽，把「建一次 / 撤一次」的配对关系钉死 ——
    /// 那正是手工验收里最容易漏掉的部分（图标闪一下、或者撤了又建）。
    /// </summary>
    public sealed class TrayAndSettingsTests
    {
        // ==================== TrayPolicy：该不该有托盘 ====================

        /// <summary>
        /// 条件是「自启形态 = 后台+托盘」**或**「关闭行为 = 收进托盘」。
        /// 两者回答的是不同问题（开机拉什么 / 关窗口留什么），任一成立就需要托盘。
        /// </summary>
        [Theory]
        [InlineData(AutoStartMode.WorkerWithTray, CloseBehavior.Exit, true)]
        [InlineData(AutoStartMode.None, CloseBehavior.Tray, true)]
        [InlineData(AutoStartMode.WorkerWithTray, CloseBehavior.Tray, true)]
        [InlineData(AutoStartMode.WorkerWithUI, CloseBehavior.Tray, true)]
        [InlineData(AutoStartMode.None, CloseBehavior.Exit, false)]
        [InlineData(AutoStartMode.WorkerOnly, CloseBehavior.Exit, false)]
        [InlineData(AutoStartMode.WorkerOnly, CloseBehavior.WorkerOnly, false)]
        [InlineData(AutoStartMode.WorkerWithUI, CloseBehavior.WorkerOnly, false)]
        public void ShouldShowTray_IsTheOrOfBothSwitches(
            AutoStartMode mode, CloseBehavior behavior, bool expected)
            => Assert.Equal(expected, TrayPolicy.ShouldShowTray(mode, behavior));

        [Theory]
        [InlineData(true, false, TrayAction.Create)]
        [InlineData(true, true, TrayAction.None)]
        [InlineData(false, true, TrayAction.Dispose)]
        [InlineData(false, false, TrayAction.None)]
        public void Decide_MapsShouldShowAndExistsToAction(bool shouldShow, bool exists, TrayAction expected)
            => Assert.Equal(expected, TrayPolicy.Decide(shouldShow, exists));

        /// <summary>
        /// 用一个存在位模拟资源槽，跑一轮「条件反复切换」：
        /// 创建与撤销必须严格配对，且重复调用同一种状态不能重复动资源。
        ///
        /// 真实实现是「属性变更 → 重算一次」。用户拖动下拉时同一状态会被重算很多次，
        /// 只要有一处写成了「false 就 Dispose」，图标就会反复消失又出现。
        /// </summary>
        [Fact]
        public void TrayLifecycle_CreatesAndDisposesExactlyOncePerTransition()
        {
            bool exists = false;
            int created = 0;
            int disposed = 0;

            void Sync(bool shouldShow)
            {
                switch (TrayPolicy.Decide(shouldShow, exists))
                {
                    case TrayAction.Create:
                        created++;
                        exists = true;
                        break;

                    case TrayAction.Dispose:
                        disposed++;
                        exists = false;
                        break;
                }
            }

            Sync(true);
            Assert.True(exists);
            Assert.Equal(1, created);

            // 幂等：条件仍为真，不该再建一次。
            Sync(true);
            Assert.Equal(1, created);
            Assert.Equal(0, disposed);

            Sync(false);
            Assert.False(exists);
            Assert.Equal(1, disposed);

            // 幂等：条件仍为假，不该再撤一次。
            Sync(false);
            Assert.Equal(1, disposed);

            // 来回切三次：建 3 次、撤 2 次，最终处于「有」。
            Sync(true);
            Sync(false);
            Sync(true);

            Assert.Equal(3, created);
            Assert.Equal(2, disposed);
            Assert.True(exists);
        }

        [Fact]
        public void Describe_ReportsBothSwitchesAndCurrentState()
        {
            string text = TrayPolicy.Describe(AutoStartMode.WorkerWithTray, CloseBehavior.Exit, exists: false);

            Assert.Contains(nameof(AutoStartMode.WorkerWithTray), text, StringComparison.Ordinal);
            Assert.Contains(nameof(CloseBehavior.Exit), text, StringComparison.Ordinal);
            Assert.Contains("未创建", text, StringComparison.Ordinal);
        }

        // ==================== 计划任务路径自愈：参数 → 形态 ====================

        /// <summary>
        /// 校正任务路径时要留住用户原来选的形态，只能从任务的参数倒推。
        /// 认不出来时按「只拉 Worker」处理：少了界面可以手动开，
        /// 猜成「带界面」会在登录时凭空弹一个窗口。
        /// </summary>
        [Theory]
        [InlineData(null, AutoStartMode.WorkerOnly)]
        [InlineData("", AutoStartMode.WorkerOnly)]
        [InlineData("--open-ui", AutoStartMode.WorkerWithUI)]
        [InlineData("--tray", AutoStartMode.WorkerWithTray)]
        [InlineData("--TRAY", AutoStartMode.WorkerWithTray)]
        public void ParseArgumentsMode_MapsTaskArgumentsBackToMode(string? arguments, AutoStartMode expected)
        {
            Assert.Equal(expected, AutoStartService.ParseArgumentsMode(arguments));
        }

        // ==================== UiSettings：属性变更广播 ====================

        /// <summary>
        /// 只有两个「影响进程生命周期」的字段会广播。
        ///
        /// 其余字段（主题、材质、动效）改的是界面自身的呈现，由各自的绑定就地刷新；
        /// 也广播出去只会让托盘在换主题时被无意义地重算一次。
        /// </summary>
        [Fact]
        public void UiSettings_RaisesPropertyChanged_OnlyForLifecycleFields()
        {
            UiSettings settings = new();
            List<string?> names = new();
            settings.PropertyChanged += (_, e) => names.Add(e.PropertyName);

            settings.AutoStartMode = AutoStartMode.WorkerWithTray;
            settings.CloseBehavior = CloseBehavior.Tray;

            settings.ThemeMode = AppThemeMode.Dark;
            settings.NotifyOnError = false;
            settings.WatchProcessBinding = false;

            Assert.Equal(
                new[] { nameof(UiSettings.AutoStartMode), nameof(UiSettings.CloseBehavior) },
                names);
        }

        /// <summary>
        /// 赋同一个值不广播。
        ///
        /// 开机自启的保存失败路径会把内存值回滚成原值再赋一遍。
        /// 无脑广播会让托盘在那一次回滚里被销毁又重建 —— 用户看到图标闪了一下。
        /// </summary>
        [Fact]
        public void UiSettings_DoesNotRaise_WhenValueUnchanged()
        {
            UiSettings settings = new() { AutoStartMode = AutoStartMode.WorkerOnly };
            int raised = 0;
            settings.PropertyChanged += (_, _) => raised++;

            settings.AutoStartMode = AutoStartMode.WorkerOnly;

            Assert.Equal(0, raised);
        }

        /// <summary>
        /// 落盘形状里不能再出现任何「托盘开关」字段。
        ///
        /// 删掉一个属性不会让编译报错 —— 老用户的 ui.json 里那个键会变成
        /// 「读进来没人管」的孤儿。这条测试保证写出去的文件里不再出现它，
        /// 一次保存就把旧键自然清掉。
        ///
        /// 断言写成「键名里不含 Tray」而不是比对某个具体字面量：
        /// 后者会让这个已被删除的字段名在仓库里留下一处永久残留，
        /// 而验收要求全局搜索它零命中。
        /// </summary>
        [Fact]
        public void UiSettings_SerializesWithoutAnyTraySwitch()
        {
            string json = JsonSerializer.Serialize(
                new UiSettings { AutoStartMode = AutoStartMode.WorkerWithTray },
                AppJsonContext.Default.UiSettings);

            using JsonDocument doc = JsonDocument.Parse(json);

            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                Assert.DoesNotContain("Tray", property.Name, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("WorkerWithTray", json, StringComparison.Ordinal);
        }

        /// <summary>事件不参与序列化，实现 INotifyPropertyChanged 不该改变文件形状。</summary>
        [Fact]
        public void UiSettings_ImplementsINotifyPropertyChanged()
            => Assert.IsAssignableFrom<INotifyPropertyChanged>(new UiSettings());

        // ==================== ScheduleRule.Id ====================

        [Fact]
        public void ScheduleRule_GeneratesUniqueIdByDefault()
        {
            ScheduleRule a = new();
            ScheduleRule b = new();

            Assert.Equal(32, a.Id.Length);
            Assert.NotEqual(a.Id, b.Id);
            Assert.True(Guid.TryParseExact(a.Id, "N", out _));
        }

        /// <summary>
        /// 规则文件换成 schedules.json 之后，旧 bindings.json 不该再被读取。
        ///
        /// 「不迁移」是个**行为约定**，不是一句注释。这里造一个旧文件名 + 空的新文件名，
        /// 断言 Load 读出来的是空表 —— 一旦有人哪天顺手加了兼容逻辑，这条会红。
        /// </summary>
        [Fact]
        public void SchedulingStore_DoesNotReadLegacyRuleFile()
        {
            FakeAppPaths paths = new();

            try
            {
                Directory.CreateDirectory(paths.DataDirectory);
                File.WriteAllText(
                    paths.File("bindings.json"),
                    """
                    { "Rules": [ { "Name": "game.exe", "Target": 1 } ] }
                    """);

                SchedulingStore store = new(new FakeLogService(), paths);
                store.Load();

                Assert.Empty(store.Profile.Rules);
            }
            finally
            {
                TryDelete(paths.DataDirectory);
            }
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响断言结果。
            }
        }
    }
}
