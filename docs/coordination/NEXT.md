# 给 Gemini 的一句话指令（托盘备着）

GUI 通道断的时候，把下面这段粘给 Gemini 就行，其余内容都在仓库里。

---

任务 04 我方已在 Windows 实测并合并：`flutter analyze` 无 issue，`flutter test` 40 例全绿。
你留的两处都修了：① 宽屏用例用 `MediaQuery` 改尺寸影响不到 `LayoutBuilder` 的实际约束，
改成 `tester.binding.setSurfaceSize`，拖拽用例一次通过（你未决问题 1 已确认可行）；
② 全部 `package:flutter/material.dart` 换成 `package:material_ui/material_ui.dart`
（Flutter 3.50 已把 material 拆包，原写法是 deprecated info），`onReorder` 换成
`onReorderItem`。另外 `PlaceholderCard` 里重复渲染了 `card.kind.label`，
与外壳标题撞名，已改为只显示「尚未接入」。

请先 `git pull`，然后做 **任务 05**：

- 读 `docs/coordination/gemini-task-05.md`，按边界与清单实现 `lib/format/units.dart`、
  `lib/cards/**`（九张卡）、`default_registry.dart`、`lib/shell/shell_page.dart` 与对应测试。

纪律：

- **禁止** import `dart:ffi` / `dart:io` / `package:win32` / `package:window_manager` / 任何平台插件。
- **不得修改** `lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`pubspec.yaml`、`main.dart`、任何 `.cs`；
  不得改既有 40 例测试的断言。
- 一律 `package:material_ui/material_ui.dart`；不新增依赖，不留 TODO 或空实现。
- 数据缺失一律显示 `—`，**不要填 0**（后端读不到就是 null，不是零）。
- 每轮必须自己跑 `flutter analyze && flutter test` 并贴原始尾行；沙箱确实没有 SDK 时，
  照实写「未执行」并逐文件静态核对，不要编造输出。
- `test/manual/pipe_smoke_check.dart` 是我的 Windows 侧文件，已知会卡死，不要跑它。

交付：能推就继续推到 `feat/gui-protocol`，不能推就按文件输出完整内容。
