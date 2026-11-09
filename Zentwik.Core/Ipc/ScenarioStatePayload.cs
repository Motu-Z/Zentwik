using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 场景自动切换的总闸状态。<c>query-scenario-state</c> 与
    /// <c>set-scenario-enabled</c> 共用这一个形状：前者读回，后者写入并回读。
    ///
    /// 总闸就一件事：开或关。规则数量、命中情况、上次切到哪套，都不属于
    /// 「功能开没开」。将来要显示「当前场景」时另开命令，不要往这里堆字段 ——
    /// 一个既表达开关又表达运行态的对象，迟早会出现「关着但显示已切到 X」。
    /// </summary>
    public sealed class ScenarioStatePayload
    {
        /// <summary>true = 自动切换生效。</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }
}
