using System.Collections.Generic;
using System.Threading;

namespace Zentwik.Core.Services
{
    /// <summary>调校通道的可用性。区分「平台不匹配」与「环境没起来」是本类型的唯一目的。</summary>
    public enum TuningChannelStatus
    {
        /// <summary>尚未探测。</summary>
        Unknown = 0,

        /// <summary>通道就绪，可读可写。</summary>
        Ready = 1,

        /// <summary>当前处理器不属于该通道服务的厂商，通道不适用。</summary>
        NotSupported = 2,

        /// <summary>PawnIO 设备打不开：驱动未安装、模块文件缺失或无管理员权限。</summary>
        DriverUnavailable = 3,

        /// <summary>设备已打开但硬件未应答或读取失败。</summary>
        ProbeFailed = 4
    }

    /// <summary>
    /// 调校通道的公共状态面。只含状态与等待，不含任何厂商专属读写方法 ——
    /// 厂商方法留在各自的接口上，避免出现同时带两套数据结构的「统一通道」。
    /// </summary>
    public interface ITuningChannel : System.IDisposable
    {
        /// <summary>该通道服务的处理器厂商。</summary>
        CpuVendor Vendor { get; }

        TuningChannelStatus Status { get; }

        /// <summary>通道就绪（等价于 <see cref="Status"/> == <see cref="TuningChannelStatus.Ready"/>）。</summary>
        bool IsAvailable { get; }

        /// <summary>不可用原因原文；就绪时为 null。</summary>
        string? LastError { get; }

        /// <summary>
        /// 等待驱动就绪，期间只重发测试消息、不重建实例。
        /// 返回 false 表示超时、被取消或该通道本就不适用。
        /// </summary>
        bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default);
    }

    /// <summary>
    /// AMD SMU 通道。CO 逐核偏移与 FMax 上限是 SMU 固件消息，
    /// 与 Intel 的 MSR 通道没有共同数据结构，因此不放进同一个接口。
    /// </summary>
    public interface IAmdTuningChannel : ITuningChannel
    {
        /// <summary>探测到的物理核心数（未探测时为 0）。</summary>
        int CoreCount { get; }

        /// <summary>执行一次完整的只读探测（CO 逐核 + FMax）。</summary>
        SmuSnapshot ReadSnapshot();

        /// <summary>写入每核 Curve Optimizer；<c>int.MinValue</c> 表示跳过该核心。</summary>
        SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins);

        /// <summary>写入 FMax 上限（MHz）。</summary>
        SmuFMaxResult WriteFMax(uint mhz);
    }

    /// <summary>
    /// Intel MSR 通道。电压三平面与睿频倍率走 OC Mailbox，
    /// 不提供 CO 数组或 FMax 概念。
    /// </summary>
    public interface IIntelTuningChannel : ITuningChannel
    {
        /// <summary>读取电压偏移与睿频上限；通道未就绪时返回失败快照。</summary>
        IntelTuningSnapshot ReadIntelTuning();

        /// <summary>写入调校值；<c>RestoreBaseline</c> 为真时回到首次读到的值。</summary>
        IntelTuningResult ApplyIntelTuning(IntelTuningRequest request);
    }
}
