# 遥测与可选组件 · 字段契约

给表现层（WPF/CLI）接遥测和可选组件时用的字段约定。以 `src/Zentwik.Contracts/Dtos.cs` 的 record 定义为准，本文只解释语义，不重复类型定义。

## 通用约定

- 所有跨进程 JSON 都是 **camelCase**（`PropertyNamingPolicy = CamelCase`），字段名即 record 构造参数名转小写首字母。
- **值为 null 的字段整段省略**（`DefaultIgnoreCondition = WhenWritingNull`）。所以客户端读不到某个 key，等价于该项是 null，不要当成 0。
- 管道名 `zentwik-agent`，协议版本 2，单帧上限 4 MiB。
- 表现层只依赖 `Zentwik.Contracts`，不得引用 `Platform`，不得持有设备句柄。

## 遥测

订阅操作 `telemetry.subscribe`，取消 `telemetry.unsubscribe`；服务端推送事件 `telemetry.sample`，载荷 `TelemetryDto`。

### 订阅请求 / 应答

`SubscriptionRequest(IntervalMs, SensorIntervalMs?)`：

| 字段 | 类型 | 说明 |
|---|---|---|
| `intervalMs` | int | 推送间隔。传 0 或 null 用设置里的持久值 |
| `sensorIntervalMs` | int? | 传感器间隔，null 用设置里的值 |

`SubscriptionAck(IntervalMs, SensorIntervalMs)` 返回服务端**实际生效**的间隔（请求值可能被夹到上下限），客户端超时必须按 ack 算，不能用请求值。

### TelemetryDto 字段

| 字段 | 类型 | 节奏 | 说明 |
|---|---|---|---|
| `timestamp` | DateTimeOffset | 每拍 | 服务端时间戳，接收端不得用本地时钟替代 |
| `cpuTotalPercent` | double | 每拍 | 全机 CPU 占用 |
| `memoryTotalBytes` | ulong | 每拍 | 物理内存总量 |
| `memoryAvailableBytes` | ulong | 每拍 | 物理内存可用量 |
| `processCount` | int | 每拍（另有 2 秒 TTL） | 进程计数，已滤除已退出未回收的进程 |
| `processorCount` | int | 每拍 | 逻辑处理器数 |
| `processorUtilization` | double[]? | **每拍** | 逐逻辑处理器占用率（0–100），索引是操作系统处理器序号（含 SMT），与 `coreClocks` 的核号**不是同一编号**。首帧/失败/处理器数变化时为 null |
| `temperatureCelsius` | double? | 按传感器间隔 | 封装温度，Tctl 口径 |
| `packagePowerWatts` | double? | 按传感器间隔 | 封装功耗，是读取间隔内的**平均值**，非瞬时值 |
| `coreClocks` | `TelemetryCoreClockDto[]?` | 按传感器间隔 | 逐物理核频率 |
| `sensorDetail` | string? | 按传感器间隔 | 传感器异常或说明文字；正常为 null。同一句只会在变化时重发 |

`TelemetryCoreClockDto(Ccd, Core, Megahertz)`：

- `ccd` / `core`：SMU 核掩码字段，与 Curve Optimizer 的核编号同源，**不是** Windows 处理器序号。
- `megahertz`：窗口内平均频率，睡眠时间计入。整窗睡眠的核**不出现在数组里**，不能当成 0 MHz。

### 两条节奏不同的通道

- **系统字段**（cpu/mem/proc/`processorUtilization`）每拍都读，成本低（读系统计数器）。
- **传感器字段**（温度/功耗/`coreClocks`）按 `sensorIntervalMs` 读，两次读取之间**沿用上一次读数**。客户端不能因为相邻两帧温度相同就判定卡死。

## 设置

`SettingsDto(Language, Theme, Accent, UiScale, MonitorIntervalMs, SensorIntervalMs)`；`SettingsPatch` 同名可空字段，null 表示不改。

| 字段 | 范围 | 默认 |
|---|---|---|
| `monitorIntervalMs` | 500–5000 | 1000 |
| `sensorIntervalMs` | 500–30000 | 1000 |

## 可选组件

模块 `ModuleDto(Id, Name, State, Detail?, Capabilities[], Optional, Enabled)`；能力 `CapabilityDto(Id, State, Detail?, Requires[])`。

能力状态字符串：`unavailable` / `installed` / `enabled` / `loaded` / `running` / `degraded` / `failed`。

模块清单（`modules.list`，切换 `modules.enable` / `modules.disable`）：

| 模块 Id | 能力 | Optional |
|---|---|---|
| `monitoring` | `monitor.telemetry.system`、`monitor.telemetry.cpu.sensors` | 否（能力 AnyOf：传感器缺席不拖垮监控） |
| `power` | `system.power.policy` | 否 |
| `cpu-tuning` | `hardware.amd.smu`、`hardware.intel.msr` | 否（AnyOf：另一厂商缺席不算降级） |
| `scheduling` | `system.scheduling.affinity`、`system.scheduling.cpusets` | 否 |
| `automation` | `automation.scene.engine` | 否 |
| `diagnostics` | — | 否 |
| `power-limits` | `hardware.amd.power.limit` | **是，默认关** |

### 功耗墙（可选组件启用后）

`PowerLimitStateDto`：`stapmWatts`/`fastWatts`/`slowWatts` 是当前值（来自 PM 表），`fusedFastWatts`/`fusedSlowWatts`/`fusedTdcAmps` 是熔断值（来自 SMU 命令），两者**不能互相替代**。

写入 `PowerLimitApplyRequest(FastWatts?, SlowWatts?, TdcAmps?, EdcAmps?)`，返回 `PowerLimitApplyDto`，其 `items` 是 `PowerLimitWriteDto(Item, Requested, Actual?, State, Detail?)`：

- `state ∈ applied / diverged / accepted / rejected`。
- `diverged` 时请求值和 `actual` 都显示；`note` 只描述回读比例，**不代用户换算**。
