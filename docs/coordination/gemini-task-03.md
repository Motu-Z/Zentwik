# 任务 03：连接状态机与聚合状态（纯 Dart）

**前置**：任务 01、02 已合入 `feat/gui-protocol`。
**执行者**：Gemini（Linux）。**审查与 Windows 验证**：WorkBuddy。

---

## 0. 先修两条 analyze（优先级高于本任务）

任务 01 在我这边 `flutter analyze` 还剩两条，按验收标准算不通过：

| 级别 | 位置 | 内容 |
|---|---|---|
| info | `lib/protocol/client.dart:17:8` | `prefer_initializing_formals` —— 用 `this._requestTimeout` 初始化 |
| warning | `test/protocol_client_test.dart:15:36` | `unused_element_parameter` —— 参数 `replies` 从未被传入 |

修到 `flutter analyze` 输出 `No issues found!`。

**另**：`test/win32_pipe_smoke_test.dart` 已由我移到 `test/manual/pipe_smoke_check.dart`（它会在真机管道上卡死，是我的 bug，不是你的）。`flutter test` 不会再收它，你也不必管它。

## 1. 边界

| 可以做 | 不可以做 |
|---|---|
| 改 `lib/state/**`、`test/state_test.dart` 及新增测试 | 改 `lib/platform/**`（含已冻结的 `frame_channel.dart`） |
| 读 `docs/protocol/gui-contract.md` 取 op 名与字段 | 改 `lib/protocol/**` 的对外行为（只许修 analyze 项） |
| 纯 Dart 单测 | 写任何 Widget / 手势 / 页面；改 `pubspec.yaml` |

## 2. `lib/state/connection_state.dart`

状态机（字符串值即对外契约，测试按此断言）：

```
disconnected → connecting → connected → reconnecting → failed
```

要求：

- `connect()`：`connected` 时不重复建连（幂等返回）；`connecting` 时返回同一个 Future
- `disconnect()`：任意状态调用都安全，最终落到 `disconnected`；重复调用不抛
- 断线（`ProtocolException('connection.closed')` 或流关闭）自动转 `reconnecting`
- 重连退避：**200 ms 起，每次翻倍，上限 30 s**，即 `200, 400, 800, 1600, 3200, 6400, …`；累计 **6 次**失败后转 `failed` 并停止重试
- 退避等待必须可取消：`disconnect()` 能立刻打断等待，不等满 30 s
- 对外暴露：`status`（上述字符串）、`attempt`（当前第几次）、`changes`（广播流，状态变化时推）
- 时间源可注入（构造参数 `Duration Function(int attempt)? backoff` 或 `Clock` 抽象），测试不得真等 30 秒

## 3. `lib/state/app_state.dart`

聚合四块只读缓存，**不做业务判断**：

| 字段 | 来源 op | 缓存内容 |
|---|---|---|
| `status` | `agent.status` | `AgentStatusDto` |
| `capabilities` | `capabilities.list` | `List<CapabilityDto>` |
| `modules` | `modules.list` | `List<ModuleDto>` |
| `telemetry` | `telemetry.sample` 事件 | 最新 `TelemetryDto`（保留最近 60 帧） |

要求：

- `refresh()` 依次发三个请求，**单个失败不影响其余**：失败项保留上一次的缓存值，并把 `lastError` 记成 `ProtocolException`
- 三个请求可并发发出（不要求顺序），但 `refresh()` 返回的 Future 要等三个都落定
- `refresh()` 并发调用去重：同一时刻只跑一轮
- op 名一律用契约里的字面量，不许自创；字段解析用任务 01 的 DTO
- 不持有 `FrameChannel`，只持有 `ProtocolClient`

## 4. 测试（`test/state_test.dart` 扩写 + 新增）

至少 10 例：

1. 退避序列正好是 `200/400/800/1600/3200/6400 ms`（用注入的假时钟断言，不真等）
2. 第 6 次失败后状态为 `failed`，第 7 次不再重试
3. `disconnect()` 在退避等待期间调用 → 立即转 `disconnected`，不残留定时器
4. `connect()` 在 `connected` 时幂等（底层建连只发生一次）
5. `connect()` 在 `connecting` 时返回同一 Future
6. `disconnect()` 重复调用不抛
7. 断线后自动进入 `reconnecting`
8. `refresh()` 成功时三块缓存都被填入
9. `refresh()` 中 `modules.list` 失败 → 另两块仍更新，`modules` 保留旧值，`lastError` 非空
10. `refresh()` 并发两次只发一轮请求
11. 遥测事件超过 60 帧时丢最旧

## 5. 自检（必须贴原始输出）

```bash
cd src/Zentwik.Gui
flutter analyze      # 必须 No issues found!
flutter test         # 全绿
```

沙箱没有 Flutter 就先装一份 stable（官方 zip 解压即用）。**不接受"环境不支持所以没验证"。**

## 6. 报告格式

```
## 改了哪些文件
## 自检输出
## 未决问题
```

只放可核对输出。
