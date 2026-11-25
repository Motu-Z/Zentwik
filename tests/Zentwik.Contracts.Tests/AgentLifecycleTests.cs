using System.Security.Principal;
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
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Platform;
using Zentwik.Platform.Fakes;

namespace Zentwik.Contracts.Tests;

public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task Stopping_Agent_Reverts_Active_Scene()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zentwik-lifecycle-{Guid.NewGuid():N}");
        var pipeName = $"zentwik-lifecycle-{Guid.NewGuid():N}";

        using var log = new DiagnosticLog(directory, echoToConsole: false);
        var store = new StateStore(directory, log);
        await store.LoadAsync();

        var ownerSid = WindowsIdentity.GetCurrent().User!.Value;
        await store.UpdateAsync(document => document with
        {
            OwnerSid = ownerSid,
            Plans =
            [
                new CpuTuningPlan
                {
                    Id = "cpu",
                    Name = "CPU",
                    Revision = 1,
                    Cores = [new CoreTuning { CoreIndex = 0, VoltageOffsetMillivolts = -25 }],
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now,
                },
            ],
            Scenes =
            [
                new Scene
                {
                    Id = "manual",
                    Name = "Manual",
                    Priority = 1,
                    Triggers = [new SceneTrigger { Kind = SceneTriggerKinds.Manual }],
                    Actions = [new SceneAction { PlanId = "cpu", PlanRevision = 1 }],
                },
            ],
        });

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
            new CallerPolicy(ownerSid, RequireInteractiveSession: true, IntegrityLevel.Medium));

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        try
        {
            await using (var connection = await AgentConnection.ConnectAsync(pipeName, TimeSpan.FromSeconds(5)))
            {
                var activation = await connection.ActivateSceneAsync("manual");
                Assert.True(activation.Active);
            }

            Assert.Equal(-25, ReadOffset(tuning));

            await cts.CancelAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, ReadOffset(tuning));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            log.Dispose();

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static int ReadOffset(ICpuTuningProvider tuning) =>
        tuning.TryReadOffsets(out var offsets, out _) ? offsets[0].Millivolts : int.MinValue;
}
