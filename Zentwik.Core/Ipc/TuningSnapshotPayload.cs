using System;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-smu-snapshot</c> 的 Payload 形状：从硬件读回当前 CO / FMax。
    ///
    /// 成功与否由 <c>PipeResponse.Success</c> + <c>Message</c> 表达；本类只承载
    /// 「读到的值」这一件事。把 Success 同时放进两个地方，迟早会出现
    /// 「外层 true、内层 false」的矛盾态，而消费方不知道该信哪个。
    ///
    /// 与 <see cref="SmuStatusPayload"/> 同一条理由：Worker 序列化、UI 反序列化、
    /// 源生成上下文三处共用同一份类型。
    /// </summary>
    public sealed class TuningSnapshotPayload
    {
        /// <summary>每核 CO 值。<c>int.MinValue</c> = 该核读取失败/禁用，不是「偏移 0」。</summary>
        [JsonPropertyName("coreMargins")]
        public int[] CoreMargins { get; set; } = Array.Empty<int>();

        /// <summary>当前 FMax 上限（MHz，0 = 未知）。</summary>
        [JsonPropertyName("fmaxMhz")]
        public uint FMaxMHz { get; set; }

        /// <summary>FMax 可写下限（MHz，0 = 未知）。</summary>
        [JsonPropertyName("fmaxMinMhz")]
        public uint FMaxMinMHz { get; set; }

        /// <summary>FMax 可写上限（MHz，0 = 未知）。</summary>
        [JsonPropertyName("fmaxMaxMhz")]
        public uint FMaxMaxMHz { get; set; }

        /// <summary>读取过程中的非致命错误原文；正常时为 null。</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
