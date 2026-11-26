using Zentwik.Domain.Capabilities;

namespace Zentwik.Platform.Windows;

public sealed class CpuSetsCapabilityProbe(IProcessScheduler scheduler) : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.CpuSets;

    public CapabilityDescriptor Probe()
    {
        if (scheduler is not IProcessCpuSetScheduler cpuSets)
        {
            return new CapabilityDescriptor
            {
                Id = CapabilityId,
                State = CapabilityState.Unavailable,
                Detail = "当前调度器未实现 CPU Sets 接口。",
            };
        }

        var supported = cpuSets.IsCpuSetsSupported(out var detail);
        return new CapabilityDescriptor
        {
            Id = CapabilityId,
            State = supported ? CapabilityState.Enabled : CapabilityState.Unavailable,
            Detail = detail ?? (supported ? "CPU Sets 可用。" : "CPU Sets 不可用。"),
        };
    }
}