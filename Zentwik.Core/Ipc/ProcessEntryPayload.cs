using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-processes</c> 的单条进程记录。
    ///
    /// 【为什么不直接序列化 ProcessRefresher.Entry】
    /// 那是 Core 的内部数据模型，字段会随采样逻辑演进（加线程数、加内存……）。
    /// 把它原样当协议发出去，等于让 UI 与采样实现耦合：改一次采样就要检查 UI。
    /// 这里显式挑出协议需要的六个字段，UI 只依赖这份形状。
    ///
    /// 【matchedRuleName 的含义】
    /// 命中的调度规则的 <c>Name</c>（即规则里的进程名），未命中为 null。
    /// 它表示「**规则匹配**」，不是「实际已绑定」—— CpuSetScheduler 没有读运行时
    /// CPU Set 的接口，本工具无从得知某个进程此刻真实绑在哪些核上。
    /// </summary>
    public sealed class ProcessEntryPayload
    {
        [JsonPropertyName("pid")]
        public int Pid { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        /// <summary>CPU 占用百分比（采样值，不是精确计量）。</summary>
        [JsonPropertyName("cpu")]
        public double Cpu { get; set; }

        /// <summary>Windows 优先级类（ProcessPriorityClass 的数值）。</summary>
        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        /// <summary>命中规则的 Name；未命中为 null。</summary>
        [JsonPropertyName("matchedRuleName")]
        public string? MatchedRuleName { get; set; }
    }
}
