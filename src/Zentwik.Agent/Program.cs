using System.Security.Principal;
using Zentwik.Agent;
using Zentwik.Agent.Security;
using Zentwik.Agent.Service;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.Scenes;
using Zentwik.Core.State;
using Zentwik.Core.Telemetry;
using Zentwik.Platform;
using Zentwik.Platform.Fakes;
using Zentwik.Platform.Windows;

var options = AgentOptions.Parse(args);

if (options.Mode == AgentMode.InstallService)
{
    return NativeService.Install(options);
}

if (options.Mode == AgentMode.UninstallService)
{
    return NativeService.Uninstall(options);
}

using var log = new DiagnosticLog(options.DataDirectory, options.EchoLog);

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
IPowerPolicyService power = options.UseFake ? new FakePowerPolicyService() : new Win32PowerPolicyService();
IProcessScheduler scheduler = options.UseFake ? new FakeProcessScheduler() : new AffinityScheduler();
IPowerStatus powerStatus = options.UseFake ? new FakePowerStatus() : new Win32PowerStatus();
ITelemetrySource telemetry = options.UseFake ? new FakeTelemetrySource() : new Win32TelemetrySource();

var hub = new TelemetryHub(telemetry);
var capabilities = new CapabilityService(WindowsCapabilityProbes.Create(system, telemetry, tuning, scheduler, power));
var plans = new PlanCoordinator(store, tuning, power, scheduler, log);
var scenes = new SceneEngine(store, plans, scheduler, powerStatus, log);

var version = typeof(AgentServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
var runtime = new AgentRuntime(version, options.ServiceMode, options.UseFake, DateTimeOffset.Now);
var doctor = new DoctorService(store, capabilities, log, runtime, system, () => scenes.ActiveSceneId);

var policy = new CallerPolicy(store.Current.OwnerSid, RequireInteractiveSession: true, IntegrityLevel.Medium);
var server = new AgentServer(options, store, hub, capabilities, system, log, plans, scenes, doctor, tuning, policy);

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
