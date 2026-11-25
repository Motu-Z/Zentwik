using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Zentwik.Core.Capabilities;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// <c>query-capabilities</c> 的 Payload。五个状态维度各自以独立字符串给出，
    /// 同时附上合并后的 <c>usable</c>：界面不需要自己按位推算可用性。
    /// </summary>
    public sealed class CapabilitySnapshotPayload
    {
        [JsonPropertyName("vendor")]
        public string? Vendor { get; set; }

        [JsonPropertyName("modules")]
        public List<ModuleInfoPayload> Modules { get; set; } = new();

        [JsonPropertyName("capabilities")]
        public List<CapabilityInfoPayload> Capabilities { get; set; } = new();

        /// <summary>把内部快照转成线格式。</summary>
        public static CapabilitySnapshotPayload From(CapabilitySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            CapabilitySnapshotPayload payload = new() { Vendor = snapshot.Vendor };

            foreach (ModuleDescriptor module in snapshot.Modules)
            {
                ModuleInfoPayload item = new()
                {
                    Id = module.Id,
                    Name = module.DisplayName,
                    Version = module.Version.ToString(),
                    Reason = module.Reason
                };

                item.Requires.AddRange(module.Requires);

                foreach (CapabilityContribution contribution in module.Provides)
                    item.Provides.Add(contribution.Capability);

                AddModuleStates(item.States, module.State);
                payload.Modules.Add(item);
            }

            foreach (CapabilityEntry entry in snapshot.Capabilities)
            {
                CapabilityInfoPayload item = new()
                {
                    Id = entry.Capability,
                    Provider = entry.ProviderModuleId,
                    Usable = entry.State.IsUsable(),
                    Reason = entry.Reason
                };

                AddModuleStates(item.States, ToModuleState(entry.State));
                payload.Capabilities.Add(item);
            }

            return payload;
        }

        private static void AddModuleStates(List<string> target, ModuleState state)
        {
            if ((state & ModuleState.Installed) != 0)
                target.Add("installed");

            if ((state & ModuleState.Enabled) != 0)
                target.Add("enabled");

            if ((state & ModuleState.HardwareSupported) != 0)
                target.Add("hardwareSupported");

            if ((state & ModuleState.RuntimeAvailable) != 0)
                target.Add("runtimeAvailable");

            if ((state & ModuleState.Active) != 0)
                target.Add("active");

            if ((state & ModuleState.Failed) != 0)
                target.Add("failed");
        }

        /// <summary>能力状态没有失败位；线格式统一按模块状态枚举输出。</summary>
        private static ModuleState ToModuleState(CapabilityState state)
        {
            ModuleState result = ModuleState.None;

            if ((state & CapabilityState.Installed) != 0)
                result |= ModuleState.Installed;

            if ((state & CapabilityState.Enabled) != 0)
                result |= ModuleState.Enabled;

            if ((state & CapabilityState.HardwareSupported) != 0)
                result |= ModuleState.HardwareSupported;

            if ((state & CapabilityState.RuntimeAvailable) != 0)
                result |= ModuleState.RuntimeAvailable;

            if ((state & CapabilityState.Active) != 0)
                result |= ModuleState.Active;

            return result;
        }
    }

    public sealed class ModuleInfoPayload
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("requires")]
        public List<string> Requires { get; set; } = new();

        [JsonPropertyName("provides")]
        public List<string> Provides { get; set; } = new();

        [JsonPropertyName("states")]
        public List<string> States { get; set; } = new();

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    public sealed class CapabilityInfoPayload
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("states")]
        public List<string> States { get; set; } = new();

        [JsonPropertyName("usable")]
        public bool Usable { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
