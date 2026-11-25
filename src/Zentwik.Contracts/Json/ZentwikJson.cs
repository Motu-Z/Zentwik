using System.Text.Json;
using System.Text.Json.Serialization;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Protocol;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Persistence;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Domain.Settings;

namespace Zentwik.Contracts.Json;

/// <summary>新增跨进程类型必须在此登记：AOT 裁剪后反射式序列化会静默失效。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(ResponseEnvelope))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(CapabilityDto))]
[JsonSerializable(typeof(CapabilityDto[]))]
[JsonSerializable(typeof(TelemetryDto))]
[JsonSerializable(typeof(AgentStatusDto))]
[JsonSerializable(typeof(PlanSummaryDto))]
[JsonSerializable(typeof(PlanSummaryDto[]))]
[JsonSerializable(typeof(SceneSummaryDto))]
[JsonSerializable(typeof(SceneSummaryDto[]))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(SettingsPatch))]
[JsonSerializable(typeof(SubscriptionRequest))]
[JsonSerializable(typeof(SubscriptionAck))]
[JsonSerializable(typeof(IdRequest))]
[JsonSerializable(typeof(LogRequest))]
[JsonSerializable(typeof(TargetOutcomeDto))]
[JsonSerializable(typeof(TargetOutcomeDto[]))]
[JsonSerializable(typeof(PlanApplyResultDto))]
[JsonSerializable(typeof(SceneActivationDto))]
[JsonSerializable(typeof(ModuleDto))]
[JsonSerializable(typeof(ModuleDto[]))]
[JsonSerializable(typeof(LogEntryDto))]
[JsonSerializable(typeof(LogEntryDto[]))]
[JsonSerializable(typeof(DoctorDto))]
[JsonSerializable(typeof(CoreOffsetDto))]
[JsonSerializable(typeof(CoreOffsetDto[]))]
[JsonSerializable(typeof(TuningSnapshotDto))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(CpuTuningPlan))]
[JsonSerializable(typeof(PowerPlan))]
[JsonSerializable(typeof(SchedulingPlan))]
[JsonSerializable(typeof(Plan[]))]
[JsonSerializable(typeof(PlanApplyRecord))]
[JsonSerializable(typeof(Scene))]
[JsonSerializable(typeof(Scene[]))]
[JsonSerializable(typeof(SceneTrigger))]
[JsonSerializable(typeof(SceneAction))]
[JsonSerializable(typeof(LedgerEntry))]
[JsonSerializable(typeof(LedgerEntry[]))]
[JsonSerializable(typeof(StateDocument))]
[JsonSerializable(typeof(UserSettings))]
public sealed partial class ZentwikJson : JsonSerializerContext;
