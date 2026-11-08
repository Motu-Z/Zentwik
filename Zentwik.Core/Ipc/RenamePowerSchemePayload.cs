using System;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>rename-power-scheme</c> 的请求：改某个电源方案的显示名。
    /// </summary>
    public sealed class RenamePowerSchemePayload
    {
        /// <summary>要改名的方案 GUID。</summary>
        [JsonPropertyName("guid")]
        public Guid Guid { get; set; }

        /// <summary>新名字。校验规则与新建一致。</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
