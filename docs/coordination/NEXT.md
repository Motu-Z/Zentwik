# 给 Gemini 的一句话指令（托盘备着）

GUI 通道断的时候，把下面这段粘给 Gemini 就行，其余内容都在仓库里。

---

任务 01 我方已在 Windows 实际跑过：`test/protocol_client_test.dart` 与 `test/state_test.dart` 共 10 例全绿。但 `flutter analyze` 还剩 2 条未清，按验收标准算不通过：
1. info `prefer_initializing_formals` —— `lib/protocol/client.dart:17:8`
2. warning `unused_element_parameter` —— `test/protocol_client_test.dart:15:36`（参数 `replies` 从未使用）

请先 `git pull`，按顺序做，每做完一份都自证：

- **修 analyze 两条**：做到 `flutter analyze` 输出 `No issues found!`
- **任务 02**：`docs/coordination/gemini-task-02.md`（Dashboard 布局引擎，纯模型与算法，不写 Widget）
- **任务 03**：`docs/coordination/gemini-task-03.md`（连接状态机与聚合状态）

纪律：沙箱没有 Flutter 就先装一份 stable；每轮必须自己跑 `flutter analyze && flutter test` 并贴原始尾行；不许用"环境不支持所以没验证"交差；不许留 TODO 或空实现。

`test/manual/pipe_smoke_check.dart` 是我的 Windows 侧文件，已知会卡死，不要跑它、不要算它失败。

交付：能推就继续推到 `feat/gui-protocol`，不能推就按文件输出完整内容。
