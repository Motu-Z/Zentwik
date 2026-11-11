using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Helpers;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 进程快照服务（低占用版）。
    ///
    /// 关键点：
    ///   1. 用 Win32 <c>Toolhelp32Snapshot</c> 一次性拿全系统进程名 + PID，
    ///      不再逐个 <c>Process.GetProcesses()</c> 再打开 <c>MainModule</c>（那会为每个进程
    ///      打开模块句柄，权限不足时还抛异常，开销与失败率都很高）。
    ///   2. 可执行文件路径用 <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> +
    ///      <c>QueryFullProcessImageName</c> 取，权限要求比 MainModule 低得多；
    ///      并按 PID 缓存。<b>解析失败会退避重试</b>，见 <see cref="ResolvePath"/>。
    ///   3. CPU 占用率用 <c>GetProcessTimes</c> 做<b>滑动窗口平均</b>，见 <see cref="ComputeCpu"/>。
    ///
    /// 采样跑在独立后台线程上（旧实现把整轮 Toolhelp 遍历与对每个进程的
    /// OpenProcess 都压在主线程，是消费方列表掉帧的直接原因）。
    ///
    /// 生命周期：应用启动即开始周期采样，调度页打开后直接消费最新快照。
    /// </summary>
    public partial class ProcessRefresher
    {
        private readonly ILogService _log;

        public ProcessRefresher(ILogService log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
        }

        // ---------- Win32 互操作 ----------

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // 必须是 64 位全 1：new IntPtr(uint) 会零扩展成 0x00000000FFFFFFFF，
        // 与 64 位进程里真实的 INVALID_HANDLE_VALUE（0xFFFFFFFFFFFFFFFF）比不上。
        private const long INVALID_HANDLE_VALUE = -1;

        /// <summary>
        /// Toolhelp 快照条目。
        ///
        /// <c>szExeFile</c> 用固定长度内联缓冲区而不是 <c>ByValTStr</c>：
        /// 源生成的 P/Invoke 不支持字符串字段的按值封送，含 string 的结构体会被
        /// 判为不可封送（SYSLIB1051）。定长 char 缓冲区是纯 blittable 的，
        /// 既不需要运行时封送，也不会因裁剪掉封送桩而在 AOT 下失败。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            public fixed char szExeFile[260];
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(
            uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        /// <summary>
        /// 取进程映像路径。缓冲区由调用方用 stackalloc 提供，
        /// <paramref name="lpdwSize"/> 传入容量、回填实际字符数。
        /// </summary>
        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool QueryFullProcessImageNameW(
            IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(
            IntPtr hProcess,
            out long lpCreationTime,
            out long lpExitTime,
            out long lpKernelTime,
            out long lpUserTime);

        /// <summary>读取定长缓冲区里的进程名（以第一个 NUL 为界，不越过 260 字符）。</summary>
        private static unsafe string ReadExeName(ref PROCESSENTRY32 entry)
        {
            fixed (char* raw = entry.szExeFile)
            {
                ReadOnlySpan<char> span = new(raw, 260);
                int end = span.IndexOf('\0');
                return end < 0 ? new string(span) : new string(span[..end]);
            }
        }

        // ---------- 采样状态 ----------

        /// <summary>单个进程的采样记录（用于 CPU 滑动窗口与路径缓存）。</summary>
        private sealed class ProcSample
        {
            public long KernelTicks;
            public long UserTicks;
            public long StampTicks;

            public string Path = string.Empty;
            public bool PathResolved;

            /// <summary>上一次取路径失败的时间戳（0 = 从未失败过）。用于退避重试。</summary>
            public long PathFailStamp;

            /// <summary>CPU 占用滑动窗口：(采样时刻, 当时的累计 CPU 时间)。</summary>
            public readonly Queue<(long Stamp, long Ticks)> CpuWindow = new();
        }

        /// <summary>一个进程的快照条目。</summary>
        public sealed class Entry
        {
            public string Name { get; init; } = string.Empty;
            public int Pid { get; init; }
            public string Path { get; init; } = string.Empty;
            public double Cpu { get; init; }

            /// <summary>
            /// 进程基础优先级类（来自 PROCESSENTRY32.pcPriClassBase），只读展示用。
            ///
            /// 只取不设：SetPriorityClass 提到 RealTime / High 可能让一个忙循环的进程
            /// 独占 CPU，把输入响应一起锁死（整机看起来像卡死）。而 pcPriClassBase
            /// 是 Toolhelp 快照自带字段，不需要 OpenProcess 额外权限，
            /// 对受保护进程也能拿到值，成本为零。
            /// </summary>
            public int PriorityClass { get; init; }
        }

        private readonly Dictionary<int, ProcSample> _samples = new();
        private readonly List<Entry> _latest = new();
        private readonly object _gate = new();

        /// <summary>最新一帧的采样时刻（Stopwatch 时间戳），用于「够不够新」判断。</summary>
        private long _latestStamp;

        /// <summary>
        /// 保护 <see cref="_samples"/>：单例字典会被采样线程与任何外部 Sample 调用同时接触。
        /// </summary>
        private readonly object _sampleGate = new();

        private CancellationTokenSource? _cts;
        private int _loopRunning;

        /// <summary>「立即采一帧」请求。采样线程只在这条信号与取消信号上等待。</summary>
        private readonly SemaphoreSlim _sampleNowSignal = new(0);

        private Thread? _samplerThread;

        /// <summary>CPU 占用滑动窗口的长度（秒）。</summary>
        private const double CpuWindowSeconds = 30.0;

        /// <summary>窗口最多保留的采样点数，防止某个周期被拖长时队列膨胀。</summary>
        private const int MaxWindowPoints = 64;

        /// <summary>路径解析失败后的重试间隔（秒）。</summary>
        private const double PathRetrySeconds = 30.0;

        // 一轮采集要给几百个进程各算一次，这三个值都是常量，不必逐进程重算。
        private static readonly long CpuWindowTicks = (long)(CpuWindowSeconds * Stopwatch.Frequency);
        private static readonly long PathRetryTicks = (long)(PathRetrySeconds * Stopwatch.Frequency);
        private static readonly int ProcessorCount = Math.Max(1, Environment.ProcessorCount);

        /// <summary>最新快照（只读副本）。</summary>
        public IReadOnlyList<Entry> Latest
        {
            get { lock (_gate) return _latest.ToArray(); }
        }

        /// <summary>
        /// 取一帧不超过 <paramref name="maxAge"/> 的快照，过期或无数据返回 null。
        ///
        /// 给「查询即现采」的调用方用：列表每 3 秒查一次，若每次都强制重采，
        /// 就等于每 3 秒做一轮全量 OpenProcess。2 秒内的数据对列表没有区别。
        /// </summary>
        public IReadOnlyList<Entry>? TryGetSnapshot(TimeSpan maxAge)
        {
            long now = Stopwatch.GetTimestamp();

            lock (_gate)
            {
                if (_latest.Count == 0 || _latestStamp == 0)
                    return null;

                double ageSeconds = (now - _latestStamp) / (double)Stopwatch.Frequency;
                return ageSeconds > maxAge.TotalSeconds ? null : _latest.ToArray();
            }
        }

        /// <summary>启动后台采样线程（幂等）。应用启动时调用一次即可。
        ///
        /// 线程启动后挂在信号量上等待，只有 <see cref="RequestSample"/> 才会让它采一帧。
        /// </summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _loopRunning, 1) == 1)
                return;

            CancellationTokenSource cts = new();
            _cts = cts;

            Thread thread = new(() => RunLoop(cts.Token))
            {
                IsBackground = true,
                Name = "Zentwik.ProcessSampler",
                // 采样属于「后台维护」工作，绝不该和 UI 抢时间片。
                Priority = ThreadPriority.BelowNormal,
            };

            // 采样线程只做 Win32 调用与托管集合写入，不创建窗口、不跑消息循环。
            // STA 只对需要单元亲和性的 COM / 界面组件有意义，这里不存在那种对象。
            thread.SetApartmentState(ApartmentState.MTA);
            _samplerThread = thread;
            thread.Start();
        }

        /// <summary>
        /// 请求「现在立刻采一帧」，用于刚打开列表时不要干等一个完整周期。
        ///
        /// 排队而不是直接同步执行：采样必须在采样线程上跑（MTA + 串行），
        /// 在 UI 线程上同步 Collect 会把几百次 OpenProcess 压到界面上。
        /// </summary>
        public void RequestSample()
        {
            try
            {
                _sampleNowSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已经有一个待处理的请求，合并即可
            }
        }

        private void RunLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 只等「立即采一帧」与取消：采样完全由查询驱动，没有周期唤醒。
                    int index = WaitHandle.WaitAny(
                        new[]
                        {
                            _sampleNowSignal.AvailableWaitHandle,
                            token.WaitHandle,
                        },
                        Timeout.Infinite);

                    if (index == 1)
                        break;

                    // 消费掉待处理的请求，多来源并发时只采一帧。
                    while (_sampleNowSignal.Wait(0))
                    {
                    }

                    SampleOnce("立即采样请求");
                }
                catch (ObjectDisposedException)
                {
                    // 退出阶段信号量被释放，正常收尾
                    break;
                }
            }
        }

        /// <summary>采一帧并广播；异常只记 WARN，绝不让循环停摆。</summary>
        private void SampleOnce(string reason)
        {
            try
            {
                Sample();
            }
            catch (Exception ex)
            {
                // WARN 而不是 ERROR：单轮采样失败（某个进程刚退出、句柄失效）
                // 下一秒就会自愈，它不构成「操作失败」，只是「这一轮没采全」。
                // 记 ERROR 会让日志页红一片，把真正的失败埋掉。
                _log.Warn($"进程快照失败（{reason}，本轮已跳过）: {ex.Message}");
            }
            finally
            {
                CompleteWaiters();
            }
        }

        /// <summary>停止后台采样。</summary>
        public void Stop()
        {
            Interlocked.Exchange(ref _loopRunning, 0);

            CancellationTokenSource? cts = _cts;
            _cts = null;

            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch
            {
                // 已释放等情况忽略
            }

            // 循环可能正挂在信号量上等待，光 Cancel 不会让它醒来，必须补一次信号。
            TryRelease(_sampleNowSignal);

            cts.Dispose();
        }

        private void TryRelease(SemaphoreSlim semaphore)
        {
            try
            {
                semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // 计数已满说明本来就有待消费的信号，无需再补
            }
        }

        /// <summary>
        /// 执行一次采样并写入快照。
        ///
        /// 需要「同步拿到结果」的调用方（启动补扫、查询）走
        /// <see cref="SampleOnSamplerThread"/>，不要直调本方法。
        /// </summary>
        public void Sample()
        {
            List<Entry> collected;
            try
            {
                collected = Collect();
            }
            catch (Exception ex)
            {
                _log.Warn($"进程快照失败（本次请求已跳过）: {ex.Message}");
                return;
            }

            lock (_gate)
            {
                _latest.Clear();
                _latest.AddRange(collected);
                _latestStamp = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// 在采样线程上同步跑一次采样，并等它完成。
        ///
        /// 给「启动补扫 / 查询」这类需要结果立刻可用的调用方用：它不该在调用
        /// 线程上直接跑（几百次 OpenProcess 会压到界面线程上）。
        /// </summary>
        public IReadOnlyList<Entry> SampleOnSamplerThread(TimeSpan timeout)
        {
            using ManualResetEventSlim done = new(false);

            // 把「采一帧 + 通知完成」打包成一个请求：请求队列每被消费一次，
            // 就说明采样线程已经处理到它了，此时 Latest 一定是最新的。
            lock (_pendingWaiters)
                _pendingWaiters.Enqueue(done);

            RequestSample();

            done.Wait(timeout);
            return Latest;
        }

        /// <summary>等待中的同步调用方。采样线程每完成一帧就把它们全部唤醒。</summary>
        private readonly Queue<ManualResetEventSlim> _pendingWaiters = new();

        private void CompleteWaiters()
        {
            lock (_pendingWaiters)
            {
                while (_pendingWaiters.Count > 0)
                {
                    try
                    {
                        _pendingWaiters.Dequeue().Set();
                    }
                    catch
                    {
                        // 调用方可能已经超时释放了事件，忽略
                    }
                }
            }
        }

        /// <summary>执行一次快照采集（单次 Toolhelp 遍历 + 按需查路径）。</summary>
        private List<Entry> Collect()
        {
            List<Entry> result = new();
            long now = Stopwatch.GetTimestamp();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(INVALID_HANDLE_VALUE))
                return result;

            try
            {
                PROCESSENTRY32 entry = new() { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };

                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        string name = ReadExeName(ref entry);
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name += ".exe";

                        int pid = (int)entry.th32ProcessID;

                        ProcSample sample;
                        lock (_sampleGate)
                            sample = GetOrCreateSample(pid);

                        double cpu = 0.0;
                        if (ReadProcessTimes(pid, sample))
                            cpu = ComputeCpu(sample, now);

                        ResolvePath(pid, sample, now);

                        result.Add(new Entry
                        {
                            Name = name,
                            Pid = pid,
                            Path = sample.Path,
                            Cpu = cpu,
                            PriorityClass = entry.pcPriClassBase
                        });
                    }
                    while (Process32NextW(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            PruneDeadSamples(result);

            return result;
        }

        /// <summary>
        /// 用滑动窗口算平均 CPU 占用（0-100）。
        ///
        /// 旧实现拿「本轮累计 - 上轮累计」除以「本轮时刻 - 上轮时刻」做两点差分：
        /// 采样间隔只要抖一下（GC、系统忙、定时器不准），算出来的百分比就会大幅跳动，
        /// 而列表默认按占用降序排，于是整张表每 2 秒重排一次，行不停跳位。
        ///
        /// 改成维护一个 30 秒窗口，每次用窗口里<b>最老的那一点</b>与当前比，
        /// 得到的是这整段时间的平均值，短时抖动被摊平，排序才站得住。
        /// </summary>
        private double ComputeCpu(ProcSample sample, long now)
        {
            long total = sample.KernelTicks + sample.UserTicks;
            sample.CpuWindow.Enqueue((now, total));

            while (sample.CpuWindow.Count > 0 && now - sample.CpuWindow.Peek().Stamp > CpuWindowTicks)
                sample.CpuWindow.Dequeue();

            while (sample.CpuWindow.Count > MaxWindowPoints)
                sample.CpuWindow.Dequeue();

            if (sample.CpuWindow.Count < 2)
                return 0.0;

            (long Stamp, long Ticks) oldest = sample.CpuWindow.Peek();

            long elapsedStamp = now - oldest.Stamp;
            long cpuTicks = total - oldest.Ticks;
            if (elapsedStamp <= 0 || cpuTicks <= 0)
                return 0.0;

            // FILETIME 的单位是 100ns，与 TimeSpan 的 tick 一致，这里显式换算。
            // 旧代码用 Stopwatch.Frequency 去除 FILETIME 计数 —— 在 Windows 上这两者
            // 恰好都是 10^7，所以「碰巧算对」，但那是巧合，不是定义。
            double usedSeconds = TimeSpan.FromTicks(cpuTicks).TotalSeconds;
            double elapsedSeconds = elapsedStamp / (double)Stopwatch.Frequency;
            if (elapsedSeconds <= 0)
                return 0.0;

            double pct = usedSeconds / elapsedSeconds / ProcessorCount * 100.0;

            return pct < 0 ? 0 : (pct > 100 ? 100 : pct);
        }

        /// <summary>
        /// 解析路径，失败后按 <see cref="PathRetrySeconds"/> 退避重试。
        ///
        /// 旧写法把 PathResolved 无条件置 true，查回来是空串也算「已解析」，
        /// 于是进程刚被创建的那一瞬间取不到路径，这条记录就永久留空 ——
        /// 界面上表现为大片「路径未记录」，而且重启软件前救不回来。
        ///
        /// 绝大多数失败是时序问题（进程刚创建、句柄还没准备好），不是权限问题，
        /// 所以失败后延迟重试通常第二次就成功；受保护进程（反作弊、PPL）属于真失败，
        /// 会按同样节奏重试但不纠缠到耗尽 CPU。
        /// </summary>
        private void ResolvePath(int pid, ProcSample sample, long now)
        {
            if (sample.PathResolved)
                return;

            if (sample.PathFailStamp != 0 && now - sample.PathFailStamp < PathRetryTicks)
                return;

            string path = QueryImagePath(pid);
            if (path.Length > 0)
            {
                sample.Path = path;
                sample.PathResolved = true;
                sample.PathFailStamp = 0;
            }
            else
            {
                sample.PathFailStamp = now;
            }
        }

        /// <summary>
        /// 清掉已退出进程的采样记录。
        ///
        /// 顺带解决 PID 复用：每个新 PID 都会拿到全新的 ProcSample，
        /// 不会继承上一个同 PID 进程的路径、失败状态或 CPU 窗口。
        /// </summary>
        private void PruneDeadSamples(List<Entry> result)
        {
            lock (_sampleGate)
            {
                // 每个 Entry 持有一个 30 秒的 CPU 采样窗口（最多 MaxWindowPoints 个点），
                // 进程退出后这份对象没人再引用它，留着就是纯泄漏。
                //
                // 旧阈值 4096 的问题：日常机器上同时存在的 PID 通常只有几百，
                // 永远到不了 4096，等于这段清理从不执行 —— 开机一天后 _samples 里
                // 会积压成千上万个已退出进程的空壳。改成「样本数明显多于本轮存活数」
                // 就扫一次，代价是每轮一次 HashSet 构建（几百个 int，微秒级）。
                if (_samples.Count <= result.Count)
                    return;

                // 一次遍历填存活集合、一次遍历收死亡 PID：省掉 Select / Where
                // 两层迭代器与它们各自的中间分配。
                HashSet<int> alive = new();
                foreach (Entry e in result)
                    alive.Add(e.Pid);

                List<int>? dead = null;
                foreach (int k in _samples.Keys)
                {
                    if (!alive.Contains(k))
                        (dead ??= new List<int>()).Add(k);
                }

                if (dead is not null)
                {
                    foreach (int k in dead)
                        _samples.Remove(k);
                }
            }
        }

        private ProcSample GetOrCreateSample(int pid)
        {
            if (!_samples.TryGetValue(pid, out ProcSample? s))
            {
                s = new ProcSample();
                _samples[pid] = s;
            }
            return s;
        }

        /// <summary>读取进程时间片（内核 + 用户）。</summary>
        private bool ReadProcessTimes(int pid, ProcSample sample)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
                return false;

            try
            {
                if (!GetProcessTimes(handle, out _, out _, out long kernel, out long user))
                    return false;

                sample.KernelTicks = kernel;
                sample.UserTicks = user;
                sample.StampTicks = Stopwatch.GetTimestamp();
                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>取进程可执行文件完整路径（权限不足时返回空串）。</summary>
        private unsafe string QueryImagePath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
                return string.Empty;

            try
            {
                // stackalloc 而不是 StringBuilder：源生成的 P/Invoke 不支持 StringBuilder，
                // 且这条路径在 Toolhelp 遍历里逐进程调用，栈分配免去每次一个托管缓冲区。
                const int Capacity = 2048;
                char* buffer = stackalloc char[Capacity];
                uint size = Capacity;

                if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size) || size == 0)
                    return string.Empty;

                return new string(buffer, 0, (int)size);
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
