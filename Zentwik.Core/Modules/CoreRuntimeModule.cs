using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Core.Modules
{
    /// <summary>
    /// 核心运行时：路径、日志、四类持久化、启动护栏、采样与电源状态轮询。
    /// 不依赖任何厂商通道。
    /// </summary>
    internal sealed class CoreRuntimeModule : IModuleRegistrar
    {
        public ModuleDescriptor Descriptor { get; } = new()
        {
            Id = "core.runtime",
            DisplayName = "核心运行时",
            Version = new Version(1, 0),
            Provides = new[] { new CapabilityContribution(CapabilityIds.DiagnosticsLogs) }
        };

        public void Register(IServiceCollection services, ModuleRegistrationContext context)
        {
            // 先登记具体类型，再把接口映射到同一个实例：
            // 两个分开的 TryAddSingleton 会造出两个对象，接口与具体类型各持一份状态。
            services.TryAddSingleton<AppPaths>();
            services.TryAddSingleton<IAppPaths>(provider => provider.GetRequiredService<AppPaths>());

            services.TryAddSingleton<LogService>(provider =>
            {
                LogOptions options = new();
                context.ConfigureLog?.Invoke(options);

                // 未显式指定目录时落到 %AppData%\Zentwik\Logs
                if (string.IsNullOrWhiteSpace(options.Directory))
                    options.Directory = provider.GetRequiredService<IAppPaths>().Logs;

                return new LogService(options);
            });
            services.TryAddSingleton<ILogService>(provider => provider.GetRequiredService<LogService>());

            services.TryAddSingleton<MaskStore>();
            services.TryAddSingleton<IMaskStore>(provider => provider.GetRequiredService<MaskStore>());

            services.TryAddSingleton<SchedulingStore>();
            services.TryAddSingleton<ISchedulingStore>(provider => provider.GetRequiredService<SchedulingStore>());

            services.TryAddSingleton<TuningStore>();
            services.TryAddSingleton<ITuningStore>(provider => provider.GetRequiredService<TuningStore>());

            services.TryAddSingleton<UiSettingsStore>();
            services.TryAddSingleton<IUiSettingsStore>(provider => provider.GetRequiredService<UiSettingsStore>());

            // 启动护栏必须先于 TuningApplyService 可解析：后者在写 SMU 前后要落标记。
            services.TryAddSingleton<BootGuard>();
            services.TryAddSingleton<IBootGuard>(provider => provider.GetRequiredService<BootGuard>());

            services.TryAddSingleton<LogThrottle>();
            services.TryAddSingleton<ProcessRefresher>();
            services.TryAddSingleton<PowerStatusWatcher>();
        }
    }
}
