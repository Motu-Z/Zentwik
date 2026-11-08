using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-power-schemes</c> 的返回。
    /// </summary>
    public sealed class PowerSchemeListPayload
    {
        /// <summary>当前活动方案的 GUID。取不到时为 <see cref="Guid.Empty"/>。</summary>
        [JsonPropertyName("activeGuid")]
        public Guid ActiveGuid { get; set; }

        /// <summary>本机已有的全部方案，顺序即系统枚举顺序。</summary>
        [JsonPropertyName("schemes")]
        public List<PowerSchemeEntry> Schemes { get; set; } = new();
    }

    /// <summary>一个电源方案。</summary>
    public sealed class PowerSchemeEntry
    {
        [JsonPropertyName("guid")]
        public Guid Guid { get; set; }

        /// <summary>
        /// 显示名。读不到名字时为「未知」，不是 null ——
        /// 界面上总要显示点什么，null 会让卡片变成空白。
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
