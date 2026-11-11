using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>save-scheduling-rule</c> 的 Payload 形状。
    ///
    /// UI 侧的下拉框是「掩码表 / 不调度」平铺，
    /// 选中的是一个**文本**目标。让 UI 自己判断「这是哪个掩码、掩码值是多少」，
    /// 等于把 <c>masks.json</c> 的解析逻辑复制一份到界面进程 —— 而 UI 手上那份掩码列表
    /// 可能是几秒前的快照，掩码被删掉时它会传上来一个不存在的值。
    /// 这里只传「哪一类 + 哪个」两个词，由持有主副本的 Worker 去查、去快照。
    ///
    /// 落盘的就是 "High" / "Above Normal" 这些英文常量（见 <c>ProcessPriority</c>），
    /// 引入枚举会让 schedules.json 出现第二套表示法，旧规则反序列化时直接对不上。
    /// </summary>
    public sealed class SaveScheduleRulePayload
    {
        /// <summary>
        /// 要更新的规则 Id。留空 = 按进程名新增或覆盖（旧版行为）。
        ///
        /// 用户在规则列表里选中一行改掩码 / 改优先级时，界面上那行的身份是 Id。
        /// 只按进程名覆盖也能成立，但一旦这次编辑顺带改了进程名，就会变成
        /// 「旧名字那条还在、新名字多出一条」。带上 Id 才能表达「改的是这一条」。
        ///
        /// 这是旧 UI 的唯一写法，必须继续支持：升级途中两个 exe 混搭是常态。
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>进程名（含 .exe，如 <c>msedge.exe</c>）。Worker 侧会 Trim；空白一律拒绝。</summary>
        [JsonPropertyName("processName")]
        public string ProcessName { get; set; } = string.Empty;

        /// <summary>目标类型：<c>ccd</c> 或 <c>mask</c>（忽略大小写）。</summary>
        [JsonPropertyName("targetType")]
        public string TargetType { get; set; } = string.Empty;

        /// <summary>
        /// 目标值：<c>targetType=ccd</c> 时为**核心组序号**（<c>0</c> / <c>1</c> / …）；
        /// <c>targetType=mask</c> 时为掩码 Id。
        ///
        /// <c>ccd</c> 是旧版协议，只为兼容升级途中混搭的旧 UI 而留。
        /// 它现在也走「序号 → 掩码」换算，落盘一律是掩码。
        /// </summary>
        [JsonPropertyName("targetValue")]
        public string TargetValue { get; set; } = string.Empty;

        /// <summary>优先级档位（<c>High</c> / <c>Above Normal</c> / …）。空串 = 这条规则不碰优先级。</summary>
        [JsonPropertyName("priority")]
        public string Priority { get; set; } = string.Empty;

        /// <summary>
        /// 规则是否启用。留空（null）= 保持该规则原值，新建时为 true。
        ///
        /// <c>query-scheduling-rules</c> 一直会返回 <c>enabled</c>，而保存侧没有对应入参，
        /// 于是「查出来 → 改一个字段 → 存回去」这条路走不通：任何一次保存都会把
        /// 一条已禁用的规则悄悄改回启用。协议两个方向必须能对上。
        ///
        /// 用 <c>bool?</c> 而不是 <c>bool</c>：false 与「没传」是两件事，
        /// 前者是「禁用」，后者是「别动它」。
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }
    }
}
