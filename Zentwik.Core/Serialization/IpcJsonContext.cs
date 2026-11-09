using System.Collections.Generic;
using System.Text.Json.Serialization;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;

namespace Zentwik.Core.Serialization
{
    /// <summary>
    /// 管道协议的 JSON 源生成上下文。
    ///
    /// Core 开着 <c>IsAotCompatible</c>。任何一处走反射的
    /// <c>JsonSerializer.Serialize(object)</c> / <c>Deserialize&lt;T&gt;(string)</c>
    /// 都会在构建期报 IL2026 / IL3050，并在原生编译产物里抛异常。
    /// 所有 IPC 序列化调用点必须显式传本上下文的 <c>Default.PipeRequest</c> /
    /// <c>Default.PipeResponse</c>，没有例外。
    ///
    /// 两端分属两个程序集：客户端在 Core 里，服务端在 Worker 里。
    /// 上下文如果只对 Core 可见，Worker 就只能自己再生成一份，
    /// 于是「两端各有一套契约」这个局面又回来了。
    ///
    /// 协议按行切分消息，缩进会引入真实换行，第一行之后的每一个字节都会被
    /// 对端当成下一条消息的开头。
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        GenerationMode = JsonSourceGenerationMode.Default)]
    [JsonSerializable(typeof(PipeRequest))]
    [JsonSerializable(typeof(PipeResponse))]
    [JsonSerializable(typeof(SmuStatusPayload))]
    [JsonSerializable(typeof(TuningSnapshotPayload))]
    [JsonSerializable(typeof(PlanQueryPayload))]
    [JsonSerializable(typeof(SavePlanPayload))]
    [JsonSerializable(typeof(PlanRefPayload))]
    [JsonSerializable(typeof(RenamePlanPayload))]
    [JsonSerializable(typeof(PlanListPayload))]
    [JsonSerializable(typeof(SaveScheduleRulePayload))]
    [JsonSerializable(typeof(ScheduleRulePayload))]
    [JsonSerializable(typeof(List<ScheduleRulePayload>))]
    [JsonSerializable(typeof(ScenarioRulePayload))]
    [JsonSerializable(typeof(List<ScenarioRulePayload>))]
    [JsonSerializable(typeof(ProcessEntryPayload))]
    [JsonSerializable(typeof(List<ProcessEntryPayload>))]
    [JsonSerializable(typeof(MaskListPayload))]
    [JsonSerializable(typeof(SaveMaskPayload))]
    [JsonSerializable(typeof(CpuTopologyPayload))]
    [JsonSerializable(typeof(ScenarioStatePayload))]
    [JsonSerializable(typeof(ApplyPowerPolicyPayload))]
    [JsonSerializable(typeof(PowerPolicySnapshot))]
    [JsonSerializable(typeof(PowerSchemeListPayload))]
    [JsonSerializable(typeof(CreatePowerSchemePayload))]
    [JsonSerializable(typeof(RenamePowerSchemePayload))]
    [JsonSerializable(typeof(UiSettingsPayload))]
    public sealed partial class IpcJsonContext : JsonSerializerContext
    {
    }
}
