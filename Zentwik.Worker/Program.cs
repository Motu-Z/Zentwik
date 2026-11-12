using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Zentwik.Core.DependencyInjection;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;

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
                bool tray = args.Contains(AutoStartService.TrayArgument);

                ServiceCollection services = new();
                services.AddSingleton<IWorkerLifetime, WorkerLifetime>();

                // FilePrefix 是双进程日志不冲突的关键：UI 用 zentwik-，Worker 用 worker-。
                services.AddZentwikCore(cfg => cfg.FilePrefix = "worker-");

                services.AddSingleton<BackendHostService>();
                services.AddSingleton<PipeServerService>();

                using ServiceProvider provider = services.BuildServiceProvider();

                BackendHostService backend = provider.GetRequiredService<BackendHostService>();
                PipeServerService pipe = provider.GetRequiredService<PipeServerService>();
                IWorkerLifetime lifetime = provider.GetRequiredService<IWorkerLifetime>();

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

                // 登录后拉起 UI：openUi 优先，只留托盘时把 --tray 原样转给 UI。
                if (openUi || tray)
                {
                    ILogService uiLog = provider.GetRequiredService<ILogService>();
                    string? uiArgs = openUi ? null : AutoStartService.TrayArgument;

                    // fire-and-forget：拉不起 UI 不能让常驻进程退出，异常在 LaunchUi 内部吃掉。
                    _ = Task.Run(() => LaunchUi(uiLog, uiArgs));
                }

                // 阻塞至管道 shutdown 命令触发停止信号。
                lifetime.StoppingToken.WaitHandle.WaitOne();

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
