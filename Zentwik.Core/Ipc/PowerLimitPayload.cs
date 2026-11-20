using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-power-limit</c> 的返回形状。单位为瓦；<c>null</c> 表示读不到。
    /// </summary>
    public sealed class PowerLimitPayload
    {
        /// <summary>该平台是否有可用的功耗命令组。</summary>
        [JsonPropertyName("supported")]
        public bool Supported { get; set; }

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        /// <summary>持续功耗上限。</summary>
        [JsonPropertyName("stapm")]
        public int? StapmWatts { get; set; }

        /// <summary>短时功耗上限。</summary>
        [JsonPropertyName("fast")]
        public int? FastWatts { get; set; }

        /// <summary>平均功耗上限。</summary>
        [JsonPropertyName("slow")]
        public int? SlowWatts { get; set; }
    }

    /// <summary>
    /// <c>apply-power-limit</c> 的请求。为 null 的字段不动。
    /// </summary>
    public sealed class ApplyPowerLimitPayload
    {
        [JsonPropertyName("stapm")]
        public int? StapmWatts { get; set; }

        [JsonPropertyName("fast")]
        public int? FastWatts { get; set; }

        [JsonPropertyName("slow")]
        public int? SlowWatts { get; set; }
    }
}
