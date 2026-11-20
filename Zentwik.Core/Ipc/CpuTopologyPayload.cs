using System.Text.Json.Serialization;
using Zentwik.Core.Helpers;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-cpu-topology</c> 的返回体：掩码网格需要的核关系。
    ///
    /// <c>cores</c> / <c>coresPerCcd</c> / <c>threadsPerCore</c> 是旧版字段，
    /// 界面按公式推格子；混合架构推不出 P / E 边界，改由 <c>coreMasks</c> 直给。
    /// </summary>
    public sealed class CpuTopologyPayload
    {
        /// <summary>物理核数。</summary>
        [JsonPropertyName("cores")]
        public int Cores { get; set; }

        /// <summary>每个 CCD 的物理核数。</summary>
        [JsonPropertyName("coresPerCcd")]
        public int CoresPerCcd { get; set; }

        /// <summary>每核逻辑线程数（SMT 宽度）。</summary>
        [JsonPropertyName("threadsPerCore")]
        public int ThreadsPerCore { get; set; } = 1;

        /// <summary>处理器厂商，线格式 <c>amd</c> / <c>intel</c> / <c>unknown</c>。</summary>
        [JsonPropertyName("vendor")]
        public string Vendor { get; set; } = "unknown";

        /// <summary>
        /// 每个物理核的线程掩码，位 i = 逻辑处理器 i。顺序即核序号。
        /// 空数组表示探测不到，界面退回公式推导。
        /// </summary>
        [JsonPropertyName("coreMasks")]
        public CoreMask[] CoreMasks { get; set; } = System.Array.Empty<CoreMask>();

        /// <summary>与 <c>coreMasks</c> 一一对应的效率类，越大越偏性能。</summary>
        [JsonPropertyName("coreClasses")]
        public int[] CoreClasses { get; set; } = System.Array.Empty<int>();
    }
}
