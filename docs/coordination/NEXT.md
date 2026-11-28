# 给 Gemini 的一句话指令（托盘备着）

GUI 通道断的时候，把下面这段粘给 Gemini 就行，其余内容都在仓库里。

---

任务 05 我方已在 Windows 实测并合并：`flutter analyze` 无 issue，`flutter test` 50 例全绿，
`flutter build windows --release` 通过。我修了三处：

1. `memory_card.dart` 有 **4 个编译错误**（`total > 0` 在 `hasValid` 三元表达式之外不会做类型提升），
   已改成显式 `if (usable)` 分支；顺带无数据时不再画空进度条（会被读成「占用 0%」）、
   不再同一行刷两个「—」。
2. `ModuleStatusCard` 的状态文案原来中英混排（`running`/`stopped`/`未安装`）。
   后端 `ModuleCatalog` 里模块状态只有 `ready`/`degraded`/`unavailable`，
   而且「可选组件未启用」也报 `unavailable`——所以 `unavailable` 不等于「未安装」。
   已统一为「就绪 / 降级 / 不可用 / 未启用」（`enabled == false` 优先判）。
3. 你自己没跑的 `flutter analyze` 又一次是空的——**编译都没过**，务必想办法装 SDK。

另外我这边把应用真正接通了：`lib/runtime/agent_runtime.dart`（连接↔聚合状态）、
`lib/runtime/app_host.dart`（根组件）、`main.dart` 换成真实入口，
`AppState.attach()` 支持重连后切客户端。`lib/runtime/**` 和 `main.dart` 你不要动。

请先 `git pull`，然后做 **任务 06**：

- 读 `docs/coordination/gemini-task-06.md`，实现模块页、设置页、关于页，并接进 `ShellPage`。

纪律：

- **禁止** import `dart:ffi` / `dart:io` / `package:win32` / `package:window_manager` / 任何平台插件。
- **不得修改** `lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、
  `lib/main.dart`、`pubspec.yaml`、任何 `.cs`；不得改既有 50 例测试的断言。
- 一律 `package:material_ui/material_ui.dart`；不新增依赖，不留 TODO 或空实现。
- 数据缺失一律 `—`，不要填 0。

**验证方式已改：不用你在沙箱里装 SDK。** 仓库现在有 GitHub Actions
`.github/workflows/gui.yml`（workflow 名 `Zentwik Next | GUI (Flutter)`），
`feat/gui-protocol` 一推送就会在 GitHub 上跑 `flutter pub get` / `analyze` / `test`。

所以你的循环是：

1. 本地按 `flutter_lints` 规则逐文件核对（能做多少做多少）；
2. 推到 `feat/gui-protocol`；
3. **读这次 push 对应的 Actions 运行结果**（GitHub MCP 或仓库 Actions 页）；
4. 红就按报错改、再推，直到 `Analyze` 与 `Test` 两步都绿；
5. 交付说明里贴 workflow 名字 + 运行结论（绿/红 + 失败步骤），不要写「未执行」。

- `test/manual/pipe_smoke_check.dart` 是我的 Windows 侧文件，已知会卡死，不会被 CI 收集，也不要手动跑。

交付：能推就继续推到 `feat/gui-protocol`，不能推就按文件输出完整内容。
