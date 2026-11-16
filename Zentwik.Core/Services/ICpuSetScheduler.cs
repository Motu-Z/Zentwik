using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// CPU Sets 绑定。实现方缓存逻辑处理器拓扑，首次访问时探测。
    /// </summary>
    public interface ICpuSetScheduler
    {
        /// <summary>最近一次拓扑探测失败的原因；成功时为 null。</summary>
        string? LastTopologyError { get; }

        /// <summary>逻辑处理器数量（未探测到时为 0）。</summary>
        int LogicalProcessorCount { get; }

        /// <summary>核心组（L3 缓存域）列表，拓扑不可用时为空。</summary>
        IReadOnlyList<CpuCoreGroup> CoreGroups { get; }

        /// <summary>丢弃已缓存的拓扑，下次访问重新探测。</summary>
        void ResetTopology();

        CpuSetScheduleResult ScheduleToMask(int processId, ulong mask);

        /// <summary>按逻辑核掩码写进程的关联掩码（传统亲和性）。</summary>
        CpuSetScheduleResult ScheduleToAffinityMask(int processId, ulong mask);

        /// <summary>多处理器组时为 false：关联掩码只能覆盖处理器组 0。</summary>
        bool IsAffinityMaskSupported { get; }

        CpuSetScheduleResult ScheduleToCcd(int processId, int ccdIndex);

        bool ClearDefaultCpuSets(int processId, out string? error);

        /// <summary>把进程的关联掩码还原成系统掩码。</summary>
        bool ClearAffinityMask(int processId, out string? error);

        bool IsCcdAvailable(int ccdIndex);
    }
}
