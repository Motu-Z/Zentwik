using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>save-mask</c> 的入参。
    ///
    /// 【id 为空串表示新建】
    /// 与 <c>save-scenario-rule</c> 同一口径：空 = 让 Worker 生成 Id，
    /// 非空 = 必须命中已有掩码。把「新建」写成空 Id 而不是让界面先生成一个，
    /// 是为了让「改一套不存在的掩码」变成明确的失败，而不是悄悄变成新增。
    ///
    /// 【为什么带 mask 值而不是让 Worker 从名字推】
    /// 名字是给人看的，掩码是给 CPU Set 的。两者没有推导关系 ——
    /// 用户完全可以把一套 8 核掩码叫「纯大核」，也可能叫「白天用」。
    /// </summary>
    public sealed class SaveMaskPayload
    {
        /// <summary>掩码 Id；空串 = 新建。</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>显示名。</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>逻辑核掩码，位 i = 1 表示包含逻辑处理器 i。</summary>
        [JsonPropertyName("mask")]
        public ulong Mask { get; set; }
    }
}
