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
    /// <summary>
    /// 命名管道服务端：请求-响应模式，一行 JSON 一条消息。
    ///
    /// 协议按行切分，但服务端不做长连接复用：读完一行、写回一行、关掉本次实例。
    /// 单实例上限 1 个，因此同一时刻只有一个客户端在处理，不存在两条响应交错的风险。
    /// 代价是每次请求多一次连接建立（本机管道，微秒级）。
    ///
    /// 实测（批次三收尾）：Worker 以管理员身份运行时，CurrentUserOnly 创建的管道
    /// 拒绝中完整性（非提权）客户端连接，而这正是 UI 进程的形态 —— 用 CurrentUserOnly
    /// 会导致「UI 永远连不上 Worker」。改为显式 DACL：只授予当前用户 SID 的 ReadWrite。
    /// 语义与 CurrentUserOnly 相同（仍然只有同一个 Windows 用户能连），
    /// 但不会带上那层导致跨完整性级别拒绝的附加限制。
    ///
    /// 那是「所有本地用户」组。多用户机器上等于允许别的登录用户向提权 Worker
    /// 发 shutdown —— 与 CurrentUserOnly 的初衷相反。当前用户 SID 才是等价物。
    ///
    /// 唯一出处是 <see cref="WorkerPipeContract"/> 里的 <c>Command*</c> 常量，
    /// 分派见 <see cref="HandleAsync"/>。在这里再抄一份必然漏改 ——
    /// 曾经那份手写清单就把已删除的命令留了整整一轮。
    /// 未登记的命令一律回「未知命令」。
    /// </summary>
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
            IUiSettingsStore settings)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;

            ArgumentNullException.ThrowIfNull(lifetime);
            _lifetime = lifetime;

            // 任务计划注册需要管理员令牌 —— 这是本服务（提权进程）存在的意义之一。
            // UI 侧无提权，只能发 IPC 请求过来。
            ArgumentNullException.ThrowIfNull(autoStart);
            _autoStart = autoStart;

            // 写 SMU 同样需要提权令牌。TuningApplyService 已在 AddZentwikCore 注册，
            // 这里只是把它接进管道分派，没有新增注册、没有改 DI 结构。
            ArgumentNullException.ThrowIfNull(tuningApply);
            _tuningApply = tuningApply;

            // ISmuReader 也已在 AddZentwikCore 注册（单例，全进程只允许一个 Cpu 实例）。
            // 这里只读 IsAvailable / LastError，不做任何 SMU 读写。
            ArgumentNullException.ThrowIfNull(smu);
            _smu = smu;

            // 与 TuningApplyService 内部的 _tuning 是**同一个单例**（DI 注册为单例）。
            // apply-tuning 前显式 Load() 一次，确保读的是 UI 刚写下的那份盘上配置。
            ArgumentNullException.ThrowIfNull(tuningStore);
            _tuningStore = tuningStore;

            // PlanService 已在 AddZentwikCore 注册（单例，持有 plans.json 的唯一主副本）。
            // UI 不能自己调 Apply —— 它非提权，且直接改文件会让这里的缓存变成旧值。
            ArgumentNullException.ThrowIfNull(plans);
            _plans = plans;

            // 进程页要三样东西：当前进程快照（refresher）、已绑集合与解绑入口（watcher）、
            // 以及用来算 matchedRuleName 的规则表（bindings）。三者都已在 AddZentwikCore 注册。
            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;

            ArgumentNullException.ThrowIfNull(refresher);
            _refresher = refresher;

            ArgumentNullException.ThrowIfNull(bindings);
            _scheduling = bindings;

            // save-scheduling-rule 的 targetType=mask 需要把掩码 Id 解析成真实掩码值。
            // MaskStore 已在 AddZentwikCore 注册为单例，这里只读 FindById。
            ArgumentNullException.ThrowIfNull(masks);
            _masks = masks;

            // 拓扑查询用它的逻辑核数与 CCD 数：掩码网格的格子数必须等于本机真实核数，
            // 界面写死 16 核会在 8 核机器上画出不存在的核。
            ArgumentNullException.ThrowIfNull(cpuSets);
            _cpuSets = cpuSets;

            // ScenarioService 已在 AddZentwikCore 注册（单例，持有 scenarios.json 的唯一主副本）。
            // 场景规则的增删改必须走这里：UI 直接改文件会让 BackendHostService 里的
            // 缓存与监听闸门（HasProcessRules / HasPowerRules）停在旧状态。
            ArgumentNullException.ThrowIfNull(scenarios);
            _scenarios = scenarios;

            // 电源策略：写的是当前活动方案（HKLM 下的电源设置），需要管理员令牌 ——
            // UI 侧无提权，只能发 IPC 过来。PowerPolicyService 已在 AddZentwikCore 注册。
            ArgumentNullException.ThrowIfNull(power);
            _power = power;

            // 行为页的三个开关存在 ui.json；Worker 侧有两个消费方（启动应用调校、进程监听）。
            // 界面必须经这里读写，各存一份必然不同步。
            ArgumentNullException.ThrowIfNull(settings);
            _settings = settings;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _stopToken = new CancellationTokenSource();
            CancellationToken token = _stopToken.Token;

            _acceptLoop = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);

            // 观察任务异常：没人 await 这个循环，未观察异常会被静默丢弃。
            _ = _acceptLoop.ContinueWith(
                t => _log.Error($"管道接受循环异常退出：{t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            return Task.CompletedTask;
        }

        /// <summary>
        /// 接受循环。每次迭代 = 建一个服务端实例 → 等连接 → 读一行 → 回一行 → 关掉。
        ///
        /// 不用 CancellationTokenRegistration 关管道：`using` 在每轮末尾释放实例，
        /// 而等待阶段由 WaitForConnectionAsync(token) 自己响应取消。
        /// </summary>
        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // PipesAclExtensions.SetAccessControl 作用在「已经创建好的管道句柄」上，
                // 实测抛异常（句柄没有 WRITE_DAC），且异常发生在 try 之外，会把整个
                // accept 循环打挂、管道直接消失。Create 在创建时就带上安全描述符，
                // 既没有这个权限问题，也没有「先建后改」的竞态窗口。
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
                        // 反序列化出 null（字面量 "null"）：协议上不可能出现，
                        // 按失败回一句，不猜语义。
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
                        // 版本不一致：回固定文案，不做任何兼容降级。
                        // VersionMismatchResponse 里填的是本端已知版本，不透传对端版本。
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
                            // 单条命令的异常转成失败响应：客户端才能拿到明确原因，
                            // 而不是读到流结束；异常也绝不能冲出接受循环。
                            _log.Error($"命令 {request.Command} 执行失败：{ex.Message}");
                            response = Fail($"命令执行失败：{ex.Message}");
                        }
                    }

                    await WriteLineAsync(server, response, token);

                    // shutdown：先把 bye 写出去，再延迟 100 ms 停宿主。
                    // 100 ms 是经验值，不保证 WriteLineAsync 的对端已读完；
                    // shutdown 是幂等操作，客户端没收到 bye 但进程确实退出了也算成功。
                    //
                    // 绝不用 Environment.Exit：那会跳过 StopAsync，
                    // 导致日志最后一段（恰好是崩溃现场）丢失、WMI 订阅不释放。
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
                    // 单条命令的异常不能冲出 while：绝大多数 Run* 不自带 try/catch，
                    // 文件 ACL、P/Invoke、集合并发修改都会走到这里。循环一旦终止，
                    // Worker 仍在运行却不再接受连接，连 ping 都无响应且没有任何日志。
                    _log.Error($"管道命令处理异常：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 管道的 DACL：只允许当前用户 SID 读写。
        ///
        /// 实测四种权限位里 ReadWrite 已经足够客户端完成「写一条请求 + 读一条响应」；
        /// 授 FullControl 会把 WRITE_DAC / WRITE_OWNER 也交给客户端，
        /// 等于让 UI 能改这条管道的安全描述符 —— 没有理由给。
        ///
        /// 显式 DACL + 不设 SACL 时，管道不带强制完整性标签，
        /// 中完整性客户端因此可以连上；这正是显式 DACL 与 CurrentUserOnly 的差别所在。
        /// </summary>
        private static PipeSecurity CreatePipeSecurity()
        {
            PipeSecurity security = new();
            security.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            return security;
        }

        /// <summary>
        /// 命令分派。命令清单见类注释；未登记的命令一律「未知命令」。
        ///
        /// 注册 / 注销开机自启会同步调用 schtasks.exe（内部 15 秒超时，且要回查一次），
        /// 最长可能占用几十秒。放在接受循环线程上同步执行会把整条管道卡住，
        /// 连 ping 都进不来；丢到线程池后至少接受循环本身不会被业务阻塞。
        /// </summary>
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

        /// <summary>
        /// 解析 <c>register-autostart</c> 的 Payload：AutoStartMode 的名称。
        ///
        /// 旧版 UI 发的是空 Payload，语义固定为「拉起 Worker + 打开主界面」。
        /// 升级中途新旧混搭是常态（用户目录里两个 exe 未必同时替换），
        /// 把空 Payload 判成错误，表现就是「点了开关、任务计划没变、只弹一句协议错误」。
        /// 按旧语义继续工作，比严格校验更有用。
        ///
        /// <c>Enum.TryParse</c> 默认接受 "3" 这样的数值。这里显式拒绝：
        /// 数值形态没有任何可读性，出现它只可能是调用方拼错，静默接受等于让
        /// 「传了 3」和「传了 WorkerWithUI」在日志里长得一模一样。
        /// </summary>
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

        /// <summary>
        /// 注册 / 注销开机自启。
        ///
        /// 【失败语义】一律 <c>Success = false</c> + <c>Message</c>，**不向调用方抛异常**：
        /// 管道协议没有「异常」这种消息形状，抛出去只会让对端收到一条断连，
        /// UI 侧看到的是「通信中断」，而不是「权限不足」或「被组策略锁定」。
        /// 真实原因同时写进 Worker 日志（AutoStartService 内部已经记了 Error）。
        ///
        /// 「形态 = None」与「注销」是同一件事的两种说法。让 Enable(None) 返回失败
        /// 会逼调用方在发请求前先判断一次，而那个判断结果对端本来就知道。
        /// </summary>
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

        /// <summary>
        /// 按 Id 应用一份调校方案：Worker 侧调 <see cref="PlanService.Apply"/>，
        /// 由它写 <c>tuning.json</c> 并下发 SMU（不碰调度与电源）。
        ///
        /// UI 非提权，不能自己调；而且 <c>plans.json</c> 的主副本在 Worker 里，
        /// UI 直接改文件会让缓存变成旧值 —— 所以这条路径只能走 IPC。
        /// </summary>
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

            // Apply 内部已经调过 ApplyFromStore，结果落在 LastOutcome 上。
            // 这是「复用既有结果」而不是「再跑一次」—— 再跑一次会重复写 SMU。
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

        /// <summary>
        /// 统一的失败响应形状。四个字段里 <c>Version</c> 必须显式写本端版本 ——
        /// 漏了就是 0，对端会把它判成「版本不一致」，真实原因（Message）永远看不到。
        /// </summary>
        // ==================== 界面设置 ====================

        /// <summary><c>query-ui-settings</c>：读 ui.json 当前值。</summary>
        internal PipeResponse RunQueryUiSettings() => UiSettingsResponse("ok");

        /// <summary>
        /// <c>save-ui-settings</c>：增量写 ui.json；「监听进程」翻转时同步启停监听。
        /// </summary>
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

            return UiSettingsResponse("ok");
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
                        ToastSeconds = s.ToastSeconds
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
                _scheduling.Save(Array.Empty<ScheduleRule>());
                _tuningStore.ResetToFactory();
                _settings.ResetToFactory();

                // 设置回到默认（监听开启），监听线程要跟着起来。
                _watcher.Start();

                // 数据清了，硬件还停在旧参数上：再下发一次默认安全值。
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

        /// <summary>
        /// <c>query-plans</c>：某一类方案（调校 / 调度 / 电源）的列表与生效项。
        /// </summary>
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

        private static PlanEntryPayload ToPayload(PlanEntry entry) => new()
        {
            Id = entry.Id,
            Name = entry.Name,
            Kind = PlanKinds.ToWire(entry.Kind),
            IsActive = entry.IsActive,
            CoreMargins = entry.CurveOptimizer ?? Array.Empty<int>(),
            FMaxMHz = entry.FMaxMHz,
            RuleCount = entry.ScheduleRules?.Count ?? 0,
            PowerSchemeGuid = entry.PowerSchemeGuid,
            PowerSchemeName = entry.PowerSchemeName,
            PowerPolicy = entry.PowerPolicy
        };

        /// <summary>
        /// <c>save-plan</c>：新建或按 Id 覆盖一份方案。
        /// </summary>
        /// <remarks>
        /// 调度方案存的就是「保存那一刻的规则集」。让 UI 把几十条规则序列化送回来，
        /// 既多一次不同步的机会，也会漏掉「规则里带的掩码快照」这类只有本端算得出来的字段。
        /// </remarks>
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

                    // 新建时以「当前实际状态」为骨架：调校抓 store，调度抓当前规则集。
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
                            // 系统计划是可选的：给了就校验它在系统里真实存在并记下名字；
                            // 不给则沿用 target 的原值（新建时留空）。
                            // 从前这里强制要求 GUID，等于逼着「新建一份软件方案」
                            // 必须先派生一份 Windows 电源计划 —— 控制面板里越攒越多。
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

                            // 五项参数由界面送上来：这份方案存的就是「保存那一刻的界面值」。
                            // 不传表示只改名 / 只改 GUID，沿用原有快照。
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

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = deleted,
                Message = deleted ? "已删除方案" : $"方案不存在：{id}",
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

        /// <summary>
        /// <c>apply-plan</c>：把方案下发到它所属的那一块。
        /// </summary>
        /// <remarks>
        /// 只写自己那一块：调校写 TuningStore 并下发 SMU、调度写 SchedulingStore 并重扫、
        /// 电源按需切系统方案后写入它自带的五项参数快照。合并式方案的「切一次写三处」
        /// 正是「改一处、另外两处跟着变」的来源。
        /// </remarks>
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

                    // 方案的参数快照要在「切完系统计划之后」写：PowerPolicyService
                    // 永远写当前活动方案，顺序反了就会写进上一份计划里。
                    // 复用 apply-power-policy 的同一条写入路径，范围校验、
                    // 部分失败汇总与重新激活都不另写一套。
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


        /// <summary>
        /// 进程列表 + 规则命中情况。数据来自 ProcessRefresher 的最近一次采样快照
        /// （Worker 侧已在 BackendHostService 里 Start 过，这里只读）。
        /// </summary>
        internal Task<PipeResponse> RunQueryProcessesAsync()
        {
            // 直接读内存：save/delete 已经更新过，重读盘会在文件损坏时把规则表冲成空表。
            List<ProcessEntryPayload> items = BuildProcessEntries(
                _refresher.Latest,
                _scheduling.Profile.Rules);

            return Task.FromResult(new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = $"共 {items.Count} 个进程",
                Payload = JsonSerializer.Serialize(items, IpcJsonContext.Default.ListProcessEntryPayload)
            });
        }

        /// <summary>
        /// 把采样条目映射成协议条目，并用**同一份**规则匹配逻辑算 matchedRuleName。
        /// 抽成纯函数是为了可测：分派层只剩「取数据 + 序列化」。
        /// </summary>
        internal static List<ProcessEntryPayload> BuildProcessEntries(
            IReadOnlyList<ProcessRefresher.Entry> entries,
            IReadOnlyList<ScheduleRule> rules)
        {
            List<ProcessEntryPayload> items = new(entries.Count);

            // 与真正下发绑定时同一份索引：Enabled=false / Target=None 跳过、
            // 双方 Trim、忽略大小写。原来每个条目都把规则表线性扫一遍，
            // 这里一轮请求只建一次索引。
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

        /// <summary>
        /// 取回最近的日志快照。
        ///
        /// 日志是**批量**响应。不设上限时，一次刷新可能是几十万字符，
        /// 直接撞 MaxMessageBytes，日志页每次打开都报错。50 条足够看清最近发生了什么，
        /// 也让 Payload 稳定在几十 KB 以内。
        ///
        /// 格式见 <see cref="WorkerPipeContract.CommandSnapshotLogs"/>：一行一条，
        /// 字段以 | 分隔，消息内的换行转义成字面量 \n。
        /// </summary>
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

        /// <summary>
        /// <c>clear-logs</c>：清空内存缓冲 + 删掉已落盘的日志文件。
        /// </summary>
        /// <remarks>
        /// 删文件是异步的（<c>LogService.Clear</c> 内部起任务并重试），这里不等它：
        /// 等下去会让管道请求多挂几百毫秒，而「文件稍后消失」对用户没有区别。
        /// </remarks>
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

        /// <summary>
        /// 把消息里的换行压成字面量 \n。协议按行切分，一条日志必须占一行；
        /// 多行异常栈不转义会把一条日志拆成好几条，客户端解析直接错位。
        /// </summary>
        private static string EscapeLogMessage(string message) =>
            message.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");

        /// <summary>
        /// 解绑。空 Payload = 解绑本工具绑过的全部；Payload = 进程 Id = 只解绑那一个。
        /// 只遍历已绑集合，不扫全系统（理由见 <see cref="ProcessWatcher.ClearAllSchedules"/>）。
        /// </summary>
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

        /// <summary>
        /// 新增或覆盖一条调度规则，然后立刻重扫一次。
        ///
        ///   · 带 <c>id</c> 且能找到 → 就地更新那一条（改进程名也还是同一条记录）；
        ///   · 不带 <c>id</c>（旧 UI）或 Id 找不到 → 按进程名匹配，命中就覆盖，否则追加。
        /// 旧写法必须继续成立：升级途中新旧两个 exe 混搭是常态，
        /// 把「没传 id」判成错误，表现就是「点了保存、规则没变、只弹一句协议错误」。
        ///
        /// 后台监听只在「进程集合发生变化」时才对**新**进程下发规则。
        /// 用户改一条已经开着的游戏的规则，如果只写盘不重扫，那条规则要等到
        /// 游戏下次启动才生效 —— 用户看到的是「点了保存，什么也没发生」。
        ///
        /// 两者是有序的两半：先有新的 <c>schedules.json</c>，重扫才有意义。
        /// 拆成两个 await 会让中间状态（已重扫、尚未写盘）有机会被别的请求观察到。
        /// </summary>
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

            // Enabled 留空表示「不动这条规则」，新建时按启用处理 ——
            // 与 ScheduleRule.Enabled 的默认值一致，也与旧 UI（不传该字段）的行为一致。
            bool enabled = request.Enabled ?? true;

            ScheduleRule rule = new() { Name = processName, Enabled = enabled };

            string targetType = (request.TargetType ?? string.Empty).Trim();
            string targetValue = (request.TargetValue ?? string.Empty).Trim();

            if (string.Equals(targetType, "ccd", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseCcdTarget(targetValue, out SchedulingTarget target))
                    return Fail($"无法识别的 CCD 目标：{targetValue}（可选 CCD0 / CCD1）");

                rule.Target = target;
            }
            else if (string.Equals(targetType, "mask", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(targetValue))
                    return Fail("targetType=mask 时 targetValue 必须是掩码 Id");

                // 掩码值一起快照进规则：掩码日后被删掉，这条规则仍能按快照下发
                // （与 ScheduleRule.TargetMask 的设计意图一致）。
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
                    // Profile.Rules 是内部列表的引用，必须先复制再改：
                    // 直接改它等于在锁外篡改 SchedulingStore 的状态。
                    List<ScheduleRule> rules = _scheduling.Profile.Rules.ToList();

                    int index = -1;

                    if (requestId.Length > 0)
                    {
                        index = rules.FindIndex(r =>
                            string.Equals(r.Id, requestId, StringComparison.OrdinalIgnoreCase));
                    }

                    // Id 没命中（旧 UI 不传 Id，或那条规则刚被别处删掉）就退回按进程名匹配。
                    // 这一步也保证了「同名规则只有一条」这条业务不变量。
                    if (index < 0)
                    {
                        index = rules.FindIndex(r =>
                            string.Equals((r.Name ?? string.Empty).Trim(), processName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (index >= 0)
                    {
                        // 覆盖时保留原 Id：Id 是「这条记录是谁」，保存一次不该换一个身份，
                        // 否则界面上刚拿到的 Id 立刻失效，下一次按 Id 删除就会 404。
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

        /// <summary>
        /// 删除一条调度规则并重扫。Payload 是规则 Id；找不到时退回按进程名匹配。
        ///
        /// Id 是「这条记录是谁」，名字是「它绑哪个进程」。用户在列表里选中一行删除时，
        /// 唯一能确定他删的是哪一行的只有 Id —— 名字可以被上一步编辑改掉。
        /// 而旧 UI 只会发名字，所以保留名字回退。两条路都是单向判断：
        /// Id 一旦命中就绝不再看名字，不做二义性猜测。
        ///
        /// 删规则不会自动解绑已经绑上的进程（解绑需要显式恢复系统默认 CPU Set，
        /// 见 <see cref="IProcessWatcher.ClearAllSchedules"/>）。重扫的作用是让**之后的**
        /// 新进程不再命中这条规则；已经在跑的进程保持现状，直到用户去进程页解绑。
        /// 这个取舍写在契约里，避免用户以为「删了规则就等于解绑」。
        /// </summary>
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

                    // 空名规则也要能删成功：用 key 兜底，别把「删除成功」误报成「规则不存在」。
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
        /// CCD 目标解析。同时接受 <c>CCD0</c> 与 <c>0</c> 两种写法：
        /// UI 下拉框的 Tag 用的是前者，手工拼请求的人更可能写后者。
        /// </summary>
        private static bool TryParseCcdTarget(string value, out SchedulingTarget target)
        {
            target = SchedulingTarget.None;

            if (string.Equals(value, "CCD0", StringComparison.OrdinalIgnoreCase) || value == "0")
            {
                target = SchedulingTarget.CCD0;
                return true;
            }

            if (string.Equals(value, "CCD1", StringComparison.OrdinalIgnoreCase) || value == "1")
            {
                target = SchedulingTarget.CCD1;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 优先级档位归一化：空串合法（表示不碰优先级），其余必须命中
        /// <see cref="ProcessPriority.Options"/> 里的英文常量，并回写成标准大小写。
        ///
        /// <c>ProcessPriority.ClassOf</c> 是大小写敏感的 switch，存进 "high" 会让
        /// 这条规则在下发时静默失败（ClassOf 返回 0 = 未知档位），而用户看到的是
        /// 「规则存下来了」。归一化把错误挡在写入之前。
        /// </summary>
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

        // ==================== 调度规则查询 ====================

        /// <summary>
        /// 全部调度规则的只读投影。
        ///
        /// 不重读盘：本进程是 schedules.json 的唯一写入方，save / delete 都立刻写盘
        /// 并更新内存，重读只会在文件被外部改坏时把正确状态覆盖成空表。
        /// </summary>
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

        /// <summary>
        /// <c>query-masks</c>：列出全部自定义掩码与套数上限。
        /// </summary>
        /// <remarks>
        /// 读内存而不重读盘：<c>masks.json</c> 的唯一写入方是本进程，每次查询都 Load 一次
        /// 只会在文件被外部改坏时把内存里的正确状态覆盖成空表。
        /// </remarks>
        internal PipeResponse RunQueryMasks()
        {
            MaskListPayload payload = BuildMaskListPayload();

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,

                // 空列表本身就是答案：用户可能一套掩码都没建过。
                Success = true,
                Message = $"共 {payload.Masks.Count} 套自定义掩码",
                Payload = JsonSerializer.Serialize(payload, IpcJsonContext.Default.MaskListPayload)
            };
        }

        /// <summary>
        /// <c>query-cpu-topology</c>：掩码网格的格子数来源。
        /// </summary>
        /// <remarks>
        /// <c>SmuReader.CoreCount</c> 要跑完一次完整快照才有值，冷启动时是 0。
        /// 用它建网格会得到「每个逻辑处理器一个核」的平铺形状 —— 位编号仍然对，
        /// 但用户看不出哪些是同一个物理核的两个线程。
        ///
        /// Windows 的处理器关系信息里没有 CCD 这一层。SMU 探测到了就用它，
        /// 否则按 CCD 数均分 —— 这只影响分组标签，不影响任何一位的含义。
        /// </remarks>
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
                // 读不到处理器关系信息：不做 SMT 分组，一个逻辑处理器一个格子。
                cores = logical;
                threadsPerCore = 1;
            }

            if (cores > logical)
                cores = logical;

            int coresPerCcd = (int)_smu.PeekCoresPerCcdRaw();

            if (coresPerCcd <= 0)
            {
                // CcdCount 不在 ICpuSetScheduler 上，按可用性逐个数：拓扑已缓存，
                // 最多探测几十次，代价远低于为此扩一个公开成员。
                int ccdCount = 0;
                while (ccdCount < 32 && _cpuSets.IsCcdAvailable(ccdCount))
                    ccdCount++;

                coresPerCcd = ccdCount > 0 ? cores / ccdCount : 0;
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

        /// <summary>
        /// <c>save-mask</c>：新增或按 Id 覆盖一套自定义掩码，成功后回整份列表。
        /// </summary>
        /// <remarks>
        /// 界面拿到的 Id 来自上一次查询；若那套掩码已被别处删掉，继续写入就会
        /// 变成「改名没生效，反而多出一条」。用户不可能从结果里分辨这两件事。
        /// </remarks>
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

            // 已保存的调度规则按掩码**值**的快照生效，这里不连带删除 ——
            // 删一套掩码不该顺手废掉用户的绑核规则。
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

        /// <summary>
        /// <see cref="ScheduleRule"/> → 协议投影。抽成纯函数便于逐个钉住三种目标形态的编码。
        /// </summary>
        internal static List<ScheduleRulePayload> BuildScheduleRulePayloads(IReadOnlyList<ScheduleRule> rules)
        {
            List<ScheduleRulePayload> items = new(rules.Count);

            foreach (ScheduleRule rule in rules)
            {
                // ccd 给索引数字、mask 给掩码值、none 给空串 —— 格式见 ScheduleRulePayload 注释。
                (string targetType, string targetValue) = rule.Target switch
                {
                    SchedulingTarget.CCD0 => ("ccd", "0"),
                    SchedulingTarget.CCD1 => ("ccd", "1"),

                    SchedulingTarget.TargetMask => ("mask", rule.TargetMask.ToString(CultureInfo.InvariantCulture)),

                    _ => ("none", string.Empty)
                };

                items.Add(new ScheduleRulePayload
                {
                    // Id 只有一条产生路径：ScheduleRule 的初始化器在对象构造时生成。
                    // 这里不现场补 —— 投影是只读的，凭空造一个 Id 会让调用方
                    // 拿着一个「存不进盘」的 Id 去删除，删完重启又对不上。
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

        // ==================== 场景规则查询 / 增删 ====================

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

        /// <summary><see cref="ScenarioRule"/> → 协议投影。展示派生值（TriggerText / ProfileName）不传。</summary>
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

        /// <summary>
        /// 新增或覆盖一条场景规则。<c>Id</c> 为空 = 新建。
        ///
        /// <c>ScenarioRule.Id</c> 的初始化器会生成 Guid，赋空串会把它覆盖掉 ——
        /// 结果是一条 Id 为空的规则：落盘后下一次 Save 又把它当新规则，
        /// 列表里会长出一堆互相无法区分的空 Id 条目。
        ///
        /// 把「传了一个不存在的 Id」当成新建，会让调用方的一个笔误变成一条影子规则：
        /// 他以为自己改了 A，实际新增了 B，而界面上 B 又排在别处。
        /// </summary>
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

            // 三个动作全空 = 一条命中了也不知道该干什么的规则。
            if (profileId.Length == 0 && maskId.Length == 0 && powerSchemeId.Length == 0)
                return Fail("至少要选一个动作：调校 / 调度 / 电源");

            if (profileId.Length > 0 && _plans.Find(PlanKind.Tuning, profileId) is null)
                return Fail($"方案不存在：{profileId}");

            // 场景页现在引用的是「方案」；旧规则里的掩码 Id / 系统 GUID 继续放行。
            if (maskId.Length > 0
                && _plans.Find(PlanKind.Scheduling, maskId) is null
                && _masks.FindById(maskId) is null)
                return Fail($"调度方案不存在：{maskId}");

            if (powerSchemeId.Length > 0
                && _plans.Find(PlanKind.Power, powerSchemeId) is null
                && !Guid.TryParse(powerSchemeId, out _))
                return Fail($"电源方案不存在：{powerSchemeId}");

            string argument = (request.Argument ?? string.Empty).Trim();

            // 进程类触发没有进程名 = 一条永远不可能命中的规则。电源类触发的参数必须为空，
            // 否则 Matches 会用 argument 做相等比较，同样永远不命中。
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

        // ==================== 场景总闸 ====================

        /// <summary><c>query-scenario-state</c>：读总闸。</summary>
        internal PipeResponse RunQueryScenarioState() => ScenarioStateResponse($"自动切换{(_scenarios.Enabled ? "已开启" : "已关闭")}");

        /// <summary>
        /// <c>set-scenario-enabled</c>：拨总闸。
        /// </summary>
        /// <remarks>
        /// 走 <see cref="ScenarioService.SetEnabled"/> 而不是直接改字段：它在写盘之后
        /// 触发 <c>Changed</c>，<see cref="ScenarioMonitor"/> 据此订阅或退订进程 / 电源事件。
        /// 少了这一步，关掉总闸后监听线程还在跑，用户看到的是「关了还在自己切方案」。
        /// </remarks>
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

        // ==================== 电源策略 ====================

        // 三个电源命令都写成 static 且接收 service：分派逻辑（解析、判空、聚合错误）
        // 是协议层的事，与管道连接无关。让它们可被直接调用，测试就不必构造一整个
        // PipeServerService（那需要 13 个依赖，其中一半与电源无关）。

        /// <summary><c>query-power-schemes</c>：列出本机全部方案与当前活动方案。</summary>
        internal static PipeResponse RunQueryPowerSchemes(PowerPolicyService power)
        {
            PowerSchemeListPayload payload = BuildSchemeList(power);

            return new PipeResponse
            {
                Version = WorkerPipeContract.ProtocolVersion,

                // 与 query-power-policy 同一口径：一个方案都列不出来也算查询成功，
                // 空列表本身就是答案（界面上方案卡片区域为空，而不是弹一句「查询失败」）。
                Success = true,
                Message = $"共 {payload.Schemes.Count} 个电源方案",
                Payload = JsonSerializer.Serialize(payload, IpcJsonContext.Default.PowerSchemeListPayload)
            };
        }

        /// <summary>
        /// 把方案列表与当前活动方案打包。三条方案变更命令的响应都复用它 ——
        /// 改完之后界面上要显示的永远是「系统现在的真实样子」，而不是操作结果的复述。
        /// </summary>
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

        /// <summary>
        /// <c>create-power-scheme</c>：派生一份新方案，改名，然后立刻切过去。
        ///
        /// 方案已经真实存在于系统里了，用户在系统设置里也看得见。
        /// 把它报成「新建失败」只会让用户再点一次，于是又建出第二份同名方案。
        /// 事实是「建成了、但没切过去」，Message 里说清楚即可。
        /// </summary>
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

            // 新建即切换：点「新建」的下一步永远是调这套新参数，
            // 停留在旧方案上的话，用户改的滑块会落到新建的那份里却看不到效果。
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

                    // 删掉的可能正是活动方案，系统会自行回落到别的方案 ——
                    // 活动 GUID 必须重新读，不能沿用删除前的那个。
                    Success = true,
                    Message = "已删除电源方案",
                    Payload = JsonSerializer.Serialize(BuildSchemeList(power), IpcJsonContext.Default.PowerSchemeListPayload)
                }
                : Fail(error ?? PowerPolicyService.DeleteSchemeFailedMessage);
        }

        /// <summary>
        /// <c>set-active-scheme</c>：只换「哪一份方案生效」，不碰方案里的任何值。
        /// </summary>
        internal static PipeResponse RunSetActivePowerScheme(
            PowerPolicyService power,
            PlanService plans,
            string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Fail(PowerPolicyService.EmptySchemeIdMessage);

            // Payload 是裸 GUID 字符串，不是 JSON 对象 —— 与 apply-tuning 传方案 Id 的做法一致。
            // 空字符串在上面已经拦掉，Trim 是为了容忍 UI 侧可能带上的空白。
            if (!Guid.TryParse(payload.Trim(), out Guid guid))
                return Fail("方案 Id 格式错误");

            (bool success, string? error) = power.SetActiveScheme(guid);

            if (!success)
                return Fail(error ?? PowerPolicyService.ActivateFailedMessage);

            // 系统计划换人了，方案层面的「生效中」要跟着走：
            // 有方案绑着这份计划就标它，点的是系统内置计划（没人绑）就清空。
            // 不这么做，界面在卡片上切完方案，面板还写着上一份「生效中」——
            // 用户看到的就是「切了没反应」。
            try
            {
                plans.MarkActive(PlanKind.Power, plans.FindPowerByScheme(guid)?.Id);
            }
            catch (Exception)
            {
                // 记账失败不影响「系统已经切过去了」这件事，别把切换报成失败。
                // 顺手一步的标记丢了，界面下次刷新会按系统实际状态纠正回来。
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

                // 查询本身成功了。读不到方案是快照的内容（五项 Supported=false + 原因），
                // 不是查询失败 —— 理由见 WorkerPipeContract.CommandQueryPowerPolicy 的注释。
                Success = true,
                Message = snapshot.ActiveSchemeGuid == Guid.Empty
                    ? PowerPolicyService.NoActiveSchemeForSnapshotMessage
                    : $"当前电源方案：{snapshot.ActiveSchemeName}",
                Payload = JsonSerializer.Serialize(snapshot, IpcJsonContext.Default.PowerPolicySnapshot)
            };
        }

        /// <summary>
        /// <c>apply-power-policy</c>：逐项写、逐项报，最后统一激活一次。
        ///
        /// 取不到活动方案时，五项写入会各自返回同一句「无法读取当前电源方案」。
        /// 直接 join 出来的消息会把这句重复五遍 —— 用户看到的像是程序在结巴，
        /// 而真正的原因只有一个。去重后既保留了「所有项都没写成」的事实，
        /// 又不会把一句话刷屏。
        ///
        /// 逐项错误之间用逗号（「能效偏好（EPP）写入失败（错误 87），ASPM 写入失败（错误 87）」），
        /// 「逐项错误」与「激活失败」这两段之间用分号。
        /// 两类信息如果共用一种分隔符，读的人无法判断「后面那句是第三项错误，
        /// 还是整批操作的另一类失败」；分号把「哪些项没写成」与「整体有没有生效」
        /// 分成两层，语义上不会串。
        ///
        /// 只要有至少一项写成功，系统里的值就已经变了。UI 拿着旧值画界面，
        /// 用户会看到「提示失败、但滑块还停在旧位置」，下一次点应用又会把
        /// 已经生效的项重写一遍。回快照让界面能立刻对齐真实状态 ——
        /// 失败归失败，事实归事实。重读不到就回 null（见 <see cref="TryReadSnapshotPayload"/>）。
        /// </summary>
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
                // 两者都会导致 Success = false，但原因完全不同：一个是请求本身坏了
                // （调用方拼错、协议错配），一个是请求合法但什么都没填（UI 缺陷）。
                // 混成一句「没有要修改的字段」，排障的人会去查 UI 表单，
                // 而真正的问题在序列化层。
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

            // 一项都没写成：系统里什么都没变，不必激活，也没有快照可回。
            if (applied == 0)
            {
                return Fail(errors.Count == 0
                    ? PowerPolicyService.EmptyPayloadMessage
                    : string.Join("，", errors));
            }

            // 至少一项写成功 → 必须重新激活，否则值只在方案里躺着不生效。
            // 注意这一步**不看** errors：部分成功也要激活，已经写进去的项
            // 同样需要重新加载才会生效。
            (bool activated, string? activateError) = power.RefreshActiveScheme();

            // 部分失败：值写进去了，但有项没写成。仍然回失败 —— 用户要求的事情
            // 没有全部发生，回成功会让界面把失败的那几项也画成「已应用」。
            if (errors.Count > 0)
            {
                string summary = string.Join("，", errors);

                // 激活也失败时不再加「部分字段写入失败：」前缀：
                // 那时有两类问题，用「摘要；配置已写入但…」并列陈述更准确，
                // 而前缀会把「激活失败」误读成某一项的失败。
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

            // 全部写成、但激活失败：值已经在方案里了，只是没生效。
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

        /// <summary>
        /// 失败路径上的快照兜底：读得到就序列化，读不到就回 null。
        ///
        /// 降级快照（<c>ActiveSchemeGuid == Guid.Empty</c>）表达的是「连方案都没有」。
        /// 把它回给 UI 等于用一份「五项全不支持」的数据去覆盖界面 ——
        /// 而此刻系统里的值其实已经改了，只是读不回来。
        /// 这种情况回 null 更诚实：UI 看到没有 Payload，就知道该自己去查，
        /// 而不是相信一份空快照。
        /// </summary>
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
                // 读快照失败不该把一条「值已经写进去了」的响应变成断连。
                return null;
            }
        }

        /// <summary>
        /// 把一项写入结果并进「已写数 / 错误表」。失败原因去重，理由见
        /// <see cref="RunApplyPowerPolicy"/>。
        /// </summary>
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

        /// <summary>
        /// <c>save-profile-tuning</c> 只写盘不下发时的状态字。
        /// 与 applied / skipped / failed 并列，是为了让 UI 不必靠 <c>applyNow</c>
        /// 反推「这次到底有没有碰硬件」。
        /// </summary>
        internal const string SavedStatus = "saved";

        /// <summary>
        /// 应用调校。
        ///
        /// · 空 / null：按当前 <c>tuning.json</c> 应用。UI 先写盘，再发空 Payload。
        /// · 非空：方案 Id，走 <see cref="RunApplyTuningByIdAsync"/> 按方案应用。
        ///
        /// <see cref="TuningApplyOutcome.Skipped"/> 与「写入失败」都是 <c>Success = false</c>，
        /// 只靠 Success 无法区分。PipeResponse 现有四个字段里，Payload 是唯一留给
        /// 命令自定义的位置；用一个状态字（applied / skipped / failed）即可，
        /// 不必新增字段、也不必给 Payload 引入新的 JSON 类型。
        /// </summary>
        internal async Task<PipeResponse> RunApplyTuningAsync(string? payload, CancellationToken token)
        {
            if (!string.IsNullOrEmpty(payload))
                return await RunApplyTuningByIdAsync(payload, token);

            try
            {
                // ApplyFromStore 内部可能阻塞到 60 秒（WaitForAvailability），
                // 与 schtasks 同理必须离开接受循环线程，否则整条管道连 ping 都进不来。
                //
                // TuningStore 把 Profile 缓存在内存里，Worker 启动时 Load 过一份。
                // UI 之后写的 tuning.json 不会自动进这个缓存 —— 不重读就会出现
                // 「界面上改了、点了应用、硬件没变」。读盘很便宜，放在这里最稳。
                //
                // ignoreStartupGate: true —— 这是用户在调校页点的「应用」，属于显式操作，
                // 不该被「启动时自动应用调校」这个开机行为开关拦住。
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

        /// <summary>
        /// 把 <see cref="TuningApplyOutcome"/> 映射成管道响应。
        ///
        /// 抽成纯函数是为了可测：分派分支只有一行 await，真正要钉住的是
        /// 「Skipped / 失败 / 成功」三态与 Message 透传，不必把整个 IHostedService 拉起来。
        /// </summary>
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

        /// <summary>
        /// 查询 SMU 可用状态。只读 <c>IsAvailable</c> / <c>LastError</c>，不触发任何探测。
        ///
        /// <c>Success = true</c> 表示**查询本身**成功；可用性放在 Payload 的 JSON 里，
        /// 让「查询失败」与「查到了但不可用」保持可区分（理由见 WorkerPipeContract 注释）。
        /// </summary>
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

        /// <summary>
        /// 从硬件读回当前调校值。**只读**：只调 <c>ReadSnapshot()</c>，不写 SMU。
        ///
        /// <c>ReadSnapshot</c> 会真正访问硬件，与 schtasks 同理不能占用接受循环线程。
        /// </summary>
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

        /// <summary>
        /// 把 <see cref="SmuSnapshot"/> 映射成管道响应。抽成纯函数是为了可测两态。
        /// </summary>
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

                // 可写范围随快照一起给出：UI 的滑块区间必须与驱动侧校验一致，
                // 否则用户能把值拖到驱动会整片拒绝的地方，点应用才发现写不进去。
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

        /// <summary>
        /// 读一行，硬上限 <see cref="WorkerPipeContract.MaxMessageBytes"/>（512 KB）。
        ///
        /// 它没有长度上限：对端持续发送而不换行，内存会一直涨。
        /// 本协议只承载控制指令，超限即协议错误，不该当成「大消息」去容忍。
        /// </summary>
        private static async Task<string> ReadLineAsync(NamedPipeServerStream server, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            // 按需增长：ping 只有几十字节，预分配上限会把每条连接都推上 LOH。
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
                    // 一行 = 一条消息，换行之后的字节属于下一条消息；
                    // 本协议一个连接只处理一条，直接丢弃。
                    return WorkerPipeContract.Utf8.GetString(
                        accumulated.GetBuffer(), 0, (int)accumulated.Length);
                }
            }
        }

        /// <summary>
        /// 写一行并刷出。UTF-8 无 BOM（<see cref="WorkerPipeContract.Utf8"/>）：
        /// 带 BOM 会让对端解析第一行时多出三个字节，直接失败。
        /// </summary>
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
                    // 已释放：没有需要取消的循环。
                }

                Task? loop = _acceptLoop;
                if (loop is null)
                    return;

                // 5 秒上限：接受循环最坏情况下正卡在 WaitForConnectionAsync，
                // 取消会立刻把它踢出来。真的超时也只记 Warn，不阻塞进程退出。
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
                    // 正常取消路径。
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
