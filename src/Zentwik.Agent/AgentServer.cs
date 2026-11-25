using System.IO.Pipes;
using Zentwik.Agent.Security;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.Scenes;
using Zentwik.Core.State;
using Zentwik.Core.Telemetry;
using Zentwik.Platform;

namespace Zentwik.Agent;

public sealed class AgentServer
{
    private readonly AgentOptions _options;
    private readonly StateStore _store;
    private readonly TelemetryHub _hub;
    private readonly CapabilityService _capabilities;
    private readonly ISystemInfo _system;
    private readonly DiagnosticLog _log;
    private readonly PlanCoordinator _plans;
    private readonly SceneEngine _scenes;
    private readonly DoctorService _doctor;
    private readonly ICpuTuningProvider _tuning;
    private readonly CallerPolicy _policy;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly List<AgentSession> _sessions = [];
    private readonly Lock _sessionsGate = new();

    public AgentServer(
        AgentOptions options,
        StateStore store,
        TelemetryHub hub,
        CapabilityService capabilities,
        ISystemInfo system,
        DiagnosticLog log,
        PlanCoordinator plans,
        SceneEngine scenes,
        DoctorService doctor,
        ICpuTuningProvider tuning,
        CallerPolicy policy)
    {
        _options = options;
        _store = store;
        _hub = hub;
        _capabilities = capabilities;
        _system = system;
        _log = log;
        _plans = plans;
        _scenes = scenes;
        _doctor = doctor;
        _tuning = tuning;
        _policy = policy;
    }

    public event Action<SceneActivation>? SceneChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info($"代理启动：管道 {_options.PipeName}，数据目录 {_options.DataDirectory}，协议 v{ProtocolInfo.Version}，服务模式 {_options.ServiceMode}。");

        var sceneLoop = Task.Run(() => SceneLoopAsync(ct), CancellationToken.None);
        _scenes.Changed += OnSceneChanged;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(
                        _options.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        0,
                        0,
                        PipeSecurityFactory.Create());
                }
                catch (IOException ex)
                {
                    _log.Error($"创建管道实例失败：{ex.Message}");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                var caller = PipeCaller.Read(pipe);
                if (caller is null)
                {
                    _log.Warn("拒绝连接：无法读取调用方身份。");
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                if (!PipeCaller.Validate(caller, _policy, out var reason))
                {
                    _log.Warn($"拒绝连接：{reason}（pid {caller.ProcessId}）");
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var session = new AgentSession(pipe, this, _log, caller);
                Track(session);
                _ = session.RunAsync(ct).ContinueWith(
                    _ => Untrack(session),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            _scenes.Changed -= OnSceneChanged;
            await ShutdownSessionsAsync().ConfigureAwait(false);
            _log.Info("代理已停止。");
        }
    }

    internal StateStore Store => _store;

    internal TelemetryHub Hub => _hub;

    internal CapabilityService Capabilities => _capabilities;

    internal ISystemInfo System => _system;

    internal AgentOptions Options => _options;

    internal DiagnosticLog Log => _log;

    internal PlanCoordinator Plans => _plans;

    internal SceneEngine Scenes => _scenes;

    internal DoctorService Doctor => _doctor;

    internal ICpuTuningProvider Tuning => _tuning;

    internal DateTimeOffset StartedAt => _startedAt;

    private async Task SceneLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _scenes.EvaluateAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Error($"场景评估失败：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnSceneChanged(SceneActivation activation) =>
        SceneChanged?.Invoke(activation);

    private void Track(AgentSession session)
    {
        lock (_sessionsGate)
        {
            _sessions.Add(session);
        }
    }

    private void Untrack(AgentSession session)
    {
        lock (_sessionsGate)
        {
            _sessions.Remove(session);
        }
    }

    private async Task ShutdownSessionsAsync()
    {
        AgentSession[] snapshot;
        lock (_sessionsGate)
        {
            snapshot = [.. _sessions];
            _sessions.Clear();
        }

        foreach (var session in snapshot)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
