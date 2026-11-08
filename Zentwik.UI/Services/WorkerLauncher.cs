using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Ipc;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Services;

/// <summary>
/// Worker 进程的拉起与就绪判定。
/// </summary>
/// <remarks>
/// 【判定顺序：先 ping，再查互斥体】
/// 管道能 ping 通是唯一可信的「已就绪」信号。进程活着不代表管道建好了
/// （Worker 先跑宿主服务，管道监听排在后面），互斥体存在也不代表能通信。
/// 两个信号合起来才分得清「没启动」「在启动」「已就绪」。
///
/// 【等待上限 90 秒】
/// 没有 PawnIO 时 Worker 启动会走 <c>ApplyFromStore → WaitForAvailability(60)</c>，
/// 管道监听排在它之后，最长阻塞 60 秒。90 秒 = 60 秒最坏阻塞 + 30 秒余量。
/// 按常规 3 秒算，没装驱动的机器上会稳定误报「未连接」。
///
/// 【进程起不来的三种原因，日志里要能分开】
/// 用户取消 UAC（Win32Exception 1223）、找不到 exe（FileNotFoundException）、
/// 非提权进程硬调 CreateProcess（Win32Exception 740）。都写进
/// <c>%TEMP%\zentwik-worker.log</c>，否则界面只能说「未连接」，没法排查。
/// </remarks>
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

    private static bool _startedByUs;

    private static DateTime _nextAttemptUtc = DateTime.MinValue;

    /// <summary>本次 Worker 是否由本进程拉起。</summary>
    public static bool StartedByUs => _startedByUs;

    /// <summary>
    /// 保证 Worker 在跑并已就绪。<b>任何失败都返回 false，绝不抛异常</b>。
    /// </summary>
    /// <param name="progress">状态文案回调。在 UI 线程上被调用（本方法内部不使用
    /// <c>ConfigureAwait(false)</c>），可直接改控件属性。</param>
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

            // 每次调用重新判定「这次是不是本进程拉起的」：
            // 残留 true 会让退出流程给并非本进程启动的 Worker 发 shutdown。
            _startedByUs = false;

            // ---------- 1. 已经在跑？ ----------
            if (await PingAsync(ProbeTimeout).ConfigureAwait(true))
            {
                _startedByUs = false;
                _nextAttemptUtc = DateTime.MinValue;
                return true;
            }

            // ---------- 2. 刚失败过，先别再来一轮 90 秒 ----------
            if (DateTime.UtcNow < _nextAttemptUtc)
            {
                return false;
            }

            // ---------- 3. 进程在，但管道还没起来 ----------
            if (WorkerProcessExists())
            {
                Log("进程已存在，等待管道就绪");
                progress?.Invoke("后台服务正在初始化…");

                bool ready = await WaitUntilReadyAsync(progress, cancellationToken).ConfigureAwait(true);
                Finish(ready);
                return ready;
            }

            // ---------- 4. 进程不在：找 exe 并拉起 ----------
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

            // UseShellExecute 必须为 true 且 Verb=runas：Worker 清单是 requireAdministrator，
            // 非提权进程用 UseShellExecute=false 走 CreateProcess 会直接 740 失败 ——
            // 既不弹 UAC，进程也起不来。
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

    /// <summary>
    /// 单次连通性探测。
    /// </summary>
    /// <remarks>
    /// 【每次新建 client】
    /// Worker 的管道服务端是「一个连接只处理一条消息」：读一行、回一行、关掉。
    /// 复用同一个客户端在第二条消息上必然读到流结束。
    /// </remarks>
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

    /// <summary>让本次拉起的 Worker 退出。fire-and-forget，不阻塞退出路径。</summary>
    public static void ShutdownIfOwned()
    {
        if (!_startedByUs)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var client = new WorkerPipeClient();

                await client.ConnectAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                await client.SendAsync(
                    new PipeRequest
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Command = WorkerPipeContract.CommandShutdown,
                    },
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                Log("已请求 Worker 退出");
            }
            catch (Exception ex)
            {
                Log($"ShutdownIfOwned 失败：{ex.GetType().Name}: {ex.Message}");
            }
        });
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

    /// <summary>
    /// 进程是否活着。用单实例互斥体判断，不用进程名。
    /// </summary>
    /// <remarks>
    /// 互斥体带 <c>Global\</c> 前缀，由提权进程创建。非提权 UI 打开它时
    /// 实测可能直接抛 UnauthorizedAccessException —— 它的含义恰恰是「对象存在」。
    /// 当成「不存在」会让 UI 去拉第二个实例，第二个抢不到互斥体静默退出，UI 白等 90 秒。
    /// </remarks>
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

    /// <summary>
    /// 搜索顺序：先输出目录（产品形态两个 exe 同目录），再仓库里的构建产物。
    /// </summary>
    /// <remarks>
    /// 本工程当前位于仓库外的独立目录，所以除了「同级 Worker 工程」，
    /// 还要能指向 <c>..\Zentwik\Zentwik.Worker\bin</c>。合并回主仓库后前者生效。
    /// Release / Debug 都试：开发机常只有一种构建产物。
    /// </remarks>
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

    /// <summary>
    /// 启动过程落一份文件日志。
    /// </summary>
    /// <remarks>
    /// 「拉不起来」在界面上只有一句「未连接」，分不清是没找到 exe、UAC 被拒，
    /// 还是进程起来了但管道没准备好。这三种情况的处理方式完全不同。
    /// </remarks>
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
