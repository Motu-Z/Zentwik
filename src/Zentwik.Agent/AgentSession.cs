using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Zentwik.Agent.Diagnostics;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Domain.Capabilities;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Settings;

namespace Zentwik.Agent;

/// <summary>读循环负责分发，输出统一走一个写循环：两处同时写管道会让帧交错。</summary>
internal sealed class AgentSession : IAsyncDisposable
{
    private const int BacklogThreshold = 256;

    private readonly NamedPipeServerStream _pipe;
    private readonly AgentServer _server;
    private readonly AgentLog _log;
    private readonly Channel<ResponseEnvelope> _outbound = Channel.CreateUnbounded<ResponseEnvelope>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private CancellationTokenSource? _telemetryCts;
    private int _backlog;
    private long _droppedSamples;
    private int _disposed;

    public AgentSession(NamedPipeServerStream pipe, AgentServer server, AgentLog log)
    {
        _pipe = pipe;
        _server = server;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var peer = DescribePeer();
        _log.Info($"客户端接入：{peer}");

        var writer = WriteLoopAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var body = await Frames.ReadAsync(_pipe, ct).ConfigureAwait(false);
                if (body is null)
                {
                    break;
                }

                var response = await HandleFrameAsync(body, ct).ConfigureAwait(false);
                if (response is not null)
                {
                    Enqueue(response);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ProtocolException ex)
        {
            _log.Warn($"协议错误，断开连接（{peer}）：{ex.Message}");
        }
        catch (IOException ex)
        {
            _log.Warn($"管道中断（{peer}）：{ex.Message}");
        }
        finally
        {
            StopTelemetry();
            _outbound.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (IOException)
            {
            }

            _log.Info($"客户端断开：{peer}（丢弃采样 {Interlocked.Read(ref _droppedSamples)} 帧）");
        }
    }

    private async Task<ResponseEnvelope?> HandleFrameAsync(byte[] body, CancellationToken ct)
    {
        RequestEnvelope? request;
        try
        {
            request = JsonSerializer.Deserialize(body, ZentwikJson.Default.RequestEnvelope);
        }
        catch (JsonException ex)
        {
            _log.Warn($"请求解析失败：{ex.Message}");
            return ResponseEnvelope.Failure(0, string.Empty, ErrorCodes.ProtocolMalformed, ex.Message);
        }

        if (request is null)
        {
            return ResponseEnvelope.Failure(0, string.Empty, ErrorCodes.ProtocolMalformed, "空请求。");
        }

        if (request.V != ProtocolInfo.Version)
        {
            return ResponseEnvelope.Failure(
                request.Id,
                request.Op,
                ErrorCodes.ProtocolVersionMismatch,
                $"客户端 v{request.V}，代理 v{ProtocolInfo.Version}。");
        }

        try
        {
            return await DispatchAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"命令 {request.Op} 失败：{ex.Message}");
            return ResponseEnvelope.Failure(request.Id, request.Op, ErrorCodes.Internal, ex.Message);
        }
    }

    private async Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, CancellationToken ct)
    {
        switch (request.Op)
        {
            case Ops.Ping:
                return ResponseEnvelope.Success(request.Id, request.Op, null);

            case Ops.Status:
                return ResponseEnvelope.Success(
                    request.Id,
                    request.Op,
                    JsonSerializer.SerializeToElement(BuildStatus(), ZentwikJson.Default.AgentStatusDto));

            case Ops.CapabilitiesList:
            {
                var capabilities = _server.Capabilities.Get().Select(ToDto).ToArray();
                return ResponseEnvelope.Success(
                    request.Id,
                    request.Op,
                    JsonSerializer.SerializeToElement(capabilities, ZentwikJson.Default.CapabilityDtoArray));
            }

            case Ops.PlansList:
            {
                var plans = _server.Store.Current.Plans.Select(ToDto).ToArray();
                return ResponseEnvelope.Success(
                    request.Id,
                    request.Op,
                    JsonSerializer.SerializeToElement(plans, ZentwikJson.Default.PlanSummaryDtoArray));
            }

            case Ops.ScenesList:
            {
                var scenes = _server.Store.Current.Scenes
                    .OrderByDescending(static s => s.Priority)
                    .ThenBy(static s => s.Name, StringComparer.Ordinal)
                    .Select(ToDto)
                    .ToArray();
                return ResponseEnvelope.Success(
                    request.Id,
                    request.Op,
                    JsonSerializer.SerializeToElement(scenes, ZentwikJson.Default.SceneSummaryDtoArray));
            }

            case Ops.SettingsGet:
                return ResponseEnvelope.Success(
                    request.Id,
                    request.Op,
                    JsonSerializer.SerializeToElement(ToDto(_server.Store.Current.Settings), ZentwikJson.Default.SettingsDto));

            case Ops.SettingsSet:
                return await ApplySettingsAsync(request, ct).ConfigureAwait(false);

            case Ops.TelemetrySubscribe:
                return StartTelemetry(request);

            case Ops.TelemetryUnsubscribe:
                StopTelemetry();
                return ResponseEnvelope.Success(request.Id, request.Op, null);

            default:
                return ResponseEnvelope.Failure(
                    request.Id,
                    request.Op,
                    ErrorCodes.UnknownOperation,
                    $"未知命令：{request.Op}");
        }
    }

    private async Task<ResponseEnvelope> ApplySettingsAsync(RequestEnvelope request, CancellationToken ct)
    {
        if (request.Payload is null)
        {
            return ResponseEnvelope.Failure(request.Id, request.Op, ErrorCodes.PayloadInvalid, "缺少载荷。");
        }

        SettingsPatch? patch;
        try
        {
            patch = request.Payload.Value.Deserialize(ZentwikJson.Default.SettingsPatch);
        }
        catch (JsonException ex)
        {
            return ResponseEnvelope.Failure(request.Id, request.Op, ErrorCodes.PayloadInvalid, ex.Message);
        }

        if (patch is null)
        {
            return ResponseEnvelope.Failure(request.Id, request.Op, ErrorCodes.PayloadInvalid, "载荷为空。");
        }

        var current = _server.Store.Current.Settings;
        if (!UserSettingsRules.TryApply(current, patch.Language, patch.Theme, patch.Accent, patch.UiScale, out var updated, out var error))
        {
            return ResponseEnvelope.Failure(request.Id, request.Op, ErrorCodes.PayloadInvalid, error);
        }

        var saved = await _server.Store.UpdateSettingsAsync(updated, ct).ConfigureAwait(false);
        var effective = saved?.Settings ?? updated;
        _log.Info($"设置已更新：语言={effective.Language} 主题={effective.Theme} 强调色={effective.Accent} 缩放={effective.UiScale}");

        return ResponseEnvelope.Success(
            request.Id,
            request.Op,
            JsonSerializer.SerializeToElement(ToDto(effective), ZentwikJson.Default.SettingsDto));
    }

    private ResponseEnvelope StartTelemetry(RequestEnvelope request)
    {
        var interval = 1000;
        if (request.Payload is { } payload)
        {
            try
            {
                var subscription = payload.Deserialize(ZentwikJson.Default.SubscriptionRequest);
                if (subscription is not null)
                {
                    interval = Math.Clamp(subscription.IntervalMs, 200, 5000);
                }
            }
            catch (JsonException)
            {
                // 周期非法时退回默认值，而不是拒绝订阅：订阅本身是幂等操作。
            }
        }

        StopTelemetry();

        var cts = new CancellationTokenSource();
        _telemetryCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await _server.Hub.RunSubscriptionAsync(
                    interval,
                    sample =>
                    {
                        // 客户端读得慢就丢最新采样，不回压采样源，也不阻塞其他客户端。
                        if (Volatile.Read(ref _backlog) > BacklogThreshold)
                        {
                            Interlocked.Increment(ref _droppedSamples);
                            return true;
                        }

                        var dto = new TelemetryDto(
                            sample.Timestamp,
                            sample.CpuTotalPercent,
                            sample.MemoryTotalBytes,
                            sample.MemoryAvailableBytes,
                            sample.ProcessCount,
                            sample.ProcessorCount);

                        Enqueue(ResponseEnvelope.Event(
                            Events.TelemetrySample,
                            JsonSerializer.SerializeToElement(dto, ZentwikJson.Default.TelemetryDto)));
                        return true;
                    },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error($"遥测推流结束：{ex}");
            }
        }, CancellationToken.None);

        return ResponseEnvelope.Success(
            request.Id,
            request.Op,
            JsonSerializer.SerializeToElement(new SubscriptionAck(interval), ZentwikJson.Default.SubscriptionAck));
    }

    private void StopTelemetry()
    {
        var cts = Interlocked.Exchange(ref _telemetryCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private AgentStatusDto BuildStatus() => new(
        Version: typeof(AgentServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        ProtocolVersion: ProtocolInfo.Version,
        MachineName: _server.System.MachineName,
        Elevated: _server.System.IsElevated,
        ProcessorCount: _server.System.ProcessorCount,
        ProcessorGroups: _server.System.ProcessorGroups,
        StartedAt: _server.StartedAt,
        DataDirectory: _server.Options.DataDirectory,
        PlanCount: _server.Store.Current.Plans.Count,
        SceneCount: _server.Store.Current.Scenes.Count);

    private static CapabilityDto ToDto(CapabilityDescriptor descriptor) => new(
        descriptor.Id,
        descriptor.State switch
        {
            CapabilityState.Running => CapabilityStates.Running,
            CapabilityState.Degraded => CapabilityStates.Degraded,
            CapabilityState.Failed => CapabilityStates.Failed,
            CapabilityState.Loaded => CapabilityStates.Loaded,
            CapabilityState.Enabled => CapabilityStates.Enabled,
            CapabilityState.Installed => CapabilityStates.Installed,
            _ => CapabilityStates.Unavailable,
        },
        descriptor.Detail,
        descriptor.Requires);

    private static PlanSummaryDto ToDto(Plan plan) => new(
        plan.Id,
        plan.Kind switch
        {
            PlanKind.CpuTuning => "cpuTuning",
            PlanKind.Power => "power",
            _ => "scheduling",
        },
        plan.Name,
        plan.Revision,
        "defined");

    private static SceneSummaryDto ToDto(Zentwik.Domain.Scenes.Scene scene) => new(
        scene.Id,
        scene.Name,
        scene.Priority,
        scene.Enabled,
        [.. scene.Actions.Select(static a => a.PlanId)],
        scene.LastOutcome);

    private static SettingsDto ToDto(UserSettings settings) => new(
        settings.Language,
        settings.Theme,
        settings.Accent,
        settings.UiScale);

    /// <summary>自行维护积压计数：无界通道的 Reader.Count 会抛 NotSupportedException。</summary>
    private void Enqueue(ResponseEnvelope envelope)
    {
        Interlocked.Increment(ref _backlog);
        if (!_outbound.Writer.TryWrite(envelope))
        {
            Interlocked.Decrement(ref _backlog);
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var body = JsonSerializer.SerializeToUtf8Bytes(envelope, ZentwikJson.Default.ResponseEnvelope);
                    await Frames.WriteAsync(_pipe, body, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _backlog);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string DescribePeer()
    {
        try
        {
            return _pipe.GetImpersonationUserName();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopTelemetry();
        _outbound.Writer.TryComplete();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
