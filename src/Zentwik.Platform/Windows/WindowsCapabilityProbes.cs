using System.Runtime.InteropServices;
using Zentwik.Domain.Capabilities;

namespace Zentwik.Platform.Windows;

/// <summary>只读能力探测。写入能力一律不宣称可用：API 存在不等于能安全写入。</summary>
public static class WindowsCapabilityProbes
{
    public static IEnumerable<ICapabilityProbe> Create(ISystemInfo info, ITelemetrySource telemetry) =>
    [
        new PowerPolicyProbe(),
        new TelemetryProbe(telemetry),
        new CpuSetsProbe(),
        new VendorDriverProbe(CapabilityIds.AmdSmu, "AMD", "RyzenSMU", info),
        new VendorDriverProbe(CapabilityIds.IntelMsr, "Intel", "IntelMSR", info),
    ];
}

internal sealed class PowerPolicyProbe : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.PowerPolicy;

    public CapabilityDescriptor Probe()
    {
        var active = new Win32PowerPolicyService().GetActiveScheme();
        return new CapabilityDescriptor
        {
            Id = CapabilityId,
            State = active is null ? CapabilityState.Failed : CapabilityState.Enabled,
            Detail = active is null
                ? "PowerGetActiveScheme 未返回活动方案。"
                : $"活动方案 {active.FriendlyName}；写入需要管理员权限。",
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

internal sealed class CpuSetsProbe : ICapabilityProbe
{
    public string CapabilityId => CapabilityIds.CpuSets;

    public CapabilityDescriptor Probe()
    {
        var available = NativeLibrary.TryLoad("kernel32.dll", out var module)
                        && NativeLibrary.TryGetExport(module, "SetProcessDefaultCpuSets", out _);

        return new CapabilityDescriptor
        {
            Id = CapabilityId,
            State = available ? CapabilityState.Installed : CapabilityState.Unavailable,
            Detail = available
                ? "API 可用；调度写入路径尚未实现。"
                : "当前系统不支持 SetProcessDefaultCpuSets。",
        };
    }
}

/// <summary>只探测设备能否打开，不代表读写已验证，因此最高只给 Degraded。</summary>
internal sealed class VendorDriverProbe(string capabilityId, string vendorToken, string module, ISystemInfo info)
    : ICapabilityProbe
{
    public string CapabilityId => capabilityId;

    public CapabilityDescriptor Probe()
    {
        var name = info.CpuName ?? string.Empty;
        if (!name.Contains(vendorToken, StringComparison.OrdinalIgnoreCase))
        {
            return new CapabilityDescriptor
            {
                Id = capabilityId,
                State = CapabilityState.Unavailable,
                Detail = $"当前处理器不是 {vendorToken}（{name}）。",
            };
        }

        var deviceOpen = PawnIoDevice.IsAvailable();
        return new CapabilityDescriptor
        {
            Id = capabilityId,
            State = deviceOpen ? CapabilityState.Degraded : CapabilityState.Unavailable,
            Detail = deviceOpen
                ? $"PawnIO 设备可打开（模块 {module}）；写入链路未验证。"
                : $"未检测到 PawnIO 驱动，{module} 模块不可用。",
        };
    }
}

internal static class PawnIoDevice
{
    private const uint OpenExisting = 3;
    private const uint ShareReadWrite = 0x00000003;

    public static bool IsAvailable()
    {
        var handle = Native.CreateFileW(@"\\.\PawnIO", 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle == Native.InvalidHandleValue)
        {
            return false;
        }

        Native.CloseHandle(handle);
        return true;
    }
}
