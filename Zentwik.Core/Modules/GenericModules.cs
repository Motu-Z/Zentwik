using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Services;

namespace Zentwik.Core.Modules
{
    /// <summary>进程绑定、优先级与监听。核心组宽度是可选提示，缺失时按观测拓扑分组。</summary>
    internal sealed class GenericSchedulingModule : IModuleRegistrar
    {
        public ModuleDescriptor Descriptor { get; } = new()
        {
            Id = "generic.scheduling",
            DisplayName = "进程调度",
            Version = new Version(1, 0),
            Requires = new[] { "core.runtime" },
            Provides = new[] { new CapabilityContribution(CapabilityIds.SchedulingProcess) }
        };

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            // 兜底实现：厂商模块可用时由它覆盖。
            services.TryAddSingleton<ICoreGroupHintProvider>(NullCoreGroupHintProvider.Instance);

            services.TryAddSingleton<CpuSetScheduler>();
            services.TryAddSingleton<ICpuSetScheduler>(provider => provider.GetRequiredService<CpuSetScheduler>());

            services.TryAddSingleton<ProcessWatcher>();
            services.TryAddSingleton<IProcessWatcher>(provider => provider.GetRequiredService<ProcessWatcher>());
        }
    }

    /// <summary>Windows 电源方案与策略项。</summary>
    internal sealed class GenericPowerModule : IModuleRegistrar
    {
        public ModuleDescriptor Descriptor { get; } = new()
        {
            Id = "generic.power",
            DisplayName = "电源策略",
            Version = new Version(1, 0),
            Requires = new[] { "core.runtime" },
            Provides = new[] { new CapabilityContribution(CapabilityIds.PowerPolicy) }
        };

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            // PowerPolicyApi 无状态（每次调用都是一次系统 API），单例只是省一次分配。
            services.TryAddSingleton<PowerPolicyApi>();
            services.TryAddSingleton<IPowerPolicyApi>(provider => provider.GetRequiredService<PowerPolicyApi>());
            services.TryAddSingleton<PowerPolicyService>();

            // 没有硬件依赖、也没有启动步骤：服务可解析即视为运行就绪。
            context.Catalog.SetAvailability(Descriptor.Id, true, null);
        }
    }

    /// <summary>三类方案的统一存储与切换编排。方案内容按类型各自持久化，不共用字段。</summary>
    internal sealed class GenericPlanModule : IModuleRegistrar
    {
        public ModuleDescriptor Descriptor { get; } = new()
        {
            Id = "generic.plan",
            DisplayName = "方案管理",
            Version = new Version(1, 0),
            Requires = new[] { "core.runtime" }
        };

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            services.TryAddSingleton<TuningApplyService>();
            services.TryAddSingleton<PlanService>();
        }
    }

    /// <summary>场景规则、监听与休眠恢复补写。</summary>
    internal sealed class GenericScenarioModule : IModuleRegistrar
    {
        public ModuleDescriptor Descriptor { get; } = new()
        {
            Id = "generic.scenario",
            DisplayName = "场景自动化",
            Version = new Version(1, 0),
            Requires = new[] { "core.runtime", "generic.plan" },
            Provides = new[] { new CapabilityContribution(CapabilityIds.ScenarioRules) }
        };

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            services.TryAddSingleton<ScenarioService>();
            services.TryAddSingleton<ScenarioMonitor>();

            // 休眠恢复监听：Worker 启动路径与设置页开关共用同一个实例，
            // 两处分别 Start / Stop 会打架，所以只注册单例。
            services.TryAddSingleton<SleepResumeWatcher>();
        }
    }

    /// <summary>开机自启。托盘由 Worker 进程自建，不在此模块内。</summary>
    internal sealed class GenericShellModule : IModuleRegistrar
    {
        public ModuleDescriptor Descriptor { get; } = new()
        {
            Id = "generic.shell",
            DisplayName = "外壳与自启",
            Version = new Version(1, 0),
            Requires = new[] { "core.runtime" },
            Provides = new[] { new CapabilityContribution(CapabilityIds.ShellAutoStart) }
        };

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            services.TryAddSingleton<AutoStartService>();
        }
    }
}
