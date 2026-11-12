using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Zentwik.Core.Helpers;
using Zentwik.Core.Smu;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// SMU 服务：只读探测 + 受控写入。
    /// 写入后立即回读校验；SMU 未就绪时所有读写短路返回，不抛异常。
    /// </summary>
    public sealed class SmuReader : ISmuReader
    {
        private readonly ILogService _log;

        private SmuSession? _session;

        private uint _coresPerCcd;
        private int _coreCount;

        /// <summary>SMU 邮箱是进程级硬件资源，锁不随实例走。</summary>
        private static readonly object _syncRoot = new();

        /// <summary>PawnIO 驱动与管理员权限是否就绪。</summary>
        public bool IsAvailable { get; private set; }

        /// <summary>初始化或读取失败的原因。</summary>
        public string? LastError { get; private set; }

        /// <summary>探测到的物理核心数（未探测时为 0）。</summary>
        public int CoreCount => _coreCount;

        /// <summary>每 CCD 的核心数（未探测时回退为 8，安全默认值）。</summary>
        public uint CoresPerCcd => _coresPerCcd == 0 ? 8u : _coresPerCcd;

        /// <summary>
        /// 每 CCD 核心数的 **原始探测值**：0 表示还没探测到。
        /// 需要区分「真的读到 8 核/CCD」和「没读到所以兜底 8」时必须用这个，
        /// 否则任何拓扑推断都会把兜底值当成事实。
        /// </summary>
        public uint CoresPerCcdRaw => _coresPerCcd;

        /// <summary>本进程累计创建的会话数，正常为 1。</summary>
        private static int _createdCount;

        internal static int CreatedCount => Volatile.Read(ref _createdCount);

        /// <summary>上一次探测失败的时间戳（Environment.TickCount64）。</summary>
        private long _lastFailureTick = long.MinValue / 2;

        /// <summary>失败后的重试冷却（毫秒）。避免每次访问都发一遍 SMU 测试消息。</summary>
        private const int RetryCooldownMs = 2000;

        /// <summary>
        /// 只读取已探测到的每 CCD 核心数，不触发探测。
        /// 供拓扑推断这类「拿不到也能自己兜底」的场景使用。
        /// </summary>
        public uint PeekCoresPerCcdRaw()
        {
            lock (_syncRoot)
            {
                return IsAvailable ? _coresPerCcd : 0u;
            }
        }

        /// <summary>
        /// 创建实例并立即探测。PawnIO 驱动缺失或未提权时不抛异常，
        /// 实例以 <see cref="IsAvailable"/> = false 返回，原因在 <see cref="LastError"/>。
        /// </summary>
        public SmuReader(ILogService log)
        {
            ArgumentNullException.ThrowIfNull(log);

            _log = log;
            Initialize();
        }

        private static string ModulePath => Path.Combine(AppContext.BaseDirectory, "PawnIO", "RyzenSMU.bin");

        private void Initialize()
        {
            try
            {
                _session = SmuSession.TryOpen(ModulePath);
                Interlocked.Increment(ref _createdCount);

                if (_session is null)
                {
                    IsAvailable = false;
                    LastError = "PawnIO 设备不可用（驱动未安装或无管理员权限）";
                    return;
                }

                bool responding;
                try
                {
                    responding = _session.TryTestMessage();
                }
                catch (Exception ex)
                {
                    responding = false;
                    LastError = ex.Message;
                }

                if (!responding)
                {
                    IsAvailable = false;
                    LastError ??= "SMU 未应答测试消息（驱动可能尚未就绪）";
                    _log.Warn(LastError);
                    return;
                }

                IsAvailable = true;
                LastError = null;
                _log.Info($"SMU 初始化成功：{_session.Cpu.Name} ({_session.Cpu.CodeName})");
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                LastError = ex.Message;
                _log.Error($"SMU 初始化失败: {ex}");
            }
        }

        /// <summary>等待 PawnIO 驱动就绪（自启 / 静默场景专用）。</summary>
        /// <param name="maxSeconds">最长等待秒数（默认 60）。</param>
        /// <param name="token">取消令牌：关机或用户放弃时可中断等待，不拖住后台线程。</param>
        public bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default)
        {
            if (IsAvailable)
                return true;

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSeconds)
            {
                if (token.IsCancellationRequested)
                    return false;

                // 清掉冷却时间戳，立刻重发一次测试消息。
                // 只是重新探测，不重建 Cpu、不 Dispose 任何东西。
                Interlocked.Exchange(ref _lastFailureTick, long.MinValue / 2);
                RetryAvailabilityProbe();

                if (IsAvailable)
                    return true;

                // 用可取消的等待代替 Thread.Sleep：驱动常在几十毫秒内就绪，
                // 固定睡满 1 秒会让首次写入白白延后。250ms 轮询兼顾响应与开销。
                if (token.WaitHandle.WaitOne(250))
                    return false;
            }

            _log.Error($"PawnIO 未加载（{maxSeconds}s 内未就绪）");
            return false;
        }

        /// <summary>
        /// 重新探测可用性。若之前连 Cpu 都没建起来（驱动未就绪），这次补建；
        /// 若已经有 Cpu 对象，只补发一次测试消息。
        /// </summary>
        private void RetryAvailabilityProbe()
        {
            if (_session is null)
            {
                _session = SmuSession.TryOpen(ModulePath);
                Interlocked.Increment(ref _createdCount);

                if (_session is null)
                {
                    LastError = "PawnIO 设备不可用";
                    return;
                }
            }

            try
            {
                if (_session.TryTestMessage())
                {
                    IsAvailable = true;
                    LastError = null;
                    return;
                }

                LastError = "SMU 未应答测试消息";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        /// <summary>执行一次完整的只读探测。</summary>
        public SmuSnapshot ReadSnapshot()
        {
            SmuSnapshot snapshot = new();
            if (_session is null || !IsAvailable)
            {
                snapshot.Success = false;
                snapshot.Error = LastError ?? "SMU 未初始化";
                return snapshot;
            }

            // 与遥测刷新 / 写入串行化，避免争抢 PCI 总线
            lock (_syncRoot)
            {
                return ReadSnapshotCore(snapshot);
            }
        }

        private SmuSnapshot ReadSnapshotCore(SmuSnapshot snapshot)
        {
            try
            {
                SmuSession session = _session!;
                CpuIdentity info = session.Cpu;

                snapshot.CpuName = info.Name;
                snapshot.CodeName = info.CodeName;
                snapshot.Family = $"0x{info.FamilyId:X2}";
                snapshot.SmuType = info.Family.ToString();
                snapshot.Ccds = (uint)info.Ccds;
                snapshot.Cores = (uint)info.LogicalCores;
                snapshot.PhysicalCores = (uint)info.PhysicalCores;
                snapshot.ThreadsPerCore = (uint)info.ThreadsPerCore;

                snapshot.SmuVersion = SmuVersionString();

                _log.Info(
                    $"CPU: {snapshot.CpuName} | {snapshot.CodeName} | {snapshot.Family} | " +
                    $"SMU {snapshot.SmuVersion} ({snapshot.SmuType}) | " +
                    $"{snapshot.Ccds} CCD / {snapshot.Cores} 核 / {snapshot.ThreadsPerCore} 线程每核");

                uint coresPerCcd = (uint)info.CoresPerCcd;
                if (coresPerCcd == 0)
                    coresPerCcd = 8;

                int coreCount = info.PhysicalCores;
                snapshot.CoreMargins = new int[coreCount];
                int okCount = 0;
                for (int i = 0; i < coreCount; i++)
                {
                    (uint core, uint ccd, uint ccx) = SplitCore(i);
                    uint? margin = session.GetMargin(core, ccd, ccx);
                    if (margin.HasValue)
                    {
                        // SMU 返回无符号计数，取负视角即为 CO 值（负数 = 降压）
                        snapshot.CoreMargins[i] = unchecked((int)margin.Value);
                        okCount++;
                    }
                    else
                    {
                        snapshot.CoreMargins[i] = int.MinValue; // 读取失败或被熔断禁用
                    }
                }

                _coresPerCcd = coresPerCcd;
                _coreCount = coreCount;
                snapshot.CoresPerCcd = coresPerCcd;
                _log.Info($"Curve Optimizer 读取完成：{okCount}/{coreCount} 个核心返回有效值");
                _log.Info($"核心拓扑：每 CCD {coresPerCcd} 核，共 {coreCount} 物理核心");

                snapshot.FMaxMHz = session.GetFMax();
                _log.Info($"FMax 当前上限：{snapshot.FMaxMHz} MHz");

                snapshot.Success = true;
            }
            catch (Exception ex)
            {
                snapshot.Success = false;
                snapshot.Error = ex.Message;
                _log.Error($"SMU 只读探测失败: {ex}");
            }

            return snapshot;
        }

        // 受控写入

        /// <summary>
        /// CO 允许范围：[-50, +50]。
        /// 负值 = 降压（undervolt），正值 = 加压（overvolt）。
        /// 该范围仅为“输入合法性”边界，不再作为安全拦截：用户对其硬件负全责。
        /// </summary>
        public const int CoMin = -50;
        public const int CoMax = 50;

        /// <summary>FMax 允许范围（MHz）。硬件步进约 25 MHz，范围取 [4450, 5650]。</summary>
        public const uint FMaxMin = 4450;
        public const uint FMaxMax = 5650;

        /// <summary>
        /// FMax 回读容差（MHz）。SMU 按硬件步进量化写入值，回读与请求未必严格相等，
        /// 故允许 ≤10 MHz 的偏差（严格按硬件步进放宽，防止实际没写进去却误判成功）。
        /// </summary>
        public const int FMaxReadTolerance = 10;

        /// <summary>
        /// 写入每核 Curve Optimizer（负压偏移）。
        /// 调用方应已通过 <see cref="TuningStore"/> 的范围钳制，这里对越界值再做二次拒绝。
        /// 每个核心写入后立即回读校验。
        /// </summary>
        public SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins)
        {
            SmuWriteResult result = new();
            if (_session is null || !IsAvailable)
            {
                result.Fatal = LastError ?? "SMU 未初始化";
                return result;
            }

            // 写入期间独占总线：绝不能让 2 秒遥测定时器插进来读寄存器，
            // 否则可能读到写了一半的状态，也与回读校验互相干扰。
            lock (_syncRoot)
            {
                return WriteCurveOptimizerCore(margins, result);
            }
        }

        private SmuWriteResult WriteCurveOptimizerCore(IReadOnlyList<int> margins, SmuWriteResult result)
        {
            SmuSession session = _session!;
            int count = _coreCount > 0 ? Math.Min(margins.Count, _coreCount) : margins.Count;

            _log.Info(
                $"CO 写入开始：{count} 核，每 CCD {CoresPerCcd} 核，" +
                $"平台 {session.Cpu.Family}，SMU 版本 {SmuVersionString()}");
            for (int i = 0; i < count; i++)
            {
                int value = margins[i];

                // int.MinValue 表示“跳过该核心”（熔断 / 读取失败 / 不存在），不下发
                if (value == int.MinValue)
                    continue;

                // 输入合法性边界：[-50, +50]（含正值加压）。仅拦截明显的输入错误，
                // 不再限制在固定的“安全降压区间”，用户对自己的硬件负全责。
                if (value < CoMin || value > CoMax)
                {
                    result.WriteFailed.Add(i);
                    result.RejectedReasons[i] = $"越界({value})";
                    _log.Error($"核心 {i} CO 值 {value} 越界，已拒绝下发");
                    continue;
                }

                (uint core, uint ccd, uint ccx) = SplitCore(i);
                bool accepted = false;
                uint coreMask = 0;
                try
                {
                    coreMask = session.MakeCoreMask(core, ccd, ccx);
                    accepted = session.SetMargin(core, ccd, ccx, value);
                }
                catch (Exception ex)
                {
                    _log.Error($"SetMargin 核心 {i} 异常: {ex}");
                }

                _log.Info(
                    $"  核 {i,2} → core={core} ccd={ccd} ccx={ccx} mask=0x{coreMask:X8} " +
                    $"值={value} 接受={accepted}");

                if (!accepted)
                {
                    result.WriteFailed.Add(i);
                    _log.Error($"核心 {i} CO 写入被 SMU 拒绝（值 {value}）");
                    continue;
                }

                // 回读校验：SMU 返回无符号计数，按 2 补码解释为负值视角
                int readBack = int.MinValue;
                try
                {
                    uint? rb = session.GetMargin(core, ccd, ccx);
                    if (rb.HasValue)
                        readBack = unchecked((int)rb.Value);
                }
                catch (Exception ex)
                {
                    _log.Warn($"GetMargin 回读 核心 {i} 异常: {ex.Message}");
                }

                if (readBack != value)
                {
                    result.VerifyFailed.Add(i);
                    result.VerifiedValue[i] = readBack;
                    _log.Error($"核心 {i} CO 回读不一致：写入 {value}，回读 {readBack}");
                }
                else
                {
                    result.Applied[i] = value;
                }
            }

            result.AllSucceeded = result.WriteFailed.Count == 0 && result.VerifyFailed.Count == 0;
            return result;
        }

        /// <summary>
        /// 写入 FMax 频率上限（MHz）。写入后立即回读校验。
        /// </summary>
        public SmuFMaxResult WriteFMax(uint mhz)
        {
            SmuFMaxResult result = new() { Requested = mhz };
            if (_session is null || !IsAvailable)
            {
                result.Fatal = LastError ?? "SMU 未初始化";
                return result;
            }

            // 二次安全断言：越界值绝不下发
            if (mhz < FMaxMin || mhz > FMaxMax)
            {
                result.Fatal = $"FMax 越界 {mhz}（允许 {FMaxMin}-{FMaxMax}）";
                _log.Error(result.Fatal);
                return result;
            }

            try
            {
                SmuSession session = _session;
                lock (_syncRoot)
                {
                    // 写入前先读一次基线：如果基线本身就是 0，说明这台机器的
                    // FMax 读取通道不可用（部分移动平台 SMU 不实现该消息），
                    // 那么「写入后回读 0」不能当作写入失败 —— 要能区分这两件事。
                    uint before = session.GetFMax();
                    _log.Info($"FMax 写入前基线：{before} MHz");

                    result.SmuAccepted = session.SetFMax(mhz);
                    result.ReadBack = session.GetFMax();

                    _log.Info(
                        $"FMax 写入：请求 {mhz}，SMU 接受 {result.SmuAccepted}，" +
                        $"回读 {result.ReadBack}（基线 {before}）");
                }

                // 回读容差：SMU 按硬件步进量化，允许 ≤ FMaxReadTolerance MHz 的偏差；
                // 偏差过大视为实际未写进去，仍判失败（防止误判成功）。
                result.AllSucceeded = result.SmuAccepted &&
                    Math.Abs((long)result.ReadBack - (long)mhz) <= FMaxReadTolerance;
                if (!result.AllSucceeded)
                    _log.Error($"FMax 回读不一致：请求 {mhz}，SMU 接受 {result.SmuAccepted}，回读 {result.ReadBack}");
            }
            catch (Exception ex)
            {
                result.Fatal = ex.Message;
                _log.Error($"SetFMax 异常: {ex}");
            }

            return result;
        }

        /// <summary>
        /// 把「线性核序号」拆成库要求的 (core, ccd, ccx) 三元组。
        ///
        /// 掩码编码必须交给库的 MakeCoreMask —— 它按 SMU 型号区分 APU / 桌面 /
        /// EPYC 的分支，自己按「标准公式」手搓会漏掉这些分支，导致 SMU 直接拒收。
        /// （0.6.x 就是这么写的：读能读到值，写全被拒，16 核无一例外。）
        ///
        /// ccx 取 0：Zen 3 之后消费级平台是 CCD 内含单个 CCX，
        /// corners 全部落在 ccd 维度；EPYC 那种一 CCD 多 CCX 的形态不在本工具范围。
        /// </summary>
        private (uint Core, uint Ccd, uint Ccx) SplitCore(int coreIndex)
        {
            int perCcd = CoresPerCcd > 0 ? (int)CoresPerCcd : 8;
            uint ccd = (uint)(coreIndex / perCcd);
            uint local = (uint)(coreIndex % perCcd);
            return (local, ccd, 0);
        }

        private string SmuVersionString()
            => _session is not null && _session.TryGetVersion(out uint version) && version != 0
                ? $"0x{version:X}"
                : "未知";

        /// <summary>DI 单例，实例生命周期等于进程；退出期不关闭设备，避免并发访问抛异常。</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>一次只读探测的结果快照。</summary>
    public sealed class SmuSnapshot
    {
        public bool Success { get; set; }
        public string? Error { get; set; }

        public string CpuName { get; set; } = string.Empty;
        public string CodeName { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string SmuType { get; set; } = string.Empty;
        public string SmuVersion { get; set; } = string.Empty;
        public uint Ccds { get; set; }
        public uint Cores { get; set; }
        public uint PhysicalCores { get; set; }
        public uint ThreadsPerCore { get; set; }
        public uint CoresPerCcd { get; set; }

        /// <summary>每核 CO 值（int.MinValue = 读取失败/禁用）。</summary>
        public int[] CoreMargins { get; set; } = Array.Empty<int>();

        /// <summary>当前 FMax 上限（MHz，0 = 未知）。</summary>
        public uint FMaxMHz { get; set; }
    }

    /// <summary>Curve Optimizer 写入结果（含逐核回读校验）。</summary>
    public sealed class SmuWriteResult
    {
        /// <summary>全部核心均写入成功且回读一致。</summary>
        public bool AllSucceeded { get; set; }

        /// <summary>致命错误（如 SMU 未初始化），此时不会有任何核心被写入。</summary>
        public string? Fatal { get; set; }

        /// <summary>SMU 拒绝写入（SetPsmMarginSingleCore 返回 false）或越界被拦截的核心索引。</summary>
        public List<int> WriteFailed { get; } = new();

        /// <summary>写入成功但回读值与目标不一致的核心索引。</summary>
        public List<int> VerifyFailed { get; } = new();

        /// <summary>成功写入并回读一致的核心 → 值。</summary>
        public Dictionary<int, int> Applied { get; } = new();

        /// <summary>回读不一致的核心 → 实际回读值。</summary>
        public Dictionary<int, int> VerifiedValue { get; } = new();

        /// <summary>被拦截（越界等）的核心 → 原因。</summary>
        public Dictionary<int, string> RejectedReasons { get; } = new();
    }

    /// <summary>FMax 写入结果（含回读校验）。</summary>
    public sealed class SmuFMaxResult
    {
        public bool AllSucceeded { get; set; }
        public bool SmuAccepted { get; set; }
        public uint Requested { get; set; }
        public uint ReadBack { get; set; }
        public string? Fatal { get; set; }
    }
}
