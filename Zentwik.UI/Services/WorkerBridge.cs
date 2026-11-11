using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Services;

/// <summary>一条命令的结局</summary>
public readonly record struct IpcOutcome<T>(bool TransportFailed, bool Success, string Message, T? Value)
    where T : class
{
    /// <summary>未连上 / 超时 / 断连。</summary>
    public static IpcOutcome<T> Unreachable(string message) => new(true, false, message, null);

    /// <summary>对端拒绝。</summary>
    public static IpcOutcome<T> Rejected(string message) => new(false, false, message, null);
}

/// <summary>UI 与 Worker 之间的唯一通道</summary>
public sealed class WorkerBridge
{
    public static WorkerBridge Instance { get; } = new();

    /// <summary>首次连不上时快速失败的时间。真正的等待交给 <see cref="WorkerLauncher"/>。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);

    private int _connected;

    private WorkerBridge()
    {
    }

    /// <summary>诊断开关 <c>--worker=offline</c> 置真时不做任何连接尝试。</summary>
    public static bool ForcedOffline { get; set; }

    /// <summary>连不上时是否允许自动拉起 Worker 进程</summary>
    public static bool AllowAutoStart { get; set; } = true;

    /// <summary>上一次通信是否成功。不含探测：返回 true 也可能下一秒就被对端关掉。</summary>
    public bool IsConnected => !ForcedOffline && Volatile.Read(ref _connected) == 1;

    /// <summary>连不上 / 超时 / 断管一律返回 false。</summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (ForcedOffline)
        {
            return false;
        }

        bool ok = await WorkerLauncher.PingAsync(timeout).ConfigureAwait(false);

        Volatile.Write(ref _connected, ok ? 1 : 0);
        return ok;
    }

    /// <summary>发一条命令。传输层失败返回 null；对端回了失败响应则原样返回（看 Success / Message）</summary>
    public async Task<PipeResponse?> SendAsync(
        string command,
        string? payload = null,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default,
        bool allowAutoStart = true)
    {
        if (ForcedOffline)
        {
            return null;
        }

        PipeResponse? response = await TrySendOnceAsync(command, payload, timeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (response is not null)
        {
            Volatile.Write(ref _connected, 1);
            return response;
        }

        Volatile.Write(ref _connected, 0);

        // 连不上就是连不上：状态灯要跟着灭，否则界面还挂着「已连接」。
        MarkOffline();

        if (!AllowAutoStart || !allowAutoStart ||
            !await WorkerLauncher.EnsureRunningAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        response = await TrySendOnceAsync(command, payload, timeoutMs, cancellationToken).ConfigureAwait(false);

        Volatile.Write(ref _connected, response is null ? 0 : 1);

        if (response is null)
        {
            MarkOffline();
        }
        else
        {
            WorkerStatus.Instance.IsConnected = true;
        }

        return response;
    }

    /// <summary>把传输层失败同步到外壳的状态灯。</summary>
    private static void MarkOffline()
    {
        WorkerStatus status = WorkerStatus.Instance;
        status.IsConnected = false;
        status.IsElevated = false;
    }

    /// <summary>一次「连接 → 发送 → 断开」。失败返回 null，不抛。</summary>
    private static async Task<PipeResponse?> TrySendOnceAsync(
        string command,
        string? payload,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new WorkerPipeClient();

            await client.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);

            return await client.SendAsync(
                new PipeRequest
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Command = command,
                    Payload = payload,
                },
                TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 契约是「失败返回 null，不抛」：管道层的超时、拒绝访问、对象已释放
            // 都要走同一条重试/降级路径，不能冒泡到业务页。
            return null;
        }
    }

    /// <summary>刷新外壳左下角的状态灯</summary>
    public async Task RefreshStatusAsync()
    {
        WorkerStatus status = WorkerStatus.Instance;

        if (ForcedOffline)
        {
            status.IsConnected = false;
            status.IsElevated = false;
            return;
        }

        PipeResponse? pong = await SendAsync(WorkerPipeContract.CommandPing, null, 800).ConfigureAwait(true);

        if (pong is not { Success: true })
        {
            status.IsConnected = false;
            status.IsElevated = false;
            return;
        }

        status.IsConnected = true;

        IpcOutcome<SmuStatusPayload> smu = await QuerySmuStatusAsync().ConfigureAwait(true);

        status.IsElevated = smu is { TransportFailed: false, Success: true, Value.Available: true };
    }

    // 调校页的三条命令（强类型出口）

    /// <summary>Success = true 只代表查询本身成功，SMU 是否可写看 SmuStatusPayload.Available。</summary>
    public async Task<IpcOutcome<SmuStatusPayload>> QuerySmuStatusAsync(
        int timeoutMs = 2_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQuerySmuStatus,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.SmuStatusPayload);
    }

    /// <summary>CO + FMax），**只读**</summary>
    public async Task<IpcOutcome<TuningSnapshotPayload>> QueryTuningSnapshotAsync(
        int timeoutMs = 15_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQuerySmuSnapshot,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.TuningSnapshotPayload);
    }

    // 方案（plans.json，三类各自独立）

    /// <summary>三类分开查，而不是一次全拿：三页各管各的方案， 合成一次会让「切了调校方案，进程页的方案条也跟着变」</summary>
    public async Task<IpcOutcome<PlanListPayload>> QueryPlansAsync(
        PlanKind kind,
        int timeoutMs = 3_000,
        CancellationToken cancellationToken = default)
    {
        string payload = JsonSerializer.Serialize(
            new PlanQueryPayload { Kind = PlanKinds.ToWire(kind) },
            IpcJsonContext.Default.PlanQueryPayload);

        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryPlans,
            payload,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.PlanListPayload);
    }

    /// <summary>三类方案各自当前生效的 Id。轮询用，只取 Id 不带参数。</summary>
    /// <remarks>
    /// 不走自动拉起：用户手动关掉的 Worker 不该被一次轮询悄悄叫回来，
    /// 那会让人以为「杀了也还在」。
    /// </remarks>
    public async Task<IpcOutcome<ActivePlansPayload>> QueryActivePlansAsync(
        int timeoutMs = 1_500,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryActivePlans,
            null,
            timeoutMs,
            cancellationToken,
            allowAutoStart: false).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.ActivePlansPayload);
    }

    /// <summary>Id 覆盖一份方案</summary>
    public async Task<IpcOutcome<string>> SavePlanAsync(
        PlanKind kind,
        string? id,
        string name,
        int[]? coreMargins = null,
        uint? fMaxMHz = null,
        string? powerSchemeGuid = null,
        ApplyPowerPolicyPayload? powerPolicy = null,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        string payload = JsonSerializer.Serialize(
            new SavePlanPayload
            {
                Kind = PlanKinds.ToWire(kind),
                Id = string.IsNullOrWhiteSpace(id) ? null : id,
                Name = name,
                CoreMargins = coreMargins,
                FMaxMHz = fMaxMHz,
                PowerSchemeGuid = powerSchemeGuid,
                PowerPolicy = powerPolicy,
            },
            IpcJsonContext.Default.SavePlanPayload);

        return await SendSimpleAsync(WorkerPipeContract.CommandSavePlan, payload, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary><c>delete-plan</c>：删除一份方案。不回滚硬件设置。</summary>
    public async Task<IpcOutcome<string>> DeletePlanAsync(
        PlanKind kind,
        string id,
        int timeoutMs = 3_000,
        CancellationToken cancellationToken = default)
    {
        string payload = JsonSerializer.Serialize(
            new PlanRefPayload { Kind = PlanKinds.ToWire(kind), Id = id },
            IpcJsonContext.Default.PlanRefPayload);

        return await SendSimpleAsync(WorkerPipeContract.CommandDeletePlan, payload, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary><c>rename-plan</c>：改名。Id 不变，场景规则引用不会失效。</summary>
    public async Task<IpcOutcome<string>> RenamePlanAsync(
        PlanKind kind,
        string id,
        string newName,
        int timeoutMs = 3_000,
        CancellationToken cancellationToken = default)
    {
        string payload = JsonSerializer.Serialize(
            new RenamePlanPayload { Kind = PlanKinds.ToWire(kind), Id = id, NewName = newName },
            IpcJsonContext.Default.RenamePlanPayload);

        return await SendSimpleAsync(WorkerPipeContract.CommandRenamePlan, payload, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>调校下发最多等 75 秒（Worker 内部等 PawnIO 60 秒）；调度与电源共用同一超时。</summary>
    public async Task<IpcOutcome<string>> ApplyPlanAsync(
        PlanKind kind,
        string id,
        int timeoutMs = 75_000,
        CancellationToken cancellationToken = default)
    {
        string payload = JsonSerializer.Serialize(
            new PlanRefPayload { Kind = PlanKinds.ToWire(kind), Id = id },
            IpcJsonContext.Default.PlanRefPayload);

        return await SendSimpleAsync(WorkerPipeContract.CommandApplyPlan, payload, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    // 界面设置 / 维护

    /// <summary><c>query-ui-settings</c>：读 ui.json 的权威值。</summary>
    public async Task<IpcOutcome<UiSettingsPayload>> QueryUiSettingsAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryUiSettings,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.UiSettingsPayload);
    }

    /// <summary><c>save-ui-settings</c>：写 ui.json，回读全量。</summary>
    public async Task<IpcOutcome<UiSettingsPayload>> SaveUiSettingsAsync(
        UiSettingsPayload payload,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        string body = JsonSerializer.Serialize(payload, IpcJsonContext.Default.UiSettingsPayload);

        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandSaveUiSettings,
            body,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.UiSettingsPayload);
    }

    /// <summary><c>query-boot-guard</c>：读启动护栏状态（连续异常启动次数 / 是否已拦截）。</summary>
    public async Task<IpcOutcome<BootGuardStatusPayload>> QueryBootGuardAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryBootGuard,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.BootGuardStatusPayload);
    }

    /// <summary><c>reset-tuning-safe</c>：恢复默认安全参数并下发一次。</summary>
    public async Task<IpcOutcome<string>> ResetTuningSafeAsync(
        int timeoutMs = 75_000,
        CancellationToken cancellationToken = default)
        => await SendSimpleAsync(
            WorkerPipeContract.CommandResetTuningSafe,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

    /// <summary><c>factory-reset</c>：清空全部用户数据并恢复默认设置。</summary>
    public async Task<IpcOutcome<string>> FactoryResetAsync(
        int timeoutMs = 60_000,
        CancellationToken cancellationToken = default)
        => await SendSimpleAsync(
            WorkerPipeContract.CommandFactoryReset,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

    // 电源策略

    /// <summary>/ DC 五项 + 方案名 + 有无电池）</summary>
    public async Task<IpcOutcome<PowerPolicySnapshot>> QueryPowerPolicyAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryPowerPolicy,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.PowerPolicySnapshot);
    }

    /// <summary><c>query-power-schemes</c>：本机全部电源方案 + 当前活动方案。</summary>
    public async Task<IpcOutcome<PowerSchemeListPayload>> QueryPowerSchemesAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryPowerSchemes,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.PowerSchemeListPayload);
    }

    /// <summary><c>set-active-scheme</c>：只换生效方案，不碰方案里的任何值。</summary>
    public async Task<IpcOutcome<string>> SetActivePowerSchemeAsync(
        Guid guid,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        return await SendSimpleAsync(
            WorkerPipeContract.CommandSetActivePowerScheme,
            guid.ToString("D"),
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回刷新后的方案列表（含新方案的 GUID 与新的活动方案），不必再查一次</summary>
    public async Task<IpcOutcome<PowerSchemeListPayload>> CreatePowerSchemeAsync(
        Guid templateGuid,
        string name,
        int timeoutMs = 20_000,
        CancellationToken cancellationToken = default)
    {
        string payload = JsonSerializer.Serialize(
            new CreatePowerSchemePayload { TemplateGuid = templateGuid, Name = name },
            IpcJsonContext.Default.CreatePowerSchemePayload);

        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandCreatePowerScheme,
            payload,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.PowerSchemeListPayload);
    }

    /// <summary><c>rename-power-scheme</c>：改显示名，返回刷新后的方案列表。</summary>
    public async Task<IpcOutcome<PowerSchemeListPayload>> RenamePowerSchemeAsync(
        Guid guid,
        string name,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        string payload = JsonSerializer.Serialize(
            new RenamePowerSchemePayload { Guid = guid, Name = name },
            IpcJsonContext.Default.RenamePowerSchemePayload);

        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandRenamePowerScheme,
            payload,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.PowerSchemeListPayload);
    }

    /// <summary>删掉的可能是活动方案，系统会自行回落到别的方案 —— 因此活动 GUID 以返回的列表为准，不要在本地猜</summary>
    public async Task<IpcOutcome<PowerSchemeListPayload>> DeletePowerSchemeAsync(
        Guid guid,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandDeletePowerScheme,
            guid.ToString("D"),
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.PowerSchemeListPayload);
    }

    /// <summary>—— Payload 里为 null 的项保持原样</summary>
    public async Task<IpcOutcome<PowerPolicySnapshot>> ApplyPowerPolicyAsync(
        ApplyPowerPolicyPayload payload,
        int timeoutMs = 30_000,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(payload, IpcJsonContext.Default.ApplyPowerPolicyPayload);

        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandApplyPowerPolicy,
            json,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            return IpcOutcome<PowerPolicySnapshot>.Unreachable("与后台服务的连接不可用，无法写入电源策略");
        }

        string message = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : response.Message;

        return new IpcOutcome<PowerPolicySnapshot>(
            false,
            response.Success,
            message,
            TryParsePowerSnapshot(response.Payload));
    }

    private static PowerPolicySnapshot? TryParsePowerSnapshot(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, IpcJsonContext.Default.PowerPolicySnapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 进程页的六条命令

    /// <summary>CPU 是采样值，按用户操作节奏刷新，不挂高频定时器。</summary>
    public async Task<IpcOutcome<List<ProcessEntryPayload>>> QueryProcessesAsync(
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryProcesses,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.ListProcessEntryPayload);
    }

    /// <summary><c>query-scheduling-rules</c>：全部调度规则。</summary>
    public async Task<IpcOutcome<List<ScheduleRulePayload>>> QuerySchedulingRulesAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQuerySchedulingRules,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.ListScheduleRulePayload);
    }

    /// <summary><c>query-masks</c>：自定义掩码列表 + 套数上限。</summary>
    public async Task<IpcOutcome<MaskListPayload>> QueryMasksAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryMasks,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.MaskListPayload);
    }

    /// <summary><c>query-cpu-topology</c>：掩码网格的核数 / 每 CCD 核数 / SMT 宽度。</summary>
    public async Task<IpcOutcome<CpuTopologyPayload>> QueryCpuTopologyAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryCpuTopology,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.CpuTopologyPayload);
    }

    /// <summary><c>save-mask</c>：新增（id 为空）或覆盖一套掩码，返回刷新后的列表。</summary>
    public async Task<IpcOutcome<MaskListPayload>> SaveMaskAsync(
        string? id,
        string name,
        ulong mask,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(
            new SaveMaskPayload
            {
                Id = string.IsNullOrWhiteSpace(id) ? string.Empty : id,
                Name = name,
                Mask = mask
            },
            IpcJsonContext.Default.SaveMaskPayload);

        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandSaveMask,
            json,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.MaskListPayload);
    }

    /// <summary><c>delete-mask</c>：按 Id 删除，返回刷新后的列表。</summary>
    public async Task<IpcOutcome<MaskListPayload>> DeleteMaskAsync(
        string id,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandDeleteMask,
            id,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.MaskListPayload);
    }

    /// <summary>保存后按「刷新列表」处理，不就地改行：Worker 可能只接受了一部分。</summary>
    public async Task<IpcOutcome<string>> SaveScheduleRuleAsync(
        SaveScheduleRulePayload payload,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(payload, IpcJsonContext.Default.SaveScheduleRulePayload);

        return await SendSimpleAsync(
            WorkerPipeContract.CommandSaveSchedulingRule,
            json,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>delete-scheduling-rule</c>：按规则 Id 删除。</summary>
    public async Task<IpcOutcome<string>> DeleteScheduleRuleAsync(
        string ruleId,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        return await SendSimpleAsync(
            WorkerPipeContract.CommandDeleteSchedulingRule,
            ruleId,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>CPU Set 绑定并回到系统默认调度</summary>
    public async Task<IpcOutcome<string>> ClearAllSchedulesAsync(
        int? pid = null,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        return await SendSimpleAsync(
            WorkerPipeContract.CommandClearAllSchedules,
            pid?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    // 日志页的两条命令

    /// <summary>最近 50 条日志，一行一条纯文本 （yyyy-MM-ddTHH:mm:ss.fff|级别|消息）</summary>
    public async Task<IpcOutcome<string>> SnapshotLogsAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        return await SendSimpleAsync(
            WorkerPipeContract.CommandSnapshotLogs,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>开机启动方式：mode 为 null 注销计划任务，其余按名称注册。</summary>
    public async Task<IpcOutcome<string>> SetAutoStartAsync(
        string? mode,
        int timeoutMs = 15_000,
        CancellationToken cancellationToken = default)
    {
        return mode is null
            ? await SendSimpleAsync(
                WorkerPipeContract.CommandUnregisterAutoStart, null, timeoutMs, cancellationToken)
                .ConfigureAwait(false)
            : await SendSimpleAsync(
                WorkerPipeContract.CommandRegisterAutoStart, mode, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary><c>clear-logs</c>：清空 Worker 的内存缓冲与日志文件。</summary>
    public async Task<IpcOutcome<string>> ClearLogsAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        return await SendSimpleAsync(
            WorkerPipeContract.CommandClearLogs,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    // 场景页的五条命令

    /// <summary><c>query-scenario-rules</c>：全部场景规则（不含总闸）。</summary>
    public async Task<IpcOutcome<List<ScenarioRulePayload>>> QueryScenarioRulesAsync(
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryScenarioRules,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.ListScenarioRulePayload);
    }

    /// <summary><c>query-scenario-state</c>：自动切换总闸。</summary>
    public async Task<IpcOutcome<ScenarioStatePayload>> QueryScenarioStateAsync(
        int timeoutMs = 3_000,
        CancellationToken cancellationToken = default)
    {
        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandQueryScenarioState,
            null,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.ScenarioStatePayload);
    }

    /// <summary>回服务端实际状态而不是回显入参：总闸写盘失败时 Worker 保留原值。</summary>
    public async Task<IpcOutcome<ScenarioStatePayload>> SetScenarioEnabledAsync(
        bool enabled,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(
            new ScenarioStatePayload { Enabled = enabled },
            IpcJsonContext.Default.ScenarioStatePayload);

        PipeResponse? response = await SendAsync(
            WorkerPipeContract.CommandSetScenarioEnabled,
            json,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);

        return ToOutcome(response, IpcJsonContext.Default.ScenarioStatePayload);
    }

    /// <summary>为空）或覆盖一条规则</summary>
    public async Task<IpcOutcome<string>> SaveScenarioRuleAsync(
        ScenarioRulePayload rule,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(rule, IpcJsonContext.Default.ScenarioRulePayload);

        return await SendSimpleAsync(
            WorkerPipeContract.CommandSaveScenarioRule,
            json,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>delete-scenario-rule</c>：按规则 Id 删除。</summary>
    public async Task<IpcOutcome<string>> DeleteScenarioRuleAsync(
        string ruleId,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        return await SendSimpleAsync(
            WorkerPipeContract.CommandDeleteScenarioRule,
            ruleId,
            timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只要「成功 / 拒绝」两态、不解析 Payload 的命令走这里</summary>
    private static async Task<IpcOutcome<string>> SendSimpleAsync(
        string command,
        string? payload,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        PipeResponse? response = await WorkerBridge.Instance
            .SendAsync(command, payload, timeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (response is null)
        {
            return IpcOutcome<string>.Unreachable("与后台服务的连接不可用");
        }

        string message = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : response.Message;

        return response.Success
            ? new IpcOutcome<string>(false, true, message, response.Payload)
            : IpcOutcome<string>.Rejected(message);
    }

    /// <summary>把原始响应折算成强类型结果</summary>
    private static IpcOutcome<T> ToOutcome<T>(
        PipeResponse? response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (response is null)
        {
            return IpcOutcome<T>.Unreachable("与后台服务的连接不可用");
        }

        string message = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : response.Message;

        if (!response.Success)
        {
            return IpcOutcome<T>.Rejected(message);
        }

        if (string.IsNullOrWhiteSpace(response.Payload))
        {
            // 对端回了 Success = true 就不能折成 Rejected（那是「命令被拒绝」）。
            // 查询方一律判 Value is null，仍走空数据分支；文案兜底否则 Toast 为空。
            string text = message.Length == 0 ? "后台服务没有返回数据" : message;
            return new IpcOutcome<T>(false, true, text, null);
        }

        try
        {
            T? value = JsonSerializer.Deserialize(response.Payload, typeInfo);

            return value is null
                ? IpcOutcome<T>.Rejected("后台服务返回的数据无法解析")
                : new IpcOutcome<T>(false, true, message, value);
        }
        catch (JsonException)
        {
            // 线格式坏了属于版本/部署问题，按「拒绝」报给用户，而不是当成通信中断去重连。
            return IpcOutcome<T>.Rejected("后台服务返回的数据无法解析");
        }
    }
}
