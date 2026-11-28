# 任务 07：设置页可编辑化、仪表盘卡片编辑、卡片空态统一

本轮只做 `lib/pages/**`、`lib/cards/**`、`lib/dashboard/**`、`lib/widgets/**`（新建）与对应测试。

**不得修改**：`lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、
`lib/main.dart`、`pubspec.yaml`、任何 `.cs`；不得改既有 66 例测试的断言。
一律 `package:material_ui/material_ui.dart`；不新增依赖；数据缺失一律 `—`，不要填 0。

`lib/protocol/models/settings_dto.dart` 里的 `SettingsPatch` 已经支持全部六个字段，
`lib/dashboard/dashboard_layout.dart` 已经有 `resize` / `setVisible` / `remove` / `add`，
`lib/pages/settings_page.dart` 的 `onChange` 已经接到 `settings.set`（`AppState.saveSettings`）。
本轮是把这些能力用起来，不需要新增协议方法。

---

## A. 设置页三个枚举字段改为可编辑

后端以 `src/Zentwik.Domain/Settings/UserSettings.cs` 为准，取值是**固定枚举**，不是自由输入：

| 字段 | 合法值 | 控件 |
|---|---|---|
| `language` | `zh-CN` / `zh-TW` / `en-US` | 下拉，显示「简体中文 / 繁體中文 / English」 |
| `theme` | `dark` / `light` | 下拉 |
| `accent` | `blue` / `violet` / `teal` / `amber` | 下拉，显示中文色名 |
| `uiScale` | `0.8` – `1.6`，两位小数 | 滑杆，`divisions` 覆盖 0.05 步长 |

要点：

1. **`accent` 不是十六进制颜色。** 现有 `test/pages/settings_page_test.dart` 里
   `accent: '#4C8DFF'` 是错的夹具，顺手改成 `'blue'`，别让它被后来人当成样例抄走。
2. 后端 `UserSettingsRules.TryApply` 对非法值是**整条拒绝**（`payload_invalid` +
   中文 `detail`），不会静默修正；返回的是**整份**设置，不是增量。
   保存失败时 `AppState` 会把错误写进 `errorMessage`，Shell 顶部已有红色错误条会显示它——
   页面**不要**在失败后假装成功（不要把控件值改成用户提交的值），等下一轮 `settings`
   快照刷新再回显。
3. `readOnly == true` 时全部控件禁用（现有两个输入框已这样做，保持一致）。
4. 滑杆拖动过程中不要每帧发 `settings.set`：`onChangeEnd` 才提交。

## B. 仪表盘卡片可调宽度、可隐藏

`ShellPage` 已有 `onResizeCard` 参数（当前传 `null`），`DashboardLayout` 已有
`resize(card, span)` / `setVisible(card, visible)`，`DashboardCard` 带
`kind.defaultSpan` / `minSpan` / `maxSpan`。

要做的：

1. `DashboardCardShell` 右上那个 `onResize` 手柄真正生效：点击在
   `minSpan…maxSpan` 之间循环（到顶回到 `minSpan`），通过 `onLayoutChanged` 抛出去。
2. 每张卡加一个「隐藏」入口（手柄旁边一个 `Icons.visibility_off_outlined` 小按钮），
   点击后 `setVisible(card, false)` 并抛 `onLayoutChanged`。
3. 仪表盘页顶部加一行「已隐藏的卡片」：列出 `visible == false` 的卡片名，
   点一下恢复（`setVisible(card, true)`）。一个都没有时这一行整体不渲染。
4. 窄屏（`< 900`）单列时宽度无意义：**不显示 resize 手柄**，但保留隐藏入口。

## C. 九张卡空态统一

新建 `lib/widgets/card_empty.dart`，导出 `CardEmptyHint(message)`：一行 `textTertiary`
说明文字，居中或左对齐都行，但九张卡必须长得一样。

规则：

- 卡片数据为 `null`（后端读不到）→ 整张卡只显示 `CardEmptyHint('暂无数据')`，
  **不许**画 0 值、空进度条、空柱状图。空条会被读成「占用 0%」，这是在撒谎。
- 卡片有数据但字段为 `null`（比如有温度没有功耗）→ 该字段位置显示 `—`，卡片正常渲染。
- `ModuleStatusCard` 的模块列表为 `null` 与空列表要区分：`null` 是「拿不到」，
  空列表是「后端说没有模块」。前者 `CardEmptyHint('暂无数据')`，后者 `CardEmptyHint('无模块')`。

---

## 验证

推 `feat/gui-protocol`，然后读这次 push 的 GitHub Actions（workflow
`Zentwik Next | GUI (Flutter)`）：`Analyze` 与 `Test` 必须都绿。
交付说明写 workflow 名 + 结论，不要写「未执行」。

新增测试至少覆盖：

- 设置页：三个下拉的选项集合、缩放滑杆范围、`readOnly` 全禁用、`accent` 非法值不再出现。
- 仪表盘：resize 循环在 `minSpan…maxSpan` 内、隐藏后从可见列表消失且出现在「已隐藏」行、恢复后回来。
- 卡片：任一卡 `telemetry == null` 时只出现 `CardEmptyHint`，不出现 `LinearProgressIndicator`。
