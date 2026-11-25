using System.Collections.Generic;

namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 能力目录：模块注册进来，能力由模块的贡献与状态派生。
    ///
    /// 查询接口只有两个：单项目标状态与整份快照。界面与 IPC 都走这里，
    /// 不直接读某个厂商服务的字段。
    /// </summary>
    public interface ICapabilityCatalog
    {
        /// <summary>已注册模块的当前描述（含状态）。</summary>
        IReadOnlyList<ModuleDescriptor> Modules { get; }

        /// <summary>按能力 Id 合并所有提供者的状态；无人提供时返回 <see cref="CapabilityState.None"/>。</summary>
        CapabilityState Query(string capability);

        /// <summary>是否存在可执行的提供者。</summary>
        bool IsUsable(string capability);

        /// <summary>当前完整快照。</summary>
        CapabilitySnapshot CreateSnapshot();
    }
}
