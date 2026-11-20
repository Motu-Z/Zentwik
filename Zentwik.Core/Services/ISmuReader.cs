using System;
using System.Collections.Generic;
using System.Threading;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// SMU 访问通道。实现方保证进程内只有一个 PawnIO 会话。
    /// </summary>
    public interface ISmuReader : IDisposable
    {
        /// <summary>PawnIO 驱动与管理员权限是否就绪。</summary>
        bool IsAvailable { get; }

        string? LastError { get; }

        int CoreCount { get; }

        uint CoresPerCcd { get; }

        /// <summary>原始探测值；0 表示尚未探测到（与「真的读到 8」区分开）。</summary>
        uint CoresPerCcdRaw { get; }

        /// <summary>只读取已探测到的值，不触发探测。</summary>
        uint PeekCoresPerCcdRaw();

        /// <summary>执行一次完整的只读探测。</summary>
        SmuSnapshot ReadSnapshot();

        SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins);

        SmuFMaxResult WriteFMax(uint mhz);

        /// <summary>
        /// 等待驱动就绪，期间只重发测试消息、不重建实例。
        /// 返回 false 表示超时或被取消。
        /// </summary>
        bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default);

        /// <summary>处理器厂商是否为 Intel。Intel 平台走 MSR 通道，不用 SMU。</summary>
        bool IsIntel { get; }

        /// <summary>Intel MSR 调校通道是否就绪（模块已加载且可读）。</summary>
        bool IsIntelTuningAvailable { get; }

        /// <summary>读取 Intel 电压偏移与睿频上限；通道未就绪时返回失败快照。</summary>
        IntelTuningSnapshot ReadIntelTuning();

        /// <summary>写入 Intel 调校值；<c>RestoreBaseline</c> 为真时回到首次读到的值。</summary>
        IntelTuningResult ApplyIntelTuning(IntelTuningRequest request);

        /// <summary>该平台是否有可用的功耗限制命令组（AMD 专用）。</summary>
        bool IsPowerLimitSupported { get; }

        /// <summary>读取当前的持续、短时与平均功耗上限。</summary>
        PowerLimitSnapshot ReadPowerLimits();

        /// <summary>写入功耗上限并回读校验；null 表示该项不动。</summary>
        PowerLimitWriteResult ApplyPowerLimits(int? stapmMilliWatts, int? fastMilliWatts, int? slowMilliWatts);
    }
}
