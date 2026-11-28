# 任务 05 —— 卡片内容层、格式化工具与主壳导航

分支 `feat/gui-protocol`。前置已完成并经 Windows 实测：协议层、状态层（`AppState` /
`AgentConnectionState` / `TelemetryState`）、布局层（`DashboardKind` / `DashboardLayout` /
`packIntoRows`）、`theme/`、`card_registry.dart`、`pages/dashboard_page.dart`、
`shell/connection_banner.dart`、`app.dart`。当前 `flutter analyze` 无 issue，`flutter test` 40 例全绿。

## 边界（硬约束，与任务 04 相同）

1. **禁止 import**：`dart:ffi`、`dart:io`、`package:win32`、`package:window_manager`、任何平台插件。
2. **不得修改**：`lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`pubspec.yaml`、`main.dart`、任何 `.cs`。
3. **不得新增依赖**；**不得修改**既有 40 例测试的断言。
4. 风格：UTF-8、中文注释、行宽 80、`flutter_lints ^6.0.0` 零 issue。
   **注意**：Flutter 3.50 起 `package:flutter/material.dart` 已废弃，必须用
   `package:material_ui/material_ui.dart`；`ReorderableListView.onReorder` 已废弃，
   用 `onReorderItem`（内部已处理下标前移，不要再手工 `-1`）。

## 数据边界（重要）

卡片一律**只接收纯数据**，不自己发请求、不持有 `ProtocolClient`。
数据来自 `AppState`（`TelemetryDto` / `AgentStatusDto` / `ModuleDto`），由上层传入。
字段缺失时显示 `—`，**不要用 0 或 0%**（后端约定：读不到就是 null，不是零）。

## 交付内容

### 1. `lib/format/units.dart`（纯函数 + 单测）

- `formatPercent(double? value)` → `null` 时 `—`，否则 `12.3%`（一位小数）。
- `formatCelsius(double? value)` → `—` / `64.5 °C`。
- `formatWatts(double? value)` → `—` / `35.2 W`。
- `formatGigahertz(double? megahertz)` → `—` / `4.45 GHz`（MHz 输入，两位小数）。
- `formatBytes(int? bytes)` → `—` / `15.6 GB`（1024 进制，KB/MB/GB/TB，一位小数）。
- `formatCount(int? value)` → `—` / `312`。
- 所有函数对 `null`、NaN、负值都要有确定行为，测试要覆盖到。

### 2. `lib/cards/` 九张卡片（每张一个文件，均复用 `DashboardCardShell` 之外的纯内容体）

卡片内容体只返回内容，不加外壳（外壳由 `DashboardPage` 提供）：

| wire | 内容 |
|---|---|
| `cpuUsage` | 大号百分比 + 细进度条（`LinearProgressIndicator`），无数据时 `—` |
| `cpuTemperature` | 大号温度 + 阈值色（<70 正常 / 70–85 强调 / ≥85 危险） |
| `packagePower` | 大号功耗值 + 单位说明行 |
| `coreClocks` | 逐核横条：每核一行，核标签 `CCD0-C03`，条宽按 0–FMax(4750 MHz) 归一 |
| `processorUsage` | 逐逻辑处理器小竖条阵列，允许换行，缺数据整体 `—` |
| `memory` | 已用/总量文字 + 进度条，百分比自行计算，总量为 0 或 null 时不除零 |
| `processCount` | 大号进程数 + 「含受保护进程」说明（口径写实，不解释成因） |
| `ledgerSummary` | `ledger.list` 结果条数 + 按 namespace 分组的计数列表，无数据 `—` |
| `moduleStatus` | `modules` 列表：每行模块名 + 状态点（running/stopped/未安装） |

- 每张卡的入参都是 nullable 的纯数据（`TelemetryDto?`、`List<ModuleDto>` 等），
  不需要 `BuildContext` 取状态。
- 视觉只用 `ZentwikTokens`，颜色不散落硬编码；不允许出现 emoji。

### 3. `lib/cards/default_registry.dart`

- `CardRegistry defaultCardRegistry()`：把九种 wire 映射到上面九张卡的内容体
  （回调里解包卡片需要的数据）。数据从哪来由调用方决定：本任务只提供
  `DashboardCardBuilder` 需要的纯数据注入方式，**例如**给卡片体再加一层
  `TelemetryProvider`（`InheritedWidget` 提供 `TelemetryDto?`），不要直接读 `AppState`。

### 4. `lib/shell/shell_page.dart`

- 左侧固定导航（宽 ≥ 900 显示文字+图标，< 900 只显示图标）：仪表盘、进程、场景、
  CPU 调校、电源、模块、设置、关于 —— 共 8 项，未实现的页面显示统一的
  「该页面尚未接入」占位。
- 顶部叠 `ConnectionBanner`（`status` / `attempt` / `onRetry` 由外部传入）。
- 内容区默认渲染 `DashboardPage`，`layout` 与 `onLayoutChanged` 由外部传入。
- `ShellPage` 自身无状态，不持有连接逻辑。

### 5. 测试（`test/`）

- `test/format/units_test.dart`：每个函数的 null / 正常 / 边界值。
- `test/cards/cards_test.dart`：九张卡在**全 null 数据**下都显示 `—` 且不抛异常；
  有数据时关键文案正确（如 `12.3%`、`4.45 GHz`、`15.6 GB`）。
- `test/shell/shell_page_test.dart`：宽屏显示 8 项导航文字；窄屏只显示图标；
  点击第二项后内容区出现「该页面尚未接入」。
- 既有 40 例必须继续全绿。

## 自检

沙箱无 Flutter/Dart SDK 时照实写「未执行」并逐文件静态核对，不要编造尾行。
交付说明给：改动文件清单、新增测试数、未决问题。
