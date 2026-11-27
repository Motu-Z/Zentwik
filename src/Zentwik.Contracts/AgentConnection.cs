using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;

namespace Zentwik.Contracts.Protocol;

/// <summary>客户端侧连接。一个连接一个读循环，响应按 Id 配对，事件按 Op 分发。</summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ResponseEnvelope>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private long _nextId;
    private int _disposed;

    private AgentConnection(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>收到推送事件。事件可能在任意线程回调，UI 侧需自行切回同步上下文。</summary>
    public event Action<ResponseEnvelope>? EventReceived;

    public event Action<Exception?>? Closed;

    public bool IsConnected => _pipe.IsConnected && _disposed == 0;

    public static async Task<AgentConnection> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException($"连接 {pipeName} 超时（{timeout.TotalSeconds:0.#}s）。");
        }
        catch (IOException ex) when (ex.InnerException is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IpcTransportException($"连接 {pipeName} 失败：{ex.InnerException.Message}", ex);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var serverSid = PipeIdentity.ReadServerSid(pipe.SafePipeHandle.DangerousGetHandle());
        if (!PipeIdentity.IsTrustedServer(serverSid, PipeIdentity.ReadCurrentSid()))
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException(
                $"管道 {pipeName} 的创建者是 {serverSid ?? "未知账户"}，既不是 SYSTEM 也不是当前用户，已拒绝连接。");
        }

        return new AgentConnection(pipe);
    }

    public async Task<ResponseEnvelope> RequestAsync<TPayload>(
        string op,
        TPayload payload,
        JsonTypeInfo<TPayload> payloadInfo,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new RequestEnvelope
        {
            V = ProtocolInfo.Version,
            Id = id,
            Op = op,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, payloadInfo),
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(request, ZentwikJson.Default.RequestEnvelope);

        var tcs = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await Frames.WriteAsync(_pipe, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new IpcTransportException("发送请求失败。", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? ProtocolInfo.DefaultTimeout);

        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"命令 {op} 响应超时。");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task<ResponseEnvelope> RequestAsync(string op, CancellationToken ct = default) =>
        RequestAsync(op, new EmptyPayload(), ZentwikJson.Default.EmptyPayload, null, ct);

    public async Task<JsonElement> InvokeAsync(string op, CancellationToken ct = default)
    {
        var response = await RequestAsync(op, ct).ConfigureAwait(false);
        return Unwrap(op, response);
    }

    public async Task<AgentStatusDto> GetStatusAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.Status, ZentwikJson.Default.AgentStatusDto, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<CapabilityDto>> ListCapabilitiesAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.CapabilitiesList, ZentwikJson.Default.CapabilityDtoArray, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ModuleDto>> ListModulesAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.ModulesList, ZentwikJson.Default.ModuleDtoArray, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ModuleDto>> EnableModuleAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.ModulesEnable,
                new ModuleTarget(id),
                ZentwikJson.Default.ModuleTarget,
                ZentwikJson.Default.ModuleDtoArray,
                ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ModuleDto>> DisableModuleAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.ModulesDisable,
                new ModuleTarget(id),
                ZentwikJson.Default.ModuleTarget,
                ZentwikJson.Default.ModuleDtoArray,
                ct)
            .ConfigureAwait(false);

    public async Task<PowerLimitStateDto> ReadPowerLimitsAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.PowerLimitRead, ZentwikJson.Default.PowerLimitStateDto, ct).ConfigureAwait(false);

    public async Task<PowerLimitApplyDto> ApplyPowerLimitsAsync(
        PowerLimitApplyRequest request,
        CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.PowerLimitApply,
                request,
                ZentwikJson.Default.PowerLimitApplyRequest,
                ZentwikJson.Default.PowerLimitApplyDto,
                ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Plan>> ListPlansAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.PlansList, ZentwikJson.Default.PlanArray, ct).ConfigureAwait(false);

    public async Task<Plan> GetPlanAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(Ops.PlansGet, new IdRequest(id), ZentwikJson.Default.IdRequest, ZentwikJson.Default.Plan, ct)
            .ConfigureAwait(false);

    public async Task<Plan> SavePlanAsync(Plan plan, CancellationToken ct = default) =>
        await InvokeAsync(Ops.PlansSave, plan, ZentwikJson.Default.Plan, ZentwikJson.Default.Plan, ct)
            .ConfigureAwait(false);

    public async Task<bool> DeletePlanAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(Ops.PlansDelete, new IdRequest(id), ZentwikJson.Default.IdRequest, ZentwikJson.Default.Boolean, ct)
            .ConfigureAwait(false);

    public async Task<PlanApplyResultDto> ApplyPlanAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(Ops.PlansApply, new IdRequest(id), ZentwikJson.Default.IdRequest, ZentwikJson.Default.PlanApplyResultDto, ct)
            .ConfigureAwait(false);

    public async Task<PlanApplyResultDto> RevertPlanAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(Ops.PlansRevert, new IdRequest(id), ZentwikJson.Default.IdRequest, ZentwikJson.Default.PlanApplyResultDto, ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<SceneSummaryDto>> ListScenesAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.ScenesList, ZentwikJson.Default.SceneSummaryDtoArray, ct).ConfigureAwait(false);

    public async Task<Scene> GetSceneAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(Ops.ScenesGet, new IdRequest(id), ZentwikJson.Default.IdRequest, ZentwikJson.Default.Scene, ct)
            .ConfigureAwait(false);

    public async Task<Scene> SaveSceneAsync(Scene scene, CancellationToken ct = default) =>
        await InvokeAsync(Ops.ScenesSave, scene, ZentwikJson.Default.Scene, ZentwikJson.Default.Scene, ct)
            .ConfigureAwait(false);

    public async Task<bool> DeleteSceneAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(Ops.ScenesDelete, new IdRequest(id), ZentwikJson.Default.IdRequest, ZentwikJson.Default.Boolean, ct)
            .ConfigureAwait(false);

    public async Task<SceneActivationDto> ActivateSceneAsync(string id, CancellationToken ct = default) =>
        await InvokeAsync(Ops.ScenesActivate, new IdRequest(id), ZentwikJson.Default.IdRequest, ZentwikJson.Default.SceneActivationDto, ct)
            .ConfigureAwait(false);

    public async Task<SceneActivationDto> DeactivateSceneAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.ScenesDeactivate, ZentwikJson.Default.SceneActivationDto, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<LedgerEntry>> ListLedgerAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.LedgerList, ZentwikJson.Default.LedgerEntryArray, ct).ConfigureAwait(false);

    public async Task<bool> ReleaseLedgerAsync(string target, CancellationToken ct = default) =>
        await InvokeAsync(Ops.LedgerRelease, new IdRequest(target), ZentwikJson.Default.IdRequest, ZentwikJson.Default.Boolean, ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<PowerSchemeDto>> ListPowerSchemesAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.PowerSchemesList, ZentwikJson.Default.PowerSchemeDtoArray, ct).ConfigureAwait(false);

    public async Task<PowerSchemeDto> CreatePowerSchemeAsync(Guid template, string name, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.PowerSchemeCreate,
                new PowerSchemeCreateRequest(template, name),
                ZentwikJson.Default.PowerSchemeCreateRequest,
                ZentwikJson.Default.PowerSchemeDto,
                ct)
            .ConfigureAwait(false);

    public async Task<PowerSchemeDto> RenamePowerSchemeAsync(Guid scheme, string name, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.PowerSchemeRename,
                new PowerSchemeNameRequest(scheme, name),
                ZentwikJson.Default.PowerSchemeNameRequest,
                ZentwikJson.Default.PowerSchemeDto,
                ct)
            .ConfigureAwait(false);

    public async Task<PowerSchemeDeleteResultDto> DeletePowerSchemeAsync(
        Guid scheme,
        bool keepBackup = true,
        CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.PowerSchemeDelete,
                new PowerSchemeDeleteRequest(scheme, !keepBackup),
                ZentwikJson.Default.PowerSchemeDeleteRequest,
                ZentwikJson.Default.PowerSchemeDeleteResultDto,
                ct)
            .ConfigureAwait(false);

    public async Task<PowerSchemeBackupDto> BackupPowerSchemeAsync(Guid scheme, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.PowerSchemeBackup,
                new PowerSchemeTarget(scheme),
                ZentwikJson.Default.PowerSchemeTarget,
                ZentwikJson.Default.PowerSchemeBackupDto,
                ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<PowerSchemeBackupDto>> ListPowerSchemeBackupsAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.PowerSchemeBackups, ZentwikJson.Default.PowerSchemeBackupDtoArray, ct).ConfigureAwait(false);

    public async Task<PowerSchemeRestoreResultDto> RestorePowerSchemeAsync(string backup, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.PowerSchemeRestore,
                new PowerSchemeRestoreRequest(backup),
                ZentwikJson.Default.PowerSchemeRestoreRequest,
                ZentwikJson.Default.PowerSchemeRestoreResultDto,
                ct)
            .ConfigureAwait(false);

    public async Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.SettingsGet, ZentwikJson.Default.SettingsDto, ct).ConfigureAwait(false);

    public async Task<SettingsDto> SetSettingsAsync(SettingsPatch patch, CancellationToken ct = default) =>
        await InvokeAsync(Ops.SettingsSet, patch, ZentwikJson.Default.SettingsPatch, ZentwikJson.Default.SettingsDto, ct)
            .ConfigureAwait(false);

    public async Task<TuningSnapshotDto> ReadTuningAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.TuningRead, ZentwikJson.Default.TuningSnapshotDto, ct).ConfigureAwait(false);

    public async Task<IntelTuningSnapshotDto> ReadIntelTuningAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.IntelTuningRead, ZentwikJson.Default.IntelTuningSnapshotDto, ct).ConfigureAwait(false);

    public async Task<DoctorDto> GetDoctorAsync(CancellationToken ct = default) =>
        await InvokeAsync(Ops.DiagnosticsDoctor, ZentwikJson.Default.DoctorDto, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<LogEntryDto>> GetLogAsync(int limit, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.DiagnosticsLog,
                new LogRequest(limit),
                ZentwikJson.Default.LogRequest,
                ZentwikJson.Default.LogEntryDtoArray,
                ct)
            .ConfigureAwait(false);

    public async Task<SubscriptionAck> SubscribeTelemetryAsync(int? intervalMs, int? sensorIntervalMs = null, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.TelemetrySubscribe,
                new SubscriptionRequest(intervalMs ?? 0, sensorIntervalMs),
                ZentwikJson.Default.SubscriptionRequest,
                ZentwikJson.Default.SubscriptionAck,
                ct)
            .ConfigureAwait(false);

    public async Task<HistoryDto> GetTelemetryHistoryAsync(int limit, CancellationToken ct = default) =>
        await InvokeAsync(
                Ops.TelemetryHistory,
                new HistoryRequest(limit),
                ZentwikJson.Default.HistoryRequest,
                ZentwikJson.Default.HistoryDto,
                ct)
            .ConfigureAwait(false);

    private async Task<T> InvokeAsync<T>(string op, JsonTypeInfo<T> resultInfo, CancellationToken ct)
    {
        var data = await InvokeAsync(op, ct).ConfigureAwait(false);
        return data.Deserialize(resultInfo) ?? throw new AgentRequestException(op, "response.empty", null);
    }

    private async Task<TResult> InvokeAsync<TPayload, TResult>(
        string op,
        TPayload payload,
        JsonTypeInfo<TPayload> payloadInfo,
        JsonTypeInfo<TResult> resultInfo,
        CancellationToken ct)
    {
        var response = await RequestAsync(op, payload, payloadInfo, null, ct).ConfigureAwait(false);
        var data = Unwrap(op, response);
        return data.Deserialize(resultInfo) ?? throw new AgentRequestException(op, "response.empty", null);
    }

    private static JsonElement Unwrap(string op, ResponseEnvelope response)
    {
        if (!response.Ok)
        {
            throw new AgentRequestException(op, response.Error ?? "unknown", response.Detail);
        }

        return response.Data ?? default;
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var body = await Frames.ReadAsync(_pipe, _cts.Token).ConfigureAwait(false);
                if (body is null)
                {
                    break;
                }

                var envelope = JsonSerializer.Deserialize(body, ZentwikJson.Default.ResponseEnvelope);
                if (envelope is null)
                {
                    continue;
                }

                if (envelope.IsEvent)
                {
                    EventReceived?.Invoke(envelope);
                    continue;
                }

                if (_pending.TryRemove(envelope.Id, out var tcs))
                {
                    tcs.TrySetResult(envelope);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var tcs))
                {
                    tcs.TrySetException(new IpcTransportException("连接已断开。", failure));
                }
            }

            Closed?.Invoke(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

public sealed class IpcTransportException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AgentRequestException(string op, string code, string? detail)
    : Exception($"{op} 失败：{code}{(detail is null ? string.Empty : " / " + detail)}")
{
    public string Operation { get; } = op;
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}
