using Zentwik.Domain.Capabilities;

namespace Zentwik.Platform.Windows;

/// <summary>只读能力探测。写入能力一律不宣称可用：API 存在不等于能安全写入。</summary>
public static class WindowsCapabilityProbes
{
    public static IEnumerable<ICapabilityProbe> Create(
        ISystemInfo info,
        ITelemetrySource telemetry,
        ICpuTuningProvider tuning,
        IIntelTuningProvider intel,
        IProcessScheduler scheduler,
        IPowerPolicyService power,
        IPowerLimitProvider? powerLimits = null,
        Func<bool>? powerLimitsEnabled = null)
    {
        yield return new PowerPolicyProbe(power);
        yield return new TelemetryProbe(telemetry);
        yield return new CpuTuningProbe(tuning);
        yield return new SchedulingProbe();
        yield return new IntelMsrProbe(intel);
        yield return new SceneEngineProbe();

        if (powerLimits is not null)
        {
            yield return new PowerLimitProbe(powerLimits, powerLimitsEnabled ?? (static () => true));
        }
    }
}

internal sealed class PowerPolicyProbe(IPowerPolicyService power) : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.PowerPolicy;

    public CapabilityDescriptor Probe()
    {
        var active = power.GetActiveScheme();
        return new CapabilityDescriptor
        {
            Id = CapabilityId,
            State = active is null ? CapabilityState.Failed : CapabilityState.Enabled,
            Detail = active is null
                ? "PowerGetActiveScheme 未返回活动方案。"
                : $"活动方案 {active.FriendlyName}；切换与写入需要管理员权限。",
        };
    }
}

internal sealed class TelemetryProbe(ITelemetrySource telemetry) : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.TelemetryBasic;

    public CapabilityDescriptor Probe()
    {
        try
        {
            var sample = telemetry.Sample();
            return new CapabilityDescriptor
            {
                Id = CapabilityId,
                State = CapabilityState.Running,
                Detail = $"{sample.ProcessorCount} 逻辑处理器，采样正常。",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new CapabilityDescriptor
            {
                Id = CapabilityId,
                State = CapabilityState.Failed,
                Detail = ex.Message,
            };
        }
    }
}

internal sealed class CpuTuningProbe(ICpuTuningProvider tuning) : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.AmdSmu;

    public CapabilityDescriptor Probe()
    {
        var status = tuning.Query();
        var state = status.Availability switch
        {
            HardwareAvailability.Available => CapabilityState.Running,
            HardwareAvailability.ProbeFailed or HardwareAvailability.Rejected => CapabilityState.Failed,
            HardwareAvailability.DriverUnavailable
                or HardwareAvailability.ModuleMissing
                or HardwareAvailability.PrivilegeMissing => CapabilityState.Installed,
            _ => CapabilityState.Unavailable,
        };

        return new CapabilityDescriptor
        {
            Id = CapabilityId,
            State = state,
            Detail = status.Simulated ? $"模拟：{status.Detail}" : status.Detail,
        };
    }
}

internal sealed class PowerLimitProbe(IPowerLimitProvider powerLimits, Func<bool> enabled) : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.AmdPowerLimit;

    public CapabilityDescriptor Probe()
    {
        if (!enabled())
        {
            return new CapabilityDescriptor
            {
                Id = CapabilityId,
                State = CapabilityState.Unavailable,
                Detail = "可选组件，未启用。",
            };
        }

        var (availability, detail) = powerLimits.ProbePowerLimit();
        var state = availability switch
        {
            HardwareAvailability.Available => CapabilityState.Running,
            HardwareAvailability.ProbeFailed or HardwareAvailability.Rejected => CapabilityState.Failed,
            HardwareAvailability.DriverUnavailable
                or HardwareAvailability.ModuleMissing
                or HardwareAvailability.PrivilegeMissing => CapabilityState.Installed,
            _ => CapabilityState.Unavailable,
        };

        return new CapabilityDescriptor
        {
            Id = CapabilityId,
            State = state,
            Detail = detail,
        };
    }
}

internal sealed class SchedulingProbe : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.AffinityMask;

    public CapabilityDescriptor Probe() => new()
    {
        Id = CapabilityId,
        State = CapabilityState.Enabled,
        Detail = "亲和性与优先级可用；超过 64 个逻辑处理器时受处理器组限制。",
    };
}

internal sealed class IntelMsrProbe(IIntelTuningProvider intel) : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.IntelMsr;

    public CapabilityDescriptor Probe()
    {
        var status = intel.Query();
        var state = status.Availability switch
        {
            // 与 AMD 探针同口径：能读写就是运行中，不因厂商不同返回另一种语义。
            HardwareAvailability.Available => CapabilityState.Running,
            HardwareAvailability.ProbeFailed or HardwareAvailability.Rejected => CapabilityState.Failed,
            HardwareAvailability.DriverUnavailable
                or HardwareAvailability.ModuleMissing
                or HardwareAvailability.PrivilegeMissing => CapabilityState.Installed,
            _ => CapabilityState.Unavailable,
        };

        return new CapabilityDescriptor
        {
            Id = CapabilityId,
            State = state,
            Detail = status.Simulated ? $"模拟：{status.Detail}" : status.Detail,
        };
    }
}

internal sealed class SceneEngineProbe : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.SceneEngine;

    public CapabilityDescriptor Probe() => new()
    {
        Id = CapabilityId,
        State = CapabilityState.Running,
        Detail = "触发评估、优先级仲裁与账本回退已启用。",
    };
}
