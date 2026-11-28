using System.IO.Pipes;
using System.Text.Json;
using Zentwik.Agent.Security;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Capabilities;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Modules;
using Zentwik.Core.Plans;
using Zentwik.Core.Power;
using Zentwik.Core.Processes;
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
    private readonly PowerSchemeService? _powerSchemes;
    private readonly ModuleService? _modules;
    private readonly IPowerLimitProvider? _powerLimits;
    private readonly ProcessQueryService? _processes;
    private readonly SceneEngine _scenes;
    private readonly DoctorService _doctor;
    private readonly ICpuTuningProvider _tuning;
    private readonly IIntelTuningProvider _intel;
    private readonly CallerPolicy _policy;
    private readonly SchedulingWatcher _watcher;
    private readonly IPowerNotificationSource? _powerNotifications;
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
        IIntelTuningProvider intel,
        CallerPolicy policy,
        IPowerNotificationSource? powerNotifications = null,
        PowerSchemeService? powerSchemes = null,
        ModuleService? modules = null,
        IPowerLimitProvider? powerLimits = null,
        ProcessQueryService? processes = null)
    {
        _options = options;
        _store = store;
        _hub = hub;
        _capabilities = capabilities;
        _system = system;
        _log = log;
        _plans = plans;
        _powerSchemes = powerSchemes;
        _modules = modules;
        _powerLimits = powerLimits;
        _processes = processes;
        _scenes = scenes;
        _doctor = doctor;
        _tuning = tuning;
        _intel = intel;
        _policy = policy;
        _powerNotifications = powerNotifications;
        _watcher = new SchedulingWatcher(_scenes, _plans, _log);
    }

    public event Action<SceneActivation>? SceneChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info($"代理启动：管道 {_options.PipeName}，数据目录 {_options.DataDirectory}，协议 v{ProtocolInfo.Version}，服务模式 {_options.ServiceMode}。");

        _watcher.Start(ct);
        _scenes.Changed += OnSceneChanged;
        StartPowerNotifications(ct);

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
                    await RejectAsync(pipe, "无法确定调用方身份。").ConfigureAwait(false);
                    continue;
                }

                if (!PipeCaller.Validate(caller, _policy, out var reason))
                {
                    _log.Warn($"拒绝连接：{reason}（pid {caller.ProcessId}）");
                    await RejectAsync(pipe, reason).ConfigureAwait(false);
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
            _powerNotifications?.Dispose();
            await _watcher.DisposeAsync().ConfigureAwait(false);
            await RestoreActiveSceneAsync().ConfigureAwait(false);
            await ShutdownSessionsAsync().ConfigureAwait(false);
            _log.Info("代理已停止。");
        }
    }

    internal StateStore Store => _store;

    internal TelemetryHub Hub => _hub;

    internal CapabilityService Capabilities => _capabilities;

    internal ISystemInfo System => _system;

    internal IIntelTuningProvider Intel => _intel;

    internal AgentOptions Options => _options;

    internal DiagnosticLog Log => _log;

    internal PlanCoordinator Plans => _plans;

    internal PowerSchemeService? PowerSchemes => _powerSchemes;

    internal ModuleService? Modules => _modules;

    internal IPowerLimitProvider? PowerLimits => _powerLimits;

    internal ProcessQueryService? Processes => _processes;

    internal SceneEngine Scenes => _scenes;

    internal DoctorService Doctor => _doctor;

    internal ICpuTuningProvider Tuning => _tuning;

    internal SchedulingWatcher Watcher => _watcher;

    internal DateTimeOffset StartedAt => _startedAt;

    private void OnSceneChanged(SceneActivation activation) =>
        SceneChanged?.Invoke(activation);

    /// <summary>
    /// 唤醒后先核对账本再评估场景：重放前必须读到实际值，不能无条件重写历史写入。
    /// </summary>
    private void StartPowerNotifications(CancellationToken ct)
    {
        if (_powerNotifications is null)
        {
            return;
        }

        if (!_powerNotifications.Start(out var error))
        {
            _log.Warn($"系统挂起/恢复通知不可用：{error}");
            return;
        }

        _powerNotifications.Resumed += () => _ = Task.Run(() => ResumeAsync(ct), CancellationToken.None);
        _log.Info("系统挂起/恢复通知已注册。");
    }

    private async Task ResumeAsync(CancellationToken ct)
    {
        try
        {
            var result = await _plans.ReconcileAfterResumeAsync(ct).ConfigureAwait(false);
            _log.Info($"系统恢复核对：确认 {result.Verified}，重写 {result.Reapplied}，用户接管 {result.TakenOver}，未知 {result.Unknown}。");
            await _scenes.EvaluateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"系统恢复核对失败：{ex.Message}");
        }
    }

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

    /// <summary>
    /// 拒绝时必须回一帧 Unauthorized 再断开：直接关管道的客户端只能看到"管道已结束"，无法区分权限拒绝与代理崩溃。
    /// </summary>
    private static async Task RejectAsync(NamedPipeServerStream pipe, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var body = await Frames.ReadAsync(pipe, cts.Token).ConfigureAwait(false);

            long id = 0;
            var op = string.Empty;
            if (body is not null)
            {
                try
                {
                    var request = JsonSerializer.Deserialize(body, ZentwikJson.Default.RequestEnvelope);
                    id = request?.Id ?? 0;
                    op = request?.Op ?? string.Empty;
                }
                catch (JsonException)
                {
                }
            }

            var response = ResponseEnvelope.Failure(id, op, ErrorCodes.Unauthorized, reason);
            await Frames.WriteAsync(
                pipe,
                JsonSerializer.SerializeToUtf8Bytes(response, ZentwikJson.Default.ResponseEnvelope),
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 停止时传入的取消令牌已经取消，回退必须换一个带超时的令牌，否则写入路径会在第一步就抛取消。
    /// </summary>
    private async Task RestoreActiveSceneAsync()
    {
        if (_scenes.ActiveSceneId is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await _scenes.ShutdownAsync(cts.Token).ConfigureAwait(false);
            if (result is not null)
            {
                _log.Info($"退出回退：{result.Outcome}");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or CoreException)
        {
            _log.Warn($"退出回退失败：{ex.Message}");
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