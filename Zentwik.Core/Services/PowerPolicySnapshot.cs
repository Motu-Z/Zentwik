using System;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 一次电源策略读取的完整结果。
    ///
    /// 这五项分属两个不同的系统子组（处理器 / PCI Express），而且**不是每台机器都有**：
    /// 台式机常常没有 ASPM 项，部分主板 BIOS 不暴露 Boost 档位，虚拟机里可能一项都没有。
    /// 只回一个 <c>int?</c> 的话，「读到 0」与「读不到」在界面上长得一模一样 ——
    /// 用户会看到一个 EPP = 0 的滑块，拖一下才发现根本写不进去。
    /// 所以每项都自带 <see cref="EppSupported"/>：界面据此决定画滑块还是画「本机不支持」。
    ///
    /// 两者不是反义关系：读取成功时 <c>Supported = true</c> 且 Error 为 null；
    /// 读取失败时 <c>Supported = false</c> 且 Error 有值。合成一个字段会让
    /// 「支持但值为空」这种状态无处安放（将来若出现「支持但暂不可读」就会踩到）。
    ///
    /// 取不到活动方案时返回的是 <see cref="Unavailable"/> 造出来的实例，
    /// 不是 null。调用方永远不必写 <c>snapshot?</c> 判空 —— 一条「未知方案 +
    /// 五项全不支持」的快照本身就是有效信息，它明确告诉界面「现在读不到任何东西」。
    /// </summary>
    public sealed class PowerPolicySnapshot
    {
        /// <summary>取不到方案名时的占位文案。</summary>
        public const string UnknownSchemeName = "未知";

        /// <summary>当前活动方案的 GUID。降级时为 <see cref="Guid.Empty"/>。</summary>
        [JsonPropertyName("activeSchemeGuid")]
        public Guid ActiveSchemeGuid { get; set; }

        /// <summary>当前活动方案的显示名（如「平衡」「高性能」）。降级时为「未知」。</summary>
        [JsonPropertyName("activeSchemeName")]
        public string ActiveSchemeName { get; set; } = UnknownSchemeName;

        /// <summary>EPP（能效偏好），0-100，越大越省电。不受支持时为 null。</summary>
        [JsonPropertyName("epp")]
        public int? Epp { get; set; }

        /// <summary>EPP 这一项是否可读。</summary>
        [JsonPropertyName("eppSupported")]
        public bool EppSupported { get; set; }

        /// <summary>EPP 不可读的原因（含 Win32 错误码）。可读时为 null。</summary>
        [JsonPropertyName("eppError")]
        public string? EppError { get; set; }

        /// <summary>Boost 模式，0-4（0 关闭 / 1 开启 / 2 激进 / 3 高效开启 / 4 高效激进）。</summary>
        [JsonPropertyName("boostMode")]
        public int? BoostMode { get; set; }

        /// <summary>Boost 模式这一项是否可读。</summary>
        [JsonPropertyName("boostModeSupported")]
        public bool BoostModeSupported { get; set; }

        /// <summary>Boost 模式不可读的原因。</summary>
        [JsonPropertyName("boostModeError")]
        public string? BoostModeError { get; set; }

        /// <summary>最大处理器状态，50-100（百分比）。</summary>
        [JsonPropertyName("maxProcessorState")]
        public int? MaxProcessorState { get; set; }

        /// <summary>最大处理器状态这一项是否可读。</summary>
        [JsonPropertyName("maxProcessorStateSupported")]
        public bool MaxProcessorStateSupported { get; set; }

        /// <summary>最大处理器状态不可读的原因。</summary>
        [JsonPropertyName("maxProcessorStateError")]
        public string? MaxProcessorStateError { get; set; }

        /// <summary>
        /// 核心停泊最小核心数，0-100（百分比）。100 = 所有核都不停泊。
        /// 低于 100 时 Windows 会停泊空闲核，CPU Set 是软约束，线程会被调度到集合外。
        /// </summary>
        [JsonPropertyName("coreParkingMin")]
        public int? CoreParkingMin { get; set; }

        /// <summary>核心停泊这一项是否可读。</summary>
        [JsonPropertyName("coreParkingMinSupported")]
        public bool CoreParkingMinSupported { get; set; }

        /// <summary>核心停泊不可读的原因。</summary>
        [JsonPropertyName("coreParkingMinError")]
        public string? CoreParkingMinError { get; set; }

        /// <summary>ASPM（PCIe 链路电源管理），0-2（0 关闭 / 1 中等 / 2 最大节能）。</summary>
        [JsonPropertyName("aspm")]
        public int? Aspm { get; set; }

        /// <summary>ASPM 这一项是否可读。</summary>
        [JsonPropertyName("aspmSupported")]
        public bool AspmSupported { get; set; }

        /// <summary>ASPM 不可读的原因。</summary>
        [JsonPropertyName("aspmError")]
        public string? AspmError { get; set; }

        /// <summary>
        /// 处理器空闲是否被禁用（C-State 总开关）。<b>true 表示禁用空闲</b>，
        /// 与注册表里的 1 对应 —— 这个字段名沿用的是系统设置项自己的叫法
        /// （Processor idle disable），不是「空闲已启用」。
        /// </summary>
        [JsonPropertyName("idleDisable")]
        public bool? IdleDisable { get; set; }

        /// <summary>C-State 这一项是否可读。</summary>
        [JsonPropertyName("idleDisableSupported")]
        public bool IdleDisableSupported { get; set; }

        /// <summary>C-State 不可读的原因。</summary>
        [JsonPropertyName("idleDisableError")]
        public string? IdleDisableError { get; set; }

        // ==================== 电池（DC）====================
        //
        // 与上面 AC 五项一一对应。系统电源方案里每个设置项都存成 AC / DC 两个值，
        // 笔记本上两者常常不同（插电全速、用电池降频）。
        // 台式机没有电池，<see cref="HasBattery"/> 为 false，这五项没有意义。

        /// <summary>本机是否有电池。false 时 DC 五项恒为「不可读」。</summary>
        [JsonPropertyName("hasBattery")]
        public bool HasBattery { get; set; }

        /// <summary>
        /// 此刻是否在用交流电。null = 读不到供电状态。
        /// </summary>
        /// <remarks>
        /// 「读不到」与「在用电池」是两件事：前者界面上什么都不该显示，
        /// 后者要显示「电池供电」。折成一个 false 会让台式机也写着「电池供电」。
        /// </remarks>
        [JsonPropertyName("onAcPower")]
        public bool? OnAcPower { get; set; }

        /// <summary>电池供电下的 EPP，0-100。</summary>
        [JsonPropertyName("dcEpp")]
        public int? DcEpp { get; set; }

        /// <summary>电池 EPP 是否可读。</summary>
        [JsonPropertyName("dcEppSupported")]
        public bool DcEppSupported { get; set; }

        /// <summary>电池 EPP 不可读的原因。</summary>
        [JsonPropertyName("dcEppError")]
        public string? DcEppError { get; set; }

        /// <summary>电池供电下的 Boost 模式，0-4。</summary>
        [JsonPropertyName("dcBoostMode")]
        public int? DcBoostMode { get; set; }

        /// <summary>电池 Boost 是否可读。</summary>
        [JsonPropertyName("dcBoostModeSupported")]
        public bool DcBoostModeSupported { get; set; }

        /// <summary>电池 Boost 不可读的原因。</summary>
        [JsonPropertyName("dcBoostModeError")]
        public string? DcBoostModeError { get; set; }

        /// <summary>电池供电下的最大处理器状态，50-100。</summary>
        [JsonPropertyName("dcMaxProcessorState")]
        public int? DcMaxProcessorState { get; set; }

        /// <summary>电池最大处理器状态是否可读。</summary>
        [JsonPropertyName("dcMaxProcessorStateSupported")]
        public bool DcMaxProcessorStateSupported { get; set; }

        /// <summary>电池最大处理器状态不可读的原因。</summary>
        [JsonPropertyName("dcMaxProcessorStateError")]
        public string? DcMaxProcessorStateError { get; set; }

        /// <summary>电池供电下的核心停泊最小核心数，0-100。</summary>
        [JsonPropertyName("dcCoreParkingMin")]
        public int? DcCoreParkingMin { get; set; }

        /// <summary>电池核心停泊是否可读。</summary>
        [JsonPropertyName("dcCoreParkingMinSupported")]
        public bool DcCoreParkingMinSupported { get; set; }

        /// <summary>电池核心停泊不可读的原因。</summary>
        [JsonPropertyName("dcCoreParkingMinError")]
        public string? DcCoreParkingMinError { get; set; }

        /// <summary>电池供电下的 ASPM，0-2。</summary>
        [JsonPropertyName("dcAspm")]
        public int? DcAspm { get; set; }

        /// <summary>电池 ASPM 是否可读。</summary>
        [JsonPropertyName("dcAspmSupported")]
        public bool DcAspmSupported { get; set; }

        /// <summary>电池 ASPM 不可读的原因。</summary>
        [JsonPropertyName("dcAspmError")]
        public string? DcAspmError { get; set; }

        /// <summary>电池供电下是否禁用空闲。true = 禁用。</summary>
        [JsonPropertyName("dcIdleDisable")]
        public bool? DcIdleDisable { get; set; }

        /// <summary>电池 C-State 是否可读。</summary>
        [JsonPropertyName("dcIdleDisableSupported")]
        public bool DcIdleDisableSupported { get; set; }

        /// <summary>电池 C-State 不可读的原因。</summary>
        [JsonPropertyName("dcIdleDisableError")]
        public string? DcIdleDisableError { get; set; }

        /// <summary>
        /// 造一份「什么都读不到」的快照。
        ///
        /// 五项共用同一条 <paramref name="error"/>：这个状态下原因只有一个 ——
        /// 拿不到活动方案，于是没有任何一项有可读的对象。给五项各编一句不同的话
        /// 只会让界面显示五种说法，实际是同一个故障。
        /// </summary>
        public static PowerPolicySnapshot Unavailable(string error) => new()
        {
            ActiveSchemeGuid = Guid.Empty,
            ActiveSchemeName = UnknownSchemeName,
            EppSupported = false,
            EppError = error,
            BoostModeSupported = false,
            BoostModeError = error,
            MaxProcessorStateSupported = false,
            MaxProcessorStateError = error,
            CoreParkingMinSupported = false,
            CoreParkingMinError = error,
            AspmSupported = false,
            AspmError = error,
            IdleDisableSupported = false,
            IdleDisableError = error,

            // HasBattery 保持 false：这个状态下连活动方案都取不到，
            // 说明系统电源接口整体不可用，此时主张「有电池」只会让界面
            // 多画出一个同样不可用的电池策略。整页本来就是遮罩态。
            HasBattery = false,
            DcEppSupported = false,
            DcEppError = error,
            DcBoostModeSupported = false,
            DcBoostModeError = error,
            DcMaxProcessorStateSupported = false,
            DcMaxProcessorStateError = error,
            DcCoreParkingMinSupported = false,
            DcCoreParkingMinError = error,
            DcAspmSupported = false,
            DcAspmError = error,
            DcIdleDisableSupported = false,
            DcIdleDisableError = error,
        };
    }
}
