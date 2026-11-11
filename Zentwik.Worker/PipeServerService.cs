using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;

namespace Zentwik.Worker
{
    /// <summary>命名管道服务端：请求-响应，一行一条 JSON，每次请求一个连接。</summary>
    internal sealed class PipeServerService : IHostedService
    {
        private readonly ILogService _log;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly AutoStartService _autoStart;
        private readonly TuningApplyService _tuningApply;
        private readonly ISmuReader _smu;
        private readonly ITuningStore _tuningStore;
        private readonly PlanService _plans;
        private readonly IProcessWatcher _watcher;
        private readonly ProcessRefresher _refresher;
        private readonly ISchedulingStore _scheduling;
        private readonly IMaskStore _masks;
        private readonly ICpuSetScheduler _cpuSets;
        private readonly ScenarioService _scenarios;
        private readonly PowerPolicyService _power;
        private readonly IUiSettingsStore _settings;
        private readonly SleepResumeWatcher _sleepWatcher;
        private readonly IBootGuard _bootGuard;

        private CancellationTokenSource? _stopToken;
        private Task? _acceptLoop;

        public PipeServerService(
            ILogService log,
            IHostApplicationLifetime lifetime,
            AutoStartService autoStart,
            TuningApplyService tuningApply,
            ISmuReader smu,
            ITuningStore tuningStore,
            PlanService plans,
            IProcessWatcher watcher,
            ProcessRefresher refresher,
            ISchedulingStore bindings,
            IMaskStore masks,
            ICpuSetScheduler cpuSets,
            ScenarioService scenarios,
            PowerPolicyService power,
            IUiSettingsStore settings,
            SleepResumeWatcher sleepWatcher,
            IBootGuard bootGuard)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;

            ArgumentNullException.ThrowIfNull(lifetime);
            _lifetime = lifetime;

            ArgumentNullException.ThrowIfNull(autoStart);
            _autoStart = autoStart;

            ArgumentNullException.ThrowIfNull(tuningApply);
            _tuningApply = tuningApply;

            ArgumentNullException.ThrowIfNull(smu);
            _smu = smu;

            ArgumentNullException.ThrowIfNull(tuningStore);
            _tuningStore = tuningStore;

            ArgumentNullException.ThrowIfNull(plans);
            _plans = plans;

            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;

            ArgumentNullException.ThrowIfNull(refresher);
            _refresher = refresher;

            ArgumentNullException.ThrowIfNull(bindings);
            _scheduling = bindings;

            ArgumentNullException.ThrowIfNull(masks);
            _masks = masks;

            ArgumentNullException.ThrowIfNull(cpuSets);
            _cpuSets = cpuSets;

            ArgumentNullException.ThrowIfNull(scenarios);
            _scenarios = scenarios;

            ArgumentNullException.ThrowIfNull(power);
            _power = power;

            ArgumentNullException.ThrowIfNull(settings);
            _settings = settings;

            ArgumentNullException.ThrowIfNull(sleepWatcher);
            _sleepWatcher = sleepWatcher;

            ArgumentNullException.ThrowIfNull(bootGuard);
            _bootGuard = bootGuard;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _stopToken = new CancellationTokenSource();
            CancellationToken token = _stopToken.Token;

            SeedMaskPresets();
            MigrateLegacyCcdRules();

            _acceptLoop = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);

            // 观察任务异常：没人 await 这个循环，未观察异常会被静默丢弃。
            _ = _acceptLoop.ContinueWith(
                t => _log.Error($"管道接受循环异常退出：{t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            return Task.CompletedTask;
        }

        /// <summary>接受循环：建实例 → 等连接 → 读一行 → 回一行 → 关掉。</summary>
        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 描述符必须在 Create 时传入：事后改会抛 WRITE_DAC 并打挂循环。
                using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(
                    WorkerPipeContract.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity: CreatePipeSecurity(),
                    inheritability: HandleInheritability.None,
                    additionalAccessRights: default);

                try
                {
                    await server.WaitForConnectionAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    string line = await ReadLineAsync(server, token);

                    PipeRequest? request = JsonSerializer.Deserialize(
                        line,
                        IpcJsonContext.Default.PipeRequest);

                    PipeResponse response;

                    if (request is null)
                    {
                        response = new PipeResponse
                        {
                            Version = WorkerPipeContract.ProtocolVersion,
                            Success = false,
                            Message = "请求为空",
                            Payload = null
                        };
                    }
                    else if (!WorkerPipeContract.ValidateVersion(request.Version, out _))
                    {
                        response = WorkerPipeContract.VersionMismatchResponse();
                    }
                    else
                    {
                        try
                        {
                            response = await HandleAsync(request, token);
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"命令 {request.Command} 执行失败：{ex.Message}");
                            response = Fail($"命令执行失败：{ex.Message}");
                        }
                    }

                    await WriteLineAsync(server, response, token);

                    // 先写 bye，再延迟 100 ms 停宿主；绝不用 Environment.Exit（跳过 StopAsync）。
                    if (request is not null && request.Command == WorkerPipeContract.CommandShutdown)
                    {
                        _ = Task.Run(
                            async () =>
                            {
                                await Task.Delay(100, CancellationToken.None);
                                _lifetime.StopApplication();
                            },
                            CancellationToken.None);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 停机时正在读 / 写：正常收尾，不记异常。
                    break;
                }
                catch (Exception ex)
                {
                    // 单条命令的异常不能冲出循环：循环一停，Worker 活着却不再接受连接。
                    _log.Error($"管道命令处理异常：{ex.Message}");
                }
            }
        }

        /// <summary>管道 DACL：只给当前用户 SID 的 ReadWrite。显式 DACL 不带完整性标签，中完整性客户端才连得上。</summary>
        private static PipeSecurity CreatePipeSecurity()
        {
            PipeSecurity security = new();
            security.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            return security;
        }

        /// <summary>命令分派；未登记的命令回「未知命令」。耗时命令丢线程池，不占接受循环。</summary>
        private async Task<PipeResponse> HandleAsync(PipeRequest request, CancellationToken token)
        {
            switch (request.Command)
            {
                case WorkerPipeContract.CommandOpenUi:
                    return Program.LaunchUi(_log, null)
                        ? new PipeResponse
                        {
                            Version = WorkerPipeContract.ProtocolVersion,
                            Success = true,
                            Message = "已发起拉起 UI",
                            Payload = null
                        }
                        : Fail("拉起 UI 失败");

                case WorkerPipeContract.CommandPing:
                    return new PipeResponse
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Success = true,
                        Message = "pong",
                        Payload = null
                    };

                case WorkerPipeContract.CommandShutdown:
                    return new PipeResponse
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Success = true,
                        Message = "bye",
                        Payload = null
                    };

                case WorkerPipeContract.CommandRegisterAutoStart:
                    if (!TryParseAutoStartMode(request.Payload, out AutoStartMode mode))
                    {
                        return Fail(
                            $"Payload 必须是 AutoStartMode 名称（None / WorkerOnly / WorkerWithTray / WorkerWithUI），" +
                            $"收到：{request.Payload}");
                    }

                    return await RunAutoStartAsync(mode, token);

                case WorkerPipeContract.CommandUnregisterAutoStart:
                    return await RunAutoStartAsync(AutoStartMode.None, token);

                case WorkerPipeContract.CommandApplyTuning:
                    return await RunApplyTuningAsync(request.Payload, token);

                case WorkerPipeContract.CommandQuerySmuStatus:
                    return BuildSmuStatusResponse(_smu);

                case WorkerPipeContract.CommandQuerySmuSnapshot:
                    return await RunQuerySmuSnapshotAsync(token);

                case WorkerPipeContract.CommandQueryPlans:
                    return RunQueryPlans(request.Payload);

                case WorkerPipeContract.CommandQueryActivePlans:
                    return RunQueryActivePlans();

                case WorkerPipeContract.CommandSavePlan:
                    return await RunSavePlanAsync(request.Payload, token);

                case WorkerPipeContract.CommandDeletePlan:
                    return await RunDeletePlanAsync(request.Payload, token);

                case WorkerPipeContract.CommandRenamePlan:
                    return await RunRenamePlanAsync(request.Payload);

                case WorkerPipeContract.CommandApplyPlan:
                    return await RunApplyPlanAsync(request.Payload, token);

                case WorkerPipeContract.CommandQueryProcesses:
                    return await RunQueryProcessesAsync();

                case WorkerPipeContract.CommandClearAllSchedules:
                    return await RunClearAllSchedulesAsync(request.Payload, token);

                case WorkerPipeContract.CommandSnapshotLogs:
                    return RunSnapshotLogs();

                case WorkerPipeContract.CommandClearLogs:
                    return RunClearLogs();

                case WorkerPipeContract.CommandSaveSchedulingRule:
                    return await RunSaveScheduleRuleAsync(request.Payload, token);

                case WorkerPipeContract.CommandDeleteSchedulingRule:
                    return await RunDeleteScheduleRuleAsync(request.Payload, token);

                case WorkerPipeContract.CommandQuerySchedulingRules:
                    return RunQuerySchedulingRules();

                case WorkerPipeContract.CommandQueryMasks:
                    return RunQueryMasks();

                case WorkerPipeContract.CommandSaveMask:
                    return RunSaveMask(request.Payload);

                case WorkerPipeContract.CommandDeleteMask:
                    return RunDeleteMask(request.Payload);

                case WorkerPipeContract.CommandQueryCpuTopology:
                    return RunQueryCpuTopology();

                case WorkerPipeContract.CommandQueryScenarioRules:
                    return RunQueryScenarioRules();

                case WorkerPipeContract.CommandSaveScenarioRule:
                    return await RunSaveScenarioRuleAsync(request.Payload, token);

                case WorkerPipeContract.CommandDeleteScenarioRule:
                    return await RunDeleteScenarioRuleAsync(request.Payload, token);

                case WorkerPipeContract.CommandQueryScenarioState:
                    return RunQueryScenarioState();

                case WorkerPipeContract.CommandSetScenarioEnabled:
                    return RunSetScenarioEnabled(request.Payload);

                case WorkerPipeContract.CommandQueryPowerPolicy:
                    return RunQueryPowerPolicy(_power);

                case WorkerPipeContract.CommandApplyPowerPolicy:
                    return RunApplyPowerPolicy(_power, request.Payload);

                case WorkerPipeContract.CommandQueryPowerSchemes:
                    return RunQueryPowerSchemes(_power);

                case WorkerPipeContract.CommandSetActivePowerScheme:
                    return RunSetActivePowerScheme(_power, _plans, request.Payload);

                case WorkerPipeContract.CommandCreatePowerScheme:
                    return RunCreatePowerScheme(_power, request.Payload);

                case WorkerPipeContract.CommandRenamePowerScheme:
                    return RunRenamePowerScheme(_power, request.Payload);

                case WorkerPipeContract.CommandDeletePowerScheme:
                    return RunDeletePowerScheme(_power, request.Payload);

                case WorkerPipeContract.CommandQueryUiSettings:
                    return RunQueryUiSettings();

                case WorkerPipeContract.CommandSaveUiSettings:
                    return RunSaveUiSettings(request.Payload);

                case WorkerPipeContract.CommandQueryBootGuard:
                    return RunQueryBootGuard();

                case WorkerPipeContract.CommandResetTuningSafe:
                    return await RunResetTuningSafeAsync(token);

                case WorkerPipeContract.CommandFactoryReset:
                    return RunFactoryReset();

                default:
                    return new PipeResponse
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Success = false,
                        Message = $"未知命令：{request.Command}",
                        Payload = null
                    };
            }
        }

        /// <summary>解析 register-autostart 的 Payload；空 Payload 按旧语义处理，数值形态一律拒绝。</summary>
        internal static bool TryParseAutoStartMode(string? payload, out AutoStartMode mode)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                mode = AutoStartMode.WorkerWithUI;
                return true;
            }

            string text = payload.Trim();

            if (int.TryParse(text, out _))
            {
                mode = AutoStartMode.None;
                return false;
            }

            if (Enum.TryParse(text, ignoreCase: true, out mode) && Enum.IsDefined(mode))
                return true;

            mode = AutoStartMode.None;
            return false;
        }

        /// <summary>注册 / 注销开机自启；失败一律 Success=false + Message，不抛异常。</summary>
        private async Task<PipeResponse> RunAutoStartAsync(AutoStartMode mode, CancellationToken token)
        {
            bool enable = mode != AutoStartMode.None;
            string action = enable ? "注册" : "注销";

            try
            {
                bool ok = await Task.Run(
                    () => enable ? _autoStart.Enable(mode) : _autoStart.Disable(),
                    token).ConfigureAwait(false);

                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = ok,
                    Message = ok
                        ? (enable
                            ? $"开机自启已{action}：{AutoStartService.DescribeMode(mode)}"
                            : $"开机自启已{action}")
                        : $"{action}开机自启失败，详见 Worker 日志",
                    Payload = null
                };
            }
            catch (Exception ex)
            {
                _log.Warn($"{action}开机自启异常：{ex.GetType().Name}: {ex.Message}");

                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = $"{action}开机自启异常：{ex.Message}",
                    Payload = null
                };
            }
        }

        /// <summary>按 Id 应用调校方案；plans.json 的主副本在 Worker，只能走 IPC。</summary>
        private async Task<PipeResponse> RunApplyTuningByIdAsync(string id, CancellationToken token)
        {
            bool found = await Task.Run(
                () => _plans.Apply(PlanKind.Tuning, id, ignoreStartupGate: true).Success,
                token).ConfigureAwait(false);

            if (!found)
            {
                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = $"配置不存在：{id}",
                    Payload = ApplyStatusFailed
                };
            }

            TuningApplyOutcome? outcome = _tuningApply.LastOutcome;
            if (outcome is null)
            {
                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = "方案已切换，但未取得调校执行结果",
                    Payload = ApplyStatusFailed
                };
            }

            return BuildApplyTuningResponse(outcome);
        }

        /// <summary>统一失败响应；Version 必须写本端版本，否则对端判成版本不一致。</summary>

        internal PipeResponse RunQueryUiSettings() => UiSettingsResponse("ok");

        /// <summary>save-ui-settings：增量写 ui.json；「监听进程」翻转时同步启停监听。</summary>
        internal PipeResponse RunSaveUiSettings(string? payload)
        {
            if (string.IsNullOrEmpty(payload))
                return Fail("缺少 Payload");

            UiSettingsPayload? request;

            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.UiSettingsPayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null)
                return Fail("Payload 不是合法的 save-ui-settings 请求");

            UiSettings settings = _settings.Settings;

            if (request.AutoStartMode is { } autoStartText)
            {
                if (!Enum.TryParse(autoStartText, ignoreCase: true, out AutoStartMode mode)
                    || !Enum.IsDefined(mode)
                    || int.TryParse(autoStartText, out _))
                {
                    return Fail($"无法识别的自启形态：{autoStartText}");
                }

                settings.AutoStartMode = mode;
            }

            if (request.CloseBehavior is { } closeText)
            {
                if (!Enum.TryParse(closeText, ignoreCase: true, out CloseBehavior behavior)
                    || !Enum.IsDefined(behavior)
                    || int.TryParse(closeText, out _))
                {
                    return Fail($"无法识别的关闭行为：{closeText}");
                }

                settings.CloseBehavior = behavior;
            }

            if (request.ApplyTuningOnStartup is { } applyTuning)
                settings.ApplyTuningOnStartup = applyTuning;

            if (request.NotifyOnError is { } notifyOnError)
                settings.NotifyOnError = notifyOnError;

            if (request.ToastSeconds is { } toastSeconds)
                settings.ToastSeconds = Math.Clamp(toastSeconds, 1, 30);

            if (request.SmartAntiBrick is { } antiBrick)
                settings.SmartAntiBrick = antiBrick;

            bool hibernateChanged = false;

            if (request.SmartHibernateAware is { } hibernate && hibernate != settings.SmartHibernateAware)
            {
                settings.SmartHibernateAware = hibernate;
                hibernateChanged = true;
            }

            bool watchChanged = false;

            if (request.WatchProcessBinding is { } watch && watch != settings.WatchProcessBinding)
            {
                settings.WatchProcessBinding = watch;
                watchChanged = true;
            }

            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                return Fail($"设置已改但写盘失败：{ex.Message}");
            }

            if (watchChanged)
            {
                if (settings.WatchProcessBinding)
                    _watcher.Start();
                else
                    _watcher.Stop();

                _log.Info($"进程监听已{(settings.WatchProcessBinding ? "启动" : "停止")}");
            }

            if (hibernateChanged)
            {
                if (settings.SmartHibernateAware)
                    _sleepWatcher.Start();
                else
                    _sleepWatcher.Stop();
            }

            return UiSettingsResponse("ok");
        }

        /// <summary><c>query-boot-guard</c>：读启动护栏状态（连续异常启动次数 / 是否已拦截）。</summary>
        private PipeResponse RunQueryBootGuard()
        {
            BootGuardStatus status = _bootGuard.Read();

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = "ok",
                Payload = JsonSerializer.Serialize(
                    new BootGuardStatusPayload
                    {
                        Failures = status.Failures,
                        Blocked = status.Blocked,
                        LastAttemptAt = status.LastAttemptAt,
                        BlockedAt = status.BlockedAt
                    },
                    IpcJsonContext.Default.BootGuardStatusPayload)
            };
        }

        private PipeResponse UiSettingsResponse(string message)
        {
            UiSettings s = _settings.Settings;

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = message,
                Payload = JsonSerializer.Serialize(
                    new UiSettingsPayload
                    {
                        AutoStartMode = s.AutoStartMode.ToString(),
                        CloseBehavior = s.CloseBehavior.ToString(),
                        ApplyTuningOnStartup = s.ApplyTuningOnStartup,
                        WatchProcessBinding = s.WatchProcessBinding,
                        NotifyOnError = s.NotifyOnError,
                        ToastSeconds = s.ToastSeconds,
                        SmartAntiBrick = s.SmartAntiBrick,
                        SmartHibernateAware = s.SmartHibernateAware
                    },
                    IpcJsonContext.Default.UiSettingsPayload)
            };
        }

        /// <summary><c>reset-tuning-safe</c>：CO 归零、FMax 回基线并下发一次。</summary>
        internal async Task<PipeResponse> RunResetTuningSafeAsync(CancellationToken token)
        {
            try
            {
                TuningApplyOutcome outcome = await Task.Run(
                    () =>
                    {
                        _tuningStore.Load();
                        _tuningStore.ResetToFactory();
                        return _tuningApply.ApplyFromStore(ignoreStartupGate: true);
                    },
                    token).ConfigureAwait(false);

                return BuildApplyTuningResponse(outcome);
            }
            catch (Exception ex)
            {
                _log.Warn($"恢复默认安全参数异常：{ex.GetType().Name}: {ex.Message}");
                return Fail($"恢复默认安全参数失败：{ex.Message}");
            }
        }

        /// <summary><c>factory-reset</c>：清空方案 / 规则 / 掩码 / 场景 / 日志并重置设置。</summary>
        internal PipeResponse RunFactoryReset()
        {
            try
            {
                foreach (PlanKind kind in new[] { PlanKind.Tuning, PlanKind.Scheduling, PlanKind.Power })
                {
                    IReadOnlyList<PlanEntry> plans = _plans.Items(kind);

                    // 倒序删：正序删会让每次 Delete 后的索引与快照错位。
                    for (int i = plans.Count - 1; i >= 0; i--)
                        _plans.Delete(kind, plans[i].Id);
                }

                IReadOnlyList<ScenarioRule> rules = _scenarios.Rules;

                for (int i = rules.Count - 1; i >= 0; i--)
                    _scenarios.Delete(rules[i].Id);

                _masks.ResetToFactory();
                SeedMaskPresets();
                _scheduling.Save(Array.Empty<ScheduleRule>());
                _tuningStore.ResetToFactory();
                _settings.ResetToFactory();

                _watcher.Start();

                // 参数已重置为安全值，护栏计数要清零，否则界面仍显示「已拦截」。
                _bootGuard.MarkHealthy();

                if (_settings.Settings.SmartHibernateAware)
                    _sleepWatcher.Start();
                else
                    _sleepWatcher.Stop();

                try
                {
                    _tuningApply.ApplyFromStore(ignoreStartupGate: true);
                }
                catch (Exception ex)
                {
                    _log.Warn($"清除后下发安全参数失败：{ex.Message}");
                }

                RunClearLogs();

                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = true,
                    Message = "已清除全部用户数据",
                    Payload = null
                };
            }
            catch (Exception ex)
            {
                _log.Warn($"清除用户数据异常：{ex.GetType().Name}: {ex.Message}");
                return Fail($"清除全部用户数据失败：{ex.Message}");
            }
        }

        private static PipeResponse Fail(string message) => new()
        {
            Version = WorkerPipeContract.ProtocolVersion,
            Success = false,
            Message = message,
            Payload = null
        };

        /// <summary>query-plans：某一类方案的列表与生效项。</summary>
        internal PipeResponse RunQueryPlans(string? payload)
        {
            if (string.IsNullOrEmpty(payload))
                return Fail("缺少 Payload（需要 {\"kind\":\"tuning|scheduling|power\"}）");

            PlanQueryPayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.PlanQueryPayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null || !PlanKinds.TryParse(request.Kind, out PlanKind kind))
                return Fail("kind 必须是 tuning / scheduling / power");

            IReadOnlyList<PlanEntry> source = _plans.Items(kind);
            List<PlanEntryPayload> items = new(source.Count);

            foreach (PlanEntry entry in source)
                items.Add(ToPayload(entry));

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"共 {items.Count} 份方案",
                Payload = JsonSerializer.Serialize(
                    new PlanListPayload
                    {
                        ActiveId = _plans.ActiveId(kind),
                        Plans = items
                    },
                    IpcJsonContext.Default.PlanListPayload)
            };
        }

        /// <summary>query-active-plans：三类方案各自当前生效的 Id。</summary>
        internal PipeResponse RunQueryActivePlans()
        {
            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = "ok",
                Payload = JsonSerializer.Serialize(
                    new ActivePlansPayload
                    {
                        Tuning = _plans.ActiveId(PlanKind.Tuning),
                        Scheduling = _plans.ActiveId(PlanKind.Scheduling),
                        Power = _plans.ActiveId(PlanKind.Power)
                    },
                    IpcJsonContext.Default.ActivePlansPayload)
            };
        }

        private static PlanEntryPayload ToPayload(PlanEntry entry) => new()
        {
            Id = entry.Id,
            Name = entry.Name,
            Kind = PlanKinds.ToWire(entry.Kind),
            IsActive = entry.IsActive,
            CoreMargins = entry.CurveOptimizer ?? Array.Empty<int>(),
            FMaxMHz = entry.FMaxMHz,
            RuleCount = entry.ScheduleRules?.Count ?? 0,
            RuleDigest = ScheduleRuleDigest.Of(
                entry.ScheduleRules is null
                    ? null
                    : BuildScheduleRulePayloads(entry.ScheduleRules)),
            PowerSchemeGuid = entry.PowerSchemeGuid,
            PowerSchemeName = entry.PowerSchemeName,
            PowerPolicy = entry.PowerPolicy
        };

        /// <summary>save-plan：新建或按 Id 覆盖一份方案；调度方案存保存那一刻的规则集。</summary>
        internal async Task<PipeResponse> RunSavePlanAsync(string? payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(payload))
                return Fail("缺少 Payload（需要 {\"kind\":...,\"name\":...}）");

            SavePlanPayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.SavePlanPayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null || !PlanKinds.TryParse(request.Kind, out PlanKind kind))
                return Fail("kind 必须是 tuning / scheduling / power");

            string name = (request.Name ?? string.Empty).Trim();
            if (name.Length == 0)
                return Fail("名称不能为空");

            string? id = string.IsNullOrWhiteSpace(request.Id) ? null : request.Id.Trim();
            string? guid = request.PowerSchemeGuid;

            (bool ok, string message, string? savedId) = await Task.Run(
                () =>
                {
                    PlanEntry? target = null;

                    if (id is not null)
                    {
                        target = _plans.Find(kind, id);
                        if (target is null)
                            return (false, $"方案不存在：{id}", (string?)null);
                    }

                    if (_plans.NameExists(kind, name, target?.Id))
                        return (false, $"已存在同名方案：{name}", (string?)null);

                    PlanEntry entry = target ?? _plans.CaptureCurrent(kind, name);
                    entry.Name = name;

                    switch (kind)
                    {
                        case PlanKind.Tuning:
                            if (request.CoreMargins is not null)
                                entry.CurveOptimizer = request.CoreMargins;
                            if (request.FMaxMHz.HasValue)
                                entry.FMaxMHz = request.FMaxMHz.Value;
                            break;

                        case PlanKind.Scheduling:
                        {
                            PlanEntry captured = _plans.CaptureCurrent(kind, name);
                            entry.ScheduleRules = captured.ScheduleRules;
                            break;
                        }

                        default:
                        {
                            // 系统计划可选：给了就校验存在，不给沿用原值。
                            if (!string.IsNullOrWhiteSpace(guid))
                            {
                                if (!Guid.TryParse(guid, out Guid scheme))
                                    return (false, "电源方案 GUID 无效", (string?)null);

                                bool exists = false;
                                string systemName = string.Empty;

                                try
                                {
                                    foreach ((Guid itemGuid, string? itemName) in _power.ListSchemes())
                                    {
                                        if (itemGuid != scheme)
                                            continue;

                                        exists = true;
                                        systemName = itemName ?? string.Empty;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    return (false, $"读取电源方案失败：{ex.Message}", (string?)null);
                                }

                                if (!exists)
                                    return (false, "这个电源方案不存在（可能已被系统删除）", (string?)null);

                                entry.PowerSchemeGuid = scheme.ToString("D");
                                entry.PowerSchemeName = systemName;
                            }

                            // 五项参数由界面送上；不传表示只改名 / 只改 GUID。
                            if (request.PowerPolicy is not null)
                                entry.PowerPolicy = request.PowerPolicy;

                            break;
                        }
                    }

                    _plans.Save(entry);
                    return (true, $"已保存「{name}」", entry.Id);
                },
                token).ConfigureAwait(false);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = ok,
                Message = message,
                Payload = savedId
            };
        }

        /// <summary><c>delete-plan</c>：删除一份方案。生效中方案被删只清标记，不回滚硬件。</summary>
        internal async Task<PipeResponse> RunDeletePlanAsync(string? payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(payload))
                return Fail("缺少 Payload（需要 {\"kind\":...,\"id\":\"...\"}）");

            PlanRefPayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.PlanRefPayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null
                || !PlanKinds.TryParse(request.Kind, out PlanKind kind)
                || string.IsNullOrWhiteSpace(request.Id))
            {
                return Fail("kind 与 id 都必须给全");
            }

            string id = request.Id.Trim();
            bool deleted = await Task.Run(() => _plans.Delete(kind, id), token).ConfigureAwait(false);

            // 方案没了，引用它的场景规则要一并置为「保持原样」，否则命中即失败。
            int detached = deleted ? _scenarios.DetachPlan(kind, id) : 0;

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = deleted,
                Message = deleted
                    ? (detached > 0
                        ? $"已删除方案，{detached} 条场景规则解除引用"
                        : "已删除方案")
                    : $"方案不存在：{id}",
                Payload = null
            };
        }

        /// <summary><c>rename-plan</c>：改名。改名不改 Id —— 场景规则引用的是 Id。</summary>
        internal Task<PipeResponse> RunRenamePlanAsync(string? payload)
        {
            if (string.IsNullOrEmpty(payload))
                return Task.FromResult(Fail("缺少 Payload（需要 {\"kind\":...,\"id\":\"...\",\"newName\":\"...\"}）"));

            RenamePlanPayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.RenamePlanPayload);
            }
            catch (JsonException ex)
            {
                return Task.FromResult(Fail($"Payload 不是合法 JSON：{ex.Message}"));
            }

            if (request is null
                || !PlanKinds.TryParse(request.Kind, out PlanKind kind)
                || string.IsNullOrWhiteSpace(request.Id))
            {
                return Task.FromResult(Fail("kind 与 id 都必须给全"));
            }

            string newName = (request.NewName ?? string.Empty).Trim();
            if (newName.Length == 0)
                return Task.FromResult(Fail("名称不能为空"));

            bool ok = _plans.Rename(kind, request.Id.Trim(), newName, out string message);

            return Task.FromResult(new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = ok,
                Message = message,
                Payload = null
            });
        }

        /// <summary>apply-plan：只写方案所属的那一块（调校 / 调度 / 电源）。</summary>
        internal async Task<PipeResponse> RunApplyPlanAsync(string? payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(payload))
                return Fail("缺少 Payload（需要 {\"kind\":...,\"id\":\"...\"}）");

            PlanRefPayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.PlanRefPayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null
                || !PlanKinds.TryParse(request.Kind, out PlanKind kind)
                || string.IsNullOrWhiteSpace(request.Id))
            {
                return Fail("kind 与 id 都必须给全");
            }

            string id = request.Id.Trim();

            (bool success, string message, string status) = await Task.Run(
                () =>
                {
                    PlanApplyResult result = _plans.Apply(kind, id, ignoreStartupGate: true);
                    if (!result.Success)
                        return (false, result.Message, result.Status);

                    if (kind != PlanKind.Power)
                        return (true, result.Message, result.Status);

                    // 快照必须在切完系统计划之后写：PowerPolicyService 只写当前活动方案。
                    ApplyPowerPolicyPayload? patch = _plans.Find(PlanKind.Power, id)?.PowerPolicy;
                    if (patch is null || patch.IsEmpty)
                        return (true, result.Message, result.Status);

                    PipeResponse write = RunApplyPowerPolicy(
                        _power,
                        JsonSerializer.Serialize(patch, IpcJsonContext.Default.ApplyPowerPolicyPayload));

                    return write.Success
                        ? (true, result.Message, result.Status)
                        : (false, $"{result.Message}；{write.Message}", "failed");
                },
                token).ConfigureAwait(false);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = success,
                Message = message,
                Payload = status
            };
        }


        /// <summary>进程列表查询可接受的快照年龄。</summary>
        private static readonly TimeSpan ProcessSnapshotMaxAge = TimeSpan.FromSeconds(2);

        /// <summary>进程列表 + 规则命中。2 秒内的快照直接复用，过期才现采。</summary>
        internal Task<PipeResponse> RunQueryProcessesAsync()
        {
            IReadOnlyList<ProcessRefresher.Entry> snapshot =
                _refresher.TryGetSnapshot(ProcessSnapshotMaxAge)
                ?? _refresher.SampleOnSamplerThread(TimeSpan.FromSeconds(2));

            List<ProcessEntryPayload> items = BuildProcessEntries(
                snapshot,
                _scheduling.Profile.Rules);

            return Task.FromResult(new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"共 {items.Count} 个进程",
                Payload = JsonSerializer.Serialize(items, IpcJsonContext.Default.ListProcessEntryPayload)
            });
        }

        /// <summary>采样条目 → 协议条目，用与下发绑定时同一份规则匹配算 matchedRuleName。</summary>
        internal static List<ProcessEntryPayload> BuildProcessEntries(
            IReadOnlyList<ProcessRefresher.Entry> entries,
            IReadOnlyList<ScheduleRule> rules)
        {
            List<ProcessEntryPayload> items = new(entries.Count);

            // 与真正下发绑定时同一份索引：跳过未启用、双方 Trim、忽略大小写。
            Dictionary<string, ScheduleRule> index = ProcessWatcher.BuildRuleIndex(rules);

            foreach (ProcessRefresher.Entry entry in entries)
            {
                ScheduleRule? rule = index.TryGetValue(entry.Name.Trim(), out ScheduleRule? hit) ? hit : null;

                items.Add(new ProcessEntryPayload
                {
                    Pid = entry.Pid,
                    Name = entry.Name,
                    Path = entry.Path,
                    Cpu = entry.Cpu,
                    Priority = entry.PriorityClass,
                    MatchedRuleName = rule?.Name
                });
            }

            return items;
        }

        internal const int SnapshotLogLimit = 50;

        /// <summary>取最近 50 条日志；批量响应不设上限会撞 MaxMessageBytes。</summary>
        internal PipeResponse RunSnapshotLogs()
        {
            IReadOnlyList<LogEntry> entries = _log.Snapshot();
            int skip = Math.Max(0, entries.Count - SnapshotLogLimit);

            StringBuilder builder = new();
            for (int i = skip; i < entries.Count; i++)
            {
                LogEntry entry = entries[i];

                if (builder.Length > 0)
                    builder.Append('\n');

                builder.Append(entry.Time.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))
                       .Append('|')
                       .Append(entry.LevelText)
                       .Append('|')
                       .Append(EscapeLogMessage(entry.Message));
            }

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"最近 {entries.Count - skip} 条日志",
                Payload = builder.ToString()
            };
        }

        /// <summary>clear-logs：清空内存缓冲并删日志文件；删文件不等待。</summary>
        internal PipeResponse RunClearLogs()
        {
            _log.Clear();

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = "已清空日志",
                Payload = null
            };
        }

        /// <summary>把换行转义成字面量：协议按行切分，一条日志必须占一行。</summary>
        private static string EscapeLogMessage(string message) =>
            message.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");

        /// <summary>解绑；空 Payload = 解绑全部，给进程 Id = 只解绑那一个。</summary>
        internal async Task<PipeResponse> RunClearAllSchedulesAsync(
            string? payload,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                int unbound = await Task.Run(() => _watcher.ClearAllSchedules(), token).ConfigureAwait(false);

                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = true,
                    Message = $"已解绑 {unbound} 个进程",
                    Payload = null
                };
            }

            if (!int.TryParse(payload.Trim(), out int pid) || pid <= 0)
            {
                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = $"Payload 必须是进程 Id 或留空，收到：{payload}",
                    Payload = null
                };
            }

            bool ok = await Task.Run(() => _watcher.ClearSchedule(pid)).ConfigureAwait(false);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = ok,
                Message = ok
                    ? $"已解绑 PID {pid}"
                    : $"PID {pid} 解绑失败（进程可能已退出，或权限不足）",
                Payload = null
            };
        }

        /// <summary>新增或覆盖调度规则后立刻重扫；带 id 就地更新，否则按进程名匹配。</summary>
        internal async Task<PipeResponse> RunSaveScheduleRuleAsync(string? payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return Fail(
                    "缺少 Payload（需要 {\"processName\":\"...\",\"targetType\":\"ccd|mask\"," +
                    "\"targetValue\":\"...\",\"priority\":\"...\"}）");
            }

            SaveScheduleRulePayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.SaveScheduleRulePayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null || string.IsNullOrWhiteSpace(request.ProcessName))
                return Fail("processName 不能为空");

            string processName = request.ProcessName.Trim();
            string requestId = (request.Id ?? string.Empty).Trim();

            // Enabled 留空 = 不动这条规则，新建按启用处理。
            bool enabled = request.Enabled ?? true;

            ScheduleRule rule = new() { Name = processName, Enabled = enabled };

            string targetType = (request.TargetType ?? string.Empty).Trim();
            string targetValue = (request.TargetValue ?? string.Empty).Trim();

            if (string.Equals(targetType, "ccd", StringComparison.OrdinalIgnoreCase))
            {
                // 旧协议：ccd + 核心组序号。换算成掩码后按掩码规则存，不再有 CCD 枚举。
                if (!int.TryParse(targetValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int groupIndex))
                    return Fail($"ccd 的 targetValue 必须是核心组序号，收到：{targetValue}");

                if (!TryResolveGroupMask(groupIndex, out ulong groupMask, out CustomMask? preset))
                    return Fail(
                        $"核心组 {groupIndex} 不存在或读不到拓扑（本机 {_cpuSets.CoreGroups.Count} 组）");

                rule.Target = SchedulingTarget.TargetMask;
                rule.TargetMask = groupMask;
                rule.MaskId = preset?.Id ?? string.Empty;
                rule.MaskName = preset?.Name ?? MaskFormatter.ToRangeText(groupMask);
            }
            else if (string.Equals(targetType, "mask", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(targetValue))
                    return Fail("targetType=mask 时 targetValue 必须是掩码 Id");

                // 掩码值一起快照进规则：掩码日后被删，规则仍能按快照下发。
                CustomMask? mask = _masks.FindById(targetValue);
                if (mask is null)
                    return Fail($"掩码不存在：{targetValue}");

                rule.Target = SchedulingTarget.TargetMask;
                rule.MaskId = mask.Id;
                rule.TargetMask = mask.Mask;
                rule.MaskName = mask.Name;
            }
            else
            {
                return Fail($"targetType 必须是 ccd 或 mask，收到：{request.TargetType}");
            }

            if (!TryNormalizePriority(request.Priority, out string priority))
            {
                return Fail(
                    $"未知的优先级档位：{request.Priority}" +
                    $"（可选 {string.Join(" / ", ProcessPriority.Options)}，留空表示不改优先级）");
            }

            rule.Priority = priority;

            await Task.Run(
                () =>
                {
                    // Profile.Rules 是内部列表的引用，先复制再改。
                    List<ScheduleRule> rules = _scheduling.Profile.Rules.ToList();

                    int index = -1;

                    if (requestId.Length > 0)
                    {
                        index = rules.FindIndex(r =>
                            string.Equals(r.Id, requestId, StringComparison.OrdinalIgnoreCase));
                    }

                    // Id 没命中就退回按进程名匹配，保证同名规则只有一条。
                    if (index < 0)
                    {
                        index = rules.FindIndex(r =>
                            string.Equals((r.Name ?? string.Empty).Trim(), processName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (index >= 0)
                    {
                        // 覆盖时保留原 Id：换 Id 会让界面手里的 Id 立刻失效。
                        rule.Id = rules[index].Id;
                        rules[index] = rule;
                    }
                    else
                    {
                        rules.Add(rule);
                    }

                    _scheduling.Save(rules);
                    _watcher.ReapplyNow();
                },
                token).ConfigureAwait(false);

            _log.Info($"调度规则已保存：{processName} → {rule.Label}（优先级：{(priority.Length == 0 ? "不改" : priority)}，{(enabled ? "启用" : "已禁用")}）");

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"已保存规则：{processName} → {rule.Label}",
                Payload = null
            };
        }

        /// <summary>删除调度规则并重扫；按 Id 删，找不到退回按名字。删规则不解绑已绑进程。</summary>
        internal async Task<PipeResponse> RunDeleteScheduleRuleAsync(string? payload, CancellationToken token)
        {
            string key = (payload ?? string.Empty).Trim();
            if (key.Length == 0)
                return Fail("Payload 必须是规则 Id（也可继续传进程名，如 msedge.exe）");

            string? removedName = await Task.Run(
                () =>
                {
                    List<ScheduleRule> rules = _scheduling.Profile.Rules.ToList();

                    int index = rules.FindIndex(r =>
                        string.Equals(r.Id, key, StringComparison.OrdinalIgnoreCase));

                    if (index < 0)
                    {
                        index = rules.FindIndex(r =>
                            string.Equals((r.Name ?? string.Empty).Trim(), key, StringComparison.OrdinalIgnoreCase));
                    }

                    if (index < 0)
                        return (string?)null;

                    string name = string.IsNullOrEmpty(rules[index].Name) ? key : rules[index].Name;
                    rules.RemoveAt(index);

                    _scheduling.Save(rules);
                    _watcher.ReapplyNow();
                    return name;
                },
                token).ConfigureAwait(false);

            if (removedName is null)
                return Fail($"规则不存在：{key}");

            _log.Info($"调度规则已删除：{removedName}");

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"已删除规则：{removedName}",
                Payload = null
            };
        }

        /// <summary>
        /// 把核心组铺成预置掩码项。
        ///
        /// 核心组由硬件决定，运行中不会变，所以只在启动时跑一次。
        /// 已铺过的组合记在 ledger 里，用户删掉某一项后重启不会又冒出来。
        /// </summary>
        private void SeedMaskPresets()
        {
            IReadOnlyList<CpuCoreGroup> groups = _cpuSets.CoreGroups;
            if (groups.Count == 0)
            {
                _log.Warn("核心组拓扑不可用，跳过预置掩码生成");
                return;
            }

            CpuVendor vendor = CpuVendorProbe.Detect();

            List<(string Key, string Name, ulong Mask)> presets = new(groups.Count);
            foreach (CpuCoreGroup group in groups)
            {
                presets.Add((
                    $"group:0x{group.Mask:X}",
                    CpuVendorProbe.GroupName(vendor, group.Index, groups.Count),
                    group.Mask));
            }

            int added = _masks.SeedPresets(presets);
            if (added > 0)
                _log.Info($"生成 {added} 个核心组预置掩码（{vendor}，共 {groups.Count} 组）");
        }

        /// <summary>
        /// 旧版按 CCD 序号绑定的规则改写成掩码。
        ///
        /// 不改也能跑（ProcessWatcher 留了 ScheduleToCcd 兜底），改完才能让
        /// 「组数超过 2」和「核心组改名 / 删除」这些场景成立。
        /// </summary>
        private void MigrateLegacyCcdRules()
        {
            IReadOnlyList<CpuCoreGroup> groups = _cpuSets.CoreGroups;
            if (groups.Count == 0)
                return;

            List<ScheduleRule> rules = _scheduling.Profile.Rules.ToList();
            int migrated = 0;

            foreach (ScheduleRule rule in rules)
            {
                if (MigrateRule(rule, groups))
                    migrated++;
            }

            if (migrated > 0)
            {
                _scheduling.Save(rules);
                _log.Info($"把 {migrated} 条旧版 CCD 规则改写成掩码");
            }

            MigratePlanSnapshots(groups);
        }

        /// <summary>
        /// 方案快照里的旧 CCD 规则一并改写。
        ///
        /// 只迁移 schedules.json 的话，方案里还是 `ccd|1`、实时规则已是 `mask|值`，
        /// 指纹永远不相等 —— 界面每次重启都把方案标成「未保存」。
        /// </summary>
        private void MigratePlanSnapshots(IReadOnlyList<CpuCoreGroup> groups)
        {
            foreach (PlanEntry plan in _plans.Items(PlanKind.Scheduling))
            {
                int changed = 0;

                foreach (ScheduleRule rule in plan.ScheduleRules)
                {
                    if (MigrateRule(rule, groups))
                        changed++;
                }

                if (changed == 0)
                    continue;

                _plans.Save(plan);
                _log.Info($"方案「{plan.Name}」里的 {changed} 条旧版 CCD 规则已改写成掩码");
            }
        }

        /// <summary>单条规则的 ccd → mask 改写。已经是掩码时返回 false。</summary>
        private bool MigrateRule(ScheduleRule rule, IReadOnlyList<CpuCoreGroup> groups)
        {
            int index = rule.LegacyCcdIndex;
            if (index < 0 || index >= groups.Count)
                return false;

            CpuCoreGroup group = groups[index];
            CustomMask? preset = _masks.Masks.FirstOrDefault(m => m.Mask == group.Mask);

            return rule.ApplyLegacyMask(
                group.Mask,
                preset?.Id ?? string.Empty,
                preset?.Name ?? MaskFormatter.ToRangeText(group.Mask));
        }

        /// <summary>核心组序号换算成预置掩码项。旧协议（targetType=ccd）的落点。</summary>
        private bool TryResolveGroupMask(int groupIndex, out ulong mask, out CustomMask? preset)
        {
            mask = 0;
            preset = null;

            IReadOnlyList<CpuCoreGroup> groups = _cpuSets.CoreGroups;
            if (groupIndex < 0 || groupIndex >= groups.Count)
                return false;

            ulong resolved = groups[groupIndex].Mask;
            if (resolved == 0)
                return false;

            mask = resolved;

            // 同掩码的预置项优先，让规则能挂上稳定的 MaskId
            preset = _masks.Masks.FirstOrDefault(m => m.Mask == resolved);
            return true;
        }

        /// <summary>优先级归一化：空串合法，其余必须命中 Options 里的常量并回写标准大小写。</summary>
        private static bool TryNormalizePriority(string? raw, out string canonical)
        {
            canonical = string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
                return true;

            string text = raw.Trim();

            foreach (string option in ProcessPriority.Options)
            {
                if (string.Equals(option, text, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = option;
                    return true;
                }
            }

            return false;
        }


        /// <summary>全部调度规则的只读投影；不重读盘，本进程是唯一写入方。</summary>
        internal PipeResponse RunQuerySchedulingRules()
        {
            List<ScheduleRulePayload> items = BuildScheduleRulePayloads(_scheduling.Profile.Rules);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"共 {items.Count} 条调度规则",
                Payload = JsonSerializer.Serialize(items, IpcJsonContext.Default.ListScheduleRulePayload)
            };
        }

        /// <summary>query-masks：列出全部掩码与套数上限；读内存不重读盘。</summary>
        internal PipeResponse RunQueryMasks()
        {
            MaskListPayload payload = BuildMaskListPayload();

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,

                Success = true,
                Message = $"共 {payload.Masks.Count} 套自定义掩码",
                Payload = JsonSerializer.Serialize(payload, IpcJsonContext.Default.MaskListPayload)
            };
        }

        /// <summary>query-cpu-topology：掩码网格格子数来源（逻辑核数 + CCD 数）。</summary>
        internal PipeResponse RunQueryCpuTopology()
        {
            int logical = _cpuSets.LogicalProcessorCount;
            if (logical <= 0)
                logical = Environment.ProcessorCount;
            if (logical <= 0)
                return Fail("无法读取本机逻辑处理器数量");

            int cores;
            int threadsPerCore;

            if (!CpuTopologyProbe.TryRead(out cores, out threadsPerCore) || cores <= 0)
            {
                cores = logical;
                threadsPerCore = 1;
            }

            if (cores > logical)
                cores = logical;

            int coresPerCcd = (int)_smu.PeekCoresPerCcdRaw();

            if (coresPerCcd <= 0)
            {
                int groupCount = _cpuSets.CoreGroups.Count;
                coresPerCcd = groupCount > 0 ? cores / groupCount : 0;
            }

            if (coresPerCcd <= 0 || coresPerCcd > cores)
                coresPerCcd = cores;

            CpuTopologyPayload payload = new()
            {
                Cores = cores,
                CoresPerCcd = coresPerCcd,
                ThreadsPerCore = threadsPerCore > 0 ? threadsPerCore : 1
            };

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"{cores} 核 / 每 CCD {coresPerCcd} 核 / 每核 {payload.ThreadsPerCore} 线程",
                Payload = JsonSerializer.Serialize(payload, IpcJsonContext.Default.CpuTopologyPayload)
            };
        }

        /// <summary>save-mask：新增或按 Id 覆盖一套掩码，成功后回整份列表。</summary>
        internal PipeResponse RunSaveMask(string? payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return Fail("缺少 Payload（需要 {\"id\":\"...\",\"name\":\"...\",\"mask\":65280}）");
            }

            SaveMaskPayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.SaveMaskPayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null)
                return Fail("Payload 不是合法的 save-mask 请求");

            if (request.Mask == 0)
                return Fail("至少要选一个逻辑核");

            string id = (request.Id ?? string.Empty).Trim();

            CustomMask mask = new()
            {
                Name = (request.Name ?? string.Empty).Trim(),
                Mask = request.Mask
            };

            if (id.Length > 0)
            {
                if (_masks.FindById(id) is null)
                    return Fail($"掩码不存在：{id}");

                mask.Id = id;
            }

            if (!_masks.Upsert(mask, out MaskStore.UpsertError error))
                return Fail(MaskStore.Describe(error));

            _log.Info($"掩码已保存：{mask.Name}（{mask.CoreCount} 个逻辑核，掩码 {mask.Mask}）");

            return MaskListResponse($"已保存掩码：{mask.Name}");
        }

        /// <summary><c>delete-mask</c>：按 Id 删除，成功后回整份列表。</summary>
        internal PipeResponse RunDeleteMask(string? payload)
        {
            string id = (payload ?? string.Empty).Trim();

            if (id.Length == 0)
                return Fail("缺少掩码 Id");

            CustomMask? mask = _masks.FindById(id);
            if (mask is null)
                return Fail($"掩码不存在：{id}");

            _masks.Remove(id);
            _log.Info($"掩码已删除：{mask.Name}");

            // 调度规则按掩码值快照生效，这里不连带删除。
            return MaskListResponse($"已删除掩码：{mask.Name}");
        }

        private PipeResponse MaskListResponse(string message) => new()
        {
            Version = WorkerPipeContract.ProtocolVersion,
            Success = true,
            Message = message,
            Payload = JsonSerializer.Serialize(BuildMaskListPayload(), IpcJsonContext.Default.MaskListPayload)
        };

        private MaskListPayload BuildMaskListPayload()
        {
            MaskListPayload payload = new() { Limit = MaskStore.MaxCount };

            foreach (CustomMask mask in _masks.Masks)
            {
                payload.Masks.Add(new MaskEntryPayload
                {
                    Id = mask.Id,
                    Name = mask.Name,
                    Mask = mask.Mask,
                    CoreCount = mask.CoreCount
                });
            }

            return payload;
        }

        /// <summary>ScheduleRule → 协议投影。</summary>
        internal static List<ScheduleRulePayload> BuildScheduleRulePayloads(IReadOnlyList<ScheduleRule> rules)
        {
            List<ScheduleRulePayload> items = new(rules.Count);

            foreach (ScheduleRule rule in rules)
            {
                // 旧版 CCD 规则给组序号，掩码规则给掩码值，none 给空串 —— 格式见 ScheduleRulePayload 注释。
                string targetType;
                string targetValue;

                if (rule.IsLegacyCcd)
                {
                    targetType = "ccd";
                    targetValue = rule.LegacyCcdIndex.ToString(CultureInfo.InvariantCulture);
                }
                else if (rule.Target == SchedulingTarget.TargetMask)
                {
                    targetType = "mask";
                    targetValue = rule.TargetMask.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    targetType = "none";
                    targetValue = string.Empty;
                }

                items.Add(new ScheduleRulePayload
                {
                    // Id 只由 ScheduleRule 初始化器生成，投影时不现场补。
                    Id = rule.Id ?? string.Empty,
                    ProcessName = rule.Name ?? string.Empty,
                    TargetType = targetType,
                    TargetValue = targetValue,
                    Priority = rule.Priority ?? string.Empty,
                    Enabled = rule.Enabled,
                    Path = rule.Path ?? string.Empty
                });
            }

            return items;
        }


        /// <summary>全部场景规则的只读投影（不含总闸，理由见契约注释）。</summary>
        internal PipeResponse RunQueryScenarioRules()
        {
            List<ScenarioRulePayload> items = BuildScenarioRulePayloads(_scenarios.Rules);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"共 {items.Count} 条场景规则",
                Payload = JsonSerializer.Serialize(items, IpcJsonContext.Default.ListScenarioRulePayload)
            };
        }

        /// <summary>ScenarioRule → 协议投影；派生值（TriggerText / ProfileName）不传。</summary>
        internal static List<ScenarioRulePayload> BuildScenarioRulePayloads(IReadOnlyList<ScenarioRule> rules)
        {
            List<ScenarioRulePayload> items = new(rules.Count);

            foreach (ScenarioRule rule in rules)
            {
                items.Add(new ScenarioRulePayload
                {
                    Id = rule.Id,
                    Trigger = rule.Trigger,
                    Argument = rule.Argument ?? string.Empty,
                    ProfileId = rule.ProfileId ?? string.Empty,
                    MaskId = rule.MaskId ?? string.Empty,
                    PowerSchemeId = rule.PowerSchemeId ?? string.Empty,
                    Priority = rule.Priority,
                    Enabled = rule.Enabled
                });
            }

            return items;
        }

        /// <summary>新增或覆盖一条场景规则；Id 为空 = 新建，传不存在的 Id 也按新建处理。</summary>
        internal async Task<PipeResponse> RunSaveScenarioRuleAsync(string? payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(payload))
                return Fail("缺少 Payload（需要 {id,trigger,argument,profileId,priority,enabled}）");

            ScenarioRulePayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.ScenarioRulePayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null)
                return Fail("Payload 为空");

            string profileId = (request.ProfileId ?? string.Empty).Trim();
            string maskId = (request.MaskId ?? string.Empty).Trim();
            string powerSchemeId = (request.PowerSchemeId ?? string.Empty).Trim();

            // 三个动作全空 = 命中也没事可做。
            if (profileId.Length == 0 && maskId.Length == 0 && powerSchemeId.Length == 0)
                return Fail("至少要选一个动作：调校 / 调度 / 电源");

            if (profileId.Length > 0 && _plans.Find(PlanKind.Tuning, profileId) is null)
                return Fail($"方案不存在：{profileId}");

            if (maskId.Length > 0
                && _plans.Find(PlanKind.Scheduling, maskId) is null
                && _masks.FindById(maskId) is null)
                return Fail($"调度方案不存在：{maskId}");

            if (powerSchemeId.Length > 0
                && _plans.Find(PlanKind.Power, powerSchemeId) is null
                && !Guid.TryParse(powerSchemeId, out _))
                return Fail($"电源方案不存在：{powerSchemeId}");

            string argument = (request.Argument ?? string.Empty).Trim();

            // 进程类触发必须有进程名；电源类的 argument 必须为空，否则永不命中。
            bool isProcessTrigger = request.Trigger == ScenarioTrigger.ProcessRunning
                                 || request.Trigger == ScenarioTrigger.ProcessExited;

            if (isProcessTrigger && argument.Length == 0)
                return Fail($"trigger={request.Trigger} 时 argument 必须是进程名");

            if (!isProcessTrigger && argument.Length != 0)
                return Fail("电源类触发的 argument 必须为空");

            string id = (request.Id ?? string.Empty).Trim();
            bool created = id.Length == 0;

            (bool ok, string message) = await Task.Run(
                () =>
                {
                    ScenarioRule rule = new();

                    if (!created)
                    {
                        if (_scenarios.Rules.All(r => !string.Equals(r.Id, id, StringComparison.Ordinal)))
                            return (false, $"规则不存在：{id}");

                        rule.Id = id;
                    }

                    rule.Trigger = request.Trigger;
                    rule.Argument = argument;
                    rule.ProfileId = profileId;
                    rule.MaskId = maskId;
                    rule.PowerSchemeId = powerSchemeId;
                    rule.Priority = request.Priority;
                    rule.Enabled = request.Enabled;

                    _scenarios.Save(rule);
                    return (true, created ? $"已新增场景规则（{rule.Id}）" : "已保存场景规则");
                },
                token).ConfigureAwait(false);

            if (!ok)
                return Fail(message);

            _log.Info($"场景规则已保存：{message}");

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = message,
                Payload = null
            };
        }

        /// <summary>删除一条场景规则。Payload 是规则 Id（不是触发参数）。</summary>
        internal async Task<PipeResponse> RunDeleteScenarioRuleAsync(string? payload, CancellationToken token)
        {
            string id = (payload ?? string.Empty).Trim();
            if (id.Length == 0)
                return Fail("Payload 必须是规则 Id");

            bool deleted = await Task.Run(() => _scenarios.Delete(id), token).ConfigureAwait(false);

            if (!deleted)
                return Fail($"规则不存在：{id}");

            _log.Info($"场景规则已删除：{id}");

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = "已删除场景规则",
                Payload = null
            };
        }


        /// <summary><c>query-scenario-state</c>：读总闸。</summary>
        internal PipeResponse RunQueryScenarioState() => ScenarioStateResponse($"自动切换{(_scenarios.Enabled ? "已开启" : "已关闭")}");

        /// <summary>set-scenario-enabled：拨总闸；必须走 SetEnabled 才会触发订阅 / 退订。</summary>
        internal PipeResponse RunSetScenarioEnabled(string? payload)
        {
            if (string.IsNullOrEmpty(payload))
                return Fail("缺少 Payload（需要 {\"enabled\":true}）");

            ScenarioStatePayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.ScenarioStatePayload);
            }
            catch (JsonException ex)
            {
                return Fail($"Payload 不是合法 JSON：{ex.Message}");
            }

            if (request is null)
                return Fail("Payload 不是合法的 set-scenario-enabled 请求");

            _scenarios.SetEnabled(request.Enabled);

            return ScenarioStateResponse(request.Enabled ? "自动切换已开启" : "自动切换已关闭");
        }

        private PipeResponse ScenarioStateResponse(string message) => new()
        {
            Version = WorkerPipeContract.ProtocolVersion,
            Success = true,
            Message = message,
            Payload = JsonSerializer.Serialize(
                new ScenarioStatePayload { Enabled = _scenarios.Enabled },
                IpcJsonContext.Default.ScenarioStatePayload)
        };


        // 三个电源命令做成 static 便于单测，不必构造整个 PipeServerService。

        /// <summary><c>query-power-schemes</c>：列出本机全部方案与当前活动方案。</summary>
        internal static PipeResponse RunQueryPowerSchemes(PowerPolicyService power)
        {
            PowerSchemeListPayload payload = BuildSchemeList(power);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,

                // 一个方案都列不出来也算查询成功，空列表就是答案。
                Success = true,
                Message = $"共 {payload.Schemes.Count} 个电源方案",
                Payload = JsonSerializer.Serialize(payload, IpcJsonContext.Default.PowerSchemeListPayload)
            };
        }

        /// <summary>方案列表 + 当前活动方案；三条变更命令共用。</summary>
        private static PowerSchemeListPayload BuildSchemeList(PowerPolicyService power)
        {
            PowerSchemeListPayload payload = new()
            {
                ActiveGuid = power.GetActiveScheme()
            };

            foreach ((Guid guid, string name) in power.ListSchemes())
            {
                payload.Schemes.Add(new PowerSchemeEntry
                {
                    Guid = guid,
                    Name = name
                });
            }

            return payload;
        }

        /// <summary>create-power-scheme：派生 + 改名 + 切过去；切失败报「建成了但没切」。</summary>
        internal static PipeResponse RunCreatePowerScheme(PowerPolicyService power, string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail(PowerPolicyService.EmptySchemeNameMessage);

            CreatePowerSchemePayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.CreatePowerSchemePayload);
            }
            catch (JsonException ex)
            {
                return Fail($"请求格式错误：{ex.Message}");
            }

            if (request is null)
                return Fail(PowerPolicyService.EmptySchemeNameMessage);

            (bool created, Guid guid, string? error) = power.CreateScheme(request.TemplateGuid, request.Name);

            if (!created)
                return Fail(error ?? PowerPolicyService.CreateSchemeFailedMessage);

            // 新建即切换。
            (bool activated, string? activateError) = power.SetActiveScheme(guid);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = activated
                    ? "已创建并切换电源方案"
                    : $"已创建电源方案，但未能切换：{activateError}",
                Payload = JsonSerializer.Serialize(BuildSchemeList(power), IpcJsonContext.Default.PowerSchemeListPayload)
            };
        }

        /// <summary><c>rename-power-scheme</c>：改显示名。</summary>
        internal static PipeResponse RunRenamePowerScheme(PowerPolicyService power, string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail(PowerPolicyService.EmptySchemeNameMessage);

            RenamePowerSchemePayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.RenamePowerSchemePayload);
            }
            catch (JsonException ex)
            {
                return Fail($"请求格式错误：{ex.Message}");
            }

            if (request is null)
                return Fail(PowerPolicyService.EmptySchemeNameMessage);

            (bool renamed, string? error) = power.RenameScheme(request.Guid, request.Name);

            return renamed
                ? new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = true,
                    Message = "已重命名电源方案",
                    Payload = JsonSerializer.Serialize(BuildSchemeList(power), IpcJsonContext.Default.PowerSchemeListPayload)
                }
                : Fail(error ?? PowerPolicyService.RenameSchemeFailedMessage);
        }

        /// <summary><c>delete-power-scheme</c>：删除方案，返回重新枚举后的列表。</summary>
        internal static PipeResponse RunDeletePowerScheme(PowerPolicyService power, string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail(PowerPolicyService.EmptySchemeIdMessage);

            if (!Guid.TryParse(payload.Trim(), out Guid guid))
                return Fail("方案 Id 格式错误");

            (bool deleted, string? error) = power.DeleteScheme(guid);

            return deleted
                ? new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,

                    // 删掉的可能正是活动方案，活动 GUID 必须重读。
                    Success = true,
                    Message = "已删除电源方案",
                    Payload = JsonSerializer.Serialize(BuildSchemeList(power), IpcJsonContext.Default.PowerSchemeListPayload)
                }
                : Fail(error ?? PowerPolicyService.DeleteSchemeFailedMessage);
        }

        /// <summary>set-active-scheme：只换生效方案，不碰方案里的任何值。</summary>
        internal static PipeResponse RunSetActivePowerScheme(
            PowerPolicyService power,
            PlanService plans,
            string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail(PowerPolicyService.EmptySchemeIdMessage);

            // Payload 是裸 GUID 字符串，不是 JSON 对象。
            if (!Guid.TryParse(payload.Trim(), out Guid guid))
                return Fail("方案 Id 格式错误");

            (bool success, string? error) = power.SetActiveScheme(guid);

            if (!success)
                return Fail(error ?? PowerPolicyService.ActivateFailedMessage);

            // 系统计划换人，「生效中」标记要跟着走，否则界面还写上一份。
            try
            {
                plans.MarkActive(PlanKind.Power, plans.FindPowerByScheme(guid)?.Id);
            }
            catch (Exception)
            {
                // 记账失败不影响「系统已切过去」这件事。
            }

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = "已切换电源方案",
                Payload = null
            };
        }

        /// <summary><c>query-power-policy</c>：读一份快照。</summary>
        internal static PipeResponse RunQueryPowerPolicy(PowerPolicyService power)
        {
            PowerPolicySnapshot snapshot = power.ReadSnapshot();

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,

                // 查询成功；读不到方案是快照内容，不是查询失败。
                Success = true,
                Message = snapshot.ActiveSchemeGuid == Guid.Empty
                    ? PowerPolicyService.NoActiveSchemeForSnapshotMessage
                    : $"当前电源方案：{snapshot.ActiveSchemeName}",
                Payload = JsonSerializer.Serialize(snapshot, IpcJsonContext.Default.PowerPolicySnapshot)
            };
        }

        /// <summary>apply-power-policy：逐项写、逐项报，最后统一激活一次。</summary>
        internal static PipeResponse RunApplyPowerPolicy(PowerPolicyService power, string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail(PowerPolicyService.EmptyPayloadMessage);

            ApplyPowerPolicyPayload? request;
            try
            {
                request = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.ApplyPowerPolicyPayload);
            }
            catch (JsonException ex)
            {
                // 请求坏了与请求合法但没填字段是两回事，分开报。
                return Fail($"请求格式错误：{ex.Message}");
            }

            if (request is null || request.IsEmpty)
                return Fail(PowerPolicyService.EmptyPayloadMessage);

            List<string> errors = new();
            int applied = 0;

            if (request.Epp is int epp)
                MergeWriteResult(errors, ref applied, power.WriteEpp(epp));

            if (request.BoostMode is int boostMode)
                MergeWriteResult(errors, ref applied, power.WriteBoostMode(boostMode));

            if (request.MaxProcessorState is int maxState)
                MergeWriteResult(errors, ref applied, power.WriteMaxProcessorState(maxState));

            if (request.Aspm is int aspm)
                MergeWriteResult(errors, ref applied, power.WriteAspm(aspm));

            if (request.IdleDisable is bool idleDisable)
                MergeWriteResult(errors, ref applied, power.WriteIdleDisable(idleDisable));

            if (request.DcEpp is int dcEpp)
                MergeWriteResult(errors, ref applied, power.WriteEpp(dcEpp, dc: true));

            if (request.DcBoostMode is int dcBoostMode)
                MergeWriteResult(errors, ref applied, power.WriteBoostMode(dcBoostMode, dc: true));

            if (request.DcMaxProcessorState is int dcMaxState)
                MergeWriteResult(errors, ref applied, power.WriteMaxProcessorState(dcMaxState, dc: true));

            if (request.DcAspm is int dcAspm)
                MergeWriteResult(errors, ref applied, power.WriteAspm(dcAspm, dc: true));

            if (request.DcIdleDisable is bool dcIdleDisable)
                MergeWriteResult(errors, ref applied, power.WriteIdleDisable(dcIdleDisable, dc: true));

            if (applied == 0)
            {
                return Fail(errors.Count == 0
                    ? PowerPolicyService.EmptyPayloadMessage
                    : string.Join("，", errors));
            }

            // 至少一项写成功就必须重新激活，部分成功也要。
            (bool activated, string? activateError) = power.RefreshActiveScheme();

            // 部分失败仍报失败，否则界面会把没写成的项也画成已应用。
            if (errors.Count > 0)
            {
                string summary = string.Join("，", errors);

                // 激活失败时不加「部分字段写入失败」前缀：两类问题并列陈述。
                string message = activated
                    ? $"部分字段写入失败：{summary}"
                    : $"{summary}；配置已写入但{activateError}";

                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = message,
                    Payload = TryReadSnapshotPayload(power)
                };
            }

            if (!activated)
            {
                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = $"配置已写入但{activateError}",
                    Payload = TryReadSnapshotPayload(power)
                };
            }

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"已应用 {applied} 项电源策略",
                Payload = TryReadSnapshotPayload(power)
            };
        }

        /// <summary>失败路径的快照兜底：读得到就回，降级快照回 null。</summary>
        private static string? TryReadSnapshotPayload(PowerPolicyService power)
        {
            try
            {
                PowerPolicySnapshot snapshot = power.ReadSnapshot();
                if (snapshot.ActiveSchemeGuid == Guid.Empty)
                    return null;

                return JsonSerializer.Serialize(snapshot, IpcJsonContext.Default.PowerPolicySnapshot);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把一项写入结果并进已写数 / 错误表，失败原因去重。</summary>
        private static void MergeWriteResult(
            List<string> errors, ref int applied, (bool Success, string? Error) result)
        {
            if (result.Success)
            {
                applied++;
                return;
            }

            string message = string.IsNullOrEmpty(result.Error) ? "未知错误" : result.Error!;
            if (!errors.Contains(message))
                errors.Add(message);
        }

        internal const string ApplyStatusApplied = "applied";
        internal const string ApplyStatusSkipped = "skipped";
        internal const string ApplyStatusFailed = "failed";

        /// <summary>save-profile-tuning 只写盘不下发时的状态字。</summary>
        internal const string SavedStatus = "saved";

        /// <summary>应用调校：空 Payload 按盘上 tuning.json 应用，非空为方案 Id。</summary>
        internal async Task<PipeResponse> RunApplyTuningAsync(string? payload, CancellationToken token)
        {
            if (!string.IsNullOrEmpty(payload))
                return await RunApplyTuningByIdAsync(payload, token);

            try
            {
                // ApplyFromStore 最长阻塞 60 秒，必须离开接受循环。
                TuningApplyOutcome outcome = await Task.Run(
                    () =>
                    {
                        _tuningStore.Load();
                        return _tuningApply.ApplyFromStore(ignoreStartupGate: true);
                    },
                    token).ConfigureAwait(false);

                return BuildApplyTuningResponse(outcome);
            }
            catch (Exception ex)
            {
                _log.Warn($"应用调校异常：{ex.GetType().Name}: {ex.Message}");

                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = $"应用调校异常：{ex.Message}",
                    Payload = ApplyStatusFailed
                };
            }
        }

        /// <summary>TuningApplyOutcome → 管道响应；三态靠状态字区分。</summary>
        internal static PipeResponse BuildApplyTuningResponse(TuningApplyOutcome outcome)
        {
            string status = outcome.Skipped
                ? ApplyStatusSkipped
                : outcome.Success ? ApplyStatusApplied : ApplyStatusFailed;

            string message;
            if (outcome.Skipped)
            {
                message = string.IsNullOrEmpty(outcome.SkipReason)
                    ? "本次未写入 SMU（未给出原因）"
                    : outcome.SkipReason;
            }
            else if (outcome.Success)
            {
                List<string> parts = new();
                if (outcome.CurveOptimizerApplied)
                    parts.Add("CO");
                if (outcome.FMaxApplied)
                    parts.Add("FMax");

                message = parts.Count > 0
                    ? $"已写入 SMU：{string.Join(" + ", parts)}"
                    : "已写入 SMU";
            }
            else
            {
                message = string.IsNullOrEmpty(outcome.Summary)
                    ? "SMU 写入未完成（无明细）"
                    : outcome.Summary;
            }

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = outcome.Success,
                Message = message,
                Payload = status
            };
        }

        /// <summary>查询 SMU 可用性；查询是否成功与是否可用分开表达。</summary>
        internal static PipeResponse BuildSmuStatusResponse(ISmuReader smu)
        {
            SmuStatusPayload status = new()
            {
                Available = smu.IsAvailable,
                Error = smu.IsAvailable ? null : smu.LastError
            };

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = status.Available ? "SMU 可用" : "SMU 不可用",
                // 必须走源生成上下文：反射重载会触发 IL2026 / IL3050（Core 开了 IsAotCompatible）。
                Payload = JsonSerializer.Serialize(status, IpcJsonContext.Default.SmuStatusPayload)
            };
        }

        /// <summary>从硬件读回当前调校值；只读，不写 SMU。</summary>
        private async Task<PipeResponse> RunQuerySmuSnapshotAsync(CancellationToken token)
        {
            try
            {
                SmuSnapshot snapshot = await Task.Run(
                    () => _smu.ReadSnapshot(),
                    token).ConfigureAwait(false);

                return BuildSmuSnapshotResponse(snapshot);
            }
            catch (Exception ex)
            {
                _log.Warn($"读取 SMU 快照异常：{ex.GetType().Name}: {ex.Message}");

                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = $"读取 SMU 快照异常：{ex.Message}",
                    Payload = null
                };
            }
        }

        /// <summary>SmuSnapshot → 管道响应。</summary>
        internal static PipeResponse BuildSmuSnapshotResponse(SmuSnapshot snapshot)
        {
            if (!snapshot.Success)
            {
                return new PipeResponse
                {
                    Version = WorkerPipeContract.ProtocolVersion,
                    Success = false,
                    Message = string.IsNullOrEmpty(snapshot.Error)
                        ? "读取 SMU 快照失败（未提供原因）"
                        : snapshot.Error,
                    Payload = null
                };
            }

            TuningSnapshotPayload payload = new()
            {
                CoreMargins = snapshot.CoreMargins,
                FMaxMHz = snapshot.FMaxMHz,

                // 可写范围随快照给出：滑块区间必须与驱动侧校验一致。
                FMaxMinMHz = SmuReader.FMaxMin,
                FMaxMaxMHz = SmuReader.FMaxMax,

                Error = snapshot.Error
            };

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"已读取 {payload.CoreMargins.Length} 核",
                Payload = JsonSerializer.Serialize(payload, IpcJsonContext.Default.TuningSnapshotPayload)
            };
        }

        /// <summary>读一行，硬上限 MaxMessageBytes（512 KB）。</summary>
        private static async Task<string> ReadLineAsync(NamedPipeServerStream server, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            // 按需增长：ping 只有几十字节。
            using MemoryStream accumulated = new(4096);

            while (true)
            {
                int read = await server.ReadAsync(buffer, token);

                if (read == 0)
                    throw new IOException("对端在消息结束前关闭了管道");

                int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                int take = newline >= 0 ? newline : read;

                if (accumulated.Length + take > WorkerPipeContract.MaxMessageBytes)
                {
                    throw new IOException(
                        $"消息超过 {WorkerPipeContract.MaxMessageBytes} 字节上限");
                }

                accumulated.Write(buffer, 0, take);

                if (newline >= 0)
                {
                    // 一个连接只处理一条消息，换行后的字节丢弃。
                    return WorkerPipeContract.Utf8.GetString(
                        accumulated.GetBuffer(), 0, (int)accumulated.Length);
                }
            }
        }

        /// <summary>写一行并刷出；UTF-8 无 BOM。</summary>
        private static async Task WriteLineAsync(
            NamedPipeServerStream server,
            PipeResponse response,
            CancellationToken token)
        {
            string json = JsonSerializer.Serialize(response, IpcJsonContext.Default.PipeResponse);
            byte[] payload = WorkerPipeContract.Utf8.GetBytes(json + "\n");

            await server.WriteAsync(payload, token);
            await server.FlushAsync(token);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                try
                {
                    _stopToken?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                Task? loop = _acceptLoop;
                if (loop is null)
                    return;

                // 5 秒上限；超时只记 Warn，不阻塞进程退出。
                Task finished = await Task.WhenAny(
                    loop,
                    Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

                if (!ReferenceEquals(finished, loop))
                {
                    _log.Warn("acceptLoop 未在 5 秒内退出");
                    return;
                }

                try
                {
                    await loop;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _log.Warn($"acceptLoop 退出时异常：{ex.Message}");
                }
            }
            finally
            {
                _stopToken?.Dispose();
                _stopToken = null;
            }
        }
    }
}
