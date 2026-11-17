using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>掩码被引用的情况：当前规则集与已保存的调度方案各有多少处。</summary>
    public sealed class MaskUsagePayload
    {
        [JsonPropertyName("maskId")]
        public string MaskId { get; set; } = string.Empty;

        /// <summary>当前生效规则里引用该掩码的条数。</summary>
        [JsonPropertyName("rules")]
        public int Rules { get; set; }

        /// <summary>已保存的调度方案里含该掩码引用的方案数。</summary>
        [JsonPropertyName("plans")]
        public int Plans { get; set; }
    }
}
