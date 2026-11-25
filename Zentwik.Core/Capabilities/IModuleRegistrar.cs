using System;
using Microsoft.Extensions.DependencyInjection;
using Zentwik.Core.Services;

namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 模块注册入口。实现方只负责把服务登记进宿主的容器，
    /// 不得自行 <c>BuildServiceProvider</c>：整个进程只有一个容器。
    /// </summary>
    public interface IModuleRegistrar
    {
        /// <summary>模块描述。状态与能力在构造时按当前平台确定。</summary>
        ModuleDescriptor Descriptor { get; }

        /// <summary>把模块的服务登记进容器。抛异常时该模块被标记为失败，其他模块继续。</summary>
        void Register(IServiceCollection services, ModuleRegistrationContext context);
    }

    /// <summary>注册期上下文。只暴露注册需要的东西，不暴露容器。</summary>
    public sealed class ModuleRegistrationContext
    {
        public ModuleRegistrationContext(CapabilityCatalog catalog, Action<LogOptions>? configureLog = null)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            Catalog = catalog;
            ConfigureLog = configureLog;
        }

        public CapabilityCatalog Catalog { get; }

        /// <summary>日志参数，只有 core.runtime 使用；其余模块为 null。</summary>
        public Action<LogOptions>? ConfigureLog { get; }
    }
}
