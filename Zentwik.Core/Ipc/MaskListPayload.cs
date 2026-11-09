using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-masks</c> 的单条自定义掩码。
    /// </summary>
    /// <remarks>
    /// 界面要在下拉里显示「纯大核 · 8 核」。数量能从 <see cref="Mask"/> 数出来，
    /// 但那要 UI 自己实现 popcount 并保证与 Core 的口径一致（63 位截断、0 值处理）。
    /// 由持有权威数据的这一侧算好，界面只负责显示。
    /// </remarks>
    public sealed class MaskEntryPayload
    {
        /// <summary>稳定 Id。规则引用的是它，改名不影响引用。</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>用户起的名字，如「纯大核」。</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>逻辑核掩码，位 i = 1 表示包含逻辑处理器 i。</summary>
        [JsonPropertyName("mask")]
        public ulong Mask { get; set; }

        /// <summary>置位的核数。</summary>
        [JsonPropertyName("coreCount")]
        public int CoreCount { get; set; }
    }

    /// <summary>
    /// <c>query-masks</c> 的返回体：掩码列表 + 套数上限。
    /// </summary>
    /// <remarks>
    /// 上限（<c>masks.json</c> 最多 10 套）决定界面上的「＋ 新建掩码」能不能点。
    /// 只给数组，UI 就得自己写死一个 10 —— 两处上限迟早不一致，
    /// 表现为「按钮还能点，点了却提示已满」。
    /// </remarks>
    public sealed class MaskListPayload
    {
        [JsonPropertyName("masks")]
        public List<MaskEntryPayload> Masks { get; set; } = new();

        /// <summary>允许的最大套数。</summary>
        [JsonPropertyName("limit")]
        public int Limit { get; set; }
    }
}
