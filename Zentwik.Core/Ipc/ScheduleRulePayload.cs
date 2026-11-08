using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-scheduling-rules</c> 的返回元素形状：一条调度规则的**只读投影**。
    ///
    /// 【为什么不直接序列化 ScheduleRule】
    /// 它是落盘模型：有 <c>MaskId</c> / <c>MaskName</c> 这类只在 Worker 内部有意义的字段，
    /// 还有 <c>Label</c> 这种派生属性。直接把落盘模型当协议模型用，等于把
    /// schedules.json 的字段演进绑死到 IPC 上 —— 改一个存储字段就破坏一次协议。
    ///
    /// 【targetType / targetValue 的格式约定（UI 侧按此解析）】
    ///   · <c>"ccd"</c>  → targetValue 是 CCD 索引的十进制字符串（"0" / "1"），UI 用 int.Parse；
    ///   · <c>"mask"</c> → targetValue 是掩码的十进制字符串（ulong，如 "65280"），UI 用 ulong.Parse；
    ///   · <c>"none"</c> → targetValue 为空串。
    ///
    /// 【注意：这与 save-scheduling-rule 的入参格式**不同**】
    /// 保存时 <c>targetValue</c> 取 <c>"CCD0"</c> / <c>"CCD1"</c> 或**掩码 Id**；
    /// 查询时给的是索引数字与掩码**值**。两者不对称是有意的：
    ///   · 查询要的是「这条规则现在绑到哪」，用值最直观，UI 不必再反查 masks.json；
    ///   · 保存要的是「绑到哪」，用 Id 才能让 Worker 把掩码名称一起快照进规则。
    /// 因此「查出来 → 原样存回去」对 mask 类规则**不成立**，UI 改掩码规则必须重新选一次掩码。
    /// </summary>
    public sealed class ScheduleRulePayload
    {
        /// <summary>
        /// 规则 Id（32 位无横线 GUID）。<c>delete-scheduling-rule</c> 与
        /// <c>save-scheduling-rule</c> 的 <c>id</c> 字段都用它。
        ///
        /// 放在数组第一个位置是有意的：它是「这条记录是谁」，
        /// 后面四个字段才是「它绑到哪」。UI 建列表时按 Id 做行标识，
        /// 用户改完进程名再删，删的仍然是同一行。
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>进程名（含 .exe）。规则的业务键 —— 同名规则视为同一条。</summary>
        [JsonPropertyName("processName")]
        public string ProcessName { get; set; } = string.Empty;

        /// <summary>目标类型：<c>ccd</c> / <c>mask</c> / <c>none</c>。</summary>
        [JsonPropertyName("targetType")]
        public string TargetType { get; set; } = "none";

        /// <summary>目标值，格式见类注释。</summary>
        [JsonPropertyName("targetValue")]
        public string TargetValue { get; set; } = string.Empty;

        /// <summary>优先级档位（<c>High</c> / <c>Above Normal</c> / …）。空串 = 不改优先级。</summary>
        [JsonPropertyName("priority")]
        public string Priority { get; set; } = string.Empty;

        /// <summary>规则是否启用。关掉的规则仍保留，但后台不会按它调度。</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 旧版按路径存储时留下的路径。新版按进程名匹配，此字段仅用于迁移与排障；
        /// 空串表示没有记录（绝大多数规则都是空串）。
        /// </summary>
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }
}
