using System;

namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 模块状态。与 <see cref="CapabilityState"/> 同构但多一个失败位：
    /// 模块自身的初始化失败必须能被单独看到，而不是笼统地表现为「不可用」。
    /// </summary>
    [Flags]
    public enum ModuleState
    {
        None = 0,

        /// <summary>清单里存在该模块，载荷文件完整。</summary>
        Installed = 1,

        /// <summary>用户未禁用。</summary>
        Enabled = 2,

        /// <summary>当前硬件匹配该模块。</summary>
        HardwareSupported = 4,

        /// <summary>驱动、权限、设备应答等运行条件就绪。</summary>
        RuntimeAvailable = 8,

        /// <summary>模块已启动并在工作。</summary>
        Active = 16,

        /// <summary>注册或启动过程中抛出异常。其他模块不受影响。</summary>
        Failed = 32
    }

    public static class ModuleStateExtensions
    {
        /// <summary>默认状态：内置模块视为已安装并启用，硬件与运行条件待探测。</summary>
        public const ModuleState BuiltIn =
            ModuleState.Installed | ModuleState.Enabled | ModuleState.HardwareSupported;
    }
}
