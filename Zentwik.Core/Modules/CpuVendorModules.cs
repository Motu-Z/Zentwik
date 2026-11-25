using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Services;

namespace Zentwik.Core.Modules
{
    /// <summary>
    /// AMD CPU 调校：SMU 会话、CO 掩码、FMax、CCD 宽度提示。
    /// 平台不匹配时模块仍登记，但状态缺少硬件支持位，能力全部不可执行。
    /// </summary>
    internal sealed class AmdCpuModule : IModuleRegistrar
    {
        private readonly CpuVendor _vendor;

        public AmdCpuModule(CpuVendor vendor)
        {
            _vendor = vendor;

            bool supported = vendor != CpuVendor.Intel;

            Descriptor = new ModuleDescriptor
            {
                Id = "amd.cpu",
                DisplayName = "AMD 处理器调校",
                Version = new Version(1, 0),
                Requires = new[] { "core.runtime" },
                Provides = new[]
                {
                    new CapabilityContribution(CapabilityIds.TuningRead),
                    new CapabilityContribution(CapabilityIds.TuningApply),
                    new CapabilityContribution(CapabilityIds.TuningPlan),
                    new CapabilityContribution(CapabilityIds.TuningBaselineRestore),
                    new CapabilityContribution(CapabilityIds.TopologyGroupHint)
                },
                State = supported
                    ? ModuleStateExtensions.BuiltIn
                    : ModuleState.Installed | ModuleState.Enabled,
                Reason = supported ? null : "当前处理器不是 AMD，SMU 通道不适用"
            };
        }

        public ModuleDescriptor Descriptor { get; }

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            if (_vendor == CpuVendor.Intel)
            {
                services.TryAddSingleton<IAmdTuningChannel>(_ => new UnsupportedAmdTuningChannel());
                return;
            }

            // SmuReader 必须是单例：进程内只允许存在一个 Cpu 实例，
            // Opcode 的代码缓存是进程级静态资源，多一个实例就会互相打哑。
            services.TryAddSingleton<SmuReader>();
            services.TryAddSingleton<IAmdTuningChannel>(provider => provider.GetRequiredService<SmuReader>());

            // 核心组宽度只有 SMU 能直给，用它覆盖调度模块的兜底实现。
            services.Replace(ServiceDescriptor.Singleton<ICoreGroupHintProvider, AmdCoreGroupHintProvider>());
        }
    }

    /// <summary>
    /// Intel CPU 调校：MSR 电压三平面与睿频倍率。
    /// 不提供方案保存：Intel 调校值目前不进 plans.json，能力清单如实反映这一点。
    /// </summary>
    internal sealed class IntelCpuModule : IModuleRegistrar
    {
        private readonly CpuVendor _vendor;

        public IntelCpuModule(CpuVendor vendor)
        {
            _vendor = vendor;

            bool supported = vendor == CpuVendor.Intel;

            Descriptor = new ModuleDescriptor
            {
                Id = "intel.cpu",
                DisplayName = "Intel 处理器调校",
                Version = new Version(1, 0),
                Requires = new[] { "core.runtime" },
                Provides = new[]
                {
                    new CapabilityContribution(CapabilityIds.TuningRead),
                    new CapabilityContribution(CapabilityIds.TuningApply),
                    new CapabilityContribution(CapabilityIds.TuningBaselineRestore)
                },
                State = supported
                    ? ModuleStateExtensions.BuiltIn
                    : ModuleState.Installed | ModuleState.Enabled,
                Reason = supported ? null : "当前处理器不是 Intel，MSR 通道不适用"
            };
        }

        public ModuleDescriptor Descriptor { get; }

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            if (_vendor != CpuVendor.Intel)
            {
                services.TryAddSingleton<IIntelTuningChannel>(_ => new UnsupportedIntelTuningChannel());
                return;
            }

            services.TryAddSingleton<IntelTuningChannel>();
            services.TryAddSingleton<IIntelTuningChannel>(provider => provider.GetRequiredService<IntelTuningChannel>());
        }
    }
}
