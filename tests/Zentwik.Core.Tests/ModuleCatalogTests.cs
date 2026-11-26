using Xunit;
using Zentwik.Core.Modules;
using Zentwik.Domain.Capabilities;

namespace Zentwik.Core.Tests;

public sealed class ModuleCatalogTests
{
    [Fact]
    public void CpuTuning_Is_Ready_When_Only_One_Vendor_Applies()
    {
        var modules = ModuleCatalog.Describe(
        [
            new CapabilityDescriptor { Id = CapabilityIds.AmdSmu, State = CapabilityState.Running, Detail = "AMD 可用" },
            new CapabilityDescriptor { Id = CapabilityIds.IntelMsr, State = CapabilityState.Unavailable, Detail = "处理器不是 Intel" },
        ], simulated: false);

        var module = modules.Single(static candidate => candidate.Id == "cpu-tuning");

        Assert.Equal("ready", module.State);
        Assert.DoesNotContain("Intel", module.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuTuning_Is_Unavailable_When_No_Vendor_Applies()
    {
        var modules = ModuleCatalog.Describe(
        [
            new CapabilityDescriptor { Id = CapabilityIds.AmdSmu, State = CapabilityState.Unavailable },
            new CapabilityDescriptor { Id = CapabilityIds.IntelMsr, State = CapabilityState.Unavailable },
        ], simulated: false);

        Assert.Equal("unavailable", modules.Single(static candidate => candidate.Id == "cpu-tuning").State);
    }

    [Fact]
    public void Scheduling_Degrades_When_CpuSets_Missing()
    {
        var modules = ModuleCatalog.Describe(
        [
            new CapabilityDescriptor { Id = CapabilityIds.AffinityMask, State = CapabilityState.Enabled },
            new CapabilityDescriptor { Id = CapabilityIds.CpuSets, State = CapabilityState.Unavailable },
        ], simulated: false);

        Assert.Equal("degraded", modules.Single(static candidate => candidate.Id == "scheduling").State);
    }

    [Fact]
    public void Optional_Module_Is_Not_Ready_Until_Enabled()
    {
        var capabilities = new[]
        {
            new CapabilityDescriptor { Id = CapabilityIds.AmdPowerLimit, State = CapabilityState.Running, Detail = "功耗表可读" },
        };

        var disabled = ModuleCatalog.Describe(capabilities, simulated: false, enabledOptional: new HashSet<string>()).Single(
            static candidate => candidate.Id == ModuleIds.PowerLimits);

        Assert.Equal("unavailable", disabled.State);
        Assert.False(disabled.Enabled);
        Assert.True(disabled.Optional);

        var enabled = ModuleCatalog.Describe(capabilities, simulated: false, enabledOptional: new HashSet<string> { ModuleIds.PowerLimits })
            .Single(static candidate => candidate.Id == ModuleIds.PowerLimits);

        Assert.Equal("ready", enabled.State);
        Assert.True(enabled.Enabled);
    }

    [Fact]
    public void BuiltIn_Modules_Are_Always_Enabled()
    {
        var modules = ModuleCatalog.Describe([], simulated: false, enabledOptional: new HashSet<string>());

        var monitoring = modules.Single(static candidate => candidate.Id == "monitoring");

        Assert.True(monitoring.Enabled);
        Assert.False(monitoring.Optional);
    }
}
