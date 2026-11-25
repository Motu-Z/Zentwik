using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;

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

    /// <summary>连接断开（对端退出或管道错误）后触发。</summary>
    public event Action<Exception?>? Closed;

    public bool IsConnected => _pipe.IsConnected && _disposed == 0;

    public static async Task<AgentConnection> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

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
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
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

    /// <summary>发送请求并断言成功，返回 Data。</summary>
    public async Task<JsonElement> InvokeAsync(string op, CancellationToken ct = default)
    {
        var response = await RequestAsync(op, ct).ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new AgentRequestException(op, response.Error ?? "unknown", response.Detail);
        }

        return response.Data ?? default;
    }

    public async Task<AgentStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var data = await InvokeAsync(Ops.Status, ct).ConfigureAwait(false);
        return data.Deserialize(ZentwikJson.Default.AgentStatusDto)
               ?? throw new AgentRequestException(Ops.Status, "response.empty", null);
    }

    public async Task<IReadOnlyList<CapabilityDto>> ListCapabilitiesAsync(CancellationToken ct = default)
    {
        var data = await InvokeAsync(Ops.CapabilitiesList, ct).ConfigureAwait(false);
        return data.Deserialize(ZentwikJson.Default.CapabilityDtoArray) ?? [];
    }

    public async Task<IReadOnlyList<PlanSummaryDto>> ListPlansAsync(CancellationToken ct = default)
    {
        var data = await InvokeAsync(Ops.PlansList, ct).ConfigureAwait(false);
        return data.Deserialize(ZentwikJson.Default.PlanSummaryDtoArray) ?? [];
    }

    public async Task<IReadOnlyList<SceneSummaryDto>> ListScenesAsync(CancellationToken ct = default)
    {
        var data = await InvokeAsync(Ops.ScenesList, ct).ConfigureAwait(false);
        return data.Deserialize(ZentwikJson.Default.SceneSummaryDtoArray) ?? [];
    }

    public async Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var data = await InvokeAsync(Ops.SettingsGet, ct).ConfigureAwait(false);
        return data.Deserialize(ZentwikJson.Default.SettingsDto)
               ?? throw new AgentRequestException(Ops.SettingsGet, "response.empty", null);
    }

    public async Task<SettingsDto> SetSettingsAsync(SettingsPatch patch, CancellationToken ct = default)
    {
        var response = await RequestAsync(Ops.SettingsSet, patch, ZentwikJson.Default.SettingsPatch, null, ct)
            .ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new AgentRequestException(Ops.SettingsSet, response.Error ?? "unknown", response.Detail);
        }

        return (response.Data ?? default).Deserialize(ZentwikJson.Default.SettingsDto)
               ?? throw new AgentRequestException(Ops.SettingsSet, "response.empty", null);
    }

    public async Task<SubscriptionAck> SubscribeTelemetryAsync(int intervalMs, CancellationToken ct = default)
    {
        var response = await RequestAsync(
            Ops.TelemetrySubscribe,
            new SubscriptionRequest(intervalMs),
            ZentwikJson.Default.SubscriptionRequest,
            null,
            ct).ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new AgentRequestException(Ops.TelemetrySubscribe, response.Error ?? "unknown", response.Detail);
        }

        return (response.Data ?? default).Deserialize(ZentwikJson.Default.SubscriptionAck)
               ?? throw new AgentRequestException(Ops.TelemetrySubscribe, "response.empty", null);
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
            // 对端无响应时不再等待，管道销毁会终止读取。
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
