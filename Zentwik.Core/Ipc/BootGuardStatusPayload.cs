using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 启动护栏状态（<c>query-boot-guard</c> 的响应体）。
    /// </summary>
    public sealed class BootGuardStatusPayload
    {
        /// <summary>连续多少次启动在「写入调校」之后没能正常完成。</summary>
        [JsonPropertyName("failures")]
        public int Failures { get; set; }

        /// <summary>是否已经达到拦截阈值（本次及下次启动都会跳过自动调校）。</summary>
        [JsonPropertyName("blocked")]
        public bool Blocked { get; set; }

        /// <summary>最近一次进入写入窗口的时间（ISO8601）。从未写过时为空。</summary>
        [JsonPropertyName("lastAttemptAt")]
        public string? LastAttemptAt { get; set; }

        /// <summary>最近一次触发拦截的时间（ISO8601）。未触发过时为空。</summary>
        [JsonPropertyName("blockedAt")]
        public string? BlockedAt { get; set; }
    }
}
