# GUI 客户端协议契约（v2）

**用途**：Flutter 客户端（`src/Zentwik.Gui`）对接现有服务的唯一依据。
**来源**：`src/Zentwik.Contracts/Protocol.cs`、`Dtos.cs`、`AgentSession.DispatchAsync`，并用真实管道请求核对过响应（2026-11-28）。
**原则**：客户端不得猜测字段。本文件没写的字段，就是不存在的字段；要新增能力只改后端，不在客户端臆造。

---

## 1. 传输层

| 项 | 值 |
|---|---|
| 管道名 | `\\.\pipe\zentwik-agent` |
| 模式 | 字节模式、双工、单连接多路复用（响应与事件共用一条流） |
| 帧格式 | 4 字节**小端** `int32` 长度 + UTF-8 JSON 正文 |
| 单帧上限 | 4 MiB（`4 * 1024 * 1024`） |
| 协议版本 | `v = 2`，字段名固定为 `v` |

### 1.1 请求

```json
{"v":2,"id":1,"op":"agent.status"}
{"v":2,"id":2,"op":"telemetry.subscribe","payload":{"intervalMs":1000,"sensorIntervalMs":2000}}
```

- `id`：非 0 整数，由客户端自增；响应原样带回，用来配对。
- `payload`：无参 op 直接省略该字段（不是 `null`）。
- `v` 不匹配时服务端返回 `protocol.version_mismatch` 并关闭连接。

### 1.2 响应

```json
{"v":2,"id":1,"op":"agent.status","ok":true,"data":{...},"isEvent":false}
{"v":2,"id":9,"op":"plans.apply","ok":false,"error":"plan.not_found","detail":"...","isEvent":false}
```

- `data` 为 null 时**整个字段被省略**（`JsonIgnoreCondition.WhenWritingNull`），Dart 侧一律按可空处理。
- **`isEvent` 是服务端计算属性，每个响应都带**。客户端应忽略它，事件帧用 `id == 0` 判定。
- 失败只有 `ok`、`error`、`detail` 三个字段可用，`data` 不出现。

### 1.3 事件帧

```json
{"v":2,"id":0,"op":"telemetry.sample","ok":true,"data":{...},"isEvent":true}
```

`id` 恒为 0。当前只有 `telemetry.sample` 一种事件。

### 1.4 序列化口径（踩过的坑）

| 现象 | 说明 |
|---|---|
| 枚举是**数字** | `ledger.list` 的 `owner`/`status` 是 `0..4`，不是字符串。映射见 §5 |
| 时间戳带偏移 | `DateTimeOffset` 形如 `2026-11-27T17:44:34.816571+00:00`；解析用保留偏移的类型，不要当成 UTC 强转 |
| 中文是 `\uXXXX` | 标准 JSON 转义，`jsonDecode` 自动还原，不需要额外处理 |
| 空字符串 ≠ 缺字段 | `detail: ""` 与字段缺失含义不同：前者是"确定没有说明" |

---

## 2. op 总表（36 个）

阶段口径：**S0** 壳层与协议层 / **S1** 只读页 / **S2** 写操作页 / **S3** 场景与运维。

| op | 请求 payload | 响应 data | 阶段 |
|---|---|---|---|
| `agent.ping` | — | 无（`data` 缺失） | S0 |
| `agent.status` | — | `AgentStatusDto` | S0 |
| `capabilities.list` | — | `CapabilityDto[]` | S0 |
| `modules.list` | — | `ModuleDto[]` | S0 |
| `modules.enable` | `{"id":"power-limits"}` | `ModuleDto[]`（启用后的全表） | S2 |
| `modules.disable` | `{"id":"..."}` | `ModuleDto[]` | S2 |
| `telemetry.subscribe` | `{"intervalMs":1000,"sensorIntervalMs":2000}` | `SubscriptionAck` | S0 |
| `telemetry.unsubscribe` | — | 无 | S0 |
| `telemetry.history` | `{"limit":60}` | `HistoryDto` | S1 |
| `plans.list` | — | `Plan[]`（完整对象，非摘要） | S2 |
| `plans.get` | `{"id":"..."}` | `Plan` | S2 |
| `plans.save` | `Plan`（完整对象） | `Plan`（保存后，含新 `revision`） | S2 |
| `plans.delete` | `{"id":"..."}` | `bool` | S2 |
| `plans.apply` | `{"id":"..."}` | `PlanApplyResultDto` | S2 |
| `plans.revert` | `{"id":"..."}` | `PlanApplyResultDto`（`state:"reverted"`） | S2 |
| `scenes.list` | — | `SceneSummaryDto[]` | S3 |
| `scenes.get` | `{"id":"..."}` | `Scene` | S3 |
| `scenes.save` | `Scene` | `Scene` | S3 |
| `scenes.delete` | `{"id":"..."}` | `bool` | S3 |
| `scenes.activate` | `{"id":"..."}` | `SceneActivationDto` | S3 |
| `scenes.deactivate` | `{"id":"..."}` | `SceneActivationDto` | S3 |
| `ledger.list` | — | `LedgerEntry[]` | S1 |
| `ledger.release` | `{"id":"<target>"}` | `bool` | S2 |
| `power.scheme.list` | — | `PowerSchemeDto[]` | S2 |
| `power.scheme.create` | `{"template":"<guid>","name":"..."}` | `PowerSchemeDto` | S2 |
| `power.scheme.rename` | `{"guid":"<guid>","name":"..."}` | `PowerSchemeDto` | S2 |
| `power.scheme.delete` | `{"guid":"<guid>","noBackup":false}` | `PowerSchemeDeleteResultDto` | S2 |
| `power.scheme.backup` | `{"guid":"<guid>"}` | `PowerSchemeBackupDto` | S2 |
| `power.scheme.backups` | — | `PowerSchemeBackupDto[]` | S2 |
| `power.scheme.restore` | `{"backup":"<id 或路径>"}` | `PowerSchemeRestoreResultDto` | S2 |
| `settings.get` | — | `SettingsDto` | S0 |
| `settings.set` | `SettingsPatch`（只发改动字段） | `SettingsDto`（生效后全量） | S1 |
| `hardware.tuning.read` | — | `TuningSnapshotDto` | S1 |
| `hardware.tuning.intel` | — | `IntelTuningSnapshotDto` | S1 |
| `hardware.power.limit.read` | — | `PowerLimitStateDto` | S1 |
| `hardware.power.limit.apply` | `PowerLimitApplyRequest` | `PowerLimitApplyDto` | S2 |
| `diagnostics.doctor` | — | `DoctorDto` | S1 |
| `diagnostics.log` | `{"limit":200}` | `LogEntryDto[]` | S1 |

---

## 3. DTO 字段（照抄，不要改写命名）

### 3.1 AgentStatusDto

```json
{"version":"0.1.0","protocolVersion":2,"machineName":"MOTUIC-LAPTOP","elevated":true,
 "runningAsService":true,"simulated":false,"processorCount":32,"physicalCoreCount":16,
 "processorGroups":1,"startedAt":"2026-11-27T23:49:47.7340302+08:00","uptimeSeconds":6879,
 "dataDirectory":"C:\\ProgramData\\Zentwik","planCount":0,"sceneCount":0,"ledgerEntryCount":2}
```

`activeSceneId` 仅在存在活动场景时出现。

### 3.2 CapabilityDto / ModuleDto

```json
{"id":"hardware.amd.smu","state":"running","detail":"SMU 0x04621A00，16 个物理核 / 32 线程。…","requires":[]}
{"id":"monitoring","name":"监控","state":"ready","detail":"32 逻辑处理器，采样正常。",
 "capabilities":["monitor.telemetry.system","monitor.telemetry.cpu.sensors"],"optional":false,"enabled":true}
```

- `state` 是**字符串**（与上文的账本枚举不同），取值见 §5。
- `detail` 是给用户看的中文句子，可能为空串或缺失。

### 3.3 TelemetryDto（事件与 history 共用）

```json
{"timestamp":"2026-11-27T17:44:34.816571+00:00","cpuTotalPercent":9.616295594504974,
 "memoryTotalBytes":33485860864,"memoryAvailableBytes":19368648704,"processCount":354,
 "processorCount":32,"temperatureCelsius":73.75,"packagePowerWatts":33.74272290087188,
 "coreClocks":[{"ccd":0,"core":0,"megahertz":310}],
 "processorUtilization":[9.23076923076923,23.076923076923077,0]}
```

| 字段 | 口径 |
|---|---|
| `cpuTotalPercent` | 全系统 CPU 占用率，0–100 |
| `memoryTotalBytes` / `MemoryAvailableBytes` | 字节。**可用**不等于空闲，不要用 `total - available` 当"已用"以外的含义 |
| `processCount` | 比任务管理器口径更接近真实（含受保护/跨会话进程），不要与 `Win32_Process` 数量对比后判为错误 |
| `temperatureCelsius` | 可缺失。缺失 = 读不到，**不是 0** |
| `packagePowerWatts` | 可缺失，同上 |
| `sensorDetail` | 传感器失败原因，正常时不出现 |
| `coreClocks` | 物理核的平均频率；**睡眠核不出现**，缺失不等于 0 MHz |
| `processorUtilization` | 逐**逻辑**处理器占用率，索引是操作系统处理器序号，**与 `coreClocks.core` 不是同一编号体系**，不要混用 |

### 3.4 LedgerEntry

```json
{"target":"power.active","planId":"probe-hidden","previousValue":"f60bedaa-…","desiredValue":"8c5e7fda-…",
 "actualValue":"f60bedaa-…","source":"plan:probe-hidden","owner":0,"priority":2147483646,
 "status":1,"updatedAt":"2026-11-26T14:56:37.2378083+00:00","restorable":false,"processStartTicks":0}
```

`processStartTicks` 是进程创建时间（调度类条目用于区分 PID 复用），非调度条目为 0。

### 3.5 SubscriptionAck / HistoryDto

```json
{"intervalMs":1000,"sensorIntervalMs":2000}
{"capacity":3600,"count":6,"samples":[{...TelemetryDto...}]}
```

`Samples` 按时间**升序**；`count < capacity` 表示缓冲还没填满。

### 3.6 SettingsDto / SettingsPatch

```json
{"language":"zh-CN","theme":"dark","accent":"#4C8DFF","uiScale":1.0,
 "monitorIntervalMs":1000,"sensorIntervalMs":1000}
```

`settings.set` 只发改动字段（`SettingsPatch` 全字段可空），响应返回生效后的**全量**。

### 3.7 方案与场景

`Plan` 是抽象基类，序列化后按 `kind` 区分子类（`kind` 为数字，见 §5）：

| kind | 类型 | 专有字段 |
|---|---|---|
| 0 | `CpuTuningPlan` | `platformId`、`defaultOffset`、`ccdOffsets`、`cores[]`（`coreIndex`/`ccdIndex`/`auto`/`offset`/`maxFrequencyMhz`）、`fMaxLimitMhz`、`intelVoltageOffsetMillivolts` |
| 1 | `PowerPlan` | `baseSchemeGuid`、`settings[]`（`settingId`/`subGroupId`/`acValue`/`dcValue`） |
| 2 | `SchedulingPlan` | `rules[]`（`processMatch`/`cpuMask`/`priority`/`cpuSets`/`enforce`） |

公共字段：`id`、`name`、`revision`、`createdAt`、`updatedAt`、`lastApply`（`state`/`appliedAt`/`persistsAcrossReboot`/`outcome`）、`kind`。

`Scene`：`id`、`name`、`priority`、`enabled`、`triggers[]`（`kind`/`argument`）、`actions[]`（`planId`）、`restoreOnExit`、`lastActivatedAt`、`lastOutcome`。
触发 `kind` 取值：`manual` / `process` / `processExited` / `powerSource`（`argument` 为 `ac`/`dc` 或进程名）。

---

## 4. 错误码（32 个，全部字符串）

```
protocol.version_mismatch  protocol.malformed  request.unknown_operation  request.payload_invalid
request.unauthorized  agent.internal
plan.not_found  plan.invalid  scene.not_found  scene.invalid  ledger.entry_missing
hardware.unavailable  hardware.write_failed  hardware.verify_failed
power.scheme_not_found  power.scheme_builtin  power.scheme_name_conflict  power.scheme_create_failed
power.scheme_delete_failed  power.scheme_backup_failed  power.scheme_backup_not_found  power.scheme_restore_failed
module.not_found  module.not_optional  module.disabled
power.limit_rejected  power.limit_invalid  power.limit_unsupported
```

客户端处理原则：

- **不重试写操作**（`hardware.write_failed` / `hardware.verify_failed` 重试只会重复写硬件）
- `request.unauthorized`：提示需要以管理员运行，不自行提权
- `module.disabled`：引导 `modules.enable <id>`，不要自动开启

---

## 5. 枚举映射

| 类型 | 值 |
|---|---|
| `LedgerEntry.owner` | `0` None / `1` Scene / `2` User / `3` Plan / `4` System |
| `LedgerEntry.status` | `0` Applied / `1` Reverted / `2` Failed / `3` Skipped / `4` Unknown |
| `Plan.kind` | `0` CpuTuning / `1` Power / `2` Scheduling |
| `lastApply.state` | `0` NotApplied / `1` Applied / `2` Partial / `3` Failed |
| `CapabilityDto.state`（字符串） | `unavailable` / `installed` / `enabled` / `loaded` / `running` / `degraded` / `failed` |
| `ModuleDto.state`（字符串） | `ready` / `degraded` / `unavailable` |

`unknown`（`status=4`）**不得**显示为成功或失败，它是"写了但读不回来"。

---

## 6. 订阅节奏（客户端必须照此实现）

1. `telemetry.subscribe` 的 `intervalMs` 会被服务端夹到 `500..5000`，`sensorIntervalMs` 夹到 `0`（关闭传感器）或 `500..30000`。
2. **一律使用 ack 返回的生效值**算超时；用请求值会在被抬到下限时误报超时。
3. 传感器字段（温度/功耗/逐核频率）按 `sensorIntervalMs` 更新，中间帧沿用上一次读数；`processorUtilization` 与 `cpuTotalPercent` 每拍都新。
4. 无订阅者时后端完全不采样——客户端退出前必须 `unsubscribe` 或断开连接。
5. 历史缓冲只在订阅期间积累，上限 3600 帧，不落盘、不跨重启。

---

## 7. 客户端硬约束

- **仅 Windows**：后端是 Windows 服务（LocalSystem），管道与 `PawnIO` 都是 Windows 专有。
- 连接方身份：服务接受 SYSTEM 或当前登录用户；客户端不需要提权即可读，写操作由服务端判断。
- 一帧 4 MiB 上限：`telemetry.history` 取大 `limit` 时可能超限，客户端应限制在 3600 以内。
- 客户端**不允许**直接读写 `state.json`：唯一写入方是服务，绕过它会导致账本与磁盘不一致。

---

## 8. 本文件未覆盖的部分

- 功耗墙四态文案与 `PowerLimitApplyDto.note` 的展示口径 → 见 `docs/protocol/telemetry.md`
- `hardware.tuning.read` 返回的 `TuningSnapshotDto` 只含 CO 分核偏移，不含 FMax 之外的写入通道；FMax 走 `plans.save` 的 `fMaxLimitMhz`
