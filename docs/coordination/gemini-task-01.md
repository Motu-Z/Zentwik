# 任务 01：协议层与状态层（Dart）

**执行者**：Gemini（Linux）
**审查与 Windows 验证**：WorkBuddy（Windows，真服务 + 真硬件）
**仓库**：本仓库根（含 `src/Zentwik.Gui`、`src/Zentwik.Agent` 等）

---

## 0. 一句话

在 `src/Zentwik.Gui` 里实现**协议层与状态层**，让 Flutter 客户端能连上已有的 Windows 服务并收发遥测。不写界面、不碰后端、不写 Win32。

## 1. 边界（越界即返工）

| 可以做 | 不可以做 |
|---|---|
| 在 `src/Zentwik.Gui/lib/protocol/`、`lib/state/`、`test/` 下新增文件 | 修改 `lib/platform/frame_channel.dart`（已冻结，是唯一接缝） |
| 用 `InMemoryFrameChannel` 写端到端测试 | 写任何 `dart:ffi` / Win32 / 命名管道实现（Windows 侧由 WorkBuddy 写） |
| 读 `docs/protocol/gui-contract.md`、`src/Zentwik.Contracts/*.cs` 作为事实源 | 修改任何 `.cs` / `.csproj` / `scripts/` |
| 用 `flutter test`、`dart analyze` 自证 | 运行 `flutter build windows`（Linux 没有 MSVC，必然失败，不算问题） |

**字段来源只有一个**：`docs/protocol/gui-contract.md`。文档里没有的字段就是不存在，禁止按“通常会叫这个名字”补。凡是拿不准的，写进报告的“未决问题”，不要自己拍。

## 2. 交付物

### 2.1 `lib/protocol/envelope.dart`

- `RequestEnvelope`：`v`(int)、`id`(int)、`op`(String)、`payload`(`Map<String, Object?>?`)
- `ResponseEnvelope`：`v`、`id`、`op`、`ok`、`error`、`detail`、`data`(`Object?`)
- 编解码用 `jsonEncode` / `jsonDecode`，字段名就是契约里的名字（camelCase）
- **`payload` 为 null 时必须整个字段省略**（不是 `"payload": null`），服务端按字段存在与否分派
- `isEvent` 字段收到后**忽略**；事件判定用 `id == 0`

### 2.2 `lib/protocol/models/`

按契约 §3 逐字段实现，默认值一律 `null`（缺失 ≠ 0，缺失 ≠ 空串）。至少覆盖：

`AgentStatusDto`、`CapabilityDto`、`ModuleDto`、`SettingsDto`、`SettingsPatch`、`SubscriptionAck`、`TelemetryDto`、`CoreClockDto`、`HistoryDto`、`LedgerEntry`

要求：

- 全部 `final` 字段 + `const` 构造，不使用可变状态
- `fromJson` 手写（不引入 `json_serializable` / `freezed`，本轮不加依赖）
- 数字字段遇到 `int` 与 `double` 两种 JSON 表示都要能吃（Dart 的 `num.toDouble()`）
- 时间戳解析保留偏移：用 `DateTime.parse` 后**不要** `toUtc()` 覆盖原始语义，需要本地时间时另存

### 2.3 `lib/protocol/errors.dart`

- 契约 §4 的 32 个错误码写成常量
- `ProtocolException`：带 `code`、`detail`、`op`
- `describe(code)`：返回中文用户文案（面向界面直接显示，不要出现英文错误码原串）
- 契约 §5 的枚举映射写成 `static const` 或 `enum` + `fromWire(int)`

### 2.4 `lib/protocol/client.dart`

`ProtocolClient`，构造参数只接受 `FrameChannel`：

```dart
ProtocolClient(FrameChannel channel, {Duration requestTimeout});
```

必须支持：

| 能力 | 要求 |
|---|---|
| `connect()` | 建连后启动单个读循环，读循环是唯一读取者 |
| `request(op, [payload])` | 自增 `id`，写帧，按 `id` 配对响应；超时抛 `TimeoutException`；`ok == false` 抛 `ProtocolException` |
| 并发请求 | 多个 `request` 可同时在飞，按 `id` 分发，不能用全局锁串行化 |
| 事件流 | `Stream<TelemetryDto> get telemetry`，广播流（`StreamController.broadcast`），无人监听时不报错 |
| `unsubscribe()` / `close()` | 先发 `telemetry.unsubscribe` 再关通道；`close()` 幂等 |
| 断线 | 读循环收到 `null` 即对端关闭：完成所有挂起请求（抛 `ProtocolException('connection.closed')`），关闭事件流，之后 `request` 立即失败 |

约束：

- 读循环里**不要** `await` 业务处理，事件只往流里塞
- 4 MiB 上限由 `FrameChannel` 负责，客户端不重复检查

### 2.5 `test/`

必须包含（用 `InMemoryFrameChannel` + 契约里的**真实 JSON 样本**）：

1. 请求编码：`payload` 缺失时 JSON 里没有 `payload` 键
2. 响应解码：`data` 缺失、`detail` 为空串、`isEvent` 存在——三种都要能吃
3. 事件帧：`id == 0` 进广播流，不消耗请求配对
4. 并发：两个 `request` 乱序返回，各自拿到自己的结果
5. 错误：`ok:false` 抛 `ProtocolException`，`code` 与 `detail` 正确
6. 超时：不返回响应时抛 `TimeoutException`，且不泄漏挂起项
7. 对端关闭：挂起请求全部失败、事件流关闭、后续请求立即失败
8. 枚举映射：`owner`/`status`/`kind` 数字 → 语义值的双向表
9. `TelemetryDto`：`temperatureCelsius` 缺失解析为 `null` 而不是 0；`coreClocks` 缺失为空列表

## 3. 自检（提交前必须全过）

```bash
cd src/Zentwik.Gui
flutter analyze      # 0 issues
flutter test         # 全部通过
```

`flutter analyze` 有 warning 也算未完成。

## 4. 报告格式

```
## 改了哪些文件
（路径 + 一句话职责）

## 自检输出
flutter analyze: ...
flutter test: ... (X passed)

## 未决问题
（契约里没写清、你做了假设的地方，逐条列出；没有就写"无"）
```

**不要写"已完成""效果良好"这类结论**，只放可核对的输出。审查由 WorkBuddy 做。

## 5. 已知坑（省时间用）

- PowerShell 抓的样本里 `+08:00` 被打印成 `\u002B08:00`，那是控制台转义，JSON 本身没问题
- 服务端 `data` 为 null 时字段整个省略，`data == null` 与"字段不存在"在 Dart 里都是 `null`，不用区分
- 客户端解析 `ledger.list` 时 `owner`/`status` 是数字；`capabilities.list` 的 `state` 是字符串。两套不要混
- 不要为 `isEvent` 建模型字段，它只是服务端的计算属性
