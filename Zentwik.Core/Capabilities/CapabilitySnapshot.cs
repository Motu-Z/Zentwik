using System.Collections.Generic;

namespace Zentwik.Core.Capabilities
{
    /// <summary>能力快照中的一条：同一个能力可由多个模块提供，因此按提供者分行。</summary>
    public sealed class CapabilityEntry
    {
        public required string Capability { get; init; }

        public required string ProviderModuleId { get; init; }

        public CapabilityState State { get; init; }

        /// <summary>该条状态的原因；不可用时指明是缺硬件、缺驱动还是被禁用。</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// 一次能力查询的完整结果：模块清单 + 每项能力的按提供者分行状态。
    ///
    /// 界面拿它决定导航与按钮可用性，不得反过来按厂商名推断功能是否存在。
    /// </summary>
    public sealed class CapabilitySnapshot
    {
        public string Vendor { get; init; } = string.Empty;

        public IReadOnlyList<ModuleDescriptor> Modules { get; init; } = new List<ModuleDescriptor>();

        public IReadOnlyList<CapabilityEntry> Capabilities { get; init; } = new List<CapabilityEntry>();

        /// <summary>按能力 Id 合并所有提供者的状态：任一提供者具备某位即计为该位。</summary>
        public CapabilityState StateOf(string capability)
        {
            CapabilityState state = CapabilityState.None;

            for (int i = 0; i < Capabilities.Count; i++)
            {
                CapabilityEntry entry = Capabilities[i];
                if (entry.Capability == capability)
                    state |= entry.State;
            }

            return state;
        }

        /// <summary>该能力是否可以执行。</summary>
        public bool IsUsable(string capability) => StateOf(capability).IsUsable();
    }
}
