using System;

namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 能力状态。五个维度各自独立，不做线性层级：
    /// 「装了但硬件不支持」与「硬件支持但驱动没起来」必须能被区分开。
    /// </summary>
    [Flags]
    public enum CapabilityState
    {
        None = 0,

        /// <summary>模块清单存在且载荷完整。</summary>
        Installed = 1,

        /// <summary>用户未禁用该模块。</summary>
        Enabled = 2,

        /// <summary>当前处理器 / 设备与该能力匹配。</summary>
        HardwareSupported = 4,

        /// <summary>权限、驱动、设备应答等运行条件就绪。</summary>
        RuntimeAvailable = 8,

        /// <summary>当前正在生效（例如已应用调校、托盘已显示）。</summary>
        Active = 16
    }

    public static class CapabilityStateExtensions
    {
        /// <summary>全部位。贡献掩码用它表示「完全跟随模块状态」。</summary>
        public const CapabilityState All =
            CapabilityState.Installed |
            CapabilityState.Enabled |
            CapabilityState.HardwareSupported |
            CapabilityState.RuntimeAvailable |
            CapabilityState.Active;

        /// <summary>能力位一律具备，可以执行对应动作。</summary>
        public const CapabilityState UsableMask =
            CapabilityState.Installed |
            CapabilityState.Enabled |
            CapabilityState.HardwareSupported |
            CapabilityState.RuntimeAvailable;

        /// <summary>可执行：安装、启用、硬件支持、运行就绪四项同时成立。</summary>
        public static bool IsUsable(this CapabilityState state) => (state & UsableMask) == UsableMask;

        /// <summary>硬件支持但运行条件不足（驱动缺失、未提权）。界面应显示诊断入口而非隐藏。</summary>
        public static bool IsRuntimeBlocked(this CapabilityState state) =>
            (state & (CapabilityState.Installed | CapabilityState.Enabled | CapabilityState.HardwareSupported)) ==
            (CapabilityState.Installed | CapabilityState.Enabled | CapabilityState.HardwareSupported)
            && (state & CapabilityState.RuntimeAvailable) == 0;
    }
}
