namespace Zentwik.Core.Services
{
    /// <summary>
    /// 「恢复默认」按钮填入的一组值。
    ///
    /// Windows 不提供「读回方案出厂值」的接口：系统自带的方案被改过之后，
    /// 原始值就没有任何地方留着了。所以这里是一组折中值，不是权威默认。
    ///
    /// UI 与 Worker 各写一份的话，两边一旦不一致，界面会显示一组值、
    /// 写进系统的却是另一组 —— 而且两边都是「0 警告 0 错误」。
    /// </summary>
    public readonly record struct PowerPolicyDefaults(
        int Epp,
        int BoostMode,
        int MaxProcessorState,
        int Aspm,
        bool IdleDisable)
    {
        /// <summary>插电：偏向性能，但不关闭节能特性。</summary>
        public static PowerPolicyDefaults Ac { get; } = new(
            Epp: 50,
            BoostMode: 1,
            MaxProcessorState: 100,
            Aspm: 1,
            IdleDisable: false);

        /// <summary>用电池：降频、深省电。</summary>
        public static PowerPolicyDefaults Dc { get; } = new(
            Epp: 80,
            BoostMode: 1,
            MaxProcessorState: 80,
            Aspm: 2,
            IdleDisable: false);
    }
}
