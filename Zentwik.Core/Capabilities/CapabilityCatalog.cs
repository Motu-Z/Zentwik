using System;
using System.Collections.Generic;
using Zentwik.Core.Services;

namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 能力目录的实现。注册在启动早期完成，之后只更新状态，因此读多写少。
    /// 所有写入串行化，读操作复制一份数组返回，调用方不会看到半更新的集合。
    /// </summary>
    public sealed class CapabilityCatalog : ICapabilityCatalog
    {
        private readonly object _sync = new();
        private readonly List<Entry> _entries = new();

        /// <summary>构造时确定厂商，避免每次快照都读注册表。</summary>
        public CapabilityCatalog(CpuVendor vendor)
        {
            Vendor = vendor;
        }

        public CpuVendor Vendor { get; }

        public IReadOnlyList<ModuleDescriptor> Modules
        {
            get
            {
                lock (_sync)
                {
                    ModuleDescriptor[] result = new ModuleDescriptor[_entries.Count];
                    for (int i = 0; i < _entries.Count; i++)
                        result[i] = _entries[i].Descriptor;

                    return result;
                }
            }
        }

        public CapabilityState Query(string capability)
        {
            if (string.IsNullOrEmpty(capability))
                return CapabilityState.None;

            lock (_sync)
            {
                CapabilityState state = CapabilityState.None;

                foreach (Entry entry in _entries)
                    state |= entry.StateOf(capability);

                return state;
            }
        }

        public bool IsUsable(string capability) => Query(capability).IsUsable();

        public CapabilitySnapshot CreateSnapshot()
        {
            lock (_sync)
            {
                List<ModuleDescriptor> modules = new(_entries.Count);
                List<CapabilityEntry> capabilities = new();

                foreach (Entry entry in _entries)
                {
                    modules.Add(entry.Descriptor);

                    foreach (CapabilityContribution contribution in entry.Descriptor.Provides)
                    {
                        capabilities.Add(new CapabilityEntry
                        {
                            Capability = contribution.Capability,
                            ProviderModuleId = entry.Descriptor.Id,
                            State = entry.StateOf(contribution.Capability),
                            Reason = entry.ReasonOf(contribution)
                        });
                    }
                }

                return new CapabilitySnapshot
                {
                    Vendor = CpuVendorNames.ToName(Vendor),
                    Modules = modules,
                    Capabilities = capabilities
                };
            }
        }

        /// <summary>
        /// 登记一个模块。同 Id 重复登记视为替换（模块热重配只发生在测试里）。
        /// 返回 false 表示依赖缺失，模块以「未安装」状态登记，能力不可用。
        /// </summary>
        public bool Register(ModuleDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            lock (_sync)
            {
                string? missing = FindMissingDependency(descriptor);

                ModuleState state = descriptor.State;
                string? reason = descriptor.Reason;

                if (missing is not null)
                {
                    state &= ~(ModuleState.Installed | ModuleState.HardwareSupported | ModuleState.Active);
                    reason = $"缺少依赖模块：{missing}";
                }

                ModuleDescriptor effective = descriptor with { State = state, Reason = reason };

                int index = _entries.FindIndex(e => e.Descriptor.Id == descriptor.Id);
                if (index >= 0)
                    _entries[index] = new Entry(effective);
                else
                    _entries.Add(new Entry(effective));

                return missing is null;
            }
        }

        /// <summary>
        /// 模块初始化失败：置失败位并清掉运行与活动位。
        /// 失败与「尚未启动」必须能区分开，否则界面无法给出正确提示。
        /// </summary>
        public void MarkFailed(string moduleId, string reason)
        {
            if (string.IsNullOrEmpty(moduleId))
                return;

            lock (_sync)
            {
                int index = _entries.FindIndex(e => e.Descriptor.Id == moduleId);
                if (index < 0)
                    return;

                Entry entry = _entries[index];
                ModuleState state =
                    (entry.Descriptor.State | ModuleState.Failed) &
                    ~(ModuleState.RuntimeAvailable | ModuleState.Active);

                entry.Descriptor = entry.Descriptor with { State = state, Reason = reason };
            }
        }

        /// <summary>回填运行探测结果：可用则置运行与活动位，不可用则清掉并保留原因。</summary>
        public void SetAvailability(string moduleId, bool available, string? reason)
        {
            if (string.IsNullOrEmpty(moduleId))
                return;

            lock (_sync)
            {
                int index = _entries.FindIndex(e => e.Descriptor.Id == moduleId);
                if (index < 0)
                    return;

                Entry entry = _entries[index];

                // 依赖缺失的模块不具备安装位，不允许被置为运行可用。
                if (available && (entry.Descriptor.State & ModuleState.Installed) == 0)
                    return;

                ModuleState state = available
                    ? entry.Descriptor.State | ModuleState.RuntimeAvailable | ModuleState.Active
                    : entry.Descriptor.State & ~(ModuleState.RuntimeAvailable | ModuleState.Active);

                entry.Descriptor = entry.Descriptor with
                {
                    State = state,
                    Reason = available ? null : reason ?? entry.Descriptor.Reason
                };
            }
        }

        private string? FindMissingDependency(ModuleDescriptor descriptor)
        {
            foreach (string required in descriptor.Requires)
            {
                if (string.IsNullOrEmpty(required))
                    continue;

                if (!_entries.Exists(e => e.Descriptor.Id == required))
                    return required;
            }

            return null;
        }

        private sealed class Entry
        {
            public Entry(ModuleDescriptor descriptor)
            {
                Descriptor = descriptor;
            }

            public ModuleDescriptor Descriptor { get; set; }

            public CapabilityState StateOf(string capability)
            {
                foreach (CapabilityContribution contribution in Descriptor.Provides)
                {
                    if (string.Equals(contribution.Capability, capability, StringComparison.Ordinal))
                        return ToCapabilityState(Descriptor.State) & contribution.Mask;
                }

                return CapabilityState.None;
            }

            public string? ReasonOf(CapabilityContribution contribution) =>
                contribution.Reason ?? Descriptor.Reason;
        }

        /// <summary>模块位到能力位的一一映射。两套枚举刻意分开：能力不认识「失败」位。</summary>
        internal static CapabilityState ToCapabilityState(ModuleState module)
        {
            CapabilityState state = CapabilityState.None;

            if ((module & ModuleState.Installed) != 0)
                state |= CapabilityState.Installed;

            if ((module & ModuleState.Enabled) != 0)
                state |= CapabilityState.Enabled;

            if ((module & ModuleState.HardwareSupported) != 0)
                state |= CapabilityState.HardwareSupported;

            if ((module & ModuleState.RuntimeAvailable) != 0)
                state |= CapabilityState.RuntimeAvailable;

            if ((module & ModuleState.Active) != 0)
                state |= CapabilityState.Active;

            return state;
        }
    }
}
