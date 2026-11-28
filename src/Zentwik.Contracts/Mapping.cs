using Zentwik.Contracts.Dto;
using Zentwik.Domain.Capabilities;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Domain.Settings;

namespace Zentwik.Contracts;

public static class Mapping
{
    public static CapabilityDto ToDto(CapabilityDescriptor descriptor) => new(
        descriptor.Id,
        descriptor.State switch
        {
            CapabilityState.Running => CapabilityStates.Running,
            CapabilityState.Degraded => CapabilityStates.Degraded,
            CapabilityState.Failed => CapabilityStates.Failed,
            CapabilityState.Loaded => CapabilityStates.Loaded,
            CapabilityState.Enabled => CapabilityStates.Enabled,
            CapabilityState.Installed => CapabilityStates.Installed,
            _ => CapabilityStates.Unavailable,
        },
        descriptor.Detail,
        descriptor.Requires);

    public static PlanSummaryDto ToSummary(Plan plan) => new(
        plan.Id,
        plan.Kind switch
        {
            PlanKind.CpuTuning => "cpuTuning",
            PlanKind.Power => "power",
            _ => "scheduling",
        },
        plan.Name,
        plan.Revision,
        plan.LastApply?.State switch
        {
            PlanApplyState.Applied => "applied",
            PlanApplyState.Partial => "partial",
            PlanApplyState.Failed => "failed",
            PlanApplyState.Unknown => "unknown",
            _ => "draft",
        },
        plan.LastApply?.AppliedAt,
        plan.LastApply?.PersistsAcrossReboot ?? true,
        plan.LastApply?.Outcome);

    public static SceneSummaryDto ToSummary(Scene scene, string? activeSceneId) => new(
        scene.Id,
        scene.Name,
        scene.Priority,
        scene.Enabled,
        [.. scene.Actions.Select(static action => action.PlanId)],
        scene.LastActivatedAt,
        string.Equals(scene.Id, activeSceneId, StringComparison.Ordinal),
        scene.LastOutcome);

    public static SettingsDto ToDto(UserSettings settings) => new(
        settings.Language,
        settings.Theme,
        settings.Accent,
        settings.UiScale,
        settings.MonitorIntervalMs,
        settings.SensorIntervalMs);
}
