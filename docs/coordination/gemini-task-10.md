# 任务 10：顶栏收敛、设置页回归、补齐功能页面

> 前置提交：`5bb787e`（主题/强调色/缩放已从界面删除，语言暂挂在顶栏右侧，设置页已不存在）
> 交接方式：本文件即任务书，改完按文末「交付物」提交并写报告。

---

## 0. 一句话目标

顶栏只留「代理状态」，语言回到新的「设置」页且只有简体中文，然后把后端已经实现、
界面还没接的能力补成独立页面。

> 关于「把各个页面加上」这句的口径：按**后端已实现但 UI 未接**的能力补齐，清单见 §3。
> 如果只要其中某几页，删掉 §3 里对应的条目即可，其余要求不变。

---

## 1. 当前状态（动手前先看一遍）

| 文件 | 现状 |
|---|---|
| `src/Zentwik.Desktop/MainForm.cs` | 约 1000 行，一个 `TabControl` 四页：监控 / 方案 / 场景 / 调校与账本 |
| `src/Zentwik.Desktop/ViewModels/MainViewModel.cs` | 1200 行左右，框架无关（属性 + ICommand + ObservableCollection） |
| `src/Zentwik.Contracts/AgentConnection.cs` | 429 行，所有 op 的 C# 客户端已齐全，**不需要新增协议** |
| `src/Zentwik.Localization/` | 三份 JSON（zh-CN / zh-TW / en-US），`SupportedCultures` 目前三种 |
| `tests/Zentwik.Desktop.Tests/MainFormSmokeTests.cs` | 3 例：四页签齐全、无主题入口、切语言重翻页签 |

界面约定（已生效，别推翻）：只用 `Label / Button / TextBox / ComboBox / CheckBox /
DataGridView / GroupBox / TableLayoutPanel / SplitContainer / FlowLayoutPanel`；
不设自定义字体、不设窗口底色、不自绘、不做动画、不引任何第三方 UI 包。

---

## 2. 变更 A：顶栏收敛

顶栏现在有三块：`_statusLabel`（代理状态）、`_sceneLabel`（活动场景）、右侧「语言 + 下拉」。

改造后**只剩代理状态，且靠右**：

```
┌──────────────────────────────────────────────────────────────┐
│                                               已连接代理       │
├──────────────────────────────────────────────────────────────┤
│ [监控] [方案] [场景] [调校与账本] ...                          │
```

要做的：

1. 删掉 `_sceneLabel` 及其在 `BuildStatusBar`、`RegisterTexts`、`ReloadAll`、
   `OnViewModelChanged`（`ActiveSceneText` 分支）里的一切引用。
   `MainViewModel.ActiveSceneText` 属性可以留着不动（`/status` 里还有用），只是不再显示。
2. 删掉语言下拉与 `_languageCaption`（整体挪到设置页，见 §2 变更 B）。
3. `BuildStatusBar` 改成两列 `TableLayoutPanel`：第 0 列 `Percent 100`（空占位，用来把状态顶到右边），
   第 1 列 `AutoSize` 放 `_statusLabel`；`_statusLabel.TextAlign = MiddleRight`。
   **两列都必须写 `ColumnStyles`**（见 §4 陷阱 1）。
4. 删掉监控页「硬件与运行状态」那一行（`_runtimeLabel` / `RuntimeSummary` 的展示）。
   顶栏和监控页都不再出现机器名/权限摘要/Service 这类文字。
   `MainViewModel.RuntimeSummary` 保留（`OnConnectionClosed` 会清它），只是不再绑到控件。

验收：顶栏右侧只有「已连接代理 / 正在连接代理 / 代理未连接」，左侧空白；
监控页第一块分组框是「监控」，里面第一行直接是 `CPU 占用`，没有机器摘要行。

---

## 2 变更 B：设置页回归，语言只有简体中文

1. 恢复第五个页签「设置」（`nav.settings`），页面里**只放一项**：`语言` 标签 + 下拉。
   下拉只有一项 `简体中文`（值 `zh-CN`），选中项固定，不提供其它选项。
   `nav.settings` 这个键在 `5bb787e` 里被删了，三份 JSON 都要加回来
   （`设置 / 設定 / Settings`）。
2. 语言列表收敛到源头，不要只在下拉里过滤：
   `LocalizationCatalog.SupportedCultures` 改为只含 `zh-CN`。
   zh-TW / en-US 两份 JSON 与 `Get(key, culture)` 的显式文化查询保留不动（测试还在用）。
3. `MainViewModel.Languages` 随之只剩一项；`SelectedLanguage` 的 setter 保留
   （agent 的 settings 里可能存着 `en-US`，读回来时会被钳到 zh-CN）。
   `ApplySettings` 里对 language 做一次 `if (!Languages.Contains(value)) value = "zh-CN";`。
4. 顶栏不再有语言下拉，`RebuildLanguageCombo` 挪到设置页的构建流程里。

验收：切到设置页能看到「语言 / 简体中文」，只有一项且能正常显示；
`LocalizationCatalog.SupportedCultures` 长度为 1；界面任何位置都不再出现英文或繁体。

---

## 3. 变更 C：补齐功能页面

后端已有、UI 没接的能力（`AgentConnection` 方法名 → 用途）：

| op 方法 | 用途 | 建议页面 |
|---|---|---|
| `ListModulesAsync` / `EnableModuleAsync` / `DisableModuleAsync` | 模块清单与启停 | 「模块」 |
| `ListPowerSchemesAsync` / `CreatePowerSchemeAsync` / `RenamePowerSchemeAsync` / `DeletePowerSchemeAsync` / `BackupPowerSchemeAsync` / `ListPowerSchemeBackupsAsync` / `RestorePowerSchemeAsync` | 电源方案增删改与备份还原 | 「电源」 |
| `ReadPowerLimitsAsync` / `ApplyPowerLimitsAsync` | 功耗墙（FAST/SLOW/STAPM）读写 | 「电源」页内的分组 |
| `GetDoctorAsync` | 环境自检 | 「诊断」 |
| `GetLogAsync(limit)` | 代理日志尾部 | 「日志」 |
| `GetTelemetryHistoryAsync(limit)` | 历史采样（折线可后置，先出表格） | 「监控」页内，或独立页 |

要求：

- 每页形态与现有页保持一致：`DataGridView` 或分组框 + 一行按钮，按钮绑定 `ICommand`。
- **不要**为了这些页面新增协议字段、改 DTO、改 Worker/领域层。DTO 已存在，直接读。
- 写操作（启停模块、应用功耗墙、删方案、还原备份）必须先有确认步骤：
  简单的 `MessageBox.Show(..., MessageBoxButtons.OKCancel)` 即可，不做自定义弹窗。
- 每页都要有：刷新入口、失败时把错误写进现有顶部提示条（`ShowNotice(..., isError: true)`），
  不允许静默失败。
- 新增文案全部走 `_catalog["..."]`，三份 JSON 同步补键；键名沿用现有风格
  （`modules.title`、`power.title`、`power.limits`、`doctor.title`、`log.title` …）。
- 页签顺序：监控 → 方案 → 场景 → 调校与账本 → 模块 → 电源 → 日志 → 诊断 → 设置。
  如果某一页数据源暂时不可用（如 Intel MSR 在 AMD 机器上），页面照常显示、表格为空、
  状态列写 `unavailable`，不要隐藏整页。

验收：九个页签都能打开，任何一页都不会因为 op 失败而抛未捕获异常；
`unavailable` 的模块/能力在页面上如实显示，不崩。

---

## 4. 四个已经踩过的坑（务必照着做）

**1）`TableLayoutPanel` 必须写 `ColumnStyles`。**
不写时隐式列按 AutoSize 算，子控件被内容撑得比窗口宽，右边整块被裁掉。
这一版所有 `ColumnCount = 1` 的根表都已经补了 `ColumnStyle(Percent, 100)`，新增的一律照抄。
行同理，`RowCount` 有几个就补几条 `RowStyles`。

**2）`Form.AutoScaleMode` / `AutoScaleDimensions` 不能留默认。**
默认 `Font` 模式会按字体度量再缩一遍：去掉自定义字体后，1120×760 的客户区被缩到 817×582。
现在固定写死：
```csharp
AutoScaleMode = AutoScaleMode.Dpi;
AutoScaleDimensions = new SizeF(96F, 96F);
```

**3）`MainViewModel` 必须在 `WindowsFormsSynchronizationContext` 装好之后再构造。**
`Program.Main` 里顺序不能变：
```csharp
ApplicationConfiguration.Initialize();
SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
var viewModel = new MainViewModel(catalog);
```
否则 `_ui.Post` 落到线程池，回调里改控件直接抛跨线程异常。

**4）重填列表时必须置 `_syncing`。**
`DataGridView.Rows.Clear()` + 逐行 `Add` 期间会触发 `SelectionChanged`，
不挡会把视图模型的 `SelectedX` 刷成 null，接着 `CanExecute` 全变 false，按钮灰掉。
新增的表格照抄现有 `ReloadXxx()` 的写法：`_syncing = true; try { ... } finally { _syncing = false; }`。

另外两条老规矩：`ICommand.CanExecute` 只能靠 `CanExecuteChanged → BeginInvoke(RefreshCommandStates)`
手动刷 `button.Enabled`；`PropertyChanged` 要在 `InvokeRequired` 时 `BeginInvoke` 回 UI 线程。

---

## 5. 明确不做的事

- 不加主题、不加强调色、不加界面缩放、不加自绘/动画/图标字体/第三方 UI 包。
- 不改 `Zentwik.Agent`、`Zentwik.Domain`、`Zentwik.Contracts` 的任何行为。
- 不删现有四页的任何功能入口，不把 `DataGridView` 换成自绘控件。
- 不写 Markdown 报告以外的文档，不改 README 的构建命令。

---

## 6. 交付物

1. 一个提交（或按 A/B/C 拆三个），提交信息说明删了什么、加了哪些页。
2. `dotnet build Zentwik.slnx` 0 警告 0 错误。
3. `dotnet test` 全绿；`MainFormSmokeTests` 的页签数量断言要跟着改成新页数，
   「只有一项语言」也要加一条断言。
4. 报告六节：改了什么 / 新增页面截图或控件树证据 / 测试结果 / 未完成项 / 已知缺陷 / 有没有动到不该动的地方。

---

## 7. 参考：现有页面的写法

以「方案」页为例，页面结构是：

```
TableLayoutPanel root（写死 ColumnStyles=Percent100）
├── row0 Percent100 : DataGridView（ReadOnly、FullRowSelect、AutoSizeColumnsMode.Fill）
├── row1 AutoSize    : FlowLayoutPanel（应用 / 回退 / 删除 / 新建按钮）
└── row2 AutoSize    : Panel _planForm（Visible 由 IsCreatePlanOpen 控制）
```

按钮一律 `Bind(button, vm.XxxCommand)`；`button.Click` 里只调 `command.Execute(null)`。
表格行挂 `row.Tag = 行对象`，`SelectionChanged` 里 `vm.SelectedX = grid.CurrentRow?.Tag as XxxRow`。
