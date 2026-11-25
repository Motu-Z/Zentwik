using Zentwik.Agent;
using Zentwik.Agent.Capabilities;
using Zentwik.Agent.Diagnostics;
using Zentwik.Agent.State;
using Zentwik.Agent.Telemetry;
using Zentwik.Platform;
using Zentwik.Platform.Fakes;
using Zentwik.Platform.Windows;

var options = AgentOptions.Parse(args);

using var log = new AgentLog(options.DataDirectory, options.EchoLog);

var store = new StateStore(options.DataDirectory, log);
await store.LoadAsync().ConfigureAwait(false);

ISystemInfo system = new WindowsSystemInfo();
ITelemetrySource telemetry = options.UseFakeTelemetry
    ? new FakeTelemetrySource()
    : new Win32TelemetrySource();

var hub = new TelemetryHub(telemetry);
var capabilities = new CapabilityService(WindowsCapabilityProbes.Create(system, telemetry));
var server = new AgentServer(options, store, hub, capabilities, system, log);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await server.RunAsync(shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
}

return 0;
