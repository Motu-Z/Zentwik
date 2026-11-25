using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Zentwik.Core.Services;

namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 模块宿主：按登记顺序执行注册，单个模块失败不影响其他模块。
    ///
    /// 只写 <see cref="IServiceCollection"/>，不建容器 —— 容器由宿主进程建一次。
    /// 模块之间共享单例，重复打开硬件或状态分裂都来自「多建一个容器」，这里从接口上就不给机会。
    /// </summary>
    public sealed class ModuleHost
    {
        private readonly List<IModuleRegistrar> _modules = new();
        private readonly List<string> _failures = new();

        public ModuleHost(CpuVendor vendor)
        {
            Vendor = vendor;
        }

        public CpuVendor Vendor { get; }

        /// <summary>注册失败的模块说明，供启动日志一次性输出。</summary>
        public IReadOnlyList<string> Failures => _failures;

        public ModuleHost Add(IModuleRegistrar module)
        {
            ArgumentNullException.ThrowIfNull(module);
            _modules.Add(module);
            return this;
        }

        /// <summary>
        /// 依次注册。失败时把模块状态改为失败并记下原因，继续下一个模块。
        /// </summary>
        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(context);

            foreach (IModuleRegistrar module in _modules)
            {
                ModuleDescriptor descriptor = module.Descriptor;

                // 先登记描述再注册服务：模块可以在自己的 Register 里回填运行状态。
                context.Catalog.Register(descriptor);

                try
                {
                    module.Register(services, context);
                }
                catch (Exception ex)
                {
                    string reason = $"注册失败：{ex.GetType().Name}: {ex.Message}";

                    context.Catalog.MarkFailed(descriptor.Id, reason);
                    _failures.Add($"{descriptor.Id} {reason}");
                }
            }
        }
    }
}
