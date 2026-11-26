using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Zentwik.Agent.Security;
using Zentwik.Contracts;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Modules;
using Zentwik.Core.Plans;
using Zentwik.Core.Power;
using Zentwik.Core.Scenes;
using Zentwik.Domain.Ownership;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Domain.Settings;
using Zentwik.Platform;

namespace Zentwik.Agent;

/// <summary>读循环负责分发，输出统一走一个写循环：两处同时写管道会让帧交错。</summary>
internal sealed class AgentSession : IAsyncDisposable
{
    private const int BacklogThreshold = 256;

    private readonly NamedPipeServerStream _pipe;
    private readonly AgentServer _server;
    private readonly DiagnosticLog _log;
    private readonly CallerIdentity _caller;
    private readonly Channel<ResponseEnvelope> _outbound = Channel.CreateUnbounded<ResponseEnvelope>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private CancellationTokenSource? _telemetryCts;
    private int _backlog;
    private long _droppedSamples;
    private int _disposed;

    public AgentSession(NamedPipeServerStream pipe, AgentServer server, DiagnosticLog log, CallerIdentity caller)
    {
        _pipe = pipe;
        _server = server;
        _log = log;
        _caller = caller;
        _server.SceneChanged += OnSceneChanged;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info($"客户端接入：pid {_caller.ProcessId}，会话 {_caller.SessionId}，用户 {_caller.UserSid}，完整性 {_caller.Integrity}");

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
            _log.Warn($"协议错误，断开连接（pid {_caller.ProcessId}）：{ex.Message}");
        }
        catch (IOException ex)
        {
            _log.Warn($"管道中断（pid {_caller.ProcessId}）：{ex.Message}");
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

            _log.Info($"客户端断开：pid {_caller.ProcessId}（丢弃采样 {Interlocked.Read(ref _droppedSamples)} 帧）");
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
        catch (CoreException ex)
        {
            return ResponseEnvelope.Failure(request.Id, request.Op, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            return ResponseEnvelope.Failure(request.Id, request.Op, ErrorCodes.PayloadInvalid, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"命令 {request.Op} 失败：{ex}");
            return ResponseEnvelope.Failure(request.Id, request.Op, ErrorCodes.Internal, ex.Message);
        }
    }

    private async Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, CancellationToken ct)
    {
        switch (request.Op)
        {
            case Ops.Ping:
                return Ok(request, null);

            case Ops.Status:
                return Ok(request, JsonSerializer.SerializeToElement(BuildStatus(), ZentwikJson.Default.AgentStatusDto));

            case Ops.CapabilitiesList:
            {
                var capabilities = _server.Capabilities.Get().Select(Mapping.ToDto).ToArray();
                return Ok(request, JsonSerializer.SerializeToElement(capabilities, ZentwikJson.Default.CapabilityDtoArray));
            }

            case Ops.ModulesList:
            {
                var modules = ModuleCatalog.Describe(_server.Capabilities.Get(), _server.Options.UseFake).ToArray();
                return Ok(request, JsonSerializer.SerializeToElement(modules, ZentwikJson.Default.ModuleDtoArray));
            }

            case Ops.PlansList:
            {
                var plans = _server.Store.Current.Plans.ToArray();
                return Ok(request, JsonSerializer.SerializeToElement(plans, ZentwikJson.Default.PlanArray));
            }

            case Ops.PlansGet:
            {
                var plan = FindPlan(RequirePayload(request, ZentwikJson.Default.IdRequest).Id);
                return Ok(request, JsonSerializer.SerializeToElement(plan, ZentwikJson.Default.Plan));
            }

            case Ops.PlansSave:
                return await SavePlanAsync(request, ct).ConfigureAwait(false);

            case Ops.PlansDelete:
                return await DeletePlanAsync(request, ct).ConfigureAwait(false);

            case Ops.PlansApply:
            {
                var id = RequirePayload(request, ZentwikJson.Default.IdRequest).Id;
                var outcome = await _server.Plans.ApplyAsync(id, $"plan:{id}", int.MaxValue - 1, ct).ConfigureAwait(false);
                return Ok(request, JsonSerializer.SerializeToElement(ToDto(outcome), ZentwikJson.Default.PlanApplyResultDto));
            }

            case Ops.PlansRevert:
            {
                var id = RequirePayload(request, ZentwikJson.Default.IdRequest).Id;
                var targets = await _server.Plans.RevertPlanAsync(id, ct).ConfigureAwait(false);
                var plan = FindPlan(id);
                return Ok(request, JsonSerializer.SerializeToElement(
                    new PlanApplyResultDto(
                        id,
                        "reverted",
                        plan.LastApply?.AppliedAt,
                        plan.LastApply?.PersistsAcrossReboot ?? true,
                        "已回退",
                        [.. targets.Select(ToDto)]),
                    ZentwikJson.Default.PlanApplyResultDto));
            }

            case Ops.ScenesList:
            {
                var scenes = _server.Store.Current.Scenes
                    .OrderByDescending(static scene => scene.Priority)
                    .ThenBy(static scene => scene.Name, StringComparer.Ordinal)
                    .Select(scene => Mapping.ToSummary(scene, _server.Scenes.ActiveSceneId))
                    .ToArray();
                return Ok(request, JsonSerializer.SerializeToElement(scenes, ZentwikJson.Default.SceneSummaryDtoArray));
            }

            case Ops.ScenesGet:
            {
                var scene = FindScene(RequirePayload(request, ZentwikJson.Default.IdRequest).Id);
                return Ok(request, JsonSerializer.SerializeToElement(scene, ZentwikJson.Default.Scene));
            }

            case Ops.ScenesSave:
                return await SaveSceneAsync(request, ct).ConfigureAwait(false);

            case Ops.ScenesDelete:
                return await DeleteSceneAsync(request, ct).ConfigureAwait(false);

            case Ops.ScenesActivate:
            {
                var id = RequirePayload(request, ZentwikJson.Default.IdRequest).Id;
                var activation = await _server.Scenes.ActivateAsync(id, ct).ConfigureAwait(false);
                return Ok(request, JsonSerializer.SerializeToElement(ToDto(activation), ZentwikJson.Default.SceneActivationDto));
            }

            case Ops.ScenesDeactivate:
            {
                var activation = await _server.Scenes.DeactivateAsync(ct).ConfigureAwait(false)
                                 ?? new SceneActivation(string.Empty, false, "没有活动场景。", []);
                return Ok(request, JsonSerializer.SerializeToElement(ToDto(activation), ZentwikJson.Default.SceneActivationDto));
            }

            case Ops.LedgerList:
            {
                var entries = _server.Plans.Entries.ToArray();
                return Ok(request, JsonSerializer.SerializeToElement(entries, ZentwikJson.Default.LedgerEntryArray));
            }

            case Ops.LedgerRelease:
            {
                var target = RequirePayload(request, ZentwikJson.Default.IdRequest).Id;
                var released = await _server.Plans.ReleaseTakeoverAsync(target, ct).ConfigureAwait(false);
                return Ok(request, JsonSerializer.SerializeToElement(released, ZentwikJson.Default.Boolean));
            }

            case Ops.PowerSchemesList:
            {
                var schemes = PowerSchemes().List().Select(ToDto).ToArray();
                return Ok(request, JsonSerializer.SerializeToElement(schemes, ZentwikJson.Default.PowerSchemeDtoArray));
            }

            case Ops.PowerSchemeCreate:
            {
                var payload = RequirePayload(request, ZentwikJson.Default.PowerSchemeCreateRequest);
                var created = PowerSchemes().Create(payload.Template, payload.Name);
                return Ok(request, JsonSerializer.SerializeToElement(ToDto(created), ZentwikJson.Default.PowerSchemeDto));
            }

            case Ops.PowerSchemeRename:
            {
                var payload = RequirePayload(request, ZentwikJson.Default.PowerSchemeNameRequest);
                var renamed = PowerSchemes().Rename(payload.Guid, payload.Name);
                return Ok(request, JsonSerializer.SerializeToElement(ToDto(renamed), ZentwikJson.Default.PowerSchemeDto));
            }

            case Ops.PowerSchemeDelete:
            {
                var payload = RequirePayload(request, ZentwikJson.Default.PowerSchemeTarget);
                PowerSchemes().Delete(payload.Guid);
                return Ok(request, JsonSerializer.SerializeToElement(true, ZentwikJson.Default.Boolean));
            }

            case Ops.SettingsGet:
                return Ok(request, JsonSerializer.SerializeToElement(
                    Mapping.ToDto(_server.Store.Current.Settings),
                    ZentwikJson.Default.SettingsDto));

            case Ops.SettingsSet:
                return await ApplySettingsAsync(request, ct).ConfigureAwait(false);

            case Ops.TuningRead:
            {
                var snapshot = ReadTuning();
                return Ok(request, JsonSerializer.SerializeToElement(snapshot, ZentwikJson.Default.TuningSnapshotDto));
            }

            case Ops.IntelTuningRead:
            {
                var snapshot = ReadIntelTuning();
                return Ok(request, JsonSerializer.SerializeToElement(snapshot, ZentwikJson.Default.IntelTuningSnapshotDto));
            }

            case Ops.DiagnosticsDoctor:
                return Ok(request, JsonSerializer.SerializeToElement(_server.Doctor.Build(), ZentwikJson.Default.DoctorDto));

            case Ops.DiagnosticsLog:
            {
                var limit = RequirePayload(request, ZentwikJson.Default.LogRequest).Limit;
                var entries = _server.Log.Recent(limit).ToArray();
                return Ok(request, JsonSerializer.SerializeToElement(entries, ZentwikJson.Default.LogEntryDtoArray));
            }

            case Ops.TelemetrySubscribe:
                return StartTelemetry(request);

            case Ops.TelemetryUnsubscribe:
                StopTelemetry();
                return Ok(request, null);

            default:
                return ResponseEnvelope.Failure(
                    request.Id,
                    request.Op,
                    ErrorCodes.UnknownOperation,
                    $"未知命令：{request.Op}");
        }
    }

    private async Task<ResponseEnvelope> SavePlanAsync(RequestEnvelope request, CancellationToken ct)
    {
        var incoming = RequirePayload(request, ZentwikJson.Default.Plan);
        ValidatePlan(incoming);

        var existing = _server.Store.Current.Plans.FirstOrDefault(plan =>
            string.Equals(plan.Id, incoming.Id, StringComparison.Ordinal));

        var now = DateTimeOffset.Now;
        var saved = existing is null
            ? incoming with { Revision = 1, CreatedAt = now, UpdatedAt = now, LastApply = null }
            : incoming with
            {
                Revision = existing.Revision + 1,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
                LastApply = existing.LastApply,
            };

        await _server.Store.UpdateAsync(
            document => document with
            {
                Plans = existing is null
                    ? [.. document.Plans, saved]
                    : document.Plans.Select(plan => plan.Id == saved.Id ? saved : plan).ToArray(),
            },
            ct).ConfigureAwait(false);

        _server.Watcher.NotifyConfigChanged();
        await _server.Scenes.ReapplyIfActiveAsync(saved.Id, ct).ConfigureAwait(false);
        _log.Info($"方案已保存：{saved.Id} rev {saved.Revision}");
        return Ok(request, JsonSerializer.SerializeToElement(saved, ZentwikJson.Default.Plan));
    }

    private async Task<ResponseEnvelope> DeletePlanAsync(RequestEnvelope request, CancellationToken ct)
    {
        var id = RequirePayload(request, ZentwikJson.Default.IdRequest).Id;
        var plan = FindPlan(id);

        var usedBy = _server.Store.Current.Scenes
            .Where(scene => scene.Actions.Any(action => string.Equals(action.PlanId, id, StringComparison.Ordinal)))
            .Select(static scene => scene.Id)
            .ToArray();

        if (usedBy.Length > 0)
        {
            throw new CoreException(ErrorCodes.PlanInvalid, $"方案被场景引用：{string.Join(",", usedBy)}。");
        }

        // 账本仍持有该方案的写入时，删除会让条目失去回退依据，必须先回退。
        var held = _server.Plans.Ledger.Entries
            .Where(entry => string.Equals(entry.PlanId, id, StringComparison.Ordinal) && StateLedger.IsHeld(entry))
            .Select(static entry => entry.Target)
            .ToArray();

        if (held.Length > 0)
        {
            throw new CoreException(ErrorCodes.PlanInvalid, $"方案仍持有目标写入：{string.Join(",", held)}，请先回退。");
        }

        await _server.Store.UpdateAsync(
            document => document with
            {
                Plans = document.Plans.Where(candidate => candidate.Id != plan.Id).ToArray(),
            },
            ct).ConfigureAwait(false);

        _server.Watcher.NotifyConfigChanged();
        _log.Info($"方案已删除：{plan.Id}");
        return Ok(request, JsonSerializer.SerializeToElement(true, ZentwikJson.Default.Boolean));
    }

    private async Task<ResponseEnvelope> SaveSceneAsync(RequestEnvelope request, CancellationToken ct)
    {
        var incoming = RequirePayload(request, ZentwikJson.Default.Scene);
        ValidateScene(incoming);

        var existing = _server.Store.Current.Scenes.FirstOrDefault(scene =>
            string.Equals(scene.Id, incoming.Id, StringComparison.Ordinal));

        var saved = existing is null
            ? incoming
            : incoming with { LastActivatedAt = existing.LastActivatedAt, LastOutcome = existing.LastOutcome };

        await _server.Store.UpdateAsync(
            document => document with
            {
                Scenes = existing is null
                    ? [.. document.Scenes, saved]
                    : document.Scenes.Select(scene => scene.Id == saved.Id ? saved : scene).ToArray(),
            },
            ct).ConfigureAwait(false);

        _server.Watcher.NotifyConfigChanged();
        _log.Info($"场景已保存：{saved.Id}");
        return Ok(request, JsonSerializer.SerializeToElement(saved, ZentwikJson.Default.Scene));
    }

    private async Task<ResponseEnvelope> DeleteSceneAsync(RequestEnvelope request, CancellationToken ct)
    {
        var id = RequirePayload(request, ZentwikJson.Default.IdRequest).Id;
        var scene = FindScene(id);

        if (string.Equals(_server.Scenes.ActiveSceneId, id, StringComparison.Ordinal))
        {
            await _server.Scenes.DeactivateAsync(ct).ConfigureAwait(false);
        }

        await _server.Store.UpdateAsync(
            document => document with
            {
                Scenes = document.Scenes.Where(candidate => candidate.Id != scene.Id).ToArray(),
            },
            ct).ConfigureAwait(false);

        _server.Watcher.NotifyConfigChanged();
        _log.Info($"场景已删除：{scene.Id}");
        return Ok(request, JsonSerializer.SerializeToElement(true, ZentwikJson.Default.Boolean));
    }

    private async Task<ResponseEnvelope> ApplySettingsAsync(RequestEnvelope request, CancellationToken ct)
    {
        var patch = RequirePayload(request, ZentwikJson.Default.SettingsPatch);
        var current = _server.Store.Current.Settings;

        if (!UserSettingsRules.TryApply(current, patch.Language, patch.Theme, patch.Accent, patch.UiScale, out var updated, out var error))
        {
            throw new CoreException(ErrorCodes.PayloadInvalid, error ?? "设置非法。");
        }

        var saved = await _server.Store.UpdateSettingsAsync(updated, ct).ConfigureAwait(false);
        var effective = saved?.Settings ?? updated;
        _log.Info($"设置已更新：语言={effective.Language} 主题={effective.Theme} 强调色={effective.Accent} 缩放={effective.UiScale}");

        return Ok(request, JsonSerializer.SerializeToElement(Mapping.ToDto(effective), ZentwikJson.Default.SettingsDto));
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

        return Ok(request, JsonSerializer.SerializeToElement(new SubscriptionAck(interval), ZentwikJson.Default.SubscriptionAck));
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

    private TuningSnapshotDto ReadTuning()
    {
        var status = _server.Tuning.Query();
        var readable = _server.Tuning.TryReadOffsets(out var offsets, out var readError);
        var cores = readable
            ? offsets.Select(static offset => new CoreOffsetDto(offset.Core, offset.Offset)).ToArray()
            : [];

        var availability = status.Availability switch
        {
            HardwareAvailability.Available => readable ? "available" : "rejected",
            HardwareAvailability.NotSupported => "notSupported",
            HardwareAvailability.DriverUnavailable => "driverUnavailable",
            HardwareAvailability.ModuleMissing => "moduleMissing",
            HardwareAvailability.PrivilegeMissing => "privilegeMissing",
            HardwareAvailability.Rejected => "rejected",
            _ => "probeFailed",
        };

        return new TuningSnapshotDto(
            availability,
            readError ?? status.Detail,
            status.Vendor,
            status.Platform,
            status.PersistsAcrossReboot,
            status.Simulated,
            cores);
    }

    private IntelTuningSnapshotDto ReadIntelTuning()
    {
        var status = _server.Intel.Query();
        var readable = _server.Intel.TryRead(out var state, out var readError);

        var availability = status.Availability switch
        {
            HardwareAvailability.Available => readable ? "available" : "rejected",
            HardwareAvailability.NotSupported => "notSupported",
            HardwareAvailability.DriverUnavailable => "driverUnavailable",
            HardwareAvailability.ModuleMissing => "moduleMissing",
            HardwareAvailability.PrivilegeMissing => "privilegeMissing",
            HardwareAvailability.Rejected => "rejected",
            _ => "probeFailed",
        };

        return new IntelTuningSnapshotDto(
            availability,
            readError ?? status.Detail,
            status.Vendor,
            status.Platform,
            status.PersistsAcrossReboot,
            status.Simulated,
            readable ? state.VoltageOffsetMillivolts : null,
            readable ? state.TurboRatio1Core : null,
            readable ? state.TurboRatio8Cores : null);
    }

    private AgentStatusDto BuildStatus() => new(
        Version: typeof(AgentServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        ProtocolVersion: ProtocolInfo.Version,
        MachineName: _server.System.MachineName,
        Elevated: _server.System.IsElevated,
        RunningAsService: _server.Options.ServiceMode,
        Simulated: _server.Options.UseFake,
        ProcessorCount: _server.System.ProcessorCount,
        PhysicalCoreCount: _server.System.PhysicalCoreCount,
        ProcessorGroups: _server.System.ProcessorGroups,
        StartedAt: _server.StartedAt,
        UptimeSeconds: (long)(DateTimeOffset.UtcNow - _server.StartedAt.ToUniversalTime()).TotalSeconds,
        DataDirectory: _server.Options.DataDirectory,
        ActiveSceneId: _server.Scenes.ActiveSceneId,
        PlanCount: _server.Store.Current.Plans.Count,
        SceneCount: _server.Store.Current.Scenes.Count,
        LedgerEntryCount: _server.Store.Current.Ledger.Count);

    private Plan FindPlan(string id) =>
        _server.Store.Current.Plans.FirstOrDefault(plan => string.Equals(plan.Id, id, StringComparison.Ordinal))
        ?? throw new CoreException(ErrorCodes.PlanNotFound, $"方案 {id} 不存在。");

    private Scene FindScene(string id) =>
        _server.Store.Current.Scenes.FirstOrDefault(scene => string.Equals(scene.Id, id, StringComparison.Ordinal))
        ?? throw new CoreException(ErrorCodes.SceneNotFound, $"场景 {id} 不存在。");

    private static void ValidatePlan(Plan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Id))
        {
            throw new CoreException(ErrorCodes.PlanInvalid, "方案 ID 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(plan.Name))
        {
            throw new CoreException(ErrorCodes.PlanInvalid, "方案名称不能为空。");
        }

        switch (plan)
        {
            case CpuTuningPlan cpu:
            {
                var duplicated = cpu.Cores
                    .GroupBy(static core => core.CoreIndex)
                    .FirstOrDefault(static group => group.Count() > 1);
                if (duplicated is not null)
                {
                    throw new CoreException(ErrorCodes.PlanInvalid, $"核 {duplicated.Key} 重复配置。");
                }

                if (cpu.DefaultOffset is { } defaultCo && !CurveOptimizerBounds.IsWithinL2(defaultCo))
                {
                    throw new CoreException(ErrorCodes.PlanInvalid, $"默认 CO 值的 {CurveOptimizerBounds.FormatL2Rejection(defaultCo)}");
                }

                if (cpu.CcdOffsets is not null)
                {
                    foreach (var pair in cpu.CcdOffsets)
                    {
                        if (pair.Key < 0)
                        {
                            throw new CoreException(ErrorCodes.PlanInvalid, $"CCD 编号 {pair.Key} 不能为负数。");
                        }

                        if (!CurveOptimizerBounds.IsWithinL2(pair.Value))
                        {
                            throw new CoreException(ErrorCodes.PlanInvalid, $"CCD {pair.Key} 的 {CurveOptimizerBounds.FormatL2Rejection(pair.Value)}");
                        }
                    }
                }

                foreach (var core in cpu.Cores)
                {
                    if (core.Auto && core.Offset is not null)
                    {
                        throw new CoreException(ErrorCodes.PlanInvalid, $"核 {core.CoreIndex} 不能同时设置 CO 偏移值与 auto。");
                    }

                    if (core.Offset is { } offset && !CurveOptimizerBounds.IsWithinL2(offset))
                    {
                        throw new CoreException(ErrorCodes.PlanInvalid, $"核 {core.CoreIndex} 的 {CurveOptimizerBounds.FormatL2Rejection(offset)}");
                    }
                }

                if (cpu.FMaxLimitMhz is { } fmax && fmax is < 1000 or > 7000)
                {
                    throw new CoreException(ErrorCodes.PlanInvalid, $"FMax 频率 {fmax} MHz 超出有效范围 [1000, 7000]。");
                }

                if (cpu.IntelVoltageOffsetMillivolts is { } mv && !IntelVoltageOffsetBounds.IsWithinRange(mv))
                {
                    throw new CoreException(ErrorCodes.PlanInvalid, IntelVoltageOffsetBounds.FormatRejection(mv));
                }

                break;
            }

            case PowerPlan power:
            {
                foreach (var setting in power.Settings)
                {
                    if (!Guid.TryParse(setting.SettingId, out _))
                    {
                        throw new CoreException(ErrorCodes.PlanInvalid, $"设置项 {setting.SettingId} 不是 GUID。");
                    }

                    if (setting.SubGroupId is not null && !Guid.TryParse(setting.SubGroupId, out _))
                    {
                        throw new CoreException(ErrorCodes.PlanInvalid, $"子组 {setting.SubGroupId} 不是 GUID。");
                    }
                }

                break;
            }

            case SchedulingPlan scheduling:
            {
                foreach (var rule in scheduling.Rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.ProcessMatch))
                    {
                        throw new CoreException(ErrorCodes.PlanInvalid, "调度规则缺少进程名。");
                    }

                    if (rule.CpuSets is not null && !CpuSetSpec.TryParse(rule.CpuSets, out _, out var csError))
                    {
                        throw new CoreException(ErrorCodes.PlanInvalid, csError ?? "CPU Sets 格式非法。");
                    }
                }

                break;
            }
        }
    }

    private void ValidateScene(Scene scene)
    {
        if (string.IsNullOrWhiteSpace(scene.Id) || string.IsNullOrWhiteSpace(scene.Name))
        {
            throw new CoreException(ErrorCodes.SceneInvalid, "场景 ID 与名称不能为空。");
        }

        foreach (var trigger in scene.Triggers)
        {
            if (trigger.Kind is not (SceneTriggerKinds.Manual or SceneTriggerKinds.Process or SceneTriggerKinds.PowerSource))
            {
                throw new CoreException(ErrorCodes.SceneInvalid, $"未知触发类型：{trigger.Kind}。");
            }

            if (trigger.Kind == SceneTriggerKinds.PowerSource && trigger.Argument is not ("ac" or "dc"))
            {
                throw new CoreException(ErrorCodes.SceneInvalid, "电源触发参数必须是 ac 或 dc。");
            }
        }

        foreach (var action in scene.Actions)
        {
            if (_server.Store.Current.Plans.All(plan => !string.Equals(plan.Id, action.PlanId, StringComparison.Ordinal)))
            {
                throw new CoreException(ErrorCodes.SceneInvalid, $"场景引用的方案 {action.PlanId} 不存在。");
            }
        }
    }

    private static T RequirePayload<T>(RequestEnvelope request, JsonTypeInfo<T> info)
    {
        if (request.Payload is not { } element)
        {
            throw new CoreException(ErrorCodes.PayloadInvalid, "缺少载荷。");
        }

        return element.Deserialize(info) ?? throw new CoreException(ErrorCodes.PayloadInvalid, "载荷为空。");
    }

    private static ResponseEnvelope Ok(RequestEnvelope request, JsonElement? data) =>
        ResponseEnvelope.Success(request.Id, request.Op, data);

    private PowerSchemeService PowerSchemes() =>
        _server.PowerSchemes
        ?? throw new CoreException(ErrorCodes.HardwareUnavailable, "电源方案服务未装配。");

    private static PowerSchemeDto ToDto(PowerSchemeState state) =>
        new(state.Guid, state.Name, state.Active, state.BuiltIn);

    private static PlanApplyResultDto ToDto(PlanApplyOutcome outcome) => new(
        outcome.Plan.Id,
        outcome.State switch
        {
            PlanApplyState.Applied => "applied",
            PlanApplyState.Partial => "partial",
            PlanApplyState.Failed => "failed",
            _ => "notApplied",
        },
        outcome.Plan.LastApply?.AppliedAt,
        outcome.Plan.LastApply?.PersistsAcrossReboot ?? true,
        outcome.Error ?? outcome.Plan.LastApply?.Outcome,
        [.. outcome.Targets.Select(ToDto)]);

    private static TargetOutcomeDto ToDto(TargetOutcome target) => new(
        target.Target,
        target.Previous,
        target.Desired,
        target.Actual,
        target.Status switch
        {
            LedgerStatus.Applied => "applied",
            LedgerStatus.Reverted => "reverted",
            LedgerStatus.Failed => "failed",
            _ => "skipped",
        },
        target.Failure);

    private static SceneActivationDto ToDto(SceneActivation activation) => new(
        activation.SceneId,
        activation.Active,
        activation.Outcome,
        activation.PlanIds);

    private void OnSceneChanged(SceneActivation activation) =>
        Enqueue(ResponseEnvelope.Event(
            Events.SceneChanged,
            JsonSerializer.SerializeToElement(ToDto(activation), ZentwikJson.Default.SceneActivationDto)));

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _server.SceneChanged -= OnSceneChanged;
        StopTelemetry();
        _outbound.Writer.TryComplete();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}