using System;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>create-power-scheme</c> 的请求：以某个模板派生一份新电源方案。
    /// </summary>
    public sealed class CreatePowerSchemePayload
    {
        /// <summary>
        /// 模板方案 GUID。可以是内置预设（平衡 / 节能 / 高性能 / 卓越性能），
        /// 也可以是本机已有的任意方案。
        /// </summary>
        [JsonPropertyName("templateGuid")]
        public Guid TemplateGuid { get; set; }

        /// <summary>新方案的显示名。去空白后不能为空、不能超过 24 字、不能与现有方案重名。</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
