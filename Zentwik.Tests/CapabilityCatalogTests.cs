using Zentwik.Core.Capabilities;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>能力模型：五个状态维度各自独立，能力由模块贡献派生，多模块同名能力按位合并。</summary>
    public sealed class CapabilityCatalogTests
    {
        private static ModuleDescriptor Module(
            string id,
            ModuleState state = ModuleStateExtensions.BuiltIn,
            string? reason = null,
            params string[] provides)
        {
            CapabilityContribution[] contributions = new CapabilityContribution[provides.Length];
            for (int i = 0; i < provides.Length; i++)
                contributions[i] = new CapabilityContribution(provides[i]);

            return new ModuleDescriptor
            {
                Id = id,
                Provides = contributions,
                State = state,
                Reason = reason
            };
        }

        [Fact]
        public void Unknown_capability_reports_none()
        {
            CapabilityCatalog catalog = new(CpuVendor.Amd);

            Assert.Equal(CapabilityState.None, catalog.Query("tuning.apply"));
            Assert.False(catalog.IsUsable("tuning.apply"));
        }

        [Fact]
        public void BuiltIn_module_makes_capability_usable()
        {
            CapabilityCatalog catalog = new(CpuVendor.Amd);
            catalog.Register(Module("amd.cpu", ModuleStateExtensions.BuiltIn | ModuleState.RuntimeAvailable,
                provides: CapabilityIds.TuningApply));

            CapabilityState state = catalog.Query(CapabilityIds.TuningApply);

            Assert.True(state.IsUsable());
            Assert.True(catalog.IsUsable(CapabilityIds.TuningApply));
            Assert.Equal(CapabilityState.None, state & CapabilityState.Active);
        }

        [Fact]
        public void Hardware_unsupported_and_runtime_unavailable_are_distinct()
        {
            CapabilityCatalog catalog = new(CpuVendor.Intel);

            // 平台不匹配：有安装与启用，无硬件支持。
            catalog.Register(Module("amd.cpu", ModuleState.Installed | ModuleState.Enabled,
                reason: "当前处理器不是 AMD，SMU 通道不适用", provides: CapabilityIds.TuningApply));

            CapabilityState unsupported = catalog.Query(CapabilityIds.TuningApply);
            Assert.False(unsupported.IsUsable());
            Assert.False(unsupported.IsRuntimeBlocked());
            Assert.Equal(CapabilityState.None, unsupported & CapabilityState.HardwareSupported);

            // 硬件支持但驱动没起来：必须能与上一种区分。
            CapabilityCatalog runtime = new(CpuVendor.Amd);
            runtime.Register(Module("amd.cpu", ModuleState.Installed | ModuleState.Enabled | ModuleState.HardwareSupported,
                reason: "PawnIO 设备不可用", provides: CapabilityIds.TuningApply));

            CapabilityState blocked = runtime.Query(CapabilityIds.TuningApply);
            Assert.False(blocked.IsUsable());
            Assert.True(blocked.IsRuntimeBlocked());
        }

        [Fact]
        public void Same_capability_from_two_modules_merges_by_bit()
        {
            CapabilityCatalog catalog = new(CpuVendor.Amd);

            catalog.Register(Module("amd.cpu", ModuleState.Installed | ModuleState.Enabled,
                reason: "SMU 通道不适用", provides: CapabilityIds.TuningRead));

            catalog.Register(Module("intel.cpu", ModuleStateExtensions.BuiltIn | ModuleState.RuntimeAvailable,
                provides: CapabilityIds.TuningRead));

            CapabilitySnapshot snapshot = catalog.CreateSnapshot();

            // 同一能力两条提供者记录，界面能看出是谁提供的。
            Assert.Equal(2, snapshot.Capabilities.Count);
            Assert.True(snapshot.IsUsable(CapabilityIds.TuningRead));
        }

        [Fact]
        public void Contribution_mask_can_remove_a_single_dimension()
        {
            CapabilityCatalog catalog = new(CpuVendor.Intel);

            // 模块整体可用，但不提供方案保存这一项。
            catalog.Register(new ModuleDescriptor
            {
                Id = "intel.cpu",
                State = ModuleStateExtensions.BuiltIn | ModuleState.RuntimeAvailable,
                Provides = new[]
                {
                    new CapabilityContribution(CapabilityIds.TuningRead),
                    new CapabilityContribution(CapabilityIds.TuningPlan, CapabilityState.None, "Intel 调校值不进方案")
                }
            });

            Assert.True(catalog.IsUsable(CapabilityIds.TuningRead));
            Assert.False(catalog.IsUsable(CapabilityIds.TuningPlan));
        }

        [Fact]
        public void Missing_dependency_keeps_module_uninstalled()
        {
            CapabilityCatalog catalog = new(CpuVendor.Amd);

            ModuleDescriptor orphan = new()
            {
                Id = "generic.scenario",
                Requires = new[] { "core.runtime" },
                State = ModuleStateExtensions.BuiltIn | ModuleState.RuntimeAvailable,
                Provides = new[] { new CapabilityContribution(CapabilityIds.ScenarioRules) }
            };

            Assert.False(catalog.Register(orphan));

            ModuleDescriptor registered = Assert.Single(catalog.Modules);
            Assert.Equal(ModuleState.None, registered.State & ModuleState.HardwareSupported);
            Assert.Contains("core.runtime", registered.Reason);
            Assert.False(catalog.IsUsable(CapabilityIds.ScenarioRules));
        }

        [Fact]
        public void MarkFailed_clears_runtime_and_active()
        {
            CapabilityCatalog catalog = new(CpuVendor.Amd);

            catalog.Register(Module("generic.scheduling",
                ModuleStateExtensions.BuiltIn | ModuleState.RuntimeAvailable | ModuleState.Active,
                provides: CapabilityIds.SchedulingProcess));

            catalog.MarkFailed("generic.scheduling", "启动失败：访问被拒绝");

            ModuleDescriptor failed = Assert.Single(catalog.Modules);
            Assert.True((failed.State & ModuleState.Failed) != 0);
            Assert.Equal(ModuleState.None, failed.State & ModuleState.RuntimeAvailable);
            Assert.Equal(ModuleState.None, failed.State & ModuleState.Active);
            Assert.Equal("启动失败：访问被拒绝", failed.Reason);
            Assert.False(catalog.IsUsable(CapabilityIds.SchedulingProcess));
        }

        [Fact]
        public void SetAvailability_round_trips()
        {
            CapabilityCatalog catalog = new(CpuVendor.Amd);
            catalog.Register(Module("amd.cpu", provides: CapabilityIds.TuningApply));

            catalog.SetAvailability("amd.cpu", true, null);
            Assert.True(catalog.IsUsable(CapabilityIds.TuningApply));

            catalog.SetAvailability("amd.cpu", false, "PawnIO 设备不可用");
            Assert.False(catalog.IsUsable(CapabilityIds.TuningApply));
            Assert.Equal("PawnIO 设备不可用", Assert.Single(catalog.Modules).Reason);
        }

        [Fact]
        public void Snapshot_payload_reports_usable_flag()
        {
            CapabilityCatalog catalog = new(CpuVendor.Amd);
            catalog.Register(Module("amd.cpu", provides: CapabilityIds.TuningApply));
            catalog.SetAvailability("amd.cpu", false, "PawnIO 设备不可用");

            Zentwik.Core.Ipc.CapabilitySnapshotPayload payload =
                Zentwik.Core.Ipc.CapabilitySnapshotPayload.From(catalog.CreateSnapshot());

            Assert.Equal("amd", payload.Vendor);
            Zentwik.Core.Ipc.CapabilityInfoPayload capability = Assert.Single(payload.Capabilities);
            Assert.False(capability.Usable);
            Assert.Contains("hardwareSupported", capability.States);
            Assert.DoesNotContain("runtimeAvailable", capability.States);
        }
    }
}
