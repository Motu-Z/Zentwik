using System.Collections.Generic;
using System.Text.Json.Serialization;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;

namespace Zentwik.Core.Serialization
{
    /// <summary>
    /// 配置文件的 JSON 源生成上下文。
    ///
    /// Native AOT 下没有运行时代码生成，<c>JsonSerializer.Deserialize&lt;T&gt;</c> 这类
    /// 基于反射的重载会被裁剪掉，调用点直接抛异常。所有配置文件类型必须在这里登记，
    /// 由编译器在构建期生成读写代码。
    ///
    /// 两个程序集都要用：Core 内部的 JsonFile，以及 UI 侧直接读 profiles.json
    /// （配置列表不走 IPC，见调校页的配置下拉）。与 IpcJsonContext 同一条理由 ——
    /// 只对 Core 可见，UI 就只能自己再生成一份，于是「两端各有一套契约」又回来了。
    ///
    /// 现有 ui.json / tuning.json / schedules.json 的键都是 PascalCase。
    /// 这里一旦加上 <c>PropertyNamingPolicy = CamelCase</c>，程序不会报错，
    /// 只会让老用户的所有配置读不出来并静默回落成默认值 ——
    /// 用户以为设置被重置了，实际上文件还在。
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        GenerationMode = JsonSourceGenerationMode.Default)]
    [JsonSerializable(typeof(UiSettings))]
    [JsonSerializable(typeof(TuningProfile))]
    [JsonSerializable(typeof(BootGuardState))]
    [JsonSerializable(typeof(SchedulingProfile))]
    [JsonSerializable(typeof(List<CustomMask>))]
    [JsonSerializable(typeof(PlanFile))]
    // PlanEntry.PowerPolicy 复用了 IPC 的载荷类型。源生成上下文必须显式登记，
    // 否则 AOT 下写 plans.json 时这整块会被静默跳过，方案里的参数快照全丢。
    [JsonSerializable(typeof(ApplyPowerPolicyPayload))]
    [JsonSerializable(typeof(LegacyProfileFile))]
    [JsonSerializable(typeof(ScenarioFile))]
    public sealed partial class AppJsonContext : JsonSerializerContext
    {
    }
}
