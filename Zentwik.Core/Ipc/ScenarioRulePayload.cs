using System.Text.Json.Serialization;
using Zentwik.Core.Services;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 场景规则在管道上的形状（<c>query-scenario-rules</c> 的返回元素、
    /// <c>save-scenario-rule</c> 的入参）。
    ///
    /// 【为什么只有这些字段】
    /// <c>ScenarioRule</c> 上还有 <c>TriggerText</c> 与 <c>ProfileName</c>，两者都是
    /// <c>[JsonIgnore]</c> 的**展示派生值**：一个由 Trigger + Argument 拼出来，
    /// 一个要反查 profiles.json。把它们塞进协议，等于让 Worker 替 UI 决定文案，
    /// 而 UI 手上本来就有方案列表，自己拼更准（方案改名后 Worker 那份快照就旧了）。
    ///
    /// 【Trigger 必须以字符串传输】
    /// 数字形态（0/1/2）在协议里不可读，且往枚举中间插一个值就是一次静默的语义漂移。
    /// 这里用 <see cref="JsonStringEnumConverter{TEnum}"/> 按属性声明，而不是给整个
    /// <c>IpcJsonContext</c> 打开 <c>UseStringEnumConverter</c>：
    /// 全局开关会让**将来**新增的每个枚举都悄悄变成字符串，
    /// 而枚举是否该按名称传输是逐个类型要判断的事，不是一条全局策略。
    /// </summary>
    public sealed class ScenarioRulePayload
    {
        /// <summary>
        /// 规则 Id。**空串表示新建** —— Worker 侧会保留
        /// <c>ScenarioRule.Id</c> 初始化器生成的新 Guid，而不是用空串覆盖它。
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>触发源。序列化为名称（<c>ProcessRunning</c> / <c>OnBattery</c> / <c>OnAcPower</c>）。</summary>
        [JsonPropertyName("trigger")]
        [JsonConverter(typeof(JsonStringEnumConverter<ScenarioTrigger>))]
        public ScenarioTrigger Trigger { get; set; } = ScenarioTrigger.ProcessRunning;

        /// <summary>触发参数：进程类触发填进程名；电源类触发留空。</summary>
        [JsonPropertyName("argument")]
        public string Argument { get; set; } = string.Empty;

        /// <summary>命中后要切换到的方案 Id。必须是已存在的方案；空串表示不动调校。</summary>
        [JsonPropertyName("profileId")]
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>命中后套到触发进程上的自定义掩码 Id；空串表示不动调度。</summary>
        [JsonPropertyName("maskId")]
        public string MaskId { get; set; } = string.Empty;

        /// <summary>命中后切换到的电源方案 Guid（字符串形态）；空串表示不动电源。</summary>
        [JsonPropertyName("powerSchemeId")]
        public string PowerSchemeId { get; set; } = string.Empty;

        /// <summary>仲裁优先级：同时命中多条时，数字小的赢。</summary>
        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        /// <summary>是否启用。</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }
}
