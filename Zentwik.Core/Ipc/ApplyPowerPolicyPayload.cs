using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>apply-power-policy</c> 的入参。
    ///
    /// 【为什么每个字段都是可空的】
    /// 这条命令是**增量**语义：只改请求里出现的那几项，没出现的保持原样。
    /// 用一个非空的 <c>int</c> 表达「不改」只能靠魔法值（-1 / 0），
    /// 而 0 在 EPP、ASPM、Boost 里都是合法取值 —— 用户想把 Boost 调成 0（关闭）
    /// 和用户根本没提 Boost，在非空类型下完全无法区分。
    /// 可空类型把「没提」与「提了 0」分开，代价只是判空。
    ///
    /// 【全为 null 时为什么报错而不是当无事发生】
    /// 一次什么都没改的写入请求，只可能来自 UI 的缺陷（用户点了「应用」但表单没收集到值）
    /// 或协议错配。静默成功会让界面显示「已应用」，而系统一点没变 ——
    /// 用户下一次打开页面发现值又回去了，却没有任何日志可查。
    /// </summary>
    public sealed class ApplyPowerPolicyPayload
    {
        /// <summary>EPP，0-100。null = 不改。</summary>
        [JsonPropertyName("epp")]
        public int? Epp { get; set; }

        /// <summary>Boost 模式，0-4。null = 不改。</summary>
        [JsonPropertyName("boostMode")]
        public int? BoostMode { get; set; }

        /// <summary>最大处理器状态，50-100。null = 不改。</summary>
        [JsonPropertyName("maxProcessorState")]
        public int? MaxProcessorState { get; set; }

        /// <summary>ASPM，0-2。null = 不改。</summary>
        [JsonPropertyName("aspm")]
        public int? Aspm { get; set; }

        /// <summary>
        /// 是否禁用处理器空闲（C-State）。null = 不改；
        /// <c>true</c> 写 1（禁用空闲），<c>false</c> 写 0（允许空闲）。
        /// </summary>
        [JsonPropertyName("idleDisable")]
        public bool? IdleDisable { get; set; }

        /// <summary>
        /// AC / DC 共十个字段一个都没填。
        ///
        /// 【为什么不参与序列化】
        /// 它是给 Worker 判空用的派生属性，不是线格式的一部分。
        /// 一旦被写进 JSON，对端会看到一个凭空多出来的 <c>isEmpty</c> 键，
        /// 而它永远为 false（序列化时请求已经非空了）。
        /// </summary>
        [JsonIgnore]
        public bool IsEmpty =>
            Epp is null
            && BoostMode is null
            && MaxProcessorState is null
            && Aspm is null
            && IdleDisable is null
            && DcEpp is null
            && DcBoostMode is null
            && DcMaxProcessorState is null
            && DcAspm is null
            && DcIdleDisable is null;

        // ==================== 电池（DC）====================
        //
        // 与上面五项一一对应，null 同样是「不改」。
        // 台式机没有电池，这五项不会有任何调用方去填。

        /// <summary>电池供电下的 EPP，0-100。null = 不改。</summary>
        [JsonPropertyName("dcEpp")]
        public int? DcEpp { get; set; }

        /// <summary>电池供电下的 Boost 模式，0-4。null = 不改。</summary>
        [JsonPropertyName("dcBoostMode")]
        public int? DcBoostMode { get; set; }

        /// <summary>电池供电下的最大处理器状态，50-100。null = 不改。</summary>
        [JsonPropertyName("dcMaxProcessorState")]
        public int? DcMaxProcessorState { get; set; }

        /// <summary>电池供电下的 ASPM，0-2。null = 不改。</summary>
        [JsonPropertyName("dcAspm")]
        public int? DcAspm { get; set; }

        /// <summary>电池供电下是否禁用空闲。null = 不改。</summary>
        [JsonPropertyName("dcIdleDisable")]
        public bool? DcIdleDisable { get; set; }
    }
}
