using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zentwik.Core.DependencyInjection;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;

namespace Zentwik.Worker
{
    /// <summary>
    /// Worker 进程入口。
    ///
    ///   1. 抢单实例互斥体（抢不到说明已有 Worker 在跑，直接退）；
    ///   2. 挂全局异常兜底（崩溃现场必须落盘，因为 WinExe 没有控制台可看）；
    ///   3. 建宿主、注册 Core 服务与两个 HostedService；
    ///   4. 跑起来，退出时释放互斥体。
    ///
    /// Worker 的职责边界是「常驻 + 听管道 + 执行提权操作」。
    /// 启动顺序（Load → Apply → 探测 → 监听）属于宿主服务的职责，
    /// 放在 Main 里会让「先 Load 谁、后 Start 谁」散落在两个文件里。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // ---------- 单实例 ----------
            // 互斥体名带 Global\ 前缀（见 WorkerPipeContract.WorkerMutexName）：
            // 跨会话可见，远程桌面另开一个会话时不会拉起第二个 Worker 去抢同一块 SMU。
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

                // ---------- 参数过滤 ----------
                // --open-ui / --tray 是本进程自己的开关，必须在交给 Host 之前摘掉：
                // Host.CreateApplicationBuilder(args) 会把参数喂给 CommandLineConfigurationProvider，
                // 让一个业务开关混进配置系统，等于把一个进程参数变成一份隐式配置。
                // （批次五实测：裸开关 -silent 并不会让 Worker 崩，所以这里不是为了「防崩」，
                //   而是为了让参数归属清晰 —— 谁解析，谁消费。）
                //
                // 同时出现时以 --open-ui 为准：「打开窗口」是更明确、用户感知更强的意图，
                // 而 --tray 只表达「别开窗口」。计划任务不会同时写两个，这条规则防的是
                // 用户手工敲错命令时得到一个既不弹窗也不建托盘的后台进程。
                bool openUi = args.Contains(AutoStartService.OpenUiArgument);
                bool tray = args.Contains(AutoStartService.TrayArgument);

                string[] hostArgs = args
                    .Where(a => a != AutoStartService.OpenUiArgument && a != AutoStartService.TrayArgument)
                    .ToArray();

                HostApplicationBuilder builder = Host.CreateApplicationBuilder(hostArgs);

                // WinExe 没有控制台，默认的 Console logger 是一个写不出任何东西的黑洞。
                builder.Logging.ClearProviders();

                // FilePrefix 是双进程日志不冲突的关键：UI 用 zentwik-，Worker 用 worker-，
                // 同一 Logs 目录下两套文件按前缀分开，排障时不会互相盖掉。
                builder.Services.AddZentwikCore(cfg => cfg.FilePrefix = "worker-");

                // 注册顺序即启动顺序：先起后端（Load / Apply / 监听），再开管道。
                builder.Services.AddHostedService<BackendHostService>();
                builder.Services.AddHostedService<PipeServerService>();

                IHost host = builder.Build();

                if (Debugger.IsAttached)
                    Console.WriteLine("Worker 已启动，调试模式");

                // ---------- 登录后拉起 UI ----------
                // 用 ApplicationStarted 而不是在 Run() 之前直接起：
                // ApplicationStarted 在所有 IHostedService.StartAsync 完成之后触发，
                // 此时管道监听已经就位，UI 一起来就能连上，不会看到「正在连接」再等一轮。
                //
                // Run() 内部自己会调 StartAsync()，先 Start 再 Run 会抛
                // InvalidOperationException（Host already started）。
                if (openUi || tray)
                {
                    ILogService uiLog = host.Services.GetRequiredService<ILogService>();

                    // IHost 接口本身没有 Lifetime 属性（只有 Services），
                    // 生命周期对象必须从容器里取。
                    IHostApplicationLifetime hostLifetime =
                        host.Services.GetRequiredService<IHostApplicationLifetime>();

                    // openUi 优先（见上面的参数过滤注释）：开窗口时不带任何参数，
                    // 只留托盘时把 --tray 原样转给 UI，由 UI 决定「不建 MainWindow、只建托盘」。
                    string? uiArgs = openUi ? null : AutoStartService.TrayArgument;

                    hostLifetime.ApplicationStarted.Register(() =>
                    {
                        // fire-and-forget：拉不起 UI 不能让常驻进程退出，
                        // 异常必须在 LaunchUi 内部吃掉。
                        _ = Task.Run(() => LaunchUi(uiLog, uiArgs));
                    });
                }

                try
                {
                    host.Run();
                }
                catch (Exception ex)
                {
                    // 启动失败：落盘堆栈，返回非 0 让计划任务 / 启动方看到失败。
                    StartupCrashReporter.Report(ex, interactive: false);
                    return 1;
                }
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }

            return 0;
        }

        /// <summary>
        /// 第二个实例退出前留一行记录。
        ///
        /// 它写的是完整异常堆栈。这里只是「已经有实例占着」这一条事实，
        /// 没有异常，写堆栈会让人误以为崩过一次。
        ///
        /// 本分支在 Host.CreateApplicationBuilder 之前执行，AppPaths 还没构造，
        /// 所以拿不到 IAppPaths —— 只能自己拼 %AppData%\Zentwik。
        /// 而首次运行时该目录可能不存在，File.AppendAllText 会抛
        /// DirectoryNotFoundException；此刻全局异常处理器尚未注册，
        /// 后果是「第二个实例崩溃退出」而不是静默退出，直接违背单实例设计。
        ///
        /// 单实例的承诺是「静默退出」，不是「一定写得出日志」。
        /// 目录只读、磁盘满、被安全软件拦截时，写日志失败不该升级成崩溃。
        /// </summary>
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

        /// <summary>
        /// 把 UI 叫起来。任何失败都只记日志、返回 false，不抛也不退出。
        /// </summary>
        /// <param name="extraArgs">null = 正常开窗口；TrayArgument = 只建托盘。</param>
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

                // Worker 是提权进程，子进程继承提权令牌 —— 不弹 UAC（事实卡 §5.2 第 2 行）。
                // UseShellExecute=false：不经过 shell，也就不会因为 UI 的 asInvoker 清单
                // 再走一次提权判定（那条路会弹 UAC，而这里恰恰不该弹）。
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

        /// <summary>
        /// 搜索顺序：先发布/输出目录（两个 exe 同目录），再开发构建目录
        /// （从 Zentwik.Worker/bin/&lt;Config&gt;/net10.0-windows 往上四级到仓库根）。
        /// </summary>
        private static IEnumerable<string> EnumerateUiPaths()
        {
            yield return Path.Combine(AppContext.BaseDirectory, UiExeName);

            yield return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                @"..\..\..\..\Zentwik.UI\bin\Release\net10.0-windows",
                UiExeName));
        }

        /// <summary>
        /// 未处理异常兜底。处理器自身绝不能再抛 —— 否则真正的崩溃原因会被
        /// 「兜底时的崩溃」顶掉，日志里只剩一条毫无信息量的 CLR 终止记录。
        /// </summary>
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                    StartupCrashReporter.Report(ex, interactive: false);

                // WinExe 没有控制台，这行在正常启动时写进黑洞；
                // 调试器附加 / 从控制台以管道方式拉起时才有意义。
                Console.Error.WriteLine($"[Worker] 未处理异常：{e.ExceptionObject}");
            }
            catch
            {
                // 兜底路径不允许再抛。
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
