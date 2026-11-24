using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Zentwik.Core.DependencyInjection;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.Worker.Tray;

namespace Zentwik.Worker
{
    /// <summary>Worker 进程入口：抢单实例互斥体 → 挂全局异常兜底 → 建容器 → 按序启停。</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Mutex mutex = new(
                initiallyOwned: true,
                name: WorkerPipeContract.WorkerMutexName,
                createdNew: out bool createdNew);

            if (!createdNew)
            {
                // 没有所有权，不能 ReleaseMutex（会抛 ApplicationException），只释放句柄。
                mutex.Dispose();
                WriteAlreadyRunningNote();
                return 0;
            }

            try
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                bool openUi = args.Contains(AutoStartService.OpenUiArgument);
                bool trayArgument = args.Contains(AutoStartService.TrayArgument);

                ServiceCollection services = new();
                services.AddSingleton<IWorkerLifetime, WorkerLifetime>();

                // FilePrefix 是双进程日志不冲突的关键：UI 用 zentwik-，Worker 用 worker-。
                // Verbose 打开：逐进程绑定、逐核 CO 这些明细只在 Debug，界面只看汇总，明细落盘备查。
                services.AddZentwikCore(cfg =>
                {
                    cfg.FilePrefix = "worker-";
                    cfg.Verbose = true;
                });

                services.AddSingleton<BackendHostService>();
                services.AddSingleton<PipeServerService>();

                using ServiceProvider provider = services.BuildServiceProvider();

                // 启动分界要抢在取 BackendHostService 之前：SMU 初始化在构造 ISmuReader 时就跑了。
                ILogService bootLog = provider.GetRequiredService<ILogService>();
                bootLog.Info(
                    $"===== 后台服务启动 {AppVersion.Current} · PID {Environment.ProcessId} · " +
                    $"{(IsElevated() ? "管理员权限" : "普通权限")} =====");

                BackendHostService backend = provider.GetRequiredService<BackendHostService>();
                PipeServerService pipe = provider.GetRequiredService<PipeServerService>();
                IWorkerLifetime lifetime = provider.GetRequiredService<IWorkerLifetime>();
                PlanService plans = provider.GetRequiredService<PlanService>();
                IUiSettingsStore settingsStore = provider.GetRequiredService<IUiSettingsStore>();

                try
                {
                    // 注册顺序即启动顺序：先起后端（Load / Apply / 监听），再开管道。
                    backend.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                    pipe.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    StartupCrashReporter.Report(ex, interactive: false);
                    return 1;
                }

                if (Debugger.IsAttached)
                    Console.WriteLine("Worker 已启动，调试模式");

                TrayHost? tray = StartTray(bootLog, pipe, plans, settingsStore, trayArgument, lifetime);

                // 只认 --open-ui：托盘归后台服务后，界面不再是托盘的宿主。
                // 托盘建不起来时必须拉界面，否则用户没有任何入口。
                if (openUi || (trayArgument && tray is null))
                {
                    _ = Task.Run(() => LaunchUi(bootLog, null));
                }

                // 有托盘时主线程跑消息循环，停止信号与窗口消息一起等。
                if (tray is not null)
                {
                    tray.RunMessageLoop(lifetime.StoppingToken.WaitHandle);
                    tray.Dispose();
                }
                else
                {
                    lifetime.StoppingToken.WaitHandle.WaitOne();
                }

                pipe.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                backend.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }

            return 0;
        }

        /// <summary>第二个实例退出前留一行记录</summary>
        private static void WriteAlreadyRunningNote()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Zentwik");

                Directory.CreateDirectory(root);

                File.AppendAllText(
                    Path.Combine(root, "startup_crash.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] " +
                    $"Worker 已在运行，单实例互斥体被占用，本次启动退出{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // 记不下来就算了，不能因为写日志失败让进程留下。
            }
        }

        /// <summary>UI 可执行文件名。</summary>
        private const string UiExeName = "Zentwik.UI.exe";

        /// <summary>建托盘宿主。返回 null 表示窗口都建不起来，调用方要走拉起界面的兜底。</summary>
        private static TrayHost? StartTray(
            ILogService log,
            PipeServerService pipe,
            PlanService plans,
            IUiSettingsStore settingsStore,
            bool forced,
            IWorkerLifetime lifetime)
        {
            TrayHost host = new(log);

            try
            {
                if (!host.Start())
                {
                    host.Dispose();
                    return null;
                }
            }
            catch (Exception ex)
            {
                // 托盘是附加能力：建不起来也不能让常驻进程退出。
                log.Warn($"托盘：创建失败 {ex.GetType().Name}: {ex.Message}");
                host.Dispose();
                return null;
            }

            bool ShouldShow()
            {
                if (forced)
                {
                    return true;
                }

                CloseBehavior behavior = CloseBehaviorPolicy.Normalize(settingsStore.Settings.CloseBehavior);
                return TrayPolicy.ShouldShowTray(settingsStore.Settings.AutoStartMode, behavior);
            }

            host.ShouldShow = ShouldShow;
            host.MenuProvider = () => BuildTrayMenu(plans);
            host.ApplyPlan = async (kind, id) =>
            {
                (bool success, string message) = await ApplyPlanFromTrayAsync(pipe, kind, id)
                    .ConfigureAwait(false);

                // 失败必须可见；成功只在用户开了托盘通知时弹。
                if (!success && message.Length == 0)
                {
                    return "后台服务未响应";
                }

                return success && !TraySuccessNotifyEnabled() ? null : message;
            };
            host.ShowRequested = () => _ = Task.Run(() => LaunchUi(log, null));

            host.ExitRequested = () =>
            {
                NotifyUiExit(log);
                lifetime.Stop();
            };

            // 设置保存后图标可能要出现或撤下；SetVisible 必须在主线程执行。
            pipe.SettingsSaved += host.PostReevaluate;

            try
            {
                host.SetVisible(ShouldShow());
            }
            catch (Exception ex)
            {
                log.Warn($"托盘：显示失败 {ex.GetType().Name}: {ex.Message}");
            }

            return host;
        }

        /// <summary>右键那一刻现算：方案可能刚在界面里被新建、改名、切换。</summary>
        private static IReadOnlyList<TrayMenuItem> BuildTrayMenu(PlanService plans)
        {
            if (!TrayPlanMenuEnabled())
            {
                return TrayMenu.Build(Array.Empty<TrayPlanGroup>());
            }

            List<TrayPlanGroup> groups = new(3);

            AddGroup(PlanKind.Tuning, "调校方案");
            AddGroup(PlanKind.Scheduling, "调度方案");
            AddGroup(PlanKind.Power, "电源方案");

            return TrayMenu.Build(groups);

            void AddGroup(PlanKind kind, string label)
            {
                IReadOnlyList<PlanEntry> items = plans.Items(kind);

                if (items.Count == 0)
                {
                    return;
                }

                string? active = plans.ActiveId(kind);
                List<TrayMenuItem> children = new(items.Count);

                foreach (PlanEntry entry in items)
                {
                    children.Add(new TrayMenuItem(
                        TrayMenu.PlanKey(kind, entry.Id), entry.Name, entry.Id == active));
                }

                groups.Add(new TrayPlanGroup(label, children));
            }
        }

        /// <summary>托盘切换后是否弹回执。该开关在 ui-settings.json，不进后台同步链路。</summary>
        private static bool TraySuccessNotifyEnabled()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Zentwik",
                    "ui-settings.json");

                if (!File.Exists(path))
                {
                    return true;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

                return !document.RootElement.TryGetProperty("NotifyOnTraySuccess", out JsonElement value)
                       || value.ValueKind != JsonValueKind.False;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>读 ui-entry.json 的托盘方案开关。AOT 下不能用反射反序列化，手工读一项。</summary>
        private static bool TrayPlanMenuEnabled()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Zentwik",
                    "ui-entry.json");

                if (!File.Exists(path))
                {
                    return true;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

                return !document.RootElement.TryGetProperty("trayPlanMenu", out JsonElement value)
                       || value.ValueKind != JsonValueKind.False;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 文件坏了按默认值（开启）处理，不能挡住菜单。
                return true;
            }
        }

        /// <summary>托盘切方案走与 IPC 同一条路径，电源方案那一份 power policy 快照才不会漏写。</summary>
        private static async Task<(bool Success, string Message)> ApplyPlanFromTrayAsync(
            PipeServerService pipe, PlanKind kind, string id)
        {
            string payload = JsonSerializer.Serialize(
                new PlanRefPayload { Kind = PlanKinds.ToWire(kind), Id = id },
                IpcJsonContext.Default.PlanRefPayload);

            PipeResponse response = await pipe
                .RunApplyPlanAsync(payload, CancellationToken.None)
                .ConfigureAwait(false);

            return (response.Success, response.Message ?? string.Empty);
        }

        /// <summary>通知界面退出。界面没在跑是正常情况，不算失败。</summary>
        private static void NotifyUiExit(ILogService log)
        {
            try
            {
                using NamedPipeClientStream client = new(
                    ".", WorkerPipeContract.UiActivatePipeName, PipeDirection.Out);

                client.Connect(500);

                using StreamWriter writer = new(client, new UTF8Encoding(false));
                writer.WriteLine(WorkerPipeContract.UiActivateExit);
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                log.Info($"退出：界面未响应关闭通知（{ex.GetType().Name}）");
            }
        }


        /// <summary>把 UI 叫起来</summary>
        internal static bool LaunchUi(ILogService log, string? extraArgs)
        {
            try
            {
                string? uiPath = ResolveUiPath();
                if (uiPath is null)
                {
                    log.Warn($"找不到 {UiExeName}，跳过拉起 UI。已搜索：{string.Join(" | ", EnumerateUiPaths())}");
                    return false;
                }

                // Worker 是提权进程，子进程继承提权令牌，不弹 UAC。
                Process.Start(new ProcessStartInfo
                {
                    FileName = uiPath,
                    Arguments = extraArgs ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                log.Info(extraArgs is null
                    ? $"已拉起 UI：{uiPath}"
                    : $"已拉起 UI（{extraArgs}）：{uiPath}");
                return true;
            }
            catch (Exception ex)
            {
                // 调用方是 fire-and-forget，异常没有观察者：不在这里捕获等于静默丢失。
                log.Warn($"拉起 UI 失败：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static string? ResolveUiPath()
        {
            foreach (string candidate in EnumerateUiPaths())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>搜索顺序：先输出目录（两个 exe 同目录），再开发构建目录。</summary>
        private static IEnumerable<string> EnumerateUiPaths()
        {
            yield return Path.Combine(AppContext.BaseDirectory, UiExeName);

            yield return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                @"..\..\..\..\Zentwik.UI\bin\Release\net10.0-windows",
                UiExeName));
        }

        /// <summary>未处理异常兜底</summary>
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                    StartupCrashReporter.Report(ex, interactive: false);

                // WinExe 没有控制台，这行在正常启动时写进黑洞；调试器附加时才有意义。
                Console.Error.WriteLine($"[Worker] 未处理异常：{e.ExceptionObject}");
            }
            catch
            {
                // 兜底路径不允许再抛。
            }
        }

        /// <summary>是否以管理员身份运行。没提权时 SMU 与部分调度功能是降级状态，日志里要能看出来。</summary>
        private static bool IsElevated()
        {
            try
            {
                using System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent();

                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>未观察的任务异常兜底。同上，只记录不抛。</summary>
        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                StartupCrashReporter.Report(e.Exception, interactive: false);
                Console.Error.WriteLine($"[Worker] 未观察的任务异常：{e.Exception}");
            }
            catch
            {
                // 兜底路径不允许再抛。
            }
        }
    }
}
