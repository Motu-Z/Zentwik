using System.Text;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// UI 进程与 Worker 进程之间的管道契约。管道名、互斥体名、协议版本、消息上限
    /// 全部集中在这里 —— 两端各写一份字面量，改名时必然漏掉一处，
    /// 表现为「UI 一直显示未连接」这种没有任何报错的故障。
    ///
    /// UTF-8 无 BOM 的单行 JSON：一条消息 = 一行，以 <c>\n</c> 结尾。
    /// 单行而不是定长帧：管道是字节流，没有消息边界，靠换行切分是最省事的边界定义，
    /// 代价是消息体里不能出现裸换行 —— JSON 序列化会把换行转义成 <c>\n</c>，天然满足。
    /// </summary>
    public static class WorkerPipeContract
    {
        /// <summary>
        /// 协议版本。任一端收到不匹配的版本必须拒绝，不做兼容降级 ——
        /// UI 与 Worker 同一次发布产出、装进同一目录，版本不一致只可能被手工替换，
        /// 此时按旧字段猜语义，等于让一个不确定的进程去写 SMU。
        ///
        /// 任何新增 / 修改 / 删除命令都必须递增版本号：不递增的后果一律是静默错误
        /// （旧 Worker 忽略不认识的字段照样回 Success，界面以为已经写成功）。
        /// 版本历史：1 初始 ｜ 2 电源策略 DC 字段 ｜ 3 电源方案增删改 ｜ 4 query-masks ｜
        /// 5 掩码与场景命令（删 apply-process-priority）｜ 6 clear-logs ｜ 7 场景三动作 ｜
        /// 8 ProcessExited 触发源 ｜ 9 方案拆三类 ｜ 10 界面设置读写与 factory-reset。
        /// 两端一律调用 <see cref="ValidateVersion"/>，不得自行比较整数。
        /// </summary>
        public const int ProtocolVersion = 10;

        /// <summary>
        /// 版本不一致时返回给对端的固定文案。用户看到的第一句话，不随上下文变化。
        /// </summary>
        public const string VersionMismatchMessage = "UI 与 Worker 版本不一致，请重新安装";

        /// <summary>
        /// 单条消息上限（512 KB）。超过即视为协议错误 —— 无上限读取等于把对端的内存
        /// 安全交给对端的实现质量。进程列表与日志快照是批量响应（几百个进程、
        /// 几十条带路径的日志行），64 KB 会稳定超限；512 KB 够用且仍能兜住实现缺陷。
        /// 旧版 Worker 仍按 64 KB 判定，连旧 Worker 时大响应会被拒绝：两端须同版本安装。
        /// </summary>
        public const int MaxMessageBytes = 512 * 1024;

        /// <summary>
        /// 管道名（不含 <c>\\.\pipe\</c> 前缀，该前缀由 NamedPipe 类型自己拼）。
        /// </summary>
        public const string PipeName = "Zentwik.Worker";

        /// <summary>
        /// Worker 单实例互斥体名。带 <c>Global\</c> 前缀：跨会话可见，
        /// 远程桌面另开一个会话时不会拉起第二个 Worker 去抢同一块 SMU。
        ///
        /// 命名规则与管道名保持一致的前缀，两者在代码里总是成对出现。
        /// </summary>
        public const string WorkerMutexName = @"Global\Zentwik.Worker.SingleInstance";

        /// <summary>连通性探测。不产生副作用，可被 UI 高频调用。</summary>
        public const string CommandPing = "ping";

        /// <summary>请求 Worker 优雅退出（刷日志、停监听、释放互斥体）。</summary>
        public const string CommandShutdown = "shutdown";

        /// <summary>
        /// 注册开机自启（创建计划任务 ZentwikAutoStart）。
        ///
        /// 参数：<c>Payload</c> = <see cref="Helpers.AutoStartMode"/> 的名称
        /// （<c>None</c> / <c>WorkerOnly</c> / <c>WorkerWithTray</c> / <c>WorkerWithUI</c>，
        /// 忽略大小写）。**空 Payload 等价于 <c>WorkerWithUI</c>** ——
        /// 旧版 UI 就是这么发的（任务固定带 <c>--open-ui</c>），保留这条是为了让
        /// 升级中途的新旧混搭不至于把用户的自启静默关掉。
        ///
        /// Payload 本身就是 <c>string?</c>，一个枚举名不需要再套一层
        /// <c>{"mode":"..."}</c>；而且裸字符串让 <c>JsonStringEnumConverter</c> 完全不必参与
        /// 协议层 —— 那个转换器管的是 ui.json 的落盘形状，与管道无关。
        ///
        /// 返回：<c>Success = true</c> 表示任务已创建或本就存在（幂等）；<c>false</c> 时
        /// <c>Message</c> 说明原因（形态无法识别 / 权限不足 / 被组策略锁定 / schtasks 返回非 0）。
        /// 该命令写系统状态，Worker 非管理员权限时必然失败。
        /// </summary>
        public const string CommandRegisterAutoStart = "register-autostart";

        /// <summary>
        /// 注销开机自启（删除同名计划任务）。
        ///
        /// 参数：<c>Payload = null</c>。
        /// 返回：<c>Success = true</c> 表示任务已删除或本就不存在（幂等）；<c>false</c> 时
        /// <c>Message</c> 说明原因。注销失败会让「关掉自启后仍随机启动」，因此失败必须
        /// 原样回传，不得吞掉。
        /// </summary>
        public const string CommandUnregisterAutoStart = "unregister-autostart";

        /// <summary>
        /// 请求 Worker 主动拉起 UI 进程。
        ///
        /// 参数：<c>Payload = null</c>。
        /// 返回：<c>Success = true</c> 表示已发起拉起（**不表示** UI 已完成启动）；
        /// <c>false</c> 表示当前组合下 Worker 不负责拉 UI，或 UI 可执行文件缺失。
        ///
        ///   · 开机自启 = 启动 Worker + 启动 UI 窗口；
        ///   · 开机自启 = 启动 Worker + 托盘常驻（托盘交互负责再唤起窗口）。
        /// 其余组合（自启关闭、自启仅启动 UI、用户手动启动）Worker 一律不主动拉 UI。
        ///
        /// Worker 的职责是常驻并写 SMU，不是第二个启动器。在不该拉的组合里拉了，
        /// 用户看到的现象是「已经关掉的程序自己又弹出来」—— 这类故障无法从日志归因，
        /// 只能靠契约提前排除。
        /// </summary>
        public const string CommandOpenUi = "open-ui";

        /// <summary>
        /// 应用调校。
        ///
        /// 参数：<c>Payload</c> = 方案 Id（<c>profiles.json</c> 中方案的 Id，非方案名）；
        /// <b>空 / null = 按当前 <c>tuning.json</c> 应用</b>（UI 先写盘，再发空 Payload）。
        /// 传 Id 而不是把整套值放进请求体：方案由 Worker 自己从盘上读，
        /// 避免 UI 与 Worker 各持一份可能不同的配置。传方案名则会在改名后失效。
        ///
        /// 返回：<c>Success = true</c> 表示 SMU 写入已全部完成并回写 AppliedAt；
        /// <c>false</c> 时 <c>Message</c> 说明失败的核或 SMU 返回码。
        /// <c>Payload</c> 回一个状态字（<c>applied</c> / <c>skipped</c> / <c>failed</c>）：
        /// skipped 与 failed 都是 <c>Success = false</c>，只靠 Success + Message 无法可靠区分
        /// —— Message 是给人看的自由文本，会随文案调整而变，不能当机器判据。
        /// </summary>
        public const string CommandApplyTuning = "apply-tuning";

        /// <summary>
        /// 查询 SMU 可用状态。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 表示**查询本身**成功，不代表 SMU 可用；
        /// <c>Payload</c> = JSON <c>{"available":bool,"error":string?}</c>，
        /// <c>error</c> 是 <c>ISmuReader.LastError</c> 原文，可用时为 null。
        ///
        /// 「查询失败」与「查到了，SMU 不可用」是两件事：前者 UI 要显示「无法检测」，
        /// 后者要显示「不可用 + 原因」。混进 Success 会让这两种状态再次不可区分。
        /// </summary>
        public const string CommandQuerySmuStatus = "query-smu-status";

        /// <summary>
        /// 从硬件读回当前调校值（只读，绝不写 SMU）。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = JSON
        /// <c>{"coreMargins":int[],"fmaxMhz":uint,"error":string?}</c>；
        /// <c>Success = false</c> 时 <c>Message</c> 为读取失败原因，<c>Payload</c> 为 null。
        ///
        /// status 只回答「能不能写」（IsAvailable / LastError，不触发探测）；
        /// snapshot 才去真正读一次硬件。两者分开，是为了让界面能在不碰硬件的前提下
        /// 显示可用性，也让「刷新」这个动作的代价（一次完整探测）显式可见。
        /// </summary>
        public const string CommandQuerySmuSnapshot = "query-smu-snapshot";
        /// <summary>
        /// 查询某一类方案（调校 / 调度 / 电源）的列表与生效项。
        ///
        /// 参数：<c>Payload</c> = JSON <c>{"kind":"tuning|scheduling|power"}</c>
        /// （<see cref="PlanQueryPayload"/>）。
        /// 返回：<c>Payload</c> = JSON <c>{"activeId":string?,"plans":[...]}</c>
        /// （<see cref="PlanListPayload"/>）。
        ///
        /// 三页各管各的方案。合成一次查询会让「切了调校方案，进程页的方案条也跟着变」，
        /// 而用户要的正是「改一块不影响另外两块」。
        /// </summary>
        public const string CommandQueryPlans = "query-plans";

        /// <summary>
        /// 新建或覆盖一份方案。
        ///
        /// 参数：<c>Payload</c> = JSON
        /// <c>{"kind":...,"id":string?,"name":...,"coreMargins":int[]?,"fmaxMhz":N?,"powerSchemeGuid":string?,"powerPolicy":object?}</c>
        /// （<see cref="SavePlanPayload"/>）。<c>id</c> 为空 = 新建。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = 新方案的 Id。
        ///
        /// 调校页的值可能还没应用，必须由界面送上来；调度方案的内容就是当前全局规则集，
        /// Worker 自己抓一份即可 —— 让 UI 把几十条规则序列化再送回来只会多一个不同步的机会。
        ///
        /// 它只表达「应用时是否顺手切到某份系统计划」，不是方案的载体。
        /// 参数存在 <c>powerPolicy</c> 快照里；不传 GUID 就不切换系统方案，
        /// 更不会为了存一份软件方案去派生新的 Windows 电源计划。
        /// </summary>
        public const string CommandSavePlan = "save-plan";

        /// <summary>
        /// 删除一份方案。
        ///
        /// 参数：<c>Payload</c> = JSON <c>{"kind":...,"id":"..."}</c>（<see cref="PlanRefPayload"/>）。
        /// 删除生效中的方案只清标记，**不回滚硬件** —— 删的是卡片，不是「恢复出厂」。
        /// </summary>
        public const string CommandDeletePlan = "delete-plan";

        /// <summary>
        /// 给方案改名。改名不改 Id —— 场景规则引用的是 Id。
        ///
        /// 参数：<c>Payload</c> = JSON <c>{"kind":...,"id":"...","newName":"..."}</c>
        /// （<see cref="RenamePlanPayload"/>）。
        /// </summary>
        public const string CommandRenamePlan = "rename-plan";

        /// <summary>
        /// 应用一份方案：把它的内容下发到本类对应的那一块。
        ///
        /// 参数：<c>Payload</c> = JSON <c>{"kind":...,"id":"..."}</c>（<see cref="PlanRefPayload"/>）。
        ///
        /// 调校方案只写 TuningStore 并下发 SMU；调度方案只写 SchedulingStore 并触发重扫；
        /// 电源方案只调 <c>PowerSetActiveScheme</c>。合并式方案的「切一次写三处」
        /// 正是「改一个地方，另外两处跟着变」的来源。
        /// </summary>
        public const string CommandApplyPlan = "apply-plan";

        /// <summary>
        /// 查询当前进程列表（含调度规则命中情况）。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = JSON 数组
        /// <c>[{pid,name,path,cpu,priority,matchedRuleName?}, ...]</c>
        /// （<see cref="ProcessEntryPayload"/>）。
        ///
        /// 匹配用的是 <c>ProcessWatcher.Matches</c> —— 与真正下发调度时**同一份**逻辑
        /// （含 Enabled=false 跳过、Target=None 跳过、去空格、忽略大小写）。
        /// 让 UI 自己再实现一遍，两处规则迟早会漂，界面显示的和实际生效的就会不一致。
        /// </summary>
        public const string CommandQueryProcesses = "query-processes";

        /// <summary>
        /// 解除 CPU Set 绑定，把目标进程恢复成由系统默认调度。
        ///
        /// 参数：<c>Payload</c> = 目标进程 Id（十进制字符串）；<c>Payload = null</c>
        /// 表示解除 Worker 当前掌握的全部绑定（即进程页里所有已绑定进程）。
        /// 返回：<c>Success = true</c> 时 <c>Message</c> 给出成功解绑的进程数
        /// （已退出的进程不在解绑列表里，不计入该数）；指定单个进程 Id 而解绑失败时
        /// 回 <c>Success = false</c>，<c>Message</c> 说明原因。
        /// </summary>
        public const string CommandClearAllSchedules = "clear-all-schedules";

        /// <summary>
        /// 取回 Worker 侧日志快照。
        ///
        /// 参数：<c>Payload = null</c>。
        /// 返回：<c>Payload</c> 为一行一条的纯文本，字段以 <c>|</c> 分隔：
        /// <c>yyyy-MM-ddTHH:mm:ss.fff|级别|消息</c>，级别取 DEBUG / INFO / WARN / ERROR / FATAL。
        /// 用文本而不是嵌套 JSON：协议按行切分消息，嵌套数组一旦带换行就必须再套一层转义，
        /// 而这份数据只给人看。消息内的 <c>\r\n</c> 由 Worker 转义成字面量 <c>\n</c> 后回传，
        /// 消息正文可以含 <c>|</c>，客户端按前两个分隔符切分（Split('|', 3)）。
        /// </summary>
        public const string CommandSnapshotLogs = "snapshot-logs";

        /// <summary>
        /// 清空 Worker 的内存日志缓冲与已落盘的日志文件。
        ///
        /// 参数：<c>Payload = null</c>。返回：<c>Success = true</c> 表示已清空。
        ///
        /// 日志的主副本在 Worker 的 <c>LogService</c> 里（内存环形缓冲 + 按天落盘）。
        /// 界面自己把列表清掉，下一次 <c>snapshot-logs</c> 会把同样的条目再拉回来 ——
        /// 用户看到的是「清空按钮没用」，而这不会报任何错。
        /// </summary>
        public const string CommandClearLogs = "clear-logs";

        /// <summary>
        /// 新增或覆盖一条进程调度规则。
        ///
        /// 参数：<c>Payload</c> = JSON
        /// <c>{"processName":"...","targetType":"ccd|mask","targetValue":"...","priority":"..."}</c>
        /// （<see cref="SaveScheduleRulePayload"/>）。
        ///   · <c>targetType = ccd</c>：<c>targetValue</c> 取 <c>CCD0</c> / <c>CCD1</c>（或 <c>0</c> / <c>1</c>）；
        ///   · <c>targetType = mask</c>：<c>targetValue</c> 是 <c>masks.json</c> 里的掩码 Id，
        ///     Worker 会把掩码值一起快照进规则，掩码日后被删也还能按快照下发；
        ///   · <c>priority</c> 可留空 = 这条规则不碰优先级。
        ///
        /// 返回：<c>Success = true</c> 表示已写入 <c>schedules.json</c> 并触发一次重扫。
        /// 同名规则（进程名，忽略大小写）视为覆盖，不产生第二条。
        ///
        /// <c>schedules.json</c> 的主副本在 <c>SchedulingStore</c> 的内存里，且后台监听每轮
        /// <c>Reload()</c> 都会重读盘。UI 直接写文件会让「内存 → 盘」和「盘 → 内存」
        /// 两个方向打架，表现为规则偶发丢失。走 IPC 则写入与重扫是同一个动作的两半。
        /// </summary>
        public const string CommandSaveSchedulingRule = "save-scheduling-rule";

        /// <summary>
        /// 删除一条调度规则。
        ///
        /// 参数：<c>Payload</c> = 规则 Id（32 位无横线 GUID，来自 <c>query-scheduling-rules</c>）。
        /// 返回：<c>Success = true</c> 表示已删除并触发一次重扫；
        /// <c>false</c> 时 <c>Message</c> 说明「规则不存在」。
        ///
        /// 按名字删只在「用户不会改名字」的前提下成立。规则编辑一旦支持改进程名，
        /// 界面上那一行的身份就变了：按旧名字删会失败，按新名字删又可能命中另一条。
        /// Id 把「这条记录是谁」与「它绑哪个进程」分开，改名不影响删除。
        ///
        /// 旧版 UI 发的是名字。升级途中新旧两个 exe 混搭是常态，把旧写法直接判错，
        /// 表现就是「点了删除、规则还在、只弹一句协议错误」。Worker 先按 Id 精确匹配，
        /// 匹配不到再退回按进程名匹配（忽略大小写与首尾空白）。
        /// 这个回退是**单向**的：Id 命中就绝不再看名字，避免一条名字恰好等于某个 Id
        /// 的规则被误删（32 位十六进制与进程名不可能撞车，但仍不做二义性判断）。
        /// </summary>
        public const string CommandDeleteSchedulingRule = "delete-scheduling-rule";

        /// <summary>
        /// 查询全部调度规则。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = JSON 数组
        /// <c>[{id,processName,targetType,targetValue,priority,enabled,path}, ...]</c>
        /// （<see cref="ScheduleRulePayload"/>，格式约定见该类注释）。
        ///
        /// <c>schedules.json</c> 的唯一写入方是本进程（save / delete 都立刻写盘并更新内存），
        /// 每次查询都 <c>Load()</c> 只是白做一次 IO，还会在文件被外部改坏时
        /// 把内存里的正确状态覆盖成空表。
        /// </summary>
        public const string CommandQuerySchedulingRules = "query-scheduling-rules";

        /// <summary>
        /// 查询全部自定义核心掩码。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = JSON 对象
        /// <c>{"masks":[{id,name,mask,coreCount}, ...], "limit":10}</c>
        /// （<see cref="MaskListPayload"/>）。
        ///
        /// <c>save-scheduling-rule</c> 的 <c>targetType=mask</c> 要求传掩码 **Id**，
        /// 而 Id 只有持有 <c>masks.json</c> 的这一侧知道。界面拿不到列表，
        /// 那个下拉就只能列「CCD0 / CCD1 / 全部核心」，用户存过的掩码一条也选不到。
        /// </summary>
        public const string CommandQueryMasks = "query-masks";

        /// <summary>
        /// 新增或按 Id 覆盖一套自定义掩码。
        ///
        /// 参数：<c>Payload</c> = JSON <c>{"id":"...","name":"纯大核","mask":65280}</c>
        /// （<see cref="SaveMaskPayload"/>）。<c>id</c> 为空串 = 新建，Id 由 Worker 生成。
        ///
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = 写入后的 <see cref="MaskListPayload"/>
        /// （掩码管理器保存后要立刻显示新列表，省一次往返）。
        /// 失败时 <c>Message</c> 说明是「名称为空 / 超长」「重名」还是「已达 10 套上限」。
        ///
        /// 重名与套数上限是 <c>masks.json</c> 的约束，不是界面的约束。
        /// 界面自己判一遍只是提前提示，真正的关卡必须在持有唯一主副本的这一侧 ——
        /// 否则绕过界面（以后的导入 / 迁移）就能写进第十一套。
        /// </summary>
        public const string CommandSaveMask = "save-mask";

        /// <summary>
        /// 删除一套自定义掩码。
        ///
        /// 参数：<c>Payload</c> = 掩码 Id。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = 删除后的 <see cref="MaskListPayload"/>；
        /// <c>false</c> 时 <c>Message</c> 说明「掩码不存在」。
        ///
        /// <c>schedules.json</c> 里的规则存的是掩码**值**的快照，掩码删掉之后
        /// 那条规则仍然按快照生效。这是刻意的：删一套掩码不该顺手把用户的绑核规则废掉。
        /// 但该规则在界面上的掩码名会退回数字区间 —— 名字已经不存在了。
        /// </summary>
        public const string CommandDeleteMask = "delete-mask";

        /// <summary>
        /// 查询本机 CPU 拓扑（掩码网格要按真实核数与 CCD 排布生成）。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = JSON
        /// <c>{"cores":16,"coresPerCcd":8,"threadsPerCore":2}</c>
        /// （<see cref="CpuTopologyPayload"/>）。
        ///
        /// 掩码是「位 i = 逻辑处理器 i」，位数直接来自拓扑。界面按 16 核画格子，
        /// 在 8 核机器上会画出不存在的核 —— 勾上就是一次写不进去的保存。
        /// </summary>
        public const string CommandQueryCpuTopology = "query-cpu-topology";

        /// <summary>
        /// 查询全部场景规则。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = JSON 数组
        /// <c>[{id,trigger,argument,profileId,priority,enabled}, ...]</c>
        /// （<see cref="ScenarioRulePayload"/>）。<c>trigger</c> 是枚举**名称**。
        ///
        /// 场景自动切换的总闸（<c>ScenarioFile.Enabled</c>）不在这条命令的返回里：
        /// 它是「功能开没开」，与「有哪些规则」是两个问题。混进数组会让 UI 分不清
        /// 「没有规则」和「有规则但总闸关着」。
        /// </summary>
        public const string CommandQueryScenarioRules = "query-scenario-rules";

        /// <summary>
        /// 新增或覆盖一条场景规则。
        ///
        /// 参数：<c>Payload</c> = JSON
        /// <c>{"trigger":"ProcessRunning","argument":"game.exe","profileId":"...",
        /// "maskId":"...","powerSchemeId":"...","priority":10,"enabled":true}</c>
        /// （<see cref="ScenarioRulePayload"/>）。
        /// <c>profileId</c> / <c>maskId</c> / <c>powerSchemeId</c> 各自可为空：空 = 不动那一块，
        /// 但三个全空会被拒绝。
        /// **<c>id</c> 为空串 = 新建**：Id 由 <c>ScenarioRule</c> 的初始化器生成。
        /// 非空 <c>id</c> 必须是已存在的规则，否则失败（不静默改写成新建）。
        ///
        /// 返回：<c>Success = false</c> 时 <c>Message</c> 说明「方案 / 掩码不存在」
        /// 「规则不存在」或「进程类触发缺少 argument」。
        ///
        /// 存一条指向不存在目标的规则，等于让它命中时静默少做一件事 ——
        /// 用户看到的是「场景切了但没反应」，而日志里只有一句「目标不存在」。
        /// </summary>
        public const string CommandSaveScenarioRule = "save-scenario-rule";

        /// <summary>
        /// 删除一条场景规则。
        ///
        /// 参数：<c>Payload</c> = 规则 Id。
        /// 返回：<c>Success = false</c> 时 <c>Message</c> 说明「规则不存在」。
        /// </summary>
        public const string CommandDeleteScenarioRule = "delete-scenario-rule";

        /// <summary>
        /// 查询场景自动切换的总闸状态。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = JSON <c>{"enabled":bool}</c>
        /// （<see cref="ScenarioStatePayload"/>）。
        ///
        /// 总闸是「功能开没开」，规则是「有哪些规则」。并进数组的返回体里，
        /// 界面就得从同一个对象上读出两件事，而其中一件在规则为零时恰恰最重要 ——
        /// 「一条规则都没有」和「有规则但总闸关着」是两种完全不同的处置。
        /// </summary>
        public const string CommandQueryScenarioState = "query-scenario-state";

        /// <summary>
        /// 开关场景自动切换的总闸。
        ///
        /// 参数：<c>Payload</c> = JSON <c>{"enabled":true}</c>（<see cref="ScenarioStatePayload"/>）。
        /// 返回：<c>Success = true</c> 时 <c>Payload</c> = 写入后的状态。
        ///
        /// 它落在 <c>scenarios.json</c> 的 <c>enabled</c> 字段，而 Worker 的
        /// <see cref="Services.ScenarioMonitor"/> 依据它决定要不要订阅进程 / 电源事件。
        /// 界面直接改文件，监听线程要等到下一次规则变更才会重新对齐 ——
        /// 关掉总闸后事件监听还在跑，用户看到的是「关了还在自己切方案」。
        /// </summary>
        public const string CommandSetScenarioEnabled = "set-scenario-enabled";

        /// <summary>
        /// 读取当前电源策略（EPP / Boost / 最大处理器状态 / C-State / ASPM）。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c> 表示**查询本身**成功，
        /// <c>Payload</c> = JSON <see cref="Services.PowerPolicySnapshot"/>。
        ///
        /// 「查询失败」与「查到了，但这台机器现在没有可用方案」是两件事。
        /// 后者是有效信息：界面据此把五项都画成「不可用」并显示原因。
        /// 把它判成失败会让 UI 走错误分支，弹一句「查询电源策略失败」，
        /// 而真正该显示的是「无法获取当前电源方案」以及为什么。
        /// 这与 query-smu-status 的口径一致（Success 表达查询本身，不表达目标可用性）。
        ///
        /// 这五项不是每台机器都有（台式机常缺 ASPM，部分主板不暴露 Boost 档位）。
        /// 只回值的话，「读到 0」与「读不到」在界面上无法区分，详见
        /// <see cref="Services.PowerPolicySnapshot"/> 的类注释。
        /// </summary>
        public const string CommandQueryPowerPolicy = "query-power-policy";

        /// <summary>
        /// 修改电源策略（增量语义：只改 Payload 里出现的项）。
        ///
        /// 参数：<c>Payload</c> = JSON
        /// <c>{"epp":50,"boostMode":2,"maxProcessorState":100,"aspm":1,"idleDisable":false}</c>
        /// （<see cref="ApplyPowerPolicyPayload"/>，**每个字段都可空，null = 不改**）。
        ///
        /// 返回：
        ///   · <c>Success = true</c>：至少一项写入成功且方案已重新激活，
        ///     <c>Payload</c> 为写入后的快照（省掉 UI 再查一次）；
        ///   · <c>Success = false</c> + 「没有要修改的字段」：Payload 缺失、为 null，
        ///     或 AC/DC 共十个字段全为 null；
        ///   · <c>Success = false</c> + 「配置已写入但…」：值已落盘但重新激活失败
        ///     （用户需要重启才生效，而不是重试）；
        ///   · <c>Success = false</c> + 逐项错误：范围校验不通过、或 API 返回非 0
        ///     （含错误码，例如主板不接受某个 Boost 档位）。
        ///
        /// <c>PowerWriteACValueIndex</c> 只改方案里的存储值，系统不会立刻按新值调度。
        /// 必须对同一方案再调 <c>PowerSetActiveScheme</c>，电源管理器才会重新加载。
        /// 少了这一步的表现是「界面显示已改、实际功耗没变、重启后才生效」——
        /// 用户会认为程序没保存。
        ///
        /// 用户一次可能改了五个滑块，其中一项本机不支持。整体回滚没有意义
        /// （已经写进去的项也没有「原值」可回），全部放弃又等于什么都没做。
        /// 所以逐项写、逐项报，最后把失败项的原因合起来回传。
        /// </summary>
        public const string CommandApplyPowerPolicy = "apply-power-policy";

        /// <summary>
        /// 列出本机全部电源方案。
        ///
        /// 参数：无（<c>Payload</c> 忽略）。
        /// 返回：<c>Success = true</c>，<c>Payload</c> = JSON <see cref="PowerSchemeListPayload"/>。
        ///
        /// 方案 GUID 随机器而变：只有「平衡 / 高性能 / 节能」三个是 Windows 内置的，
        /// 「卓越性能」默认隐藏、需要手动开启，OEM 还会预置自己的方案。
        /// UI 里写死四个 GUID 的结果是：点到一个本机不存在的方案，系统返回失败，
        /// 而界面上那个卡片看起来是正常的。
        /// </summary>
        public const string CommandQueryPowerSchemes = "query-power-schemes";

        /// <summary>
        /// 切换当前活动的电源方案。
        ///
        /// 参数：<c>Payload</c> = 方案 GUID 的字符串形式（<c>D</c> 格式）。
        /// 返回：<c>Success = false</c> 时 <c>Message</c> 说明是格式错还是系统拒绝。
        ///
        /// 改值只动当前活动方案里的存储项；切换方案换的是「哪一份方案生效」。
        /// 合成一条会让「只想切方案」的调用也必须带一份改值请求，
        /// 而改值请求一旦为空又会触发「没有要修改的字段」。
        /// </summary>
        public const string CommandSetActivePowerScheme = "set-active-power-scheme";

        /// <summary>
        /// 新建电源方案：以某个模板派生一份，改名后立即设为活动方案。
        ///
        /// 参数：<c>Payload</c> = JSON
        /// <c>{"templateGuid":"...","name":"高性能"}</c>（<see cref="CreatePowerSchemePayload"/>）。
        ///
        /// 返回：<c>Success = true</c>，<c>Payload</c> = 写入后的 <see cref="PowerSchemeListPayload"/>
        /// （含新方案的 GUID 与新的活动方案），UI 不必再查一次。
        /// 失败时 <c>Message</c> 说明是名字不合法、模板不存在，还是系统拒绝了复制。
        ///
        /// 四个内置方案的模板键一直在注册表里，哪怕对应方案已被删除、或从未在界面出现过
        /// （卓越性能默认隐藏，节能与高性能在新版 Windows 里被移除了入口）。
        /// 实测四个 GUID 都能成功派生，因此不需要「先启用再新建」的中间步骤。
        ///
        /// 一个方案有几十项设置，本工具只负责其中五项。凭空造出来的方案，
        /// 剩下几十项落在什么值上取决于系统默认，得不到「节能」「高性能」应有的完整行为。
        /// </summary>
        public const string CommandCreatePowerScheme = "create-power-scheme";

        /// <summary>
        /// 改电源方案的显示名。
        ///
        /// 参数：<c>Payload</c> = JSON <c>{"guid":"...","name":"..."}</c>
        /// （<see cref="RenamePowerSchemePayload"/>）。
        /// 返回：<c>Success = true</c> + 刷新后的 <see cref="PowerSchemeListPayload"/>。
        ///
        /// 内置方案也允许改名：名字是显示层的东西，改完仍然是那一份内置方案
        /// （删除时按 GUID 判定，不看名字）。
        /// </summary>
        public const string CommandRenamePowerScheme = "rename-power-scheme";

        /// <summary>
        /// 删除电源方案。
        ///
        /// 参数：<c>Payload</c> = 方案 GUID 的字符串形式（<c>D</c> 格式）。
        /// 返回：<c>Success = true</c> + 刷新后的 <see cref="PowerSchemeListPayload"/>。
        ///
        /// 删掉之后系统里再也没有对应模式的模板，用户无法通过「新建」把它找回来 ——
        /// 只能重装系统或用 <c>powercfg -restoredefaultschemes</c> 重置全部电源设置。
        /// 这种不可逆的破坏不该由一个「删除」按钮承担。
        ///
        /// 系统会自行回落到别的方案。因此响应里必须带回重新枚举的列表与活动 GUID，
        /// 而不是让 UI 自己猜「大概切到了第一个」。
        /// </summary>
        public const string CommandDeletePowerScheme = "delete-power-scheme";

        /// <summary>
        /// 读取界面设置（自启形态 / 关闭行为 / 三个后台开关 / 提示时长）。
        ///
        /// 参数：<c>Payload = null</c>。
        /// 返回：<c>Payload</c> = JSON <see cref="UiSettingsPayload"/>（全字段有值）。
        ///
        /// <c>ui.json</c> 的三个后台开关由 Worker 消费（启动时应用调校、进程监听）。
        /// 界面各存一份的结果是两边永远不同步 —— 用户在界面上关掉「监听进程」，
        /// 后台仍按旧值继续调度。
        /// </summary>
        public const string CommandQueryUiSettings = "query-ui-settings";

        /// <summary>
        /// 写入界面设置（增量：只改 Payload 里出现的字段）。
        ///
        /// 参数：<c>Payload</c> = JSON <see cref="UiSettingsPayload"/>。
        /// 返回：<c>Payload</c> = 写入后的全量设置，界面据此回显。
        ///
        /// 回滚内存会让「界面显示已关、后台还在跑」重现；把失败原因原样回给界面，
        /// 用户至少知道这次没保存成功，可以重试。
        /// </summary>
        public const string CommandSaveUiSettings = "save-ui-settings";

        /// <summary>
        /// 把调校恢复成默认安全参数（逐核 CO = 0、FMax = 0，并下发一次）。
        ///
        /// 参数：<c>Payload = null</c>。
        /// 返回：<c>Success = true</c> 表示已写盘并下发；<c>false</c> 时 <c>Message</c>
        /// 说明是写盘失败还是 SMU 拒绝。
        /// </summary>
        public const string CommandResetTuningSafe = "reset-tuning-safe";

        /// <summary>
        /// 清除全部用户数据（方案 / 调度规则 / 掩码 / 场景 / 日志 / 界面设置）。
        ///
        /// 参数：<c>Payload = null</c>。
        /// 返回：<c>Success = true</c> 表示已删除并重置；<c>false</c> 时 <c>Message</c>
        /// 说明卡在哪一步。
        ///
        /// 这些文件的主副本都在 Worker 的内存里（各自 store 缓存）。界面直接删文件，
        /// 下一次保存会把内存里的旧内容整份写回去 —— 用户看到「清了又回来」。
        /// </summary>
        public const string CommandFactoryReset = "factory-reset";

        /// <summary>
        /// 两端共用的编码。显式无 BOM：管道另一头如果有 BOM，
        /// 第一行 JSON 的开头会多出三个字节，解析直接失败。
        /// </summary>
        public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// 版本协商的唯一入口。匹配返回 true 且 <paramref name="error"/> 为空；
        /// 其余一切情况（不一致 / 缺失被反序列化成 0 / 任意非法值）返回 false 并给出固定文案。
        /// </summary>
        /// <param name="received">从请求或响应里读到的 <c>Version</c>。</param>
        /// <param name="error">失败时的固定文案，成功时为空串。</param>
        public static bool ValidateVersion(int received, out string error)
        {
            if (received == ProtocolVersion)
            {
                error = string.Empty;
                return true;
            }

            error = VersionMismatchMessage;
            return false;
        }

        /// <summary>
        /// 构造「版本不一致」这一条失败响应。两端都走这里，
        /// 保证 <c>Version</c> 填的是本端已知版本、<c>Message</c> 是同一句话。
        /// </summary>
        public static PipeResponse VersionMismatchResponse() => new()
        {
            Version = ProtocolVersion,
            Success = false,
            Message = VersionMismatchMessage,
            Payload = null
        };
    }
}
