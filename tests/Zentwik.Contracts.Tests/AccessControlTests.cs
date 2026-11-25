using Xunit;
using Zentwik.Agent;
using Zentwik.Agent.Security;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.Scenes;
using Zentwik.Core.State;
using Zentwik.Core.Telemetry;
using Zentwik.Platform.Fakes;

namespace Zentwik.Contracts.Tests;

public sealed class AccessControlTests
{
    private static readonly CallerPolicy Policy = new("S-1-5-21-1", RequireInteractiveSession: true, IntegrityLevel.Medium);

    [Fact]
    public void OwnerInInteractiveSession_IsAccepted()
    {
        var caller = new CallerIdentity(100, 1, "S-1-5-21-1", IntegrityLevel.Medium, false, null);

        Assert.True(PipeCaller.Validate(caller, Policy, out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void SessionZeroCaller_IsRejected()
    {
        var caller = new CallerIdentity(100, 0, "S-1-5-21-1", IntegrityLevel.Medium, false, null);

        Assert.False(PipeCaller.Validate(caller, Policy, out var reason));
        Assert.Contains("Session 0", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LowIntegrityCaller_IsRejected()
    {
        var caller = new CallerIdentity(100, 1, "S-1-5-21-1", IntegrityLevel.Low, false, null);

        Assert.False(PipeCaller.Validate(caller, Policy, out var reason));
        Assert.Contains("完整性", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignUser_IsRejected()
    {
        var caller = new CallerIdentity(100, 1, "S-1-5-21-2", IntegrityLevel.Medium, false, null);

        Assert.False(PipeCaller.Validate(caller, Policy, out var reason));
        Assert.Contains("所有者", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthorized_Caller_Gets_Explicit_Error()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zentwik-acl-{Guid.NewGuid():N}");
        var pipeName = $"zentwik-acl-{Guid.NewGuid():N}";

        using var log = new DiagnosticLog(directory, echoToConsole: false);
        var store = new StateStore(directory, log);
        await store.LoadAsync();
        await store.UpdateAsync(document => document with { OwnerSid = "S-1-5-21-9999999999-9999999999-9999999999-1001" });

        var options = new AgentOptions
        {
            PipeName = pipeName,
            DataDirectory = directory,
            ServiceName = "ZentwikTest",
            Mode = AgentMode.Run,
            UseFake = true,
            EchoLog = false,
        };

        var system = new TestSystemInfo();
        var tuning = new FakeCpuTuningProvider(4);
        var power = new FakePowerPolicyService();
        var scheduler = new FakeProcessScheduler();
        var capabilities = new CapabilityService([]);
        var plans = new PlanCoordinator(store, tuning, power, scheduler, log);
        var scenes = new SceneEngine(store, plans, scheduler, new FakePowerStatus(), log);
        var runtime = new AgentRuntime("test", false, true, DateTimeOffset.Now);
        var doctor = new DoctorService(store, capabilities, log, runtime, system, () => scenes.ActiveSceneId);
        var server = new AgentServer(
            options,
            store,
            new TelemetryHub(new FakeTelemetrySource(4)),
            capabilities,
            system,
            log,
            plans,
            scenes,
            doctor,
            tuning,
            new FakeIntelTuningProvider(),
            Policy);

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        try
        {
            await using var connection = await AgentConnection.ConnectAsync(pipeName, TimeSpan.FromSeconds(5));

            var error = await Assert.ThrowsAsync<AgentRequestException>(() => connection.GetStatusAsync());

            Assert.Equal(ErrorCodes.Unauthorized, error.Code);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }

            log.Dispose();

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
