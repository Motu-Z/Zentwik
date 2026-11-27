using System.Security.Principal;
using Zentwik.Agent;
using Zentwik.Agent.Security;
using Zentwik.Agent.Service;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Modules;
using Zentwik.Core.Plans;
using Zentwik.Core.Power;
using Zentwik.Core.Scenes;
using Zentwik.Core.State;
using Zentwik.Core.Telemetry;
using Zentwik.Domain.Ownership;
using Zentwik.Platform;
using Zentwik.Platform.Fakes;
using Zentwik.Platform.Windows;

var options = AgentOptions.Parse(args);

// 数据目录改名后的一次性搬迁。必须早于任何 StateStore 创建：
// 安装阶段会读状态文件写可选模块，先落下空 state.json 就会让「目标已有状态」的守卫把搬迁挡掉。
// 也必须早于日志创建：目标目录里先有同名日志文件会让逐文件复制失败。
var migration = options.Mode is AgentMode.Run or AgentMode.InstallService
    ? DataDirectoryMigration.TryMigrate(options.LegacyDataDirectory, options.DataDirectory)
    : DataMigrationOutcome.NotNeeded;

if (migration.Executed)
{
    Console.WriteLine($"已迁移数据目录：{migration.Source} → {options.DataDirectory}");
}
else if (migration.Error is not null)
{
    Console.Error.WriteLine($"数据目录迁移失败：{migration.Error}");
}

if (options.Mode == AgentMode.InstallService)
{
    return NativeService.Install(options);
}

if (options.Mode == AgentMode.UninstallService)
{
    return NativeService.Uninstall(options);
}

if (options.Mode == AgentMode.StartService)
{
    return NativeService.Start(options.ServiceName);
}

if (options.Mode == AgentMode.StopService)
{
    return NativeService.Stop(options.ServiceName);
}

using var log = new DiagnosticLog(options.DataDirectory, options.EchoLog);

if (migration.Executed)
{
    log.Info($"已迁移数据目录：{migration.Source} → {options.DataDirectory}");
}
else if (migration.Error is not null)
{
    log.Warn($"数据目录迁移失败：{migration.Error}。将以空状态启动。");
}

var store = new StateStore(options.DataDirectory, log);
await store.LoadAsync().ConfigureAwait(false);

var ownerSid = ResolveOwnerSid(options.DataDirectory) ?? WindowsIdentity.GetCurrent().User?.Value;
if (!string.IsNullOrEmpty(ownerSid) && !string.Equals(store.Current.OwnerSid, ownerSid, StringComparison.OrdinalIgnoreCase))
{
    await store.UpdateAsync(document => document with { OwnerSid = ownerSid }).ConfigureAwait(false);
    log.Info($"记录所有者 SID：{ownerSid}");
}

ISystemInfo system = new WindowsSystemInfo();
ICpuTuningProvider tuning = options.UseFake ? new FakeCpuTuningProvider() : new AmdSmuTuningProvider(system);
IIntelTuningProvider intel = options.UseFake ? new FakeIntelTuningProvider() : new IntelMsrTuningProvider(system);
IPowerPolicyService power = options.UseFake ? new FakePowerPolicyService() : new Win32PowerPolicyService();
IProcessScheduler scheduler = options.UseFake ? new FakeProcessScheduler() : new AffinityScheduler();
IPowerStatus powerStatus = options.UseFake ? new FakePowerStatus() : new Win32PowerStatus();
ITelemetrySource telemetry = options.UseFake ? new FakeTelemetrySource(system.ProcessorCount) : new Win32TelemetrySource();
IPowerNotificationSource powerNotifications = options.UseFake ? new NoPowerNotifications() : new Win32SuspendResumeNotifications();

// AMD 的 CO 与功耗墙共用同一个 PawnIO 会话与 PCI 互斥，实际是同一实例。
IPowerLimitProvider powerLimits = options.UseFake ? new FakePowerLimitProvider() : (AmdSmuTuningProvider)tuning;

// 温度/功耗走独立模块（AMDFamily17.bin），与会话化的 SMU 邮箱互不影响。
ICpuSensorSource cpuSensors = options.UseFake ? new FakeCpuSensorSource() : new AmdSensorSource(system);

var modules = new ModuleService(store, log);
var hub = new TelemetryHub(telemetry, cpuSensors);
var capabilities = new CapabilityService(
    WindowsCapabilityProbes.Create(
            system,
            telemetry,
            tuning,
            intel,
            scheduler,
            power,
            powerLimits,
            () => modules.IsEnabled(ModuleIds.PowerLimits),
            cpuSensors)
        .Append(new CpuSetsCapabilityProbe(scheduler)));
var plans = new PlanCoordinator(store, tuning, power, scheduler, log, intel, powerStatus);
var powerSchemes = new PowerSchemeService(power, new PowerSchemeBackupStore(options.DataDirectory, log), log);
var scenes = new SceneEngine(store, plans, scheduler, powerStatus, log, new SystemBootInfo());

var recovered = await scenes.RecoverAsync().ConfigureAwait(false);
if (recovered.Count > 0)
{
    log.Info($"启动补回退 {recovered.Count} 项，成功 {recovered.Count(static outcome => outcome.Status == LedgerStatus.Reverted)} 项。");
}

var version = typeof(AgentServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
var runtime = new AgentRuntime(version, options.ServiceMode, options.UseFake, DateTimeOffset.Now);
var doctor = new DoctorService(store, capabilities, log, runtime, system, () => scenes.ActiveSceneId, modules);

var policy = new CallerPolicy(store.Current.OwnerSid, RequireInteractiveSession: true, IntegrityLevel.Medium);
var server = new AgentServer(
    options, store, hub, capabilities, system, log, plans, scenes, doctor, tuning, intel, policy,
    powerNotifications, powerSchemes, modules, powerLimits);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

if (options.ServiceMode && WindowsServiceHost.TryRun(options.ServiceName, token => server.RunAsync(token)))
{
    return 0;
}

try
{
    await server.RunAsync(shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
}

return 0;

static string? ResolveOwnerSid(string dataDirectory)
{
    var path = Path.Combine(dataDirectory, "owner.sid");
    if (!File.Exists(path))
    {
        return null;
    }

    var text = File.ReadAllText(path).Trim();
    return string.IsNullOrEmpty(text) ? null : text;
}