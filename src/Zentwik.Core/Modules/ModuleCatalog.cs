using Zentwik.Contracts.Dto;
using Zentwik.Domain.Capabilities;

namespace Zentwik.Core.Modules;

/// <summary>模块描述能力组合，不引入独立加载器：当前没有第三方插件。</summary>
/// <param name="Optional">
/// 可选组件：默认关闭，须显式启用才加载。用于会改变硬件行为的模块——
/// 默认开启等于替所有用户做了风险选择。非可选模块一律视为启用。
/// </param>
public sealed record ModuleDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Capabilities,
    string Purpose,
    bool AnyOf = false,
    bool Optional = false);

public static class ModuleIds
{
    public const string PowerLimits = "power-limits";
}

public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDefinition> Definitions { get; } =
    [
        // 系统遥测与 CPU 传感器是两条通道：传感器缺席（非 AMD、无模块、未提权）时监控仍可用。
        new("monitoring", "监控", [CapabilityIds.TelemetryBasic, CapabilityIds.TelemetryCpuSensors], "CPU、内存与进程计数采样；CPU 温度与封装功耗", AnyOf: true),
        new("power", "电源", [CapabilityIds.PowerPolicy], "电源方案切换与策略参数写入"),
        // CPU 调校同时列 AMD 与 Intel 两套实现，同一时刻只有一方适用；另一厂商缺席不是降级。
        new("cpu-tuning", "CPU 调校", [CapabilityIds.AmdSmu, CapabilityIds.IntelMsr], "Curve Optimizer 与 MSR 调校", AnyOf: true),
        new("scheduling", "进程调度", [CapabilityIds.AffinityMask, CapabilityIds.CpuSets], "进程亲和性、优先级与 CPU Sets 调度"),
        new("automation", "自动化", [CapabilityIds.SceneEngine], "场景触发、仲裁与状态回退"),
        new("diagnostics", "诊断", [], "能力探测、日志与状态对账"),
        new(ModuleIds.PowerLimits, "硬件功耗墙", [CapabilityIds.AmdPowerLimit], "PPT/TDC/EDC 写入，绕出厂功耗限制", Optional: true),
    ];

    public static ModuleDefinition? Find(string id) =>
        Definitions.FirstOrDefault(definition =>
            string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ModuleDto> Describe(
        IReadOnlyList<CapabilityDescriptor> capabilities,
        bool simulated,
        IReadOnlySet<string>? enabledOptional = null)
    {
        var byId = capabilities.ToDictionary(static capability => capability.Id, StringComparer.Ordinal);
        var modules = new List<ModuleDto>(Definitions.Count);

        foreach (var definition in Definitions)
        {
            // 可选组件未启用时不看能力探测结果：它压根没加载，状态不能显示为就绪。
            if (definition.Optional && (enabledOptional is null || !enabledOptional.Contains(definition.Id)))
            {
                modules.Add(new ModuleDto(
                    definition.Id,
                    definition.Name,
                    "unavailable",
                    "可选组件，未启用。",
                    definition.Capabilities,
                    Optional: true,
                    Enabled: false));
                continue;
            }
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

            modules.Add(new ModuleDto(definition.Id, definition.Name, state, detail, definition.Capabilities, definition.Optional, true));
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
