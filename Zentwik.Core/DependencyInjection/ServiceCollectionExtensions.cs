using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Core.DependencyInjection
{
    /// <summary>
    /// Core 的服务注册入口。
    ///
    /// Core 只引用 <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>，
    /// 不引用 Hosting：容器由宿主（UI 进程 / Worker 进程 / 自检工具）创建，
    /// Core 只负责「把服务按什么生命周期登记进去」。
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册 Core 的全部服务。
        ///
        /// Core 不持有任何 UI 线程调度器：日志与场景服务的内部集合由各自的锁保证
        /// 并发安全，通知以纯 C# 事件在原线程上同步触发。需要回到界面线程由 UI 进程负责。
        /// </summary>
        /// <param name="services">宿主容器。</param>
        /// <param name="configureLog">日志参数。为 null 时使用默认值（临时目录、200 条上限、zentwik- 前缀）。</param>
        public static IServiceCollection AddZentwikCore(
            this IServiceCollection services,
            Action<LogOptions>? configureLog = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            // ---------- 基础设施 ----------
            // 先登记具体类型，再把接口映射到同一个实例。
            //
            // 那种写法只登记接口，具体类型不在容器里。WPF 过渡桥（Svc.Paths）与
            // HeadlessHost 都是按具体类型取的，取不到就抛
            // InvalidOperationException: No service for type ... —— 而且是在
            // App.OnStartup 里抛，表现为「双击秒崩、没有窗口、事件查看器只有
            // 0xe0434352」。v2.0.0-can2 的启动崩溃就是这么来的。
            services.TryAddSingleton<AppPaths>();
            services.TryAddSingleton<IAppPaths>(provider => provider.GetRequiredService<AppPaths>());

            services.TryAddSingleton<LogService>(provider =>
            {
                LogOptions options = new();
                configureLog?.Invoke(options);

                // 未显式指定目录时落到 %AppData%\Zentwik\Logs
                if (string.IsNullOrWhiteSpace(options.Directory))
                    options.Directory = provider.GetRequiredService<IAppPaths>().Logs;

                return new LogService(options);
            });
            services.TryAddSingleton<ILogService>(provider => provider.GetRequiredService<LogService>());

            // ---------- 持久化 ----------
            // 先登记具体类型，再把接口映射到同一个实例：
            // 两个分开的 TryAddSingleton 会造出两个对象，接口与具体类型各持一份状态。
            services.TryAddSingleton<MaskStore>();
            services.TryAddSingleton<IMaskStore>(provider => provider.GetRequiredService<MaskStore>());

            services.TryAddSingleton<SchedulingStore>();
            services.TryAddSingleton<ISchedulingStore>(provider => provider.GetRequiredService<SchedulingStore>());

            services.TryAddSingleton<TuningStore>();
            services.TryAddSingleton<ITuningStore>(provider => provider.GetRequiredService<TuningStore>());

            services.TryAddSingleton<UiSettingsStore>();
            services.TryAddSingleton<IUiSettingsStore>(provider => provider.GetRequiredService<UiSettingsStore>());

            // ---------- CPU 访问 ----------
            // SmuReader 必须是单例：进程内只允许存在一个 Cpu 实例，
            // Opcode 的代码缓存是进程级静态资源，多一个实例就会互相打哑。
            services.TryAddSingleton<SmuReader>();
            services.TryAddSingleton<ISmuReader>(provider => provider.GetRequiredService<SmuReader>());

            services.TryAddSingleton<CpuSetScheduler>();
            services.TryAddSingleton<ICpuSetScheduler>(provider => provider.GetRequiredService<CpuSetScheduler>());

            services.TryAddSingleton<LogThrottle>();
            services.TryAddSingleton<ProcessRefresher>();
            services.TryAddSingleton<PowerStatusWatcher>();
            services.TryAddSingleton<AutoStartService>();

            services.TryAddSingleton<ProcessWatcher>();
            services.TryAddSingleton<IProcessWatcher>(provider => provider.GetRequiredService<ProcessWatcher>());

            // ---------- 电源策略 ----------
            // PowerPolicyApi 无状态（每次调用都是一次系统 API），单例只是省一次分配。
            // 仍然写成「先具体类型、再映射接口」两行：与上面几组保持同一形状，
            // 否则后来的人会以为「有的能取具体类型、有的不能」，进而开始猜注册规则。
            services.TryAddSingleton<PowerPolicyApi>();
            services.TryAddSingleton<IPowerPolicyApi>(provider => provider.GetRequiredService<PowerPolicyApi>());
            services.TryAddSingleton<PowerPolicyService>();

            // ---------- 业务编排 ----------
            // 启动护栏必须先于 TuningApplyService 可解析：后者在写 SMU 前后要落标记。
            services.TryAddSingleton<BootGuard>();
            services.TryAddSingleton<IBootGuard>(provider => provider.GetRequiredService<BootGuard>());

            services.TryAddSingleton<TuningApplyService>();
            services.TryAddSingleton<PlanService>();
            services.TryAddSingleton<ScenarioService>();
            services.TryAddSingleton<ScenarioMonitor>();
            services.TryAddSingleton<ScenarioSelfCheck>();
            services.TryAddSingleton<SmuDiagnostics>();

            return services;
        }
    }
}
