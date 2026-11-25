using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Modules;
using Zentwik.Core.Services;

namespace Zentwik.Core.DependencyInjection
{
    /// <summary>
    /// Core 的模块注册入口。
    ///
    /// Core 只引用 <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>，
    /// 不引用 Hosting：容器由宿主（UI 进程 / Worker 进程 / 自检工具）创建，
    /// Core 只负责「把哪些服务按什么生命周期登记进去」。
    ///
    /// 内置模块按固定顺序注册。单个模块注册失败只影响它自己：
    /// 失败信息进 <see cref="CapabilityCatalog"/>，Worker 用其余模块继续启动。
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册全部内置模块。厂商按注册表探测。
        /// </summary>
        /// <param name="services">宿主容器。</param>
        /// <param name="configureLog">日志参数。为 null 时使用默认值（临时目录、200 条上限、zentwik- 前缀）。</param>
        public static IServiceCollection AddZentwikCore(
            this IServiceCollection services,
            Action<LogOptions>? configureLog = null)
            => services.AddZentwikModules(CpuVendorProbe.Detect(), configureLog);

        /// <summary>
        /// 按指定厂商注册内置模块。自检工具用它绕过注册表探测。
        /// </summary>
        public static IServiceCollection AddZentwikModules(
            this IServiceCollection services,
            CpuVendor vendor,
            Action<LogOptions>? configureLog = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            // 重复调用时复用已有目录，否则容器里会存在两份状态。
            CapabilityCatalog catalog =
                services.FirstOrDefault(d => d.ServiceType == typeof(CapabilityCatalog))?.ImplementationInstance
                    as CapabilityCatalog
                ?? new CapabilityCatalog(vendor);

            services.TryAddSingleton(catalog);
            services.TryAddSingleton<ICapabilityCatalog>(catalog);

            ModuleHost host = new ModuleHost(vendor)
                .Add(new CoreRuntimeModule())
                .Add(new GenericPowerModule())
                .Add(new GenericSchedulingModule())
                .Add(new GenericPlanModule())
                .Add(new GenericScenarioModule())
                .Add(new GenericShellModule())
                .Add(new AmdCpuModule(vendor))
                .Add(new IntelCpuModule(vendor));

            host.Register(services, new ModuleRegistrationContext(catalog, configureLog));

            services.TryAddSingleton(host);
            return services;
        }
    }
}
