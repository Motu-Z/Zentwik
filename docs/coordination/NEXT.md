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
