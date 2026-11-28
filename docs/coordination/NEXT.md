# 给 Gemini 的一句话指令（托盘备着）

GUI 通道断的时候，把下面这段粘给 Gemini 就行，其余内容都在仓库里。

---

任务 07 我方已复核：`flutter analyze` 无 issue，`flutter test` 69 例全绿，CI 结论对得上。
设置页枚举、卡片调宽隐藏、空态统一都做到位了，`accent: '#4C8DFF'` 那个错夹具也改了。

我又补了一个你没看到、但更严重的缺陷：**推送间隔那两个输入框写进 `state.json` 后毫无作用**。
`AgentRuntime` 订阅遥测时硬编码 `intervalMs: 1000 / sensorIntervalMs: 2000`，
从来没读过设置。已改成「先 refresh 拿设置 → 按设置订阅；设置变了比较已生效值再重订阅；
断线清零，重连必重订」，并加了 `test/runtime/agent_runtime_test.dart`（回环假服务端）锁住两条路径。

另外三件必须说清楚的现状：

1. **语言/主题/强调色/界面缩放这四个下拉，改了不会改变外观。** `ZentwikApp` 恒用暗色主题，
   `ZentwikTokens` 是 `const` 静态字段，颜色编译期定死。我在设置页加了一行说明**如实告知**，
   不是永久方案。
2. 因此在真实应用里，现在**唯一立即生效**的设置是两个推送间隔。
3. 任务 08 就是把这四个变成真的——这是本轮唯一目标，别顺手改别的页面逻辑。

请先 `git pull`，然后做 **任务 08**：

- 读 `docs/coordination/gemini-task-08.md`：`ZentwikTokens` 只留尺寸，颜色迁到
  `ZentwikPalette extends ThemeExtension`（暗/亮 × 4 强调色），
  `buildZentwikTheme({brightness, accent, textScale})`，
  全量把 `ZentwikTokens.<颜色>` 换成 `context.palette.<同名>`，
  `app.dart` 按 `settings` 选主题，`uiScale` 走 `MediaQuery` 的 `textScaler`
  （标签改成「文字缩放」，它不影响布局比例）。

纪律：

- **不得修改** `lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、
  `lib/main.dart`、`pubspec.yaml`、任何 `.cs`；不得改既有 71 例测试的**语义**。
- 一律 `package:material_ui/material_ui.dart`；不新增依赖；未知 `accent` 回落 `blue`，不要抛异常。
- 为了保住 `const` 去造两套颜色是**不接受的**——`const` 掉了就掉了。
- 验证走 GitHub Actions（workflow `Zentwik Next | GUI (Flutter)`）：推完读这次 push 的结果，
  `Analyze` 与 `Test` 都绿才算交付。红了改到绿，别写「未执行」。

交付：能推就继续推到 `feat/gui-protocol`，不能推就按文件输出完整内容。

---

## 任务 09（当前）

先 `git pull`。首页布局（跨列 masonry、自由排列、独立调宽高、持久化、独立时间线）
已由我方完成并合入 `feat/gui-protocol`，**不要重做这部分**。

本轮只做一件事：**卡片内部按自己拿到的实际尺寸改变信息组织方式**。
只定逻辑，不调风格——不换字体、不调配色、不加动画。

读 `docs/coordination/gemini-task-09.md`。三条最容易踩的：

1. **不能按 span 判断档位。** 同一个 span=2，4 列窗口是 ~706px、2 列窗口是 ~345px。
   必须由 `DashboardCardShell` 经 `LayoutBuilder` 下发真实 `bodyWidth/bodyHeight`
   （`CardMetrics`），卡片按**正文宽**分 compact(<220) / regular / wide(≥380) 三档。
2. **不能用 `MediaQuery` 判断宽度。** 时间线可以从 260 拖到 520 或直接隐藏，
   窗口尺寸不变但主工作区宽度变了。这条有专门的回归测试（F.3）。
3. **决策逻辑要抽成纯函数**（`resolveCoreClocks(...)` 这类），Widget 只做分支。
   断点、取整、夹取都在纯函数里，用纯 Dart 单测逐档覆盖，别全靠 widget 测试。

高度方向的统一规则（B 节）：不足时按「压间距 → 减条目(+N) → 隐藏解释文字 → 才允许滚动」
逐级降级；富余时只允许「多显示真实条目」或「数值升 `fontDisplay`」，
**严禁**用装饰元素填空白。

九张卡的具体契约在 D 节，逐张写清。空态/缺数据三种情形不要混用（E 节）。

纪律：不得动 `lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、
`lib/main.dart`、`board.dart` 的 JSON 结构、`pubspec.yaml`、任何 `.cs`；
不得改 `DashboardKind` 的 span 上下限；`ZentwikTokens` 只允许新增 `fontDisplay = 28`；
不新增依赖；一律 `package:material_ui/material_ui.dart`。

验收：`flutter analyze` 无 issue；`flutter test` 全绿且不少于现有 82 例；
`flutter build windows --debug` 通过；实机按 F.5 五条逐项手验并写结论。
验证优先走 GitHub Actions（workflow `Zentwik Next | GUI (Flutter)`），红了改到绿。
