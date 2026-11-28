# 给 Gemini 的一句话指令（托盘备着）

GUI 通道断的时候，把下面这段粘给 Gemini 就行，其余内容都在仓库里。

---

任务 06 我方已复核：`flutter analyze` 无 issue，`flutter test` 59 例全绿，你的 CI 结论也对得上
——这次没有编译错误，方向没问题。

但三个页面在**真实应用里是空的**：你给 `ShellPage` 加了参数，`lib/runtime/app_host.dart`
没有人传值，于是 `modules` / `settings` / `agentStatus` 全是 `null`，界面只剩「—」。
我把这层接线补上了（`AppState` 现在缓存 `settings` 与 `ledger`，新增 `setModuleEnabled` /
`saveSettings` / `errorMessage`；`app_host` 全部注入；Shell 顶部加了错误条），
并修了三处你按现有信息判断不了的问题：

1. **内置模块的开关按了必然失败。** 后端 `ModuleService.RequireOptional` 对非 `optional`
   模块直接抛 `module_not_optional`；`ModuleCatalog` 里只有 `power-limits` 是可选的。
   六张内置卡上的开关等于骗用户。已改为：非 `optional` 显示「内置」，不给开关；
   `onToggle` 回调签名改成 `(String id, bool enable)`。
2. **模块页不显示 `detail`。** 用户问「为什么不可用」的答案就在这个字段
   （「可选组件，未启用。」/「AMDFamily17 未加载。」）。已补一行 `textTertiary` 说明。
   「可选但未启用」也从「不可用」改成「未启用」——两者原因不同。
3. **设置页只有按回车才提交。** 桌面用户输完数字通常直接点别处，改动静默丢失。
   改成回车 + 失焦 + 销毁三条路径都提交，并用 `_sent*` 去重，避免重复 `settings.set`。

现在 66 例全绿，`flutter build windows --release` 通过。请先 `git pull`，然后做 **任务 07**：

- 读 `docs/coordination/gemini-task-07.md`：设置页三个枚举字段可编辑化（注意 `accent`
  是 `blue/violet/teal/amber`，**不是**十六进制颜色）、仪表盘卡片调宽与隐藏、
  九张卡空态统一。

纪律（和上一轮一致，不再重复解释）：

- **禁止** import `dart:ffi` / `dart:io` / `package:win32` / `package:window_manager`。
- **不得修改** `lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、
  `lib/main.dart`、`pubspec.yaml`、任何 `.cs`；不得改既有 66 例测试的断言。
- 一律 `package:material_ui/material_ui.dart`；不新增依赖；数据缺失一律 `—`。
- 验证走 GitHub Actions（workflow `Zentwik Next | GUI (Flutter)`）：推完读这次 push 的
  运行结果，`Analyze` 与 `Test` 都绿才算交付，红了改到绿。别写「未执行」。

交付：能推就继续推到 `feat/gui-protocol`，不能推就按文件输出完整内容。
