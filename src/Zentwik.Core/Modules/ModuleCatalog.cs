using Zentwik.Contracts.Dto;
using Zentwik.Domain.Capabilities;

namespace Zentwik.Core.Modules;

public sealed record ModuleDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Capabilities,
    string Purpose,
    bool AnyOf = false);

/// <summary>内置模块清单。模块描述能力组合，不引入独立加载器：当前没有第三方插件。</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDefinition> Definitions { get; } =
    [
        new("monitoring", "监控", [CapabilityIds.TelemetryBasic], "CPU、内存与进程计数采样"),
        new("power", "电源", [CapabilityIds.PowerPolicy], "电源方案切换与策略参数写入"),
        // CPU 调校同时列 AMD 与 Intel 两套实现，同一时刻只有一方适用；另一厂商缺席不是降级。
        new("cpu-tuning", "CPU 调校", [CapabilityIds.AmdSmu, CapabilityIds.IntelMsr], "Curve Optimizer 与 MSR 调校", AnyOf: true),
        new("scheduling", "进程调度", [CapabilityIds.AffinityMask, CapabilityIds.CpuSets], "进程亲和性、优先级与 CPU Sets 调度"),
        new("automation", "自动化", [CapabilityIds.SceneEngine], "场景触发、仲裁与状态回退"),
        new("diagnostics", "诊断", [], "能力探测、日志与状态对账"),
    ];

    public static IReadOnlyList<ModuleDto> Describe(IReadOnlyList<CapabilityDescriptor> capabilities, bool simulated)
    {
        var byId = capabilities.ToDictionary(static capability => capability.Id, StringComparer.Ordinal);
        var modules = new List<ModuleDto>(Definitions.Count);

        foreach (var definition in Definitions)
        {
            var states = definition.Capabilities
                .Select(id => byId.GetValueOrDefault(id)?.State ?? CapabilityState.Unavailable)
                .ToArray();

            var state = definition.AnyOf ? AggregateAny(states) : AggregateAll(states);

            var detail = string.Join(
                "；",
                definition.Capabilities
                    .Select(id => byId.GetValueOrDefault(id))
                    .Where(capability => capability is not null
                                         && (!definition.AnyOf || capability.State != CapabilityState.Unavailable))
                    .Select(static capability => capability!.Detail)
                    .Where(static text => !string.IsNullOrWhiteSpace(text))
                    .Distinct(StringComparer.Ordinal));

            if (simulated)
            {
                detail = string.IsNullOrEmpty(detail) ? "模拟模式。" : $"模拟模式；{detail}";
            }

            modules.Add(new ModuleDto(definition.Id, definition.Name, state, detail, definition.Capabilities));
        }

        return modules;
    }

    private static string AggregateAll(CapabilityState[] states)
    {
        if (states.Length == 0)
        {
            return "ready";
        }

        if (states.All(static value => value is CapabilityState.Running or CapabilityState.Enabled or CapabilityState.Loaded))
        {
            return "ready";
        }

        return states.Any(static value => value is CapabilityState.Running or CapabilityState.Enabled or CapabilityState.Degraded or CapabilityState.Installed)
            ? "degraded"
            : "unavailable";
    }

    private static string AggregateAny(CapabilityState[] states)
    {
        if (states.Length == 0)
        {
            return "ready";
        }

        if (states.Any(static value => value is CapabilityState.Running or CapabilityState.Enabled or CapabilityState.Loaded))
        {
            return "ready";
        }

        return states.Any(static value => value is CapabilityState.Degraded or CapabilityState.Installed)
            ? "degraded"
            : "unavailable";
    }
}