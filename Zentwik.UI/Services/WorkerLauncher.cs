using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Ipc;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Services;

/// <summary>Worker 进程的拉起与就绪判定</summary>
public static class WorkerLauncher
{
    private const string WorkerExeName = "Zentwik.Worker.exe";

    private const string LogPath = "zentwik-worker.log";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan RetryPingTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan RetryTotal = TimeSpan.FromSeconds(90);

    /// <summary>失败后的冷却时间。页面切来切去不该把 90 秒的等待重跑一遍。</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);

    /// <summary>拉起流程串行化：两个页面同时首次加载会各拉一个 Worker。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>本进程是否拉起过 Worker</summary>
    private static bool _startedByUs;

    /// <summary>已经发过 shutdown 的标记：多条退出路径（窗口关闭 / 托盘退出 / App.Exit 兜底）只发一次。</summary>
    private static int _shutdownRequested;

    private static DateTime _nextAttemptUtc = DateTime.MinValue;

    /// <summary>本次 Worker 是否由本进程拉起。</summary>
    public static bool StartedByUs => _startedByUs;

    /// <summary>保证 Worker 在跑并已就绪</summary>
    public static async Task<bool> EnsureRunningAsync(
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (WorkerBridge.ForcedOffline || !WorkerBridge.AllowAutoStart)
        {
            // 诊断模式（截图 / 自检 / 探针）：既不许拉起进程，也不许等 90 秒。
            // 拉起会弹 UAC，截图跑一半停在授权框上，看上去就是「截图失败」。
            return false;
        }

        bool entered = false;

        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(true);
            entered = true;

            // 归属标记是「粘性」的：这里不再重置。理由见字段 _startedByUs 的注释。

            // 1. 已经在跑？
            if (await PingAsync(ProbeTimeout).ConfigureAwait(true))
            {
                _nextAttemptUtc = DateTime.MinValue;
                return true;
            }

            // 2. 刚失败过，先别再来一轮 90 秒
            if (DateTime.UtcNow < _nextAttemptUtc)
            {
                return false;
            }

            // 3. 进程在，但管道还没起来
            if (WorkerProcessExists())
            {
                Log("进程已存在，等待管道就绪");
                progress?.Invoke("后台服务正在初始化…");

                bool ready = await WaitUntilReadyAsync(progress, cancellationToken).ConfigureAwait(true);
                Finish(ready);
                return ready;
            }

            // 4. 进程不在：找 exe 并拉起
            string? exePath = ResolveWorkerPath();

            if (exePath is null)
            {
                Log($"找不到 {WorkerExeName}，已搜索：{string.Join(" | ", EnumerateWorkerPaths())}");
                progress?.Invoke("找不到后台服务程序");
                Finish(false);
                return false;
            }

            progress?.Invoke("正在启动后台服务（会请求管理员权限）…");
            Log($"拉起 {exePath}");

            // UseShellExecute 必须为 true 且 Verb=runas：Worker 清单是 requireAdministrator。
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
            });

            _startedByUs = true;

            bool ok = await WaitUntilReadyAsync(progress, cancellationToken).ConfigureAwait(true);
            Finish(ok);
            return ok;
        }
        catch (OperationCanceledException)
        {
            Log("等待被取消");
            return false;
        }
        catch (Exception ex)
        {
            // 用户取消 UAC = Win32Exception 1223；文件消失 = FileNotFoundException。
            Log($"拉起失败：{ex.GetType().Name}: {ex.Message}");
            progress?.Invoke("后台服务启动失败");
            Finish(false);
            return false;
        }
        finally
        {
            if (entered)
                Gate.Release();
        }
    }

    /// <summary>单次连通性探测</summary>
    public static async Task<bool> PingAsync(TimeSpan timeout)
    {
        try
        {
            await using var client = new WorkerPipeClient();

            await client.ConnectAsync(timeout).ConfigureAwait(false);

            PipeResponse response = await client.SendAsync(
                new PipeRequest
                {
                    // Version 必须显式赋值：默认 0 会被对端 ValidateVersion 拒绝，
                    // 而它只回「版本不一致」，排查时会先去怀疑安装包。
                    Version = WorkerPipeContract.ProtocolVersion,
                    Command = WorkerPipeContract.CommandPing,
                },
                timeout).ConfigureAwait(false);

            return response.Success;
        }
        catch (Exception ex)
        {
            Log($"ping 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>同步请求 Worker 退出，返回是否真的把 shutdown 发出去了</summary>
    public static bool RequestShutdown(TimeSpan timeout, bool evenIfNotOwned = false)
    {
        if (!evenIfNotOwned && !_startedByUs)
        {
            return false;
        }

        // 诊断模式（截图 / 自检 / 探针）不碰环境里已有的 Worker：
        // 用户机器上的守护进程不该被一次回归测试顺手关掉。
        if (!WorkerBridge.AllowAutoStart || WorkerBridge.ForcedOffline)
        {
            return false;
        }

        // 窗口关闭、托盘退出、App.Exit 兜底会各调一次，只放第一条过去。
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return true;
        }

        try
        {
            WorkerPipeClient client = new();

            try
            {
                client.ConnectAsync(timeout).GetAwaiter().GetResult();

                PipeResponse response = client.SendAsync(
                    new PipeRequest
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Command = WorkerPipeContract.CommandShutdown,
                    },
                    timeout).GetAwaiter().GetResult();

                Log(response.Success ? "已请求 Worker 退出" : $"Worker 拒绝退出：{response.Message}");
                return response.Success;
            }
            finally
            {
                // 客户端只实现 IAsyncDisposable：退出路径不能再排一个 await，
                // 这里同步等它把管道句柄关掉即可（内部无 UI 线程回调）。
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Log($"RequestShutdown 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Finish(bool ready)
    {
        _nextAttemptUtc = ready ? DateTime.MinValue : DateTime.UtcNow + FailureCooldown;
        Log(ready ? "Worker 就绪" : "Worker 未就绪");
    }

    private static async Task<bool> WaitUntilReadyAsync(Action<string>? progress, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int waitedSeconds = 0;

        while (sw.Elapsed < RetryTotal)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await PingAsync(RetryPingTimeout).ConfigureAwait(true))
            {
                Log($"就绪，等待 {sw.Elapsed.TotalSeconds:F1} 秒");
                return true;
            }

            await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(true);

            waitedSeconds += (int)RetryInterval.TotalSeconds;

            if (sw.Elapsed < RetryTotal)
            {
                progress?.Invoke($"后台服务正在初始化…（已等待 {waitedSeconds} 秒）");
            }
        }

        Log($"等待就绪超时（{RetryTotal.TotalSeconds:F0} 秒）");
        return false;
    }

    /// <summary>进程是否活着</summary>
    private static bool WorkerProcessExists()
    {
        try
        {
            if (Mutex.TryOpenExisting(WorkerPipeContract.WorkerMutexName, out Mutex? mutex))
            {
                mutex?.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Exception ex)
        {
            Log($"检测互斥体失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string? ResolveWorkerPath()
    {
        foreach (string candidate in EnumerateWorkerPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>搜索顺序：先输出目录（产品形态两个 exe 同目录），再仓库里的构建产物</summary>
    private static IEnumerable<string> EnumerateWorkerPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, WorkerExeName);

        foreach (string configuration in new[] { "Release", "Debug" })
        {
            string relative = Path.Combine("bin", configuration, "net10.0-windows", WorkerExeName);

            // 合并回主仓库后的形态：UI 与 Worker 是兄弟工程
            yield return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, @"..\..\..\..", "Zentwik.Worker", relative));

            // 当前形态：本工程在 D:\Projects 下，Worker 在 D:\Projects\Zentwik 下
            yield return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, @"..\..\..\..", "Zentwik", "Zentwik.Worker", relative));

            // 兜底：UI 直接放在仓库根下（bin 里只有两层）
            yield return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, @"..\..\..", "Zentwik.Worker", relative));
        }
    }

    /// <summary>启动过程落一份文件日志</summary>
    private static void Log(string message)
    {
        Debug.WriteLine($"[WorkerLauncher] {message}");

        try
        {
            string path = Path.Combine(Path.GetTempPath(), LogPath);
            long size = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;

            // 超过 64 KB 重开一份，避免无限增长。
            if (size > 64 * 1024)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去不影响主流程。
        }
    }
}
