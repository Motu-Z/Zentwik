using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-intel-tuning</c> / <c>apply-intel-tuning</c> 的返回形状。
    /// 电压为毫伏，负值为降压；<c>null</c> 表示该平面读不到。
    /// </summary>
    public sealed class IntelTuningPayload
    {
        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("core")]
        public int? CoreMilliVolts { get; set; }

        [JsonPropertyName("gpu")]
        public int? GpuMilliVolts { get; set; }

        [JsonPropertyName("cache")]
        public int? CacheMilliVolts { get; set; }

        /// <summary>八档倍频上限，下标 0 对应单核激活。空表示读不到。</summary>
        [JsonPropertyName("ratios")]
        public int[]? Ratios { get; set; }

        /// <summary>PL1 持续功耗（W）；null 表示读不到。</summary>
        [JsonPropertyName("pl1")]
        public int? PowerLimit1Watts { get; set; }

        /// <summary>PL2 短时功耗（W）；null 表示读不到。</summary>
        [JsonPropertyName("pl2")]
        public int? PowerLimit2Watts { get; set; }

        /// <summary>PL2 持续时间（秒）；0 表示不限。</summary>
        [JsonPropertyName("tau")]
        public int? TauSeconds { get; set; }
    }

    /// <summary>
    /// <c>apply-intel-tuning</c> 的请求。为 null 的字段不动；
    /// <c>restore</c> 为真时回到本次运行首次读到的值。
    /// </summary>
    public sealed class ApplyIntelTuningPayload
    {
        [JsonPropertyName("core")]
        public int? CoreMilliVolts { get; set; }

        [JsonPropertyName("gpu")]
        public int? GpuMilliVolts { get; set; }

        [JsonPropertyName("cache")]
        public int? CacheMilliVolts { get; set; }

        [JsonPropertyName("ratios")]
        public int[]? Ratios { get; set; }

        [JsonPropertyName("pl1")]
        public int? PowerLimit1Watts { get; set; }

        [JsonPropertyName("pl2")]
        public int? PowerLimit2Watts { get; set; }

        [JsonPropertyName("tau")]
        public int? TauSeconds { get; set; }

        [JsonPropertyName("restore")]
        public bool Restore { get; set; }
    }
}
