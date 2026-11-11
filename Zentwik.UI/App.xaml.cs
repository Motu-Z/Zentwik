using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zentwik.UI.Services;

namespace Zentwik.UI;

/// <summary>应用入口。</summary>
public partial class App : Application
{
    // WinExe 没有自带控制台。
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局禁用原生虚线焦点框。
        FocusVisualKiller.Initialize();

        // 让 Win32 原生控件（托盘右键菜单、消息框、滚动条）跟随系统深浅色。
        // 不调用的话，深色系统上会从托盘弹出一张纯白菜单。
        NativeMethods.EnableNativeDarkMenus();

        // 外壳状态（诊断用）
        // --worker=offline: 把左下角的 Worker 状态灯切成断开态，用来核对两种外观。
        string? workerArg = e.Args.FirstOrDefault(a => a.StartsWith("--worker="));
        if (workerArg is not null &&
            workerArg["--worker=".Length..].Equals("offline", StringComparison.OrdinalIgnoreCase))
        {
            ViewModels.WorkerStatus.Instance.IsConnected = false;
            ViewModels.WorkerStatus.Instance.IsElevated = false;
            Services.WorkerBridge.ForcedOffline = true;
        }

        // 诊断模式不拉起后台服务。
        if (e.Args.Any(a => a is "--screenshot" or "--selftest" or "--itest"
                               or "--trayprobe" or "--perf" or "--perf-empty"))
        {
            Services.WorkerBridge.AllowAutoStart = false;
        }

        // 侧栏展开态（诊断用）
        // --sidebar=expanded: 以展开态启动，用来核对 220 宽的外观。
        string? sidebarArg = e.Args.FirstOrDefault(a => a.StartsWith("--sidebar=", StringComparison.Ordinal));
        if (sidebarArg is not null &&
            sidebarArg["--sidebar=".Length..].Equals("expanded", StringComparison.OrdinalIgnoreCase))
        {
            ViewModels.ShellState.Instance.IsExpanded = true;
        }

        // 正常启动读回界面设置；诊断模式不写用户配置。
        if (Services.WorkerBridge.AllowAutoStart)
        {
            ViewModels.AppSettings.Instance.Load();

            // 外观偏好（主题模式 / 强调色）也跟着一起落盘；
            // 诊断模式的取色与切主题不该覆盖用户的选择。
            AppThemeService.Instance.PersistenceEnabled = true;
        }

        // 依赖组的关闭态（诊断用）。
        string? offArg = e.Args.FirstOrDefault(a => a.StartsWith("--off=", StringComparison.Ordinal));
        if (offArg is not null)
        {
            foreach (string flag in offArg["--off=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (flag.Trim().ToLowerInvariant())
                {
                    case "startup":
                        ViewModels.AppSettings.Instance.Startup = ViewModels.StartupBehavior.None;
                        break;
                    case "scenes":
                        ViewModels.ScenariosViewModel.StartWithAutoSwitchOff = true;
                        break;
                }
            }
        }

        // 管道连通性探针（诊断用）
        // --workerprobe: 只验一次与 Worker 的管道，把结果打到控制台后退出。不碰硬件写入。
        if (e.Args.Contains("--workerprobe"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            RunWorkerProbe();
            return;
        }

        // 调校页数据链路探针（诊断用）。
        if (e.Args.Contains("--tuningprobe"))
        {
            RunTuningProbe();
            return;
        }

        // 电源页数据链路探针（诊断用）
        // --powerprobe: 查方案列表与当前策略，再做一次「建 → 改名 → 切回 → 删」往返。
        if (e.Args.Contains("--powerprobe"))
        {
            RunPowerProbe();
            return;
        }

        // 清理系统里的重复电源方案（诊断用）
        // --powerclean: 只列出将被删除的方案；--powerclean --apply 才真删。
        if (e.Args.Contains("--powerclean"))
        {
            RunPowerClean(e.Args.Contains("--apply"));
            return;
        }

        // 界面设置往返探针（诊断用）
        // --settingsprobe: 真拉起 Worker，读一次 ui.json、翻转监听开关、再翻回来。
        if (e.Args.Contains("--settingsprobe"))
        {
            RunSettingsProbe();
            return;
        }

        // 进程页数据链路探针（诊断用）
        // --processprobe: 读进程表 / 规则表 / 掩码表，并做一次「建规则 → 查 → 删」往返。
        if (e.Args.Contains("--processprobe"))
        {
            RunProcessProbe();
            return;
        }

        // 掩码数据链路探针（诊断用）
        // --maskprobe: 查拓扑，做一次「建掩码 → 改名改值 → 撞名 → 删」往返。
        if (e.Args.Contains("--maskprobe"))
        {
            RunMaskProbe();
            return;
        }

        // 场景数据链路探针（诊断用）
        // --scenarioprobe: 读总闸与规则，做一次「拨总闸 → 拨回 → 建规则 → 改 → 删」往返。
        if (e.Args.Contains("--scenarioprobe"))
        {
            RunScenarioProbe();
            return;
        }

        // 日志数据链路探针（诊断用）。
        if (e.Args.Contains("--logsprobe"))
        {
            RunLogsProbe();
            return;
        }

        // 进程列表压测（诊断用）
        // --procs=N: 在默认占位进程之外再铺 N 条，用来验证列表虚拟化。
        string? procsArg = e.Args.FirstOrDefault(a => a.StartsWith("--procs="));
        if (procsArg is not null &&
            int.TryParse(procsArg["--procs=".Length..], out int bulkCount) &&
            bulkCount > 0)
        {
            ViewModels.ProcessViewModel.BulkSeedCount = bulkCount;
        }

        // 核心数量（诊断用）
        // --cores=N: 改调校页的核心格子数量，用来验证换行规则。
        string? coresArg = e.Args.FirstOrDefault(a => a.StartsWith("--cores="));
        if (coresArg is not null &&
            int.TryParse(coresArg["--cores=".Length..], out int coreCount) &&
            coreCount > 0)
        {
            ViewModels.TuningViewModel.SeedCoreCount = coreCount;
        }

        // SMU 不可用态（诊断用）
        // --smu=off: 强制调校页进入「整页不可用」，用来验遮罩与禁用。
        if (e.Args.Contains("--smu=off"))
        {
            ViewModels.TuningViewModel.ForceSmuOffline = true;
        }

        // 离屏截图与自检：业务页不发起 IPC。
        if (e.Args.Any(a => a is "--screenshot" or "--selftest" or "--itest" or "--perf"))
        {
            ViewModels.PlanHub.SuppressWorkerInit = true;
            ViewModels.PowerViewModel.SuppressWorkerInit = true;
            ViewModels.ProcessViewModel.SuppressWorkerInit = true;
            ViewModels.ScenariosViewModel.SuppressWorkerInit = true;
            ViewModels.LogsViewModel.SuppressWorkerInit = true;
        }

        // 场景页编辑器的展开态截图没有鼠标点不出来，用开关直接摊开。
        if (e.Args.Contains("--editor"))
        {
            ViewModels.ScenariosViewModel.StartWithEditorOpen = true;
        }

        // 附带「进程退出」触发，用来核对调度块置灰的样子。
        if (e.Args.Contains("--trigger-exit"))
        {
            ViewModels.ScenariosViewModel.StartWithExitTrigger = true;
        }

        // 调校页只在截图时抑制：它的自检断言（切方案后换值）要走到刷新路径上。
        if (e.Args.Contains("--screenshot"))
        {
            ViewModels.TuningViewModel.SuppressWorkerInit = true;
        }

        // 托盘探针（诊断用）。
        if (e.Args.Contains("--trayprobe"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);

            AppThemeService.Instance.Initialize();

            var probePalette = LogoPaletteFactory.BuildForTray(
                darkTaskbar: !SystemTheme.IsSystemLightTheme());

            using var probeTray = new TrayIconService();
            bool probeOk = probeTray.Initialize(
                AppThemeService.Instance.BuildLogoDrawing,
                probePalette.Body,
                probePalette.Shadow);

            Console.WriteLine($"trayprobe: available  = {probeOk}");
            Console.WriteLine($"trayprobe: pixelSize  = {probeTray.RenderedPixelSize}");
            Console.WriteLine($"trayprobe: body       = {probePalette.Body}");
            Console.WriteLine($"trayprobe: shadow     = {probePalette.Shadow}");
            Console.Out.Flush();

            Shutdown(0);
            return;
        }

        // 命令行自检模式。
        if (e.Args.Contains("--selftest") || e.Args.Contains("--itest"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);

            var sb = new StringBuilder();
            int failures = 0;

            if (e.Args.Contains("--selftest"))
            {
                failures += ColorMathSelfTest.Run(sb);
            }

            if (e.Args.Contains("--itest"))
            {
                // 集成测试需要资源字典已就位
                AppThemeService.Instance.Initialize();
                failures += IntegrationTest.Run(sb);
            }

            sb.AppendLine(failures == 0 ? ">>> 全部通过" : $">>> {failures} 项失败");

            Console.Write(sb.ToString());
            Console.Out.Flush();

            string report = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-selftest.txt");
            System.IO.File.WriteAllText(report, sb.ToString());

            Shutdown(failures == 0 ? 0 : 1);
            return;
        }

        // 关闭时退出进程（避免后台线程残留）
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // 空窗口对照。
        if (e.Args.Contains("--perf-empty"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);

            var blank = new Window
            {
                Width = 1180,
                Height = 800,
                WindowStyle = WindowStyle.None,
                Background = System.Windows.Media.Brushes.Black,
                Content = new Border(),
            };

            MainWindow = blank;
            blank.Show();
            RunPerfProbe(blank);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 性能探针（诊断用）。
        if (e.Args.Contains("--perf"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);

            string? perfPage = e.Args.FirstOrDefault(a => a.StartsWith("--page="));
            if (perfPage is not null)
            {
                window.NavigateTo(perfPage["--page=".Length..]);
            }

            RunPerfProbe(window);
            return;
        }

        // 系统托盘。
        bool isScreenshotMode = e.Args.Contains("--screenshot");
        if (!isScreenshotMode)
        {
            SetupTray(window);
        }

        // 离屏截图模式。
        int shotIndex = Array.FindIndex(e.Args, a => a == "--screenshot");
        if (shotIndex >= 0)
        {
            // 紧跟其后的若还是开关（--page=… / --theme=…），说明没给输出路径，
            // 不能把开关本身当文件名写进当前工作目录。
            string next = shotIndex + 1 < e.Args.Length ? e.Args[shotIndex + 1] : string.Empty;
            string outPath = !string.IsNullOrEmpty(next) && !next.StartsWith("--")
                ? next
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-shot.png");

            // 主题可通过 --theme=light|dark 指定
            string? themeArg = e.Args.FirstOrDefault(a => a.StartsWith("--theme="));
            if (themeArg is not null)
            {
                var mode = themeArg["--theme=".Length..].Equals("light", StringComparison.OrdinalIgnoreCase)
                    ? AppTheme.Light : AppTheme.Dark;
                AppThemeService.Instance.SetThemeMode(mode);
            }

            // 可选：--page=Misc|Tuning|Process|Scenarios|Behavior|Logs 指定要渲染的页面
            string? pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--page="));
            if (pageArg is not null)
            {
                window.NavigateTo(pageArg["--page=".Length..]);
            }

            // 可选：--size=1180x1400 指定离屏渲染尺寸。
            string? sizeArg = e.Args.FirstOrDefault(a => a.StartsWith("--size="));
            if (sizeArg is not null)
            {
                string[] parts = sizeArg["--size=".Length..].Split('x', 'X');

                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out double shotWidth) &&
                    double.TryParse(parts[1], out double shotHeight))
                {
                    window.Width = shotWidth;
                    window.Height = shotHeight;
                }
            }

            // 可选：--accent=system|custom 指定色彩模式
            string? accentArg = e.Args.FirstOrDefault(a => a.StartsWith("--accent="));
            if (accentArg is not null)
            {
                AppThemeService.Instance.SetAccentMode(
                    accentArg["--accent=".Length..].Equals("system", StringComparison.OrdinalIgnoreCase)
                        ? AccentSource.System
                        : AccentSource.Custom);
            }

            // 可选：--scroll=bottom 把页面滚到底后再拍（拍折叠线以下的内容）
            bool scrollToBottom = e.Args.Contains("--scroll=bottom");

            window.Dispatcher.InvokeAsync(() =>
            {
                if (scrollToBottom)
                {
                    // 必须先跑一次布局：ScrollViewer 的 Extent（内容总高）
                    // 要等布局算完才有值，否则 ScrollToEnd 是个空操作。
                    window.UpdateLayout();
                    window.ScrollPageToEnd();
                    window.UpdateLayout();
                }

                // --sidebar=expanded 时把过渡动画跳过，直接拍展开完成的样子
                if (e.Args.Any(a => a.StartsWith("--sidebar=", StringComparison.Ordinal)))
                {
                    window.SnapSidebar();
                }

                if (e.Args.Contains("--profile"))
                {
                    window.NavigateTo("Scenarios");
                    window.UpdateLayout();
                }

                // --dirty : 把页面推进「有未保存改动」状态后再拍。
                // 调校页底部操作条只在 IsDirty 时出现，不触发就永远拍不到。
                if (e.Args.Contains("--dirty")
                    && window.ContentHost.Content is System.Windows.FrameworkElement dirtyPage)
                {
                    switch (dirtyPage.DataContext)
                    {
                        case ViewModels.TuningViewModel tuningVm:
                            // 页面还没 Loaded 时脏标记是冻结的，改一格不会留下痕迹 ——
                            // 先解冻，否则这个参数拍不到「未保存」的样子。
                            tuningVm.FreezeDirty = false;
                            tuningVm.CoreMargins[0].IncrementCommand.Execute(null);
                            break;

                        case ViewModels.PowerViewModel powerVm:
                            powerVm.Epp = Math.Min(100, powerVm.Epp + 5);
                            break;
                    }

                    window.UpdateLayout();
                }

                // --mask : 打开右栏「自定义掩码」页签并进编辑器后再拍。
                if (e.Args.Contains("--masklist"))
                {
                    window.OpenMaskManager();
                    window.UpdateLayout();
                }
                else if (e.Args.Contains("--mask"))
                {
                    window.OpenMaskManager();
                    window.OpenMaskEditor();
                    window.UpdateLayout();
                }

                // --toast : 触发一次全局 Toast 后再拍（诊断用）。
                if (e.Args.Contains("--toast"))
                {
                    ViewModels.ToastService.Instance.Show(
                        "规则已保存，并对 2 个正在运行的进程立即生效（steam.exe → CCD1）。");
                    window.UpdateLayout();
                }

                // --confirm : 打开危险操作确认叠层后再拍（诊断用）。
                if (e.Args.Contains("--confirm"))
                {
                    new ViewModels.AdvancedViewModel().ClearAllCommand.Execute(null);
                    window.UpdateLayout();
                }

                // --tooltip : 额外叠加一个 ToolTip 预览（Popup 拍不到，只能这样验）
                if (e.Args.Contains("--popover"))
                {
                    ScreenshotHelper.CaptureWithPopover(window, outPath);
                }
                else if (e.Args.Contains("--tooltip"))
                {
                    ScreenshotHelper.CaptureWithTooltip(window, outPath);
                }
                else
                {
                    ScreenshotHelper.Capture(window, outPath);
                }

                // --traymenu : 额外输出托盘菜单与托盘图标的离屏渲染
                if (e.Args.Contains("--traymenu"))
                {
                    ScreenshotHelper.CaptureTrayIcon(outPath);
                }

                Shutdown(0);
            });
        }
    }

    /// <summary>跑一次调校页初始化，结果写进 %TEMP%\zentwik-tuningprobe.txt。</summary>
    private static void RunTuningProbe()
    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                ViewModels.TuningViewModel vm = new();
                await vm.InitializeAsync().ConfigureAwait(false);

                sb.AppendLine($"smuAvailable = {vm.SmuAvailable}");
                sb.AppendLine($"workerConnected = {vm.WorkerConnected}");
                sb.AppendLine($"blocking = {vm.BlockingTitle} / {vm.BlockingDetail}");
                sb.AppendLine($"cores = {vm.CpuCoreCount}");
                sb.AppendLine($"fmax = {vm.FMaxMHz} (range {vm.FMaxMinimum}-{vm.FMaxMaximum})");
                sb.AppendLine($"dirty = {vm.IsDirty}");
                sb.AppendLine($"tuningPlan = {ViewModels.PlanHub.Tuning.CurrentName} (id={ViewModels.PlanHub.Tuning.Current?.Id ?? "-"}, synced={ViewModels.PlanHub.Tuning.IsSynced})");
                sb.AppendLine($"tuningPlanHasValues = {ViewModels.PlanHub.Tuning.Current?.CoreMargins.Length > 0}");
                sb.AppendLine($"unavailable = {vm.CoreMargins.Count(c => c.IsUnavailable)} 核");
                sb.AppendLine($"margins = {string.Join(",", vm.CoreMargins.Select(c => c.PayloadValue()))}");

                AppendProfileRoundtrip(sb).GetAwaiter().GetResult();
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("probe failed: " + ex);
        }

        string report = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-tuningprobe.txt");
        System.IO.File.WriteAllText(report, sb.ToString());

        Environment.Exit(0);
    }

    /// <summary>查一次电源方案列表与当前策略，写进 只读：两条命令都是查询，不写系统设置</summary>
    private static void RunSettingsProbe()
    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                await Services.WorkerLauncher.EnsureRunningAsync().ConfigureAwait(false);

                Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

                var before = await bridge.QueryUiSettingsAsync().ConfigureAwait(false);
                sb.AppendLine($"query.success = {before.Success} ({before.Message})");

                if (before.Value is not { } original)
                {
                    sb.AppendLine("query.value = <null>");
                    return;
                }

                sb.AppendLine($"before.autoStart = {original.AutoStartMode}");
                sb.AppendLine($"before.close = {original.CloseBehavior}");
                sb.AppendLine($"before.applyTuning = {original.ApplyTuningOnStartup}");
                sb.AppendLine($"before.watch = {original.WatchProcessBinding}");
                sb.AppendLine($"before.notify = {original.NotifyOnError}");
                sb.AppendLine($"before.toast = {original.ToastSeconds}");

                var flipped = await bridge.SaveUiSettingsAsync(new Zentwik.Core.Ipc.UiSettingsPayload
                {
                    WatchProcessBinding = !original.WatchProcessBinding,
                    ToastSeconds = 7,
                }).ConfigureAwait(false);

                sb.AppendLine($"save.success = {flipped.Success} ({flipped.Message})");
                sb.AppendLine($"save.watch = {flipped.Value?.WatchProcessBinding}");
                sb.AppendLine($"save.toast = {flipped.Value?.ToastSeconds}");

                var restored = await bridge.SaveUiSettingsAsync(new Zentwik.Core.Ipc.UiSettingsPayload
                {
                    WatchProcessBinding = original.WatchProcessBinding,
                    ToastSeconds = original.ToastSeconds,
                }).ConfigureAwait(false);

                sb.AppendLine($"restore.success = {restored.Success}");
                sb.AppendLine($"restore.watch = {restored.Value?.WatchProcessBinding}");
                sb.AppendLine($"restore.toast = {restored.Value?.ToastSeconds}");
                sb.AppendLine(
                    $"roundtrip = {flipped.Value?.WatchProcessBinding == !original.WatchProcessBinding
                                  && restored.Value?.WatchProcessBinding == original.WatchProcessBinding}");

                // 同步发：探针进程紧接着就 Environment.Exit，异步那条任务根本没机会跑。
                Services.WorkerLauncher.RequestShutdown(
                    TimeSpan.FromSeconds(3), evenIfNotOwned: true);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"error = {ex.GetType().Name}: {ex.Message}");
        }

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "zentwik-settingsprobe.txt");
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    /// <summary>删掉系统里重复的电源方案，只保留四个内置模板和用户自建的两份</summary>
    private static void RunPowerClean(bool apply)
    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

                IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> list = await bridge
                    .QueryPowerSchemesAsync()
                    .ConfigureAwait(false);

                if (!list.Success || list.Value is null)
                {
                    sb.AppendLine($"query failed: {list.Message}");
                    return;
                }

                // 保留四个内置模板，其余（用户自建的）一并清掉。
                var keep = new HashSet<Guid>
                {
                    Zentwik.Core.Services.PowerSchemePresets.Balanced.Guid,
                    Zentwik.Core.Services.PowerSchemePresets.PowerSaver.Guid,
                    Zentwik.Core.Services.PowerSchemePresets.HighPerformance.Guid,
                    Zentwik.Core.Services.PowerSchemePresets.UltimatePerformance.Guid,
                    new("bb93f328-46ba-451d-ad54-034639f8f206"),
                    new("ecc8c8d6-9437-4148-9a1c-ecc1e9792bcc"),
                };

                sb.AppendLine($"total={list.Value.Schemes.Count} apply={apply}");

                foreach (Zentwik.Core.Ipc.PowerSchemeEntry scheme in list.Value.Schemes)
                {
                    string line = $"{scheme.Name} ({scheme.Guid:D})";

                    if (keep.Contains(scheme.Guid))
                    {
                        sb.AppendLine($"keep    {line}");
                        continue;
                    }

                    if (!apply)
                    {
                        sb.AppendLine($"would   {line}");
                        continue;
                    }

                    IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> deleted = await bridge
                        .DeletePowerSchemeAsync(scheme.Guid)
                        .ConfigureAwait(false);

                    sb.AppendLine($"delete  {line} success={deleted.Success} msg={deleted.Message}");
                }

                IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> after = await bridge
                    .QueryPowerSchemesAsync()
                    .ConfigureAwait(false);

                sb.AppendLine($"remaining={after.Value?.Schemes.Count}");

                foreach (Zentwik.Core.Ipc.PowerSchemeEntry scheme in after.Value?.Schemes ?? [])
                    sb.AppendLine($"  left: {scheme.Name} ({scheme.Guid:D})");
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("powerclean failed: " + ex);
        }

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "zentwik-powerclean.txt");

        System.IO.File.WriteAllText(path, sb.ToString());

        // 删除是靠提权 Worker 做的，这里不关它会一直占着管道和 Core.dll。
        Services.WorkerLauncher.RequestShutdown(
            TimeSpan.FromSeconds(3), evenIfNotOwned: true);

        System.Threading.Thread.Sleep(500);

        Environment.Exit(0);
    }

    private static void RunPowerProbe()    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                ViewModels.PowerViewModel vm = new();
                await vm.InitializeAsync().ConfigureAwait(false);

                sb.AppendLine($"workerAvailable = {vm.IsWorkerAvailable}");
                sb.AppendLine($"unavailableText = {vm.UnavailableText}");
                sb.AppendLine($"hasBattery = {vm.HasBattery} / onBattery = {vm.IsOnBattery} / supply = {vm.SupplyLabel}");
                sb.AppendLine($"activeScheme = {vm.ActiveSchemeName} ({vm.ActiveSchemeGuid:D})");
                sb.AppendLine($"schemes = {vm.Schemes.Count}");

                foreach (ViewModels.PowerScheme scheme in vm.Schemes)
                {
                    // 列表末尾那条「＋ 新建方案」不是真实方案，GUID 是空的。
                    if (scheme.IsCreateNew)
                        continue;

                    sb.AppendLine(
                        $"  scheme: {scheme.Name} ({scheme.Guid:D}) selected={ReferenceEquals(scheme, vm.SelectedScheme)}");
                }

                AppendPowerSet(sb, vm, "ac");

                // DC 那一套只能切过去读：界面上同时只有一套可编辑。
                vm.IsAcMode = false;
                AppendPowerSet(sb, vm, "dc");
                vm.IsAcMode = true;

                sb.AppendLine($"dirty = {vm.IsDirty} / canApply = {vm.CanApply}");

                await AppendSchemeRoundtrip(sb).ConfigureAwait(false);
                await AppendUltimateSchemeCheck(sb).ConfigureAwait(false);
                await AppendPlanCreateRoundtrip(sb).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("probe failed: " + ex);
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-powerprobe.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Environment.Exit(0);
    }

    private static void AppendPowerSet(StringBuilder sb, ViewModels.PowerViewModel vm, string tag)
    {
        sb.AppendLine(
            $"{tag}: epp={Show(vm.Epp, vm.EppSupported)} " +
            $"boost={Show(vm.BoostMode, vm.BoostModeSupported)} " +
            $"max={Show(vm.MaxProcessorState, vm.MaxProcessorStateSupported)} " +
            $"aspm={Show(vm.Aspm, vm.AspmSupported)} " +
            $"idle={(vm.IdleDisableSupported ? vm.IdleDisable.ToString() : "N/A")}");
    }

    private static string Show(int value, bool supported) => supported ? value.ToString() : "N/A";

    /// <summary>读一次进程页的三份数据，并做一次「建规则 → 查 → 删」往返</summary>
    private static void RunProcessProbe()
    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

                IpcOutcome<List<Zentwik.Core.Ipc.ProcessEntryPayload>> processes =
                    await bridge.QueryProcessesAsync().ConfigureAwait(false);

                sb.AppendLine($"processes: success={processes.Success} msg={processes.Message} count={processes.Value?.Count ?? -1}");

                foreach (Zentwik.Core.Ipc.ProcessEntryPayload entry in (processes.Value ?? new()).Take(5))
                {
                    sb.AppendLine(
                        $"  {entry.Pid,6} {entry.Name,-24} cpu={entry.Cpu:0.0} prio={entry.Priority} rule={entry.MatchedRuleName ?? "-"}");
                }

                IpcOutcome<List<Zentwik.Core.Ipc.ScheduleRulePayload>> rules =
                    await bridge.QuerySchedulingRulesAsync().ConfigureAwait(false);

                sb.AppendLine($"rules: success={rules.Success} msg={rules.Message} count={rules.Value?.Count ?? -1}");

                foreach (Zentwik.Core.Ipc.ScheduleRulePayload rule in rules.Value ?? new())
                {
                    sb.AppendLine(
                        $"  {rule.Id} {rule.ProcessName} → {rule.TargetType}:{rule.TargetValue} " +
                        $"prio={rule.Priority} enabled={rule.Enabled}");
                }

                IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> masks =
                    await bridge.QueryMasksAsync().ConfigureAwait(false);

                sb.AppendLine($"masks: success={masks.Success} msg={masks.Message} count={masks.Value?.Masks.Count ?? -1} limit={masks.Value?.Limit ?? -1}");

                foreach (Zentwik.Core.Ipc.MaskEntryPayload mask in masks.Value?.Masks ?? new())
                {
                    sb.AppendLine($"  {mask.Id} {mask.Name} mask={mask.Mask} cores={mask.CoreCount}");
                }

                // 走一遍页面自己的映射（payload → 行 / 规则 / 下拉），
                // 只验 Core 的返回说明不了界面显示得对不对。
                ViewModels.ProcessViewModel vm = new();
                await vm.InitializeAsync().ConfigureAwait(false);

                sb.AppendLine(
                    $"vm: procs={vm.RunningProcesses.Count} visible={vm.FilteredProcesses.Count} " +
                    $"system={vm.RunningProcesses.Count(p => p.IsSystem)} " +
                    $"rules={vm.SavedRules.Count} masks={vm.MaskOptions.Count}");

                ViewModels.RunningProcessItem? sample = vm.FilteredProcesses.FirstOrDefault();

                if (sample is not null)
                {
                    sb.AppendLine(
                        $"vm sample: {sample.Name} cpu={sample.CpuText} prio={sample.Priority} mask={sample.MaskLabel ?? "-"}");
                }

                await AppendRuleRoundtrip(sb).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("probe failed: " + ex);
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-processprobe.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Environment.Exit(0);
    }

    /// <summary>掩码链路探针：拓扑查询 + 「建 → 改名改值 → 撞名 → 空名 → 删」往返</summary>
    private static void RunMaskProbe()
    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

                IpcOutcome<Zentwik.Core.Ipc.CpuTopologyPayload> topology =
                    await bridge.QueryCpuTopologyAsync().ConfigureAwait(false);

                sb.AppendLine(
                    $"topology: success={topology.Success} msg={topology.Message} " +
                    $"cores={topology.Value?.Cores} perCcd={topology.Value?.CoresPerCcd} " +
                    $"threads={topology.Value?.ThreadsPerCore}");

                IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> before =
                    await bridge.QueryMasksAsync().ConfigureAwait(false);

                sb.AppendLine($"masks before: success={before.Success} count={before.Value?.Masks.Count ?? -1}");

                await AppendMaskRoundtrip(sb).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("probe failed: " + ex);
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-maskprobe.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Environment.Exit(0);
    }

    private static async Task AppendMaskRoundtrip(StringBuilder sb)
    {
        Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

        // 名字必须过 MaskNamePolicy：权重上限 12，纯英文最多 12 个字符。
        string name = "m" + Guid.NewGuid().ToString("N")[..6];
        string? id = null;

        try
        {
            IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> created =
                await bridge.SaveMaskAsync(null, name, 0x00FF00FF).ConfigureAwait(false);

            id = created.Value?.Masks.FirstOrDefault(m => m.Name == name)?.Id;

            sb.AppendLine(
                $"create: success={created.Success} msg={created.Message} " +
                $"count={created.Value?.Masks.Count ?? -1} id={id}");

            IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> updated =
                await bridge.SaveMaskAsync(id, name + "x", 0x00FF).ConfigureAwait(false);

            Zentwik.Core.Ipc.MaskEntryPayload? after = updated.Value?.Masks.FirstOrDefault(m => m.Id == id);

            sb.AppendLine(
                $"update: success={updated.Success} msg={updated.Message} " +
                $"name={after?.Name} mask={after?.Mask} cores={after?.CoreCount}");

            IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> duplicate =
                await bridge.SaveMaskAsync(null, name + "x", 1).ConfigureAwait(false);

            sb.AppendLine($"duplicate: success={duplicate.Success} msg={duplicate.Message}");

            IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> blank =
                await bridge.SaveMaskAsync(null, "   ", 1).ConfigureAwait(false);

            sb.AppendLine($"blank name: success={blank.Success} msg={blank.Message}");

            IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> empty =
                await bridge.SaveMaskAsync(null, name + "y", 0).ConfigureAwait(false);

            sb.AppendLine($"empty mask: success={empty.Success} msg={empty.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(id))
            {
                IpcOutcome<Zentwik.Core.Ipc.MaskListPayload> removed =
                    await bridge.DeleteMaskAsync(id).ConfigureAwait(false);

                sb.AppendLine(
                    $"delete: success={removed.Success} msg={removed.Message} " +
                    $"stillListed={removed.Value?.Masks.Any(m => m.Id == id) ?? false}");
            }
        }
    }

    /// <summary>场景链路探针：总闸往返 + 「建规则 → 查 → 改 → 删」</summary>
    private static void RunLogsProbe()
    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

                IpcOutcome<string> snapshot = await bridge.SnapshotLogsAsync().ConfigureAwait(false);

                sb.AppendLine(
                    $"snapshot: success={snapshot.Success} msg={snapshot.Message} " +
                    $"chars={snapshot.Value?.Length ?? -1}");

                List<ViewModels.LogEntryItem> parsed = ViewModels.LogsViewModel.Parse(snapshot.Value);

                sb.AppendLine($"parsed: {parsed.Count} 条");

                foreach (ViewModels.LogEntryItem item in parsed.Take(8))
                    sb.AppendLine($"  [{item.Time}] [{item.Level}] {item.Message}");

                // 只读：不清空。清空会删掉用户目录里真实的日志文件。
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("probe failed: " + ex);
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-logsprobe.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Environment.Exit(0);
    }

    private static void RunScenarioProbe()
    {
        var sb = new StringBuilder();

        try
        {
            Task.Run(async () =>
            {
                Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

                IpcOutcome<Zentwik.Core.Ipc.ScenarioStatePayload> state =
                    await bridge.QueryScenarioStateAsync().ConfigureAwait(false);

                bool original = state.Value?.Enabled ?? false;

                sb.AppendLine($"state: success={state.Success} msg={state.Message} enabled={original}");

                IpcOutcome<Zentwik.Core.Ipc.ScenarioStatePayload> flipped =
                    await bridge.SetScenarioEnabledAsync(!original).ConfigureAwait(false);

                sb.AppendLine($"toggle: success={flipped.Success} msg={flipped.Message} enabled={flipped.Value?.Enabled}");

                IpcOutcome<Zentwik.Core.Ipc.ScenarioStatePayload> restored =
                    await bridge.SetScenarioEnabledAsync(original).ConfigureAwait(false);

                sb.AppendLine($"restore: success={restored.Success} enabled={restored.Value?.Enabled}");

                IpcOutcome<List<Zentwik.Core.Ipc.ScenarioRulePayload>> rules =
                    await bridge.QueryScenarioRulesAsync().ConfigureAwait(false);

                sb.AppendLine($"rules: success={rules.Success} count={rules.Value?.Count ?? -1}");

                foreach (Zentwik.Core.Ipc.ScenarioRulePayload rule in rules.Value ?? new())
                {
                    sb.AppendLine(
                        $"  {rule.Id} {rule.Trigger} arg={rule.Argument} → {rule.ProfileId} " +
                        $"prio={rule.Priority} enabled={rule.Enabled}");
                }

                // 本机没有调校方案时临时造一份，好让「候选同步 + 规则往返」都真跑到；
                // 自建自删，不给用户留下数据。
                IpcOutcome<Zentwik.Core.Ipc.PlanListPayload> tuningPlans =
                    await bridge.QueryPlansAsync(Zentwik.Core.Services.PlanKind.Tuning).ConfigureAwait(false);

                string? profileId = tuningPlans.Value?.Plans.FirstOrDefault()?.Id;
                string? temporaryPlanId = null;

                if (profileId is null)
                {
                    IpcOutcome<string> created = await bridge.SavePlanAsync(
                        Zentwik.Core.Services.PlanKind.Tuning, null,
                        "__probe_plan_" + Guid.NewGuid().ToString("N")[..6]).ConfigureAwait(false);

                    sb.AppendLine($"temporary plan: success={created.Success} msg={created.Message}");

                    if (created.Success && created.Value is not null)
                    {
                        temporaryPlanId = created.Value;
                        profileId = created.Value;
                    }
                }

                // 场景页三个动作候选（真实 ViewModel，不走诊断种子）：三大页的方案
                // 必须出现在这里，否则下拉只剩「不修改」。
                var sceneVm = new ViewModels.ScenariosViewModel();
                await sceneVm.RefreshActionOptionsAsync().ConfigureAwait(false);

                sb.AppendLine($"actionOptions: profiles={sceneVm.EditorProfiles.Count} " +
                              $"masks={sceneVm.EditorMasks.Count} schemes={sceneVm.EditorPowerSchemes.Count}");

                foreach (ViewModels.ScenarioActionOption option in sceneVm.EditorProfiles)
                    sb.AppendLine($"  profile: {option.Id} / {option.Name}");

                if (profileId is not null)
                    await AppendScenarioRoundtrip(sb, profileId).ConfigureAwait(false);
                else
                    sb.AppendLine("roundtrip skipped: 临时方案也没建成");

                if (temporaryPlanId is not null)
                {
                    IpcOutcome<string> removed = await bridge.DeletePlanAsync(
                        Zentwik.Core.Services.PlanKind.Tuning, temporaryPlanId).ConfigureAwait(false);

                    sb.AppendLine($"temporary plan cleaned: success={removed.Success} msg={removed.Message}");
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("probe failed: " + ex);
        }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zentwik-scenarioprobe.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Environment.Exit(0);
    }

    private static async Task AppendScenarioRoundtrip(StringBuilder sb, string profileId)
    {
        Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

        string argument = "__probe_" + Guid.NewGuid().ToString("N")[..6] + ".exe";
        string? id = null;

        string exitArgument = "__probe_exit_" + Guid.NewGuid().ToString("N")[..6] + ".exe";
        string? exitId = null;

        try
        {
            IpcOutcome<string> created = await bridge.SaveScenarioRuleAsync(new Zentwik.Core.Ipc.ScenarioRulePayload
            {
                Id = string.Empty,
                Trigger = Zentwik.Core.Services.ScenarioTrigger.ProcessRunning,
                Argument = argument,
                ProfileId = profileId,
                Priority = 900,
                Enabled = true
            }).ConfigureAwait(false);

            sb.AppendLine($"create rule: success={created.Success} msg={created.Message}");

            IpcOutcome<List<Zentwik.Core.Ipc.ScenarioRulePayload>> listed =
                await bridge.QueryScenarioRulesAsync().ConfigureAwait(false);

            Zentwik.Core.Ipc.ScenarioRulePayload? found =
                listed.Value?.FirstOrDefault(r => r.Argument == argument);

            id = found?.Id;

            sb.AppendLine($"  found: id={id} trigger={found?.Trigger} prio={found?.Priority} enabled={found?.Enabled}");

            IpcOutcome<string> updated = await bridge.SaveScenarioRuleAsync(new Zentwik.Core.Ipc.ScenarioRulePayload
            {
                Id = id ?? string.Empty,
                Trigger = Zentwik.Core.Services.ScenarioTrigger.OnBattery,
                Argument = string.Empty,
                ProfileId = profileId,
                Priority = 910,
                Enabled = false
            }).ConfigureAwait(false);

            sb.AppendLine($"update rule: success={updated.Success} msg={updated.Message}");

            IpcOutcome<string> badProfile = await bridge.SaveScenarioRuleAsync(new Zentwik.Core.Ipc.ScenarioRulePayload
            {
                Id = string.Empty,
                Trigger = Zentwik.Core.Services.ScenarioTrigger.OnAcPower,
                Argument = string.Empty,
                ProfileId = "not-a-real-profile",
                Priority = 920,
                Enabled = true
            }).ConfigureAwait(false);

            sb.AppendLine($"bad profile: success={badProfile.Success} msg={badProfile.Message}");

            // 进程退出触发（协议 8 新增）
            IpcOutcome<string> exitSaved = await bridge.SaveScenarioRuleAsync(new Zentwik.Core.Ipc.ScenarioRulePayload
            {
                Id = string.Empty,
                Trigger = Zentwik.Core.Services.ScenarioTrigger.ProcessExited,
                Argument = exitArgument,
                ProfileId = profileId,
                Priority = 930,
                Enabled = true
            }).ConfigureAwait(false);

            sb.AppendLine($"exit rule: success={exitSaved.Success} msg={exitSaved.Message}");

            if (exitSaved.Success)
            {
                IpcOutcome<List<Zentwik.Core.Ipc.ScenarioRulePayload>> afterExit =
                    await bridge.QueryScenarioRulesAsync().ConfigureAwait(false);

                Zentwik.Core.Ipc.ScenarioRulePayload? savedExit =
                    afterExit.Value?.FirstOrDefault(r => r.Argument == exitArgument);

                exitId = savedExit?.Id;

                sb.AppendLine($"  exit found: id={exitId} trigger={savedExit?.Trigger}");
            }

            // 退出触发缺进程名 = 永远不命中，Worker 必须拒。
            IpcOutcome<string> exitWithoutArg = await bridge.SaveScenarioRuleAsync(new Zentwik.Core.Ipc.ScenarioRulePayload
            {
                Id = string.Empty,
                Trigger = Zentwik.Core.Services.ScenarioTrigger.ProcessExited,
                Argument = string.Empty,
                ProfileId = profileId,
                Priority = 940,
                Enabled = true
            }).ConfigureAwait(false);

            sb.AppendLine($"exit without arg: success={exitWithoutArg.Success} msg={exitWithoutArg.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(exitId))
            {
                IpcOutcome<string> removedExit = await bridge.DeleteScenarioRuleAsync(exitId).ConfigureAwait(false);
                sb.AppendLine($"delete exit rule: success={removedExit.Success} msg={removedExit.Message}");
            }

            if (!string.IsNullOrEmpty(id))
            {
                IpcOutcome<string> removed = await bridge.DeleteScenarioRuleAsync(id).ConfigureAwait(false);
                sb.AppendLine($"delete rule: success={removed.Success} msg={removed.Message}");
            }
        }
    }

    /// <summary>规则写进去、读回来、再删掉。</summary>
    private static async Task AppendRuleRoundtrip(StringBuilder sb)
    {
        Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

        string name = "__probe__" + Guid.NewGuid().ToString("N")[..6] + ".exe";

        try
        {
            IpcOutcome<string> saved = await bridge.SaveScheduleRuleAsync(
                new Zentwik.Core.Ipc.SaveScheduleRulePayload
                {
                    ProcessName = name,
                    TargetType = "ccd",
                    TargetValue = "CCD0",
                    Priority = Zentwik.Core.Services.ProcessPriority.High
                }).ConfigureAwait(false);

            sb.AppendLine($"saveRule: success={saved.Success} msg={saved.Message}");

            IpcOutcome<List<Zentwik.Core.Ipc.ScheduleRulePayload>> afterSave =
                await bridge.QuerySchedulingRulesAsync().ConfigureAwait(false);

            Zentwik.Core.Ipc.ScheduleRulePayload? created = afterSave.Value?
                .FirstOrDefault(r => string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase));

            sb.AppendLine(
                $"afterSave: found={created is not null} " +
                $"target={created?.TargetType}:{created?.TargetValue} prio={created?.Priority} id={created?.Id}");

            if (created is null)
            {
                return;
            }

            IpcOutcome<string> removed = await bridge
                .DeleteScheduleRuleAsync(created.Id).ConfigureAwait(false);

            sb.AppendLine($"deleteRule: success={removed.Success} msg={removed.Message}");

            IpcOutcome<List<Zentwik.Core.Ipc.ScheduleRulePayload>> afterDelete =
                await bridge.QuerySchedulingRulesAsync().ConfigureAwait(false);

            bool stillThere = afterDelete.Value?
                .Any(r => string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase)) ?? false;

            sb.AppendLine($"afterDelete: stillThere={stillThere}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("roundtrip failed: " + ex.Message);
        }
        finally
        {
            // 中途异常时把可能建出来的那条删掉，不在用户盘上留垃圾。
            IpcOutcome<List<Zentwik.Core.Ipc.ScheduleRulePayload>> left =
                await bridge.QuerySchedulingRulesAsync().ConfigureAwait(false);

            Zentwik.Core.Ipc.ScheduleRulePayload? stray = left.Value?
                .FirstOrDefault(r => string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase));

            if (stray is not null)
            {
                await bridge.DeleteScheduleRuleAsync(stray.Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>电源方案的增删改往返：建一份临时方案 → 改名 → 撞名 → 删内置 → 删临时方案</summary>
    private static async Task AppendSchemeRoundtrip(StringBuilder sb)
    {
        Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

        string name = "__probe 方案__" + Guid.NewGuid().ToString("N")[..4];
        Guid created = Guid.Empty;

        try
        {
            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> create = await bridge
                .CreatePowerSchemeAsync(Zentwik.Core.Services.PowerSchemePresets.HighPerformance.Guid, name)
                .ConfigureAwait(false);

            sb.AppendLine($"createScheme: success={create.Success} msg={create.Message}");

            if (create.Value is not null)
            {
                Zentwik.Core.Ipc.PowerSchemeEntry? entry =
                    create.Value.Schemes.FirstOrDefault(s => s.Name == name);

                created = entry?.Guid ?? Guid.Empty;

                sb.AppendLine(
                    $"createScheme: guid={created:D} active={create.Value.ActiveGuid:D} count={create.Value.Schemes.Count}");
            }

            if (created == Guid.Empty)
                return;

            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> rename = await bridge
                .RenamePowerSchemeAsync(created, name + "-r")
                .ConfigureAwait(false);

            sb.AppendLine($"renameScheme: success={rename.Success} msg={rename.Message}");

            // 设置项必须真的搬过来：从「高性能」建出来的方案，EPP 应为 0（平衡是 25）。
            // 此时活动方案就是刚建的那份，快照读的正是它。
            IpcOutcome<Zentwik.Core.Services.PowerPolicySnapshot> snapshot = await bridge
                .QueryPowerPolicyAsync()
                .ConfigureAwait(false);

            sb.AppendLine(
                $"copiedSettings: epp={Show(snapshot.Value?.Epp ?? -1, snapshot.Value?.EppSupported ?? false)} " +
                $"max={Show(snapshot.Value?.MaxProcessorState ?? -1, snapshot.Value?.MaxProcessorStateSupported ?? false)} " +
                $"boost={Show(snapshot.Value?.BoostMode ?? -1, snapshot.Value?.BoostModeSupported ?? false)}");

            // 关键回归：切回默认方案后，新建的那份必须还在列表里。
            await bridge.SetActivePowerSchemeAsync(
                Zentwik.Core.Services.PowerSchemePresets.Balanced.Guid).ConfigureAwait(false);

            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> afterBack = await bridge
                .QueryPowerSchemesAsync()
                .ConfigureAwait(false);

            bool stillListed = afterBack.Value is not null
                && afterBack.Value.Schemes.Any(s => s.Guid == created);

            sb.AppendLine(
                $"stillListedAfterSwitchBack = {stillListed} " +
                $"active={afterBack.Value?.ActiveGuid:D} count={afterBack.Value?.Schemes.Count}");

            // 重名必须被拦在复制之前，否则系统里会短暂出现两份同名方案。
            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> duplicate = await bridge
                .CreatePowerSchemeAsync(Zentwik.Core.Services.PowerSchemePresets.Balanced.Guid, "平衡")
                .ConfigureAwait(false);

            sb.AppendLine($"createDuplicate: success={duplicate.Success} msg={duplicate.Message}");

            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> builtIn = await bridge
                .DeletePowerSchemeAsync(Zentwik.Core.Services.PowerSchemePresets.Balanced.Guid)
                .ConfigureAwait(false);

            sb.AppendLine($"deleteBuiltIn: success={builtIn.Success} msg={builtIn.Message}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("schemeRoundtrip failed: " + ex.Message);
        }
        finally
        {
            if (created != Guid.Empty)
            {
                IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> cleanup = await bridge
                    .DeletePowerSchemeAsync(created)
                    .ConfigureAwait(false);

                sb.AppendLine($"cleanup: success={cleanup.Success} msg={cleanup.Message}");
            }
        }
    }

    /// <summary>走一遍「方案面板新建电源方案」：只登记方案，系统电源计划列表必须原样不变</summary>
    private static async Task AppendPlanCreateRoundtrip(StringBuilder sb)
    {
        Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

        string name = "__probe 新建__" + Guid.NewGuid().ToString("N")[..4];
        string? planId = null;

        try
        {
            if (ViewModels.PlanHub.Power.CreateHandler is null)
            {
                sb.AppendLine("planCreate: handler=(null)");
                return;
            }

            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> before = await bridge
                .QueryPowerSchemesAsync()
                .ConfigureAwait(false);

            int countBefore = before.Value?.Schemes.Count ?? -1;

            planId = await ViewModels.PlanHub.Power.CreateHandler(name).ConfigureAwait(false);

            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> after = await bridge
                .QueryPowerSchemesAsync()
                .ConfigureAwait(false);

            int countAfter = after.Value?.Schemes.Count ?? -1;

            // 登记是否真的落到 plans.json，要问后台；页面缓存要等下一次刷新才更新。
            IpcOutcome<Zentwik.Core.Ipc.PlanListPayload> plans = await bridge
                .QueryPlansAsync(Zentwik.Core.Services.PlanKind.Power)
                .ConfigureAwait(false);

            List<Zentwik.Core.Ipc.PlanEntryPayload> entries = plans.Value?.Plans ?? new();

            bool listed = planId is not null && entries.Any(p => p.Id == planId);
            bool hasPolicy = entries.Any(
                p => p.Id == planId && p.PowerPolicy is not null && !p.PowerPolicy.IsEmpty);

            sb.AppendLine(
                $"planCreate: id={planId ?? "(null)"} listed={listed} policy={hasPolicy} " +
                $"schemes={countBefore}->{countAfter}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("planCreate failed: " + ex.Message);
        }
        finally
        {
            if (planId is not null)
            {
                await bridge.DeletePlanAsync(Zentwik.Core.Services.PlanKind.Power, planId)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>从「卓越性能」模板建一份方案，验两件事：说明文字搬过来了、设置不是平衡的复制品</summary>
    private static async Task AppendUltimateSchemeCheck(StringBuilder sb)
    {
        Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

        string name = "__probe 卓越__" + Guid.NewGuid().ToString("N")[..4];
        Guid created = Guid.Empty;

        try
        {
            IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> create = await bridge
                .CreatePowerSchemeAsync(
                    Zentwik.Core.Services.PowerSchemePresets.UltimatePerformance.Guid, name)
                .ConfigureAwait(false);

            if (!create.Success || create.Value is null)
            {
                sb.AppendLine($"ultimate: create failed {create.Message}");
                return;
            }

            created = create.Value.Schemes.FirstOrDefault(s => s.Name == name)?.Guid ?? Guid.Empty;

            // 建完就切过去了，快照读到的正是这一份。
            IpcOutcome<Zentwik.Core.Services.PowerPolicySnapshot> snapshot = await bridge
                .QueryPowerPolicyAsync()
                .ConfigureAwait(false);

            sb.AppendLine(
                $"ultimate: guid={created:D} " +
                $"max={Show(snapshot.Value?.MaxProcessorState ?? -1, snapshot.Value?.MaxProcessorStateSupported ?? false)} " +
                $"epp={Show(snapshot.Value?.Epp ?? -1, snapshot.Value?.EppSupported ?? false)} " +
                $"description={ReadSchemeDescription(created) ?? "<null>"}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("ultimate failed: " + ex.Message);
        }
        finally
        {
            if (created != Guid.Empty)
            {
                IpcOutcome<Zentwik.Core.Ipc.PowerSchemeListPayload> cleanup = await bridge
                    .DeletePowerSchemeAsync(created)
                    .ConfigureAwait(false);

                sb.AppendLine($"ultimate cleanup: success={cleanup.Success} msg={cleanup.Message}");
            }
        }
    }

    /// <summary>读方案说明文字（HKLM 可读，不需要提权）。</summary>
    private static string? ReadSchemeDescription(Guid guid) =>
        Microsoft.Win32.Registry.GetValue(
            $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{guid:D}",
            "Description",
            null) as string;

    /// <summary>方案的调校值读写往返：建一份临时白纸方案 → 写值（**不下发**）→ 读回 → 删除</summary>
    private static async Task AppendProfileRoundtrip(StringBuilder sb)
    {
        Services.WorkerBridge bridge = Services.WorkerBridge.Instance;

        string name = "__probe__" + Guid.NewGuid().ToString("N")[..6];
        string? id = null;

        try
        {
            IpcOutcome<string> created = await bridge
                .SavePlanAsync(Zentwik.Core.Services.PlanKind.Tuning, null, name)
                .ConfigureAwait(false);

            if (!created.Success)
            {
                sb.AppendLine($"roundtrip: 新建失败 {created.Message}");
                return;
            }

            id = created.Value;

            IpcOutcome<Zentwik.Core.Ipc.PlanListPayload> list = await bridge
                .QueryPlansAsync(Zentwik.Core.Services.PlanKind.Tuning)
                .ConfigureAwait(false);

            Zentwik.Core.Ipc.PlanEntryPayload? entry = list.Value?.Plans?
                .FirstOrDefault(p => p.Id == id);

            if (entry is null)
            {
                sb.AppendLine("roundtrip: 新建后列表里找不到它");
                return;
            }

            // 新建方案必须拿当前状态当内容，不能是空壳 ——
            // 空壳切进去等于「这套方案什么都不管」，用户会以为调校丢了。
            sb.AppendLine($"newPlanHasValues = {entry.CoreMargins.Length > 0 || entry.FMaxMHz > 0}");

            int[] wanted = { -5, -6, -7 };
            const uint wantedFMax = 5200;

            IpcOutcome<string> saved = await bridge
                .SavePlanAsync(Zentwik.Core.Services.PlanKind.Tuning, id, name, wanted, wantedFMax)
                .ConfigureAwait(false);

            if (!saved.Success)
            {
                sb.AppendLine($"roundtrip: 写入失败 {saved.Message}");
                return;
            }

            IpcOutcome<Zentwik.Core.Ipc.PlanListPayload> reread = await bridge
                .QueryPlansAsync(Zentwik.Core.Services.PlanKind.Tuning)
                .ConfigureAwait(false);

            Zentwik.Core.Ipc.PlanEntryPayload? back = reread.Value?.Plans?
                .FirstOrDefault(p => p.Id == id);

            bool matches = back is not null
                           && back.CoreMargins.SequenceEqual(wanted)
                           && back.FMaxMHz == wantedFMax;

            sb.AppendLine($"roundtripSaved = {matches} (读回 {back?.CoreMargins.Length ?? -1} 核 / {back?.FMaxMHz ?? 0} MHz)");
        }
        catch (Exception ex)
        {
            sb.AppendLine("roundtrip failed: " + ex.Message);
        }
        finally
        {
            if (id is not null)
            {
                IpcOutcome<string> removed = await bridge
                    .DeletePlanAsync(Zentwik.Core.Services.PlanKind.Tuning, id)
                    .ConfigureAwait(false);

                sb.AppendLine($"roundtripCleaned = {removed.Success}");
            }
        }
    }

    /// <summary>只读探针：ping + 查 SMU 可用性，打印后退出</summary>
    private static void RunWorkerProbe()
    {
        try
        {
            Zentwik.Core.Ipc.PipeResponse? pong = Services.WorkerBridge.Instance
                .SendAsync(Zentwik.Core.Ipc.WorkerPipeContract.CommandPing, null, 1500)
                .GetAwaiter().GetResult();

            Console.WriteLine(pong is null
                ? "worker: no-pipe"
                : $"worker: success={pong.Success} payload={pong.Payload} message={pong.Message}");

            Zentwik.Core.Ipc.PipeResponse? smu = Services.WorkerBridge.Instance
                .SendAsync(Zentwik.Core.Ipc.WorkerPipeContract.CommandQuerySmuStatus, null, 3000)
                .GetAwaiter().GetResult();

            Console.WriteLine(smu is null
                ? "smu: no-pipe"
                : $"smu: success={smu.Success} payload={smu.Payload}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("workerprobe failed: " + ex.Message);
        }

        // 探针走的是真实拉起路径，退出前必须把自己拉起来的 Worker 收掉 。
        Services.WorkerLauncher.RequestShutdown(
            TimeSpan.FromSeconds(3), evenIfNotOwned: true);

        Environment.Exit(0);
    }

    /// <summary>静置采样：量 CPU 与内存</summary>
    private static void RunPerfProbe(Window window)
    {
        int layoutCount = 0;
        window.LayoutUpdated += (_, _) => layoutCount++;

        var proc = Process.GetCurrentProcess();

        // 进程创建 → 首帧呈现：用户实际感知的「开软件要等多久」。
        double bootMs = (DateTime.Now - proc.StartTime).TotalMilliseconds;
        window.ContentRendered += (_, _) =>
        {
            double shownMs = (DateTime.Now - proc.StartTime).TotalMilliseconds;
            Console.WriteLine($"perf: boot_ms          = {bootMs:F0}");
            Console.WriteLine($"perf: boot_to_render_ms = {shownMs:F0}");
        };

        var warmup = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        warmup.Tick += (_, _) =>
        {
            warmup.Stop();

            var sw = Stopwatch.StartNew();
            TimeSpan cpuStart = proc.TotalProcessorTime;
            int baselineLayout = layoutCount;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                proc.Refresh();

                double seconds = sw.Elapsed.TotalSeconds;
                double cpuMs = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds;

                Console.WriteLine($"perf: seconds        = {seconds:F1}");
                Console.WriteLine($"perf: layout_events  = {layoutCount - baselineLayout}");
                Console.WriteLine($"perf: cpu_ms         = {cpuMs:F0}");
                Console.WriteLine($"perf: cpu_pct_1core  = {cpuMs / (seconds * 10):F1}");
                Console.WriteLine($"perf: ws_mb          = {proc.WorkingSet64 / 1048576.0:F1}");
                Console.WriteLine($"perf: private_mb     = {proc.PrivateMemorySize64 / 1048576.0:F1}");
                Console.WriteLine($"perf: gc_heap_mb     = {GC.GetTotalMemory(false) / 1048576.0:F1}");
                Console.WriteLine($"perf: gc0/gc1/gc2    = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                Console.Out.Flush();

                Current.Shutdown(0);
            };

            timer.Start();
        };

        warmup.Start();
    }

    /// <summary>托盘图标实例 —— 必须持有引用，否则会被 GC 回收， 消息窗口随之销毁，托盘图标也会消失</summary>
    private TrayIconService? _tray;

    /// <summary>创建托盘图标并接上主窗口的显示 / 退出</summary>
    private void SetupTray(MainWindow window)
    {
        var theme = AppThemeService.Instance;

        var tray = new TrayIconService();

        // 托盘图标用中性灰，**不跟随强调色** 。
        var trayPalette = LogoPaletteFactory.BuildForTray(
            darkTaskbar: !SystemTheme.IsSystemLightTheme());

        if (!tray.Initialize(theme.BuildLogoDrawing, trayPalette.Body, trayPalette.Shadow))
        {
            // 托盘不可用 → 关闭按钮必须正常退出，
            // 否则用户会把应用「关掉」却再也打不开。
            Debug.WriteLine("[App] 托盘图标创建失败，关闭窗口将直接退出。");
            // Initialize 失败时 HWND / HICON 已经建了一半，对象随即不可达。
            tray.Dispose();
            return;
        }

        _tray = tray;

        void ShowMainWindow()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                System.Windows.SystemCommands.RestoreWindow(window);
            }
            window.Activate();
        }

        tray.ShowRequested += (_, _) => ShowMainWindow();

        // 托盘右键菜单由 TrayIconService 直接用 TrackPopupMenuEx 弹出（系统原生），
        // 不再需要 WPF 自绘菜单与它的 0x1 宿主窗口。
        tray.ExitRequested += (_, _) => window.ExitApplication();

        // 托盘已就绪；关闭按钮的具体行为由设置里的「关闭主窗口」决定。
        window.MinimizeToTrayEnabled = true;

        // 系统任务栏明暗变化 → 托盘图标跟着换配色。
        theme.SystemThemeChanged += (_, _) =>
        {
            var palette = LogoPaletteFactory.BuildForTray(
                darkTaskbar: !SystemTheme.IsSystemLightTheme());
            tray.UpdateColors(palette.Body, palette.Shadow);
        };

        Exit += (_, _) =>
        {
            // 「仅留服务」= 只退 UI，Worker 常驻，不能连带杀掉。
            if (ViewModels.AppSettings.Instance.CloseBehavior
                != ViewModels.MainWindowCloseBehavior.CloseWindowOnly)
            {
                WorkerLauncher.RequestShutdown(
                    TimeSpan.FromSeconds(1.5), evenIfNotOwned: true);
            }

            _tray?.Dispose();
        };
    }
}
