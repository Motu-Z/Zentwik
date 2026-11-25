using System.Text.Json;
using System.Text.Json.Serialization;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Protocol;

namespace Zentwik.Contracts.Json;

/// <summary>新增 DTO 必须在此登记：AOT 裁剪后反射式序列化会静默失效。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(ResponseEnvelope))]
[JsonSerializable(typeof(EmptyPayload))]
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
public sealed partial class ZentwikJson : JsonSerializerContext;

public static class ZentwikJsonDefaults
{
    public static JsonSerializerOptions Options => ZentwikJson.Default.Options;
}
