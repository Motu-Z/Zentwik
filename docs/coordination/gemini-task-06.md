# 任务 06 —— 模块页、设置页、关于页（纯 Widget，数据靠注入）

分支 `feat/gui-protocol`。当前状态：`flutter analyze` 无 issue，`flutter test` 50 例全绿，
`flutter build windows --release` 通过。应用已能启动（`main.dart` → `ZentwikRoot` →
`ShellPage` → `DashboardPage`），数据来自真实服务。

## 边界（硬约束）

1. **禁止 import**：`dart:ffi`、`dart:io`、`package:win32`、`package:window_manager`、任何平台插件。
2. **不得修改**：`lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、
   `lib/main.dart`、`pubspec.yaml`、任何 `.cs`。
3. 不得新增依赖；不得改既有 50 例测试的断言（可新增测试）。
4. 一律 `package:material_ui/material_ui.dart`；行宽 80；中文注释；零 analyze issue。
5. 页面对外只接收**回调 + 纯数据**，不自己发请求、不持有 `ProtocolClient`。

## 1. `lib/pages/modules_page.dart`

- 入参：`List<ModuleDto>? modules`、`ValueChanged<String>? onToggle`。
- 每个模块一行：名称（`ModuleDto.name` 优先，回落 `id`）、状态点 + 状态文案
  （复用 `ModuleStatusCard` 的映射口径：`ready` 就绪 / `degraded` 降级 /
  `unavailable` 不可用；`enabled == false` 显示「未启用」）、右侧开关或按钮。
- 交互边界：**本页不发请求**。点击只回调模块 id，由上层决定启用还是停用；
  回调期间（外部传入 `busyModuleId`）该行显示进度指示并禁用点击。
- `modules == null` 时显示「—」占位，`isEmpty` 时显示「无模块」。

## 2. `lib/pages/settings_page.dart`

- 入参：`SettingsDto? settings`、`ValueChanged<SettingsPatch>? onChange`、
  `bool readOnly = false`。
- 至少呈现：遥测推送间隔、CPU 传感器间隔（两个数值输入，带单位 ms）、
  其余已有字段按 `SettingsDto` 实际字段逐项列出，**字段名以
  `lib/protocol/models/settings_dto.dart` 为准**，不要臆造字段。
- 数值输入需限幅：推送间隔 500–5000 ms、传感器间隔 500–30000 ms（与后端一致）；
  越界时回显夹取后的值并给出书面提示，不要静默丢弃。
- `settings == null` 时整页显示「—」；`readOnly` 时输入禁用。

## 3. `lib/pages/about_page.dart`

- 入参：`AgentStatusDto? status`。
- 显示：代理版本（`status.version`）、启动时间或运行时长（有则显示，无则 `—`）、
  管道名 `zentwik-agent`（常量文案）、当前连接状态文案。
- 文案书面客观，不写「极速」「智能」这类形容词。

## 4. `lib/shell/shell_page.dart` 接线

- 只改一处：把 `selectedIndex` 为 5（模块）、6（设置）、7（关于）时从「该页面尚未接入」
  换成上面三个页面；入参沿用现有 `ShellPage` 的可选参数（新增 optional 字段，
  保持现有测试不破）。
- 未接入的 1–4（进程/场景/CPU 调校/电源）保持不变。

## 5. 测试

- `test/pages/modules_page_test.dart`：全 null 显示 `—`；点击回调带上正确 id；
  `busyModuleId` 命中的行禁用点击且显示进度指示；三种状态文案正确。
- `test/pages/settings_page_test.dart`：越界输入被夹取并有提示；`readOnly` 时输入禁用；
  `settings == null` 显示 `—`。
- `test/pages/about_page_test.dart`：有/无 `AgentStatusDto` 两种情况。
- `test/shell/shell_page_test.dart` 追加一例：点第 6 项出现设置页标题而不是「尚未接入」。
- 既有 50 例必须继续全绿。

## 自检

沙箱无 SDK 时照实写「未执行」并逐文件静态核对，不要编造尾行。
交付说明给：改动文件清单、新增测试数、未决问题。
