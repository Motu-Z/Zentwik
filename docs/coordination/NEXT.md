# 当前状态与下一步

## 2026-11-28：UI 路线回退到裸控件

Flutter 外壳（`src/Zentwik.Gui`，约 8000 行 Dart + 925 MB 构建残留）已整体删除，
连同 `.github/workflows/gui.yml`。桌面层改为 `src/Zentwik.Desktop` 的 **WinForms 裸控件**。

原因不再重复记录，只有结论：桌面端要先把功能跑通，视觉留到功能齐了再说。

**`docs/coordination/gemini-task-01..09.md` 全部作废。** 它们描述的是已删除的 Dart 代码，
不要再按那些任务书派活。

## 现在的桌面层长什么样

| 文件 | 内容 |
|---|---|
| `src/Zentwik.Desktop/Program.cs` | 入口。装配 `LocalizationCatalog` + `ThemeService` + `MainViewModel`，`Application.Run(MainForm)` |
| `src/Zentwik.Desktop/MainForm.cs` | 唯一的视图。一个 `TabControl` 五页，只用 Label / Button / DataGridView / ComboBox / TextBox |
| `ViewModels/MainViewModel.cs` | **未改动**。1206 行，框架无关，属性 + 命令 + ObservableCollection |
| `Contracts/AgentConnection.cs` | **未改动**。429 行 C# 管道客户端，覆盖全部 op |
| `Services/ThemeService.cs` | 重写为不依赖 WPF。只提供深/浅两套系统色 + 强调色，不做控件级换肤 |

绑定方式：`MainViewModel.PropertyChanged` → `MainForm.OnViewModelChanged` 里逐个属性刷控件；
`ICommand.CanExecute` → 按钮 `Enabled`；`ObservableCollection.CollectionChanged` → 整表重填。

## 五个页签与对应能力

| 页签 | 数据源 | 命令 |
|---|---|---|
| 监控 | `Capabilities` / `CpuText` / `MemoryText` / `ProcessText` / `CoreText` | `RefreshCommand` |
| 方案 | `Plans` | 应用 / 回退 / 删除 / 新建 |
| 场景 | `Scenes` | 激活 / 停用 / 启用停用 / 删除 / 新建 |
| 调校与账本 | `CoreOffsets` / `LedgerEntries` / `TuningSummary` / `IntelTuningSummary` | 刷新 / 释放接管 |
| ~~设置~~ | 5bb787e 已整页删除，语言暂挂顶栏右侧 | — |

## 测试

`tests/Zentwik.Desktop.Tests/MainFormSmokeTests.cs`（3 例，全绿）：
窗口能在 STA 线程建出来、四个页签齐全、所有按钮有文案、五个表格都有列、
没有主题/强调色/缩放入口（设置页已删）、切语言后页签标题真的变了。

## 下一步（按优先级）

0. **任务 10 已交 Gemini**：见 `gemini-task-10.md` —— 顶栏只留代理状态（靠右）、
   活动场景与硬件摘要全删、设置页回归且语言只留简体中文、按后端已实现的能力补
   模块 / 电源 / 日志 / 诊断等页面。接手前先读该文件第 4 节的四个坑。
1. **实机逐页手验**：四个页签在真代理上跑一遍，记录哪条 op 报错。
2. **补齐 `MainViewModel` 未暴露的能力**：`AgentConnection` 里有但 VM 没接的 op 先列清单
   （`CreatePowerSchemeAsync` / `RenamePowerSchemeAsync` / `DisableModuleAsync` 等），
   再决定哪些进 UI。
3. **监控页折线**：`MainViewModel.HistoryChanged` 事件已经在了，视图现在没用。
   裸控件做法就是 `System.Windows.Forms.DataVisualization.Charting` 或自绘 `Panel`，先不着急。
4. ~~深色主题~~：已决定不做。界面全部用系统默认控件与系统色，不再有主题/强调色/缩放。

## 纪律

- 不再引入 Flutter / Dart。桌面一律 .NET WinForms。
- 新增 UI 一律先问「这是不是必须现在做」，UI 复杂度不作为交付理由。
- 跨进程契约只走 `Zentwik.Contracts`，UI 不自己拼 JSON。
