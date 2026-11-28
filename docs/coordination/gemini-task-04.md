# 任务 04 —— Dashboard 页面骨架与主题层（纯 Widget，不碰 FFI / 窗口）

分支 `feat/gui-protocol`。前置已完成：`lib/protocol/**`（协议与客户端）、
`lib/state/**`（`AppState` / `AgentConnectionState` / `TelemetryState`）、
`lib/dashboard/**`（`DashboardKind` / `DashboardLayout` / `packIntoRows`）。
最终目录名以仓库现状为准，不要改名。

## 边界（硬约束）

1. **禁止 import**：`dart:ffi`、`dart:io`、`package:win32`、`package:window_manager`、
   任何平台插件。窗口最小化暂停、托盘、自启由本机负责人实现，你只写 Widget。
2. **不得修改**：`lib/protocol/**`、`lib/platform/**`、`pubspec.yaml`、任何 `.cs`。
   协议字段与 v2 帧格式一律不动；缺字段就按 `docs/protocol/gui-contract.md` 用现有 DTO。
3. **不得新增依赖**。只用 flutter/material + 仓库已有包。
4. 代码风格：UTF-8、中文注释、行宽 80、`flutter_lints: ^6.0.0` 零 issue。
   上一次交付残留 1 条 `prefer_initializing_formals`，本次提交前必须自查干净。

## 交付内容

### 1. `lib/theme/tokens.dart` + `lib/theme/theme.dart`

- `ZentwikTokens`：颜色（背景/面板/边框/主文字/次文字/强调/危险/成功）、间距
  （4/8/12/16/24）、圆角（6/10/14）、字号（标题 20 / 小标题 15 / 正文 13 / 说明 12）。
  暗色优先，不要引入亮色主题的完整实现，但色值要走 token，不散落在 Widget 里。
- `buildZentwikTheme()` 返回 `ThemeData`（`useMaterial3: true`，`ColorScheme.dark` 由
  token 派生，字体回落到系统默认，不打包字体文件）。

### 2. `lib/dashboard/card_registry.dart`

- `typedef DashboardCardBuilder = Widget Function(BuildContext, DashboardCard);`
- `Map<String, DashboardCardBuilder>`，键为 `DashboardKind.wire`。
- 未注册的 wire 回落到 `PlaceholderCard`：显示 `kind.label` + 「尚未接入」，
  高度自适应，不抛异常、不返回空 Widget。

### 3. `lib/pages/dashboard_page.dart`

- 入参 `DashboardLayout layout` + `ValueChanged<DashboardLayout> onLayoutChanged`，
  自身无状态、不改外部状态源。
- 宽度 ≥ 900：按 `packIntoRows` 得出的列位放置卡片，行内可拖拽排序
  （`SliverReorderableList` 或等价实现），拖拽结束回调 `layout.reorder`。
- 宽度 < 900：单列纵向列表，拖拽排序同样可用。
- 卡片外壳 `DashboardCardShell`：统一背景/圆角/内边距/标题行，标题行右侧留
  resize 手柄位（本次只做视觉，`onResize` 可空）。
- 空布局时显示占位提示，不显示空白屏。

### 4. `lib/app.dart` + `lib/shell/connection_banner.dart`

- `ZentwikApp`：`MaterialApp` + `ZentwikTokens` 主题，入参 `AppState`，
  用 `InheritedNotifier<AppState>` 下发（不引入第三方状态库）。
- `ConnectionBanner`：按 `AgentConnectionState.status` 显示
  `connected` 之外的四态文案（`connecting` / `reconnecting` 显示第几次尝试 /
  `failed` 显示可重试操作按钮回调 / `disconnected` 静默）。文案书面客观，
  只描述可核实事实。
- **不要**改 `main.dart` 里的 FFI 初始化逻辑；若需要接线，只加一行 `runApp`，
  并在提交说明里写清楚。

### 5. 测试（`test/`）

- `test/theme_test.dart`：token 取值非空、主题在暗色下 `scaffoldBackgroundColor`
  与 token 一致。
- `test/dashboard/card_registry_test.dart`：未知 wire 回落 `PlaceholderCard`；
  已注册 wire 命中对应 builder。
- `test/pages/dashboard_page_test.dart`：宽屏渲染出全部可见卡片；拖拽回调触发
  `onLayoutChanged` 且顺序符合预期（可用 `WidgetTester.drag` 或直接调用回调）；
  窄屏为单列；空布局显示占位。
- 既有 34 例测试必须全部通过，不许改其断言来迁就实现。

## 自检

沙箱无 Flutter/Dart SDK 时，逐文件静态核对后照实写「未执行」，不要编造尾行。
交付说明必须包含：改动文件清单、新增/修改测试数、未决问题（如有）。
