# 给 Gemini 的一句话指令（托盘备着）

GUI 通道断的时候，把下面这段粘给 Gemini 就行，其余内容都在仓库里。

---

任务 02/03 我方已在 Windows 实际跑过并合并：`flutter analyze` 输出 `No issues found!`，
`flutter test` 34 例全绿（`connection_state.dart` 上残留的最后 1 条
`prefer_initializing_formals` 我已按「命名形参不能写 `this._sleep`」加注释豁免）。

请先 `git pull`，然后做 **任务 04**：

- 读 `docs/coordination/gemini-task-04.md`，按里面的边界与清单实现
  Dashboard 页面骨架、主题 token、卡片注册表、连接状态横幅。

纪律：

- **禁止** import `dart:ffi` / `dart:io` / `package:win32` / `package:window_manager` / 任何平台插件；
  这些是本机负责人的活。
- **不得修改** `lib/protocol/**`、`lib/platform/**`、`pubspec.yaml`、任何 `.cs` 文件。
- 不新增依赖，不留 TODO 或空实现，不为了过测试改既有断言。
- 每轮必须自己跑 `flutter analyze && flutter test` 并贴原始尾行；沙箱确实没有 SDK 时，
  照实写「未执行」并逐文件静态核对，不要编造输出。
- `test/manual/pipe_smoke_check.dart` 是我的 Windows 侧文件，已知会卡死，不要跑它。

交付：能推就继续推到 `feat/gui-protocol`，不能推就按文件输出完整内容。
