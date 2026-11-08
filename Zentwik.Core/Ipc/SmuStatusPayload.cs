using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-smu-status</c> 的 Payload 形状。
    ///
    /// 【为什么是 public】
    /// 三处都要用同一份类型：Worker 侧序列化、UI 侧反序列化、源生成上下文
    /// （<see cref="Serialization.IpcJsonContext"/>）跨程序集生成 JsonTypeInfo。
    /// 声明成 internal 就只能给 Worker 开 InternalsVisibleTo，等于用可见性补丁
    /// 换一个本来可以公开的数据契约。
    ///
    /// 【字段名为什么是小写】
    /// 协议里的 Payload 是给两端解析的线格式，不是给人读的配置。
    /// 显式标 <c>available</c> / <c>error</c>，避免将来改 <c>PropertyNamingPolicy</c>
    /// 时把线格式一起改掉。
    /// </summary>
    public sealed class SmuStatusPayload
    {
        /// <summary>PawnIO 驱动与管理员令牌是否就绪。</summary>
        [JsonPropertyName("available")]
        public bool Available { get; set; }

        /// <summary><c>ISmuReader.LastError</c> 原文；可用时为 null。</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
