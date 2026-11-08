using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-cpu-topology</c> 的返回体：掩码网格需要的三个数。
    ///
    /// 【为什么只给三个数】
    /// 网格是「CCD → 物理核 → 逻辑线程」三层生成的，三层全部可以从这三个数推导出来
    /// （CCD 数 = ⌈cores / coresPerCcd⌉）。把 CCD 数也传一份，界面就有两个能推出
    /// 同一个值的来源，某个机器上探测出 <c>coresPerCcd = 0</c> 时两者会打架。
    ///
    /// 【threadsPerCore 为 1 的含义】
    /// 没有 SMT（或探测不到）。此时每个物理核只有一个线程槽，
    /// 「只选物理核」与「全选」是同一件事 —— 界面按 1 画即可，不需要特判。
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
    }
}
