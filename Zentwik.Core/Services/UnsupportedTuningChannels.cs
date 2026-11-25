using System;
using System.Collections.Generic;
using System.Threading;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 平台不匹配时的 AMD 通道占位。状态如实报告「不适用」，
    /// 写入返回明确原因，而不是让上层以为「驱动没装」。
    /// </summary>
    public sealed class UnsupportedAmdTuningChannel : IAmdTuningChannel
    {
        public UnsupportedAmdTuningChannel(string? reason = null)
        {
            LastError = reason ?? "当前处理器不是 AMD，SMU 通道不适用";
        }

        public CpuVendor Vendor => CpuVendor.Amd;

        public TuningChannelStatus Status => TuningChannelStatus.NotSupported;

        public bool IsAvailable => false;

        public string? LastError { get; }

        public int CoreCount => 0;

        public bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default) => false;

        public SmuSnapshot ReadSnapshot() => new() { Success = false, Error = LastError };

        public SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins) => new() { Fatal = LastError };

        public SmuFMaxResult WriteFMax(uint mhz) => new() { Fatal = LastError, Requested = mhz };

        public void Dispose()
        {
        }
    }

    /// <summary>平台不匹配时的 Intel 通道占位。语义同 <see cref="UnsupportedAmdTuningChannel"/>。</summary>
    public sealed class UnsupportedIntelTuningChannel : IIntelTuningChannel
    {
        public UnsupportedIntelTuningChannel(string? reason = null)
        {
            LastError = reason ?? "当前处理器不是 Intel，MSR 通道不适用";
        }

        public CpuVendor Vendor => CpuVendor.Intel;

        public TuningChannelStatus Status => TuningChannelStatus.NotSupported;

        public bool IsAvailable => false;

        public string? LastError { get; }

        public bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default) => false;

        public IntelTuningSnapshot ReadIntelTuning() => new() { Success = false, Error = LastError };

        public IntelTuningResult ApplyIntelTuning(IntelTuningRequest request) => new() { Fatal = LastError };

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// AMD CCD 宽度提示。只在 SMU 已探测到值之后才给出 ——
    /// 未探测时返回 false，调度按 L3 缓存域分组，不拿兜底值冒充事实。
    /// </summary>
    public sealed class AmdCoreGroupHintProvider : ICoreGroupHintProvider
    {
        private readonly SmuReader _smu;

        public AmdCoreGroupHintProvider(SmuReader smu)
        {
            ArgumentNullException.ThrowIfNull(smu);
            _smu = smu;
        }

        public bool TryGetCoresPerGroup(out int cores)
        {
            uint raw = _smu.PeekCoresPerCcdRaw();
            cores = raw > 0 && raw <= int.MaxValue ? (int)raw : 0;
            return cores > 0;
        }
    }
}
