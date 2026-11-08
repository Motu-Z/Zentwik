# Zentwik 调校页 (TuningPage) 接口契约

> **状态说明（批次 A-2 后）**：本文件描述的 UI 契约已部分失效。
> `ProcessorPowerPolicy` 和 `ProcessorPowerTuning` 已在批次 A-2 中移除，
> 相关的电源策略、升频模式、核心停放契约条款不再有效。完整的 UI 契约将在
> 批次四（Zentwik.UI 重写）完成时整份重新定义。本文件当前仅作历史参考。

> **当前契约版本：`v1.6`**（2026-11-01，人类批准）。历史版本见下方「契约版本记录」。
>
> **⚠️ 强制规范**：本文档是前端 (XAML) 与后端 (ViewModel) 通信的唯一法定契约。
> 任何 AI 或开发者不得擅自修改、重命名本文档中定义的属性或命令；如需修改，请提前与人类商讨并告知另一个 AI。
>
> **契约基线**：`v2.0.0-can15`（commit `3f928f7`，分支 `main`）。
>
> **契约版本记录**：
> - **v1.6（2026-11-01，人类批准）**：新增 **2 个只读展示属性** `CpuTitle` / `CpuDetail`（详见第 1 节）—— 把后端一整串 `CpuName` 拆成「型号」与「规格 · SMU 状态」两段，供 CPU 卡片主副标题分行显示。其余属性 / 命令 / 集合定义与 v1.5 完全一致。
> - **v1.5（2026-11-01，人类批准）**：**UI 红线入典**。① 核心网格强制结构由 `ItemsRepeater` + `UniformGridLayout` 改为 **`ListView` + `ItemsWrapGrid`**（属性名 `ListView.ItemsPanel`，类型仍是 `ItemsPanelTemplate`；详见第 3 节 API 事实）。② 数值输入**禁用 `<ControlTemplate>` 重写 `TextBox`**，隐式加减号一律用 `Grid` 叠加「纯 `TextBox` + 左右两个 `Button`」，命令**直接** `Command=` 绑定，**废除** `TextBox.Tag` / `Header` 模板穿透。③ 加减号**常驻显示**（不再悬浮 / 聚焦淡入）。④ 全页**禁用 `TabView`**。**属性 / 命令定义、参数、步长、钳制范围、绑定语义一律未变** —— 后端 `.cs` **无需任何改动**，本次纯前端契约条目修订。
> - **v1.4（2026-11-01，人类批准）**：补全**步进命令**（4 个成员 / 6 处绑定）：`CoreSlotViewModel.IncrementCommand` / `DecrementCommand`（CO ±1）与 `TuningViewModel.IncrementCommand` / `DecrementCommand`（`IRelayCommand<string>`，参数 `"FMax"` / `"Parking"`）。步进**必须置脏**（不经过 `TextChanged`，脏状态不会自动产生）。前端 XAML 无需改动，详见 4.3。
> - **v1.3（2026-11-01，人类批准）**：`RefreshCommand` 升级为**全量同步** —— 一次读回 SMU 核心状态 **+** 电源策略（计划 / 升频 / 停放），并按「读到的真值覆盖草稿」处置界面状态（详见 4.2）。命令名称 / 来源方法 / 绑定方式全部未变，**前端 XAML 无需改动**；是否在同步前弹确认框由前端自行决定（判据与配方见 4.2）。
> - **v1.2（2026-11-01，人类批准）**：电源策略防抖收敛到 ViewModel —— 滑杆变更停手 700ms 自动执行、下拉立即执行。**`ApplyPowerCommand` 本身不变**（名称 / 来源方法 / 行为 / 绑定方式全部照旧），新增 View 层可调用的非绑定方法 `SchedulePowerApply()`（详见 4.1）。**前端无需改动 XAML**：既有事件处理器名称与签名保持不变。v1.1 立项的 `IsCoreParkingSupported` / `IsFMaxSupported` 两个硬闸门属性**均已实现**。
> - **v1.1（2026-11-01，人类批准）**：新增 `IsCoreParkingSupported` / `IsFMaxSupported` 两个能力闸门属性（详见 2.2 / 2.3），**由后端 AI 补全实现**（v1.1 当时代码尚无这两个属性，v1.2 起均已实现）。其余属性、命令、集合定义与 v1.0 完全一致，前端已有绑定不受影响。
> - **v1.0**：基于 `v2.0.0-can15` 真实代码反向生成。
> **反向生成来源**（已逐行核对，非推测）：
> - `Zentwik.App/ViewModels/TuningViewModel.cs`
> - `Zentwik.App/ViewModels/CoreSlotViewModel.cs`
> - `Zentwik.App/ViewModels/UiSeverity.cs` / `Zentwik.App/UiConverters.cs`
> - `Zentwik.App/Views/TuningPage.xaml` / `TuningPage.xaml.cs`
> - `Zentwik.Core/Services/ISmuReader.cs` / `SmuReader.cs`（SmuSnapshot 等数据结构）
> - `Zentwik.Core/Helpers/TuningStore.cs` / `Zentwik.Core/Services/ProfileService.cs`

---

## 0. 页面装配方式（绑定前必须知道）

| 项 | 真实情况 |
| :--- | :--- |
| ViewModel 构造 | `TuningViewModel(ISmuReader smu, ITuningStore tuning, ProfileService profiles, ILogService log, IUiDispatcher dispatcher)` —— **全部构造函数注入 Core 服务**，UI 层禁止自行 new |
| 页面构造 | `TuningPage(TuningViewModel viewModel)` —— 外壳显式 new 并注入，**不走** `Frame.Navigate(typeof(T))` |
| 初始化入口 | `await ViewModel.InitializeAsync()`，由页面 `Loaded` 事件调用，**只执行一次**（切页回来不重读 SMU） |
| 绑定模式 | 全量 `{x:Bind}` 编译期绑定；默认 `Mode=OneWay`，输入控件显式 `Mode=TwoWay` |
| 线程模型 | SMU 访问在 ViewModel 内 `Task.Run` 到线程池，`await` 后续体回到 UI 线程；UI 层不做额外封送 |

---

## 1. 基础信息 (Static/Dynamic Info)

| 属性名 | 类型 | 默认值 | 说明 |
| :--- | :--- | :--- | :--- |
| `CpuName` | `string` | `"未探测"` | 如 "AMD Ryzen 9 9955HX"，由 SMU 快照回填。**实际回填值可能把规格缀在型号后面**（"AMD Ryzen 9 9955HX 16-Core Processor"），直接显示会被截断 —— 展示请用下面两个派生属性。 |
| `CpuTitle` | `string` | `"未探测"` | **v1.6 新增（只读派生）**。CPU 主标题：纯型号，如 "AMD Ryzen 9 9955HX"。由 `CpuName` 剥掉 "16-Core Processor" 这类规格后缀得到；`CpuName` 变化时自动刷新。 |
| `CpuDetail` | `string` | `"未连接"` | **v1.6 新增（只读派生）**。CPU 副标题：规格 + SMU 状态，如 "16-Core Processor · SMU 就绪"。SMU 异常时后半段是错误全文；无规格时只显示 SMU 状态。随 `CpuName` / `SmuStateText` 自动刷新。 |
| `CpuSubText` | `string` | `"等待同步 SMU"` | 如 "Raphael · SMU SMU 90.6.0"；SMU 未响应时为 "SMU 未响应"。**v1.6 起不再直接上屏**（信息让位给 `CpuDetail`），降级为副标题的悬浮提示。 |
| `TopologyText` | `string` | `"—"` | 如 "2 CCD × 8 核 · 16 物理 / 32 线程"。 |
| `CoreSummary` | `string` | `"0 核"` | 汇总行：`"{N} 核 · 与硬件一致"` 或 `"{N} 核 · {dirty} 核待下发"`。 |
| `SmuStateText` | `string` | `"未连接"` | SMU 状态行文案："SMU 就绪" / 错误全文。 |
| `SmuAvailable` | `bool` | `false` | 最近一次 SMU 读取是否成功。**读取失败的可视化靠它 + 提示条，没有 `IsSyncFailed` 属性**（见 2.6）。 |

> **🚨 核心总数没有独立 int 属性。** `MaxCores` / `CoreCount` 在 ViewModel 中均**不存在**（`_coreCount` 是 private）。
> 物理核心总数 = `Cores.Count`（见第 3 节）。**前端严禁写死 16**——8 核机生成 8 格，16 核机生成 16 格，全部由集合驱动。

---

## 2. 状态属性 (State Properties)

### 2.1 脏数据 / 忙碌 / 提示条

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `IsDraftDirty` | `bool` | `false` | OneWay | 草稿与基准不一致。**当前唯一绑定用途**：方案下拉框右上角 `*` 角标显隐（`Visibility` 经 `UiConverters.ToVisibleWhenTrue` 转换）。 |
| `IsBusy` | `bool` | `false` | OneWay | 写入/读取中。为 `true` 时遮罩吞指针事件、禁用全部交互；同时令 5 个命令 `CanExecute=false`。 |
| `BusyText` | `string` | `"正在与 SMU 通信…"` | OneWay | 遮罩中央文案，随任务变化（如 "正在写入核心 3…"）。 |
| `IsStatusVisible` | `bool` | `false` | TwoWay | InfoBar 开关。**注意：后端只在 Error / Warning 时自动置 true**；Success / Info 不弹条。 |
| `StatusTitle` | `string` | `""` | OneWay | 提示条标题（如 "CO 下发失败"）。 |
| `StatusMessage` | `string` | `""` | OneWay | 提示条正文（失败明细为人话，含失败核号）。 |
| `StatusLevel` | `UiSeverity` | `Info` | OneWay | `Info / Success / Warning / Error` 四值；XAML 侧经 `UiConverters.ToInfoBarSeverity` 转 `InfoBarSeverity`。 |

> **脏数据浮动保存条的显隐不由 `IsDraftDirty` 绑定驱动**：它由 View 层 `MarkDirty() → ShowDirtyBar()`（180ms 淡入上移动画）与命令执行后 `HideDirtyBar()` 控制。契约上 View 必须：任何用户输入路径最终调用一次 `ViewModel.MarkDirty()`（见 5.2）。

### 2.2 FMax（最高加速频率）

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `FMaxText` | `string` | `"5400"` | TwoWay | 纯 TextBox 入口。**容错解析**：空串 / 非整数 / 负数直接忽略，不抛异常；合法值写入 `FMaxValue`。 |
| `FMaxValue` | `int` | `5400` | OneWay | 解析后的整型值（MHz）。 |
| `FMaxHint` | `string` | `"未同步"` | OneWay | 三种态：`"{n} MHz（硬件当前值）"` / `"{n} MHz（回读一致）"` / `"该平台未提供 FMax 读取通道"`。 |
| `FMaxMinimum` | `int` | `400` | OneWay | 只读常量，步进器 / 校验下限。 |
| `FMaxMaximum` | `int` | `5400` | OneWay | 只读常量，步进器 / 校验上限。 |
| `IsFMaxSupported` | `bool` | `false` | OneWay | **v1.1 新增，v1.2 起已实现**。FMax 读取通道是否受支持的硬闸门：`true` = 平台提供 FMax 通道。赋值时机：初始 `false`；每次 SMU 快照读取**成功后**按 `snapshot.FMaxMHz > 0` 赋值（与 `FMaxHint` 的判定同源）；读取失败保持上次结果不变。前端 Visibility / IsEnabled 一律绑此属性。 |

> **前端禁止解析 `FMaxHint` 文案判断支持性**（v1.0 时代的脆弱替代方案已废止）：硬闸门统一绑 `IsFMaxSupported`；`FMaxHint` 仅作辅助展示文案。
> **v1.2 起**：FMax 输入框（TextBox）的 `IsEnabled` 改绑 `IsFMaxSupported`。

### 2.3 电源策略

> **本节描述的电源策略能力已在批次 A-2 移除**，待批次四 UI 重写时重新定义。原表格中的 `PowerAvailable` / `PowerPlanIndex` / `BoostIndex` / `ParkingValue` / `IsCoreParkingSupported` 等属性，支撑它们的 Core 类型已不存在，不再构成有效契约。

### 2.4 核心网格空态

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `HasCores` | `bool` | `false` | OneWay | 已取到核心列表。为 `false` 时核心网格（`ListView`）隐藏、空态面板显示。 |
| `IsCoreGridEmpty` | `bool` | `true` | OneWay | `!HasCores`，随 `HasCores` 自动通知（`NotifyPropertyChangedFor`）。 |

### 2.5 方案选择

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `ProfileIndex` | `int` | `-1` | TwoWay | 当前选中方案，即 `ProfileOptions` 下标（与 `ProfileService.Items` 一一对应）。**切换 = 把该方案 CO/FMax 填进界面草稿，不碰硬件**；切换后 `IsDraftDirty` 重置为 `false`。回填期间索引变化被抑制（`_suppressProfile`），不会被当成用户切换。 |

### 2.6 与模板示例的差异（防幻觉对照）

| 模板示例中的属性 | 真实代码 | 结论 |
| :--- | :--- | :--- |
| `IsSyncFailed` | ❌ 不存在 | 读取失败用 `SmuAvailable=false` + `StatusLevel=Error/Warning` + `IsStatusVisible` 表达 |
| `IsCoreParkingSupported` | ✅ v1.1 立为正契属性，v1.2 起已实现 | 【A-2 后已移除】赋值时机见 2.3 状态说明；前端禁解析 `ParkingHint` 文案 |
| `IsFMaxSupported` | ✅ v1.1 立为正契属性，v1.2 起已实现 | 赋值时机见 2.2；前端禁解析 `FMaxHint` 文案 |
| `MaxCores` | ❌ 不存在 | 核心总数 = `Cores.Count`，见第 1 节警告 |
| `CoreParkingText` | 实为 `ParkingText` / `ParkingInt` / `ParkingValue` 三件套 | 【A-2 后已移除】见 2.3 状态说明 |

---

## 3. 动态集合 (Collections)

| 属性名 | 类型 | 说明 |
| :--- | :--- | :--- |
| `Cores` | `ObservableCollection<CoreSlotViewModel>` | **动态生成**：SMU 快照 `CoreMargins` 有多少个物理核心就生成多少个 Slot。数量不变时**只回填硬件值、不清空重建**（防滚动位置跳动）；数量变化才 Clear + 重建。前端必须 **`ListView` + `ItemsWrapGrid`**（列数 8，`Orientation="Horizontal"`，`MaximumRowsOrColumns="8"`，`ItemWidth` / `ItemHeight` **必须显式给值**，否则列宽随内容浮动），自动流式布局。 |
| `PowerPlanOptions` | `ObservableCollection<string>` | 电源计划文案，固定 4 项（解除限制 / 温和 / 均衡 / 静音），索引即 `PowerPlanIndex`。 |
| `ProfileOptions` | `ObservableCollection<string>` | 方案名列表，与 `ProfileService.Items` 一一对应，**索引即身份**（下拉框只需 int SelectedIndex，不塞对象）。空列表时后端会塞一项占位 `"自定义方案"` 并把索引指 0，避免下拉框空壳。 |

> **⚠️ WinAppSDK 2.x 的 `ItemsPanel` API 事实（写错直接编译不过，WMC0011 / WMC0090 unknown member）**：
>
> ```xml
> <ListView.ItemsPanel>
>     <ItemsPanelTemplate>
>         <ItemsWrapGrid Orientation="Horizontal" MaximumRowsOrColumns="8" ItemWidth="92" ItemHeight="34" />
>     </ItemsPanelTemplate>
> </ListView.ItemsPanel>
> ```
>
> - 属性名是 **`ItemsPanel`**，类型**仍是 `ItemsPanelTemplate`**（2.x 改了属性名、没改类型名，两者不一致是官方设定，不是笔误）。
> - `ListView.ItemsPanelTemplate`（旧写法）**已不存在**，属性元素与 Style Setter 两种写法都编译失败。
> - `ListView` **没有 `IsScrollEnabled` 属性**（UWP 记忆不适用）；关内部滚动用 `ScrollViewer.VerticalScrollMode` / `ScrollViewer.VerticalScrollBarVisibility` = `Disabled`，纵向滚动统一交给外层 `ScrollViewer`（两层滚动器嵌套会互相抢滚轮）。
> - `ItemsWrapGrid` 不显式给 `ItemWidth` / `ItemHeight` 时按首个元素的期望尺寸排版，列宽会浮动，8 列不再等宽。

### `CoreSlotViewModel` 字段定义（一个物理核心一个实例）

| 属性名 | 类型 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- |
| `Index` | `int` | OneWay | 线性核序号（0 起）。回滚与日志定位核心的唯一标识。 |
| `IndexText` | `string` | OneWay | 两位序号 "00"、"01"，等宽显示用。 |
| `CcdText` | `string` | OneWay | 所属 CCD，如 "CCD0" / "CCD1"（按 `CoresPerCcd` 计算，取不到时兜底 8）。 |
| `HardwareValue` | `int` | OneWay | 最近一次 SMU 读回值；`int.MinValue` = 该核读不到（熔断 / 不支持）。 |
| `HardwareKnown` | `bool` | OneWay | `HardwareValue != int.MinValue`。 |
| `HardwareText` | `string` | OneWay | 带符号展示（"+18" / "-20" / "0"）；读不到为 "—"。 |
| `Value` | `int` | OneWay | 编辑值的整型本体。 |
| `EditText` | `string` | **TwoWay**（`UpdateSourceTrigger=PropertyChanged`） | 纯 TextBox 入口。**容错解析**：空串与非法输入（含输入中间态 "-"）直接忽略，绝不抛异常。 |
| `ValueText` | `string` | OneWay | 编辑值带符号展示。 |
| `IsDirty` | `bool` | OneWay | `!HardwareKnown \|\| Value != HardwareValue`——只有 dirty 的格子才值得下发。 |
| `RoundedValue` | `int` | OneWay | 下发 / 保存时读取的值（当前 = `Value`）。 |
| `ApplyCommand` | `IRelayCommand` | — | **单核写入**入口（VM 已提供；当前 XAML 未绑定，保留给逐核下发按钮）。失败只回滚这一核，不会把别的核拽回旧值。 |
| `IncrementCommand` | `IRelayCommand` | — | **步进 +1**（CO ±1，钳制 -50~+50）。前端 `Grid` 叠加的「+」按钮**直接** `Command=` 绑定，不带 `CommandParameter`，故本命令**不接参数**。步进后必须置脏。 |
| `DecrementCommand` | `IRelayCommand` | — | **步进 -1**（同上）。前端 `Grid` 叠加的「−」按钮**直接** `Command=` 绑定，不带 `CommandParameter`，不接参数。步进后必须置脏。 |

> 方法（非绑定，后端内部 / View 不直接调）：`UpdateHardware(int)`（回填硬件值并清 dirty）、`LoadDraft(int)`（从配置文件载入草稿，`int.MinValue` 忽略）。

---

## 4. 命令 (Commands)

全部命令 `CanExecute = !IsBusy`（`CanRun`）。`IsBusy` 变化时逐个刷新可执行状态。

| 命令名 | 来源方法 | 触发时机 / 绑定方式 | 后端行为要求 |
| :--- | :--- | :--- | :--- |
| `RefreshCommand` | `RefreshAsync()` | CPU 卡片同步图标按钮，`Command=` 直接绑定 | **全量同步**（v1.3）：一次读回 SMU 快照（拓扑 / FMax / 逐核值）**与**电源策略（计划 / 升频 / 停放），两者**分别**报告成败。回填**抑制**脏状态。SMU 不可用时不硬读，直接告警提示条；电源策略不依赖 SMU 驱动，SMU 挂了照样读。草稿处置见 4.2。 |
| `ApplyCommand` | `ApplyAsync()` | CPU 卡片「应用」按钮，`Command=` 直接绑定 | 全程遮罩：①逐核 CO 越界校验（-50~+50，失败即停并报核号）→ ②读快照（回滚基线）→ ③逐核下发（未改动的核用 `int.MinValue` 哨兵跳过）→ ④回读校验（Core 完成）→ ⑤**任何失败整体回滚到下发前**并静默刷新回填 → ⑥成功后落盘 `tuning.json`、清脏状态。FMax 仅在用户改动过时才写。 |
| `ApplyPowerCommand` | `ApplyPowerAsync()` | 下拉换项时由 View 层**立即**调用；滑杆路径由 ViewModel 内部防抖自动执行（见 4.1）。命令本身未变 | 【A-2 后已移除】`PowerAvailable=false` 时拒绝。顺序硬约束：**先电源计划（整套索引，会覆盖后两者）→ 再升频模式 → 再核心停放**。三项各自独立报告成败，部分失败也如实汇总。完成后回读刷新。 |
| `LoadCommand` | `Load()` | ⚠️ **当前 XAML 未绑定**（保留入口） | 把 `tuning.json` 已保存设定填进界面草稿，**不写硬件**。无记录时提示。 |
| `SaveCommand` | `Save()` | 浮动保存条「保存覆盖」按钮（View 层 `Click → Execute(null)`） | **只落盘不下发**。保存的是"期望状态"（逐核 `RoundedValue` + FMax + 出厂基线），供开机静默自启重新应用。无核心列表时拒绝。 |
| `DiscardDraftCommand` | `DiscardDraft()` | 浮动保存条「放弃」按钮（`Click → Execute(null)`） | 界面回到**当前选中方案**（`ProfileIndex` 对应快照）的 CO / FMax 值，**不碰硬件**；清脏状态。 |
| `SaveDraftAsCommand` | `SaveDraftAs()` | 浮动保存条「另存为」按钮（`Click → Execute(null)`） | 当前草稿（CO + FMax）存成新方案（名 "自定义方案 HHmmss"）并选中新方案，落盘，清脏状态。 |
| `IncrementCommand` | `Increment(string target)` | 步进「加号」。前端**直接** `Command=` 绑定在「+」按钮上，按钮带 `CommandParameter="FMax"` / `"Parking"` | 【A-2 后已移除】仅 `"Parking"` 分支（核心停放 **+5%**，钳制 0~100）；`"FMax"` 分支（**+10 MHz**，钳制 400~5400）**仍有效**。**内部必须调 `MarkDirty()`**。 |
| `DecrementCommand` | `Decrement(string target)` | 步进「减号」。前端**直接** `Command=` 绑定在「−」按钮上，按钮带 `CommandParameter="FMax"` / `"Parking"` | 【A-2 后已移除】仅 `"Parking"` 分支（核心停放 **−5%**，钳制 0~100）；`"FMax"` 分支（**−10 MHz**，钳制 400~5400）**仍有效**。其余同 `IncrementCommand`，方向相反。**内部必须调 `MarkDirty()`**。 |

> 模板示例名 → 真实名对照：`SyncHardwareCommand`→**`RefreshCommand`**；`ApplySettingsCommand`→**`ApplyCommand`**；`DiscardChangesCommand`→**`DiscardDraftCommand`**；`SaveProfileCommand`→**`SaveCommand`**；`SaveAsProfileCommand`→**`SaveDraftAsCommand`**。

### 4.1 电源策略的提交时机（v1.2 修订）

> **本节描述的电源策略能力已在批次 A-2 移除**，待批次四 UI 重写时重新定义。原内容为电源计划 / 升频模式 / 核心停放的提交时机与 `SchedulePowerApply()` 防抖说明，支撑它们的 Core 类型已不存在，不再构成有效契约。

### 4.2 同步（刷新）的草稿语义（v1.3 新增）

用户点同步按钮的语义是「让我看看这台机器现在真实的硬件状态到底是什么」，所以后端**一次读全**：SMU 核心状态 + 电源策略。只刷一半（核心是新的、电源策略还是旧的）比不刷更糟 —— 界面一半真一半假，用户无从判断哪一半可信。

**后端行为（已实现）**：

| 项 | 规则 |
| :--- | :--- |
| 覆盖范围 | 读到的真值直接覆盖界面草稿：逐核 CO、FMax、核心停放滑杆、电源计划 / 升频模式下拉 |
| 读取失败 | 该子系统的草稿**保留**（读不到就别动用户填的值），`IsDraftDirty` **不清** |
| `IsDraftDirty` | 仅当 SMU **与**电源策略**都**读到真值时置 `false`；任一失败则保持原值 |
| 提示条 | 全成功 → `Success`「已同步」；一半失败 → `Warning`「同步不完整」；全失败 → `Error`「同步失败」。**两个子系统的成败分开写进文案**，不合成一个结论 |
| 保存条 | 后端清 `IsDraftDirty` 时，View 层自动收起浮动保存条（View 订阅 `IsDraftDirty` 的 PropertyChanged 实现），前端无需额外处理 |

> **为什么覆盖草稿不算数据丢失**：草稿是「还没下发给硬件的提案」，不是已保存的数据。同步的语义本来就是「丢弃我的提案，给我真值」。真正的保存由「保存覆盖 / 另存为」承担。

**前端可选：同步前的确认框（后端不强制，由前端决定是否做）**

- 判据用 **`IsDraftDirty`**：为 `true` 表示界面上有未保存的修改，此时弹框才有意义。
- 拦截配方：把同步按钮从 `Command="{x:Bind ViewModel.RefreshCommand}"` 改成 `Click="OnRefreshClick"`，在 `TuningPage.xaml.cs` 里做「脏 → 弹 ContentDialog → 确认后 `ViewModel.RefreshCommand.Execute(null)`」。
- ⚠️ **两种绑定不能并存**：`Command=` 与 `Click=` 同时挂在同一个按钮上会触发两次同步。改造时必须**移除** `Command=` 绑定。
- 后端已把覆盖行为写进同步按钮的 Tooltip（前端侧文案），**建议不加模态框**：脏标志很容易被置位（任何一次输入即置位），而同步是高频操作，模态框会持续打断；覆盖后数值当场回弹、角标与保存条同时消失，本身就是明确反馈。

> **给前端的两条建议（属前端文件，后端不代改）**：① 同步按钮 Tooltip 现为「同步当前硬件状态（覆盖未保存的修改）」，可改为「同步硬件与电源策略（覆盖未保存的修改）」以匹配新语义；② 若采用确认框方案，`ContentDialog` 放在 XAML 里、由后端提供 `OnRefreshClick` 处理器是可行的分工。

### 4.3 步进命令（v1.4 新增）

调校页的三个数值入口（逐核 CO、FMax、核心停放）都提供隐式加减号。**v1.5 起，加减号改为 `Grid` 叠加「纯 `TextBox` + 左右两个 `Button`」实现，命令直接绑在按钮上（`Command=` + `CommandParameter`），不再通过 `TextBox.Tag` / `Header` 穿透进模板**（红线②：禁用 `<ControlTemplate>` 重写 `TextBox`）。

> **v1.5 行为变更**：加减号**常驻显示**（不再"平时隐藏、悬浮 / 聚焦时淡入"）。原因是模板被禁用后拿不到 `TextBox` 的 `PointerOver` / `Focused` 状态机，无法再做淡入淡出。**这是有意识的取舍 —— 稳定压倒一切**，观感问题留待后续另行优化。命令语义、参数、步长、钳制范围**一律未变**。

| 入口 | 命令宿主 | 类型 | 步长 | 钳制 | 绑定方式 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 逐核 CO | `CoreSlotViewModel.IncrementCommand` / `DecrementCommand` | `IRelayCommand`（**无参**） | ±1 | -50 ~ +50 | `Grid` 叠加的两个 `Button`，各自 `Command=` 直绑，**无** `CommandParameter` |
| FMax | `TuningViewModel.IncrementCommand` / `DecrementCommand` | `IRelayCommand<string>` | ±10 MHz | 400 ~ 5400 | `Grid` 叠加的两个 `Button`，`CommandParameter="FMax"` |
| 核心停放 | 同上（同一对命令） | `IRelayCommand<string>` | ±5% | 0 ~ 100 | `Grid` 叠加的两个 `Button`，`CommandParameter="Parking"` |

**参数字面量契约**：`"FMax"` / `"Parking"` 两个字符串必须与前端 XAML 的 `CommandParameter` 逐字一致，改一处必须同时改另一处。

**步进为什么必须显式置脏**：步进按钮走的是 `Command`，**不经过 `TextChanged`** —— 后端改值 → 界面数字变化 → 但没有任何事件去报告「用户改过东西」。所以两个步进命令在改值后都必须调一次 `MarkDirty()`，否则 `IsDraftDirty` 不置位，方案名旁的 `*` 角标与底部浮动保存条都不会出现。

**停放步长为什么是 5**：滑杆 `StepFrequency=5`，步进结果必须仍落在滑杆可取的格点上，否则滑杆会把值回咬到最近的 5，两边数字对不上还会互相回写。步进前先把当前值归到格点再 ±5。

**FMax 步长为什么是 10**：值域 400~5400 跨 5000，步进 1 要点五千下；10 既够细又能一步跨过一个常见档位差。

**FMax / 停放步进受硬闸门约束**：`IsFMaxSupported == false` 时 FMax 步进不生效，`IsCoreParkingSupported == false` 时停放步进不生效 —— 与前端把输入框置灰的语义保持一致。

> **后端实现约束（前端依赖）**：`CoreSlotViewModel.Value` 变化时**必须**发 `EditText` 的 `PropertyChanged`。`EditText` 是绑在 TextBox 上的计算属性，不发通知则后端改值（步进 / 回填硬件值）后界面数字不更新，格子会一直显示旧值。

---

## 5. 交互约定 (Interactions)

### 5.1 多语言与样式
- 前端使用 `x:Uid` 绑定资源，**后端不硬编码中文字符串**（VM 内中文仅用于状态提示文案，属展示数据，不动它）。
- 数值输入：**纯 `TextBox`**（样式 `AppTextBoxMono`，34px / Consolas / 居中 / `MaxLength`），**不是** NumberBox——契约入口是 `EditText` / `FMaxText` / `ParkingText`【A-2 后已移除】 三个容错字符串属性。停放另有 `Slider`（0~100，步进 5）【A-2 后已移除】。
- **数值输入不得用 `<ControlTemplate>` 重写 `TextBox`**（红线②）：加减号一律用 `Grid` 叠加「纯 `TextBox` + 左右两个 `Button`」实现，命令直接 `Command=` 绑定在按钮上。
- 下拉框：必须用自定义 `ctl:AppComboBox`（深浅自适应），**禁止系统默认白底 ComboBox**。
- `bool → Visibility` 一律走 `UiConverters.ToVisibleWhenTrue / ToVisibleWhenFalse / ToCollapsedWhenTrue` 编译期函数；`UiSeverity → InfoBarSeverity` 走 `UiConverters.ToInfoBarSeverity`。**不引 IValueConverter 进资源字典**。

### 5.2 脏数据链路（View 层职责）
1. 任何用户输入路径（CO/FMax/停放 TextBox 打字、下拉换项、拖滑杆）最终必须调用 `ViewModel.MarkDirty()` 一次 → `IsDraftDirty=true` → `*` 角标显现。
2. 判定"是不是用户在操作"的门槛：TextBox 仅在**持有焦点**时才算用户输入（回填 / 容器延迟 realize 也触发 TextChanged，必须排除）；初始化回填全程压住（`_suppressUiDirty`）。
3. **浮动保存条的显隐完全由 `IsDraftDirty` 驱动，两个方向都自动**（v1.4 起）：置脏（用户输入 / 步进按钮）→ 弹条；清脏（应用 / 保存 / 放弃 / 同步）→ 收条。View 层订阅 `IsDraftDirty` 的 `PropertyChanged` 统一处理，前端无需参与（显式 `HideDirtyBar()` 仍作兜底）。
4. `IsBusy=true` 期间忽略一切 MarkDirty。

### 5.3 提示条语义
- 只有 **Error / Warning** 会自动打开 InfoBar；Success / Info 只更新 Title / Message 不弹条（成功反馈不占首屏）。
- `IsStatusVisible` 是 TwoWay：用户点 × 关闭只是 UI 行为，后端状态不变。

### 5.4 遮罩语义
- `IsBusy=true` → 全页遮罩 + `ProgressRing` + `BusyText`；遮罩**吞指针事件**（SMU 写入期间任何输入都会被下一次写入带上）。
- 遮罩固定文案："若失败将自动回滚至原状态"。

### 5.5 架构红线（前端改动不得破坏）
- 全量 `x:Bind`，零反射；不得引入破坏 Native AOT 的写法。
- UI 层**禁止 new Core 服务**（尤其 `SmuReader`：进程内单例，被 Dispose / 重复创建会永久"打哑"SMU）。
- Core 已 AOT 验证：不得改动 `ISmuReader` / `SmuSnapshot` / `SmuWriteResult` / `SmuFMaxResult` / JSON 源生成上下文 / `[LibraryImport]` 封送。

---

## 6. 后端数据结构参考（前端只读了解，勿绑定）

**`SmuSnapshot`**（`ISmuReader.ReadSnapshot()` 返回）：`Success`、`Error`、`CpuName`、`CodeName`、`Family`、`SmuType`、`SmuVersion`、`Ccds`、`Cores`、`PhysicalCores`、`ThreadsPerCore`、`CoresPerCcd`、`CoreMargins`（`int[]`，`int.MinValue`=读不到）、`FMaxMHz`（0=未知）。

**`SmuWriteResult`**：`AllSucceeded`、`Fatal`、`WriteFailed`（`List<int>` 被拒核号）、`VerifyFailed`（回读不一致核号）、`Applied`（`Dictionary<int,int>` 成功核→值）。

**`SmuFMaxResult`**：`AllSucceeded`、`SmuAccepted`、`Requested`、`ReadBack`、`Fatal`。

**`TuningProfile`**（tuning.json）：`CurveOptimizer`（`int[]`）、`FMaxMHz`（`uint`）、`FMaxBaselineMHz`（`uint` 出厂基线）、`AppliedAt`（`string?`）。

**`ProfileSnapshot`**（profiles.json，实现 `INotifyPropertyChanged`）：`Id`、`Name`、`Note`、`CurveOptimizer`、`FMaxMHz`、`ScheduleRules`、`IsActive`（场景切换在后台刷新，UI 只需 DataTrigger）。

**值域红线**：CO ∈ [-50, +50]；FMax ∈ [400, 5400] MHz；核心停放 ∈ [0, 100] %。

---

## 7. 契约变更登记（新增属性必须走此流程）

**v1.6 变更记录（2026-11-01，人类批准）**：

> 本次新增 **2 个只读展示属性**，**未改动 / 未删除 / 未重命名任何既有属性或命令**，命令参数、步长、钳制范围、绑定语义全部不变。

| 提案项 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| `CpuTitle` / `CpuDetail` | 新增 2 个只读展示属性（`string`） | CPU 主标题（型号）与副标题（规格 · SMU 状态）分行显示 | ✅ **已落地**（`TuningViewModel` 派生属性，随 `CpuName` / `SmuStateText` 自动刷新） |
| `AppComboBox.DisplayText` | 前端控件自有属性（**不进 ViewModel 契约**） | ComboBox 基类会在运行时覆写 `ContentPresenter.Content`，模板绑 `SelectionBoxItem` 会被写成 null，导致合上空白 | ✅ **已落地**（`AppComboBox` 内部维护纯文本通道，模板改用 `TextBlock` 显示） |
| 其余 5 项视觉修复 | 前端样式 / 视图层（**不动任何契约条目**） | 数值绝对居中、滑块 thumb 实心同色、下拉弹层去亮蓝底与带箭头滚动条、系统标题栏三键隐藏、页面滚动条极细 | ✅ **已落地**（全部在 `Tokens.xaml` / `Controls.xaml` / `MainWindow.xaml.cs` / `TuningPage.xaml`） |

**v1.5 变更记录（2026-11-01，人类批准）**：

> 本次为**纯前端结构规范修订**，**未新增 / 未删除 / 未重命名任何属性或命令**，所有绑定语义、参数、步长、钳制范围不变 —— **后端 `.cs` 无需任何改动**。

| 提案项 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| 核心网格强制结构改为 `ListView` + `ItemsWrapGrid` | 前端结构规范（替换 §3 原「`ItemsRepeater` + `UniformGridLayout`」） | 8 列等分流式布局 | ✅ **已落地**（`ListView.ItemsPanel` 属性名 + `ItemsPanelTemplate` 类型；`ItemWidth` / `ItemHeight` 显式给值） |
| `ItemsPanel` API 事实写入契约 | 前端 API 事实（§3 附注） | 防止后来者改回旧写法导致 WMC0011 / WMC0090 | ✅ **已写入** |
| 数值输入禁用 `<ControlTemplate>` 重写 `TextBox` | 前端结构规范（红线②） | 隐式加减号一律 `Grid` 叠加「纯 `TextBox` + 左右两个 `Button`」 | ✅ **已落地** |
| 废除 `TextBox.Tag` / `Header` 模板穿透 | 前端绑定方式（替换 4.3 原「穿透方式」列） | 步进命令**直接** `Command=` 绑在按钮上，按钮自带 `CommandParameter` | ✅ **已落地**（逐核 CO 仍为无参命令，按钮不带 `CommandParameter`） |
| 加减号常驻显示 | 前端行为（替换 4.3 原「平时隐藏，悬浮 / 聚焦时淡入」） | 模板禁用后无 `PointerOver` / `Focused` 状态机，退而求其次保稳定 | ✅ **已落地**（观感待后续优化） |
| 全页禁用 `TabView` | 前端结构规范（红线③） | 避免未验证控件导致黑屏 | ✅ **核对确认：本页从未使用** |

**v1.4 变更记录（2026-11-01，人类批准）**：

| 提案项 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| `CoreSlotViewModel.IncrementCommand` / `DecrementCommand` | `IRelayCommand`（无参） | 逐核 CO 步进 ±1 | ✅ **已实现**（钳制 -50~+50；步进后置脏） |
| `TuningViewModel.IncrementCommand` / `DecrementCommand` | `IRelayCommand<string>` | FMax（±10 MHz）与核心停放（±5%）步进 | ✅ **已实现**（参数 `"FMax"` / `"Parking"`；步进后置脏；受硬闸门约束） |
| `EditText` 的 `PropertyChanged` | 既有属性的通知约定 | 后端改 `Value`（步进 / 回填）后 TextBox 数字同步 | ✅ **已实现**（v1.4 补齐；此前后端改值界面不更新） |
| 浮动保存条双向绑定 `IsDraftDirty` | View 层行为 | 置脏弹条 / 清脏收条，同一条路 | ✅ **已实现**（顺带修掉「步进后保存条不弹」） |
| `Strings/Resources.resw`（中性资源） | 资源文件 | `x:Uid` 引用的属性必须有默认或中性资源，否则 PRI263 警告 | ✅ **已补齐**（前端只建了 zh-CN 卫星；与 zh-CN 逐条对应） |

**v1.3 变更记录（2026-11-01，人类批准）**：

| 提案项 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| `RefreshCommand` 语义升级 | 既有命令（名称/来源方法/绑定方式不变） | 一次读全 SMU 核心状态 + 电源策略；真值覆盖草稿；两子系统成败分开报告 | ✅ **已实现**（草稿与 `IsDraftDirty` 处置规则见 4.2；前端 XAML 无需改动） |
| `IsDraftDirty` 作为「有未保存修改」的判据 | 既有属性（语义未变） | 前端若要给同步加确认框，用它判断是否需要弹框 | ✅ **已实现**（后端已保证同步后取值正确） |
| 浮动保存条随 `IsDraftDirty` 自动收起 | View 层行为 | 后端清脏时保存条必须跟着收，否则角标与保存条自相矛盾 | ✅ **已实现**（View 订阅 PropertyChanged；顺带修掉「应用 / 切方案后保存条不收」的旧缺陷） |

**v1.2 变更记录（2026-11-01，人类批准）**：

| 提案项 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| `SchedulePowerApply()` | `void`（非绑定方法） | 滑杆停手 700ms 后由 ViewModel 自动提交电源策略 | ✅ **已实现**（防抖由 View 层收敛到 ViewModel；`ApplyPowerCommand` 本身未变，前端 XAML 无需改动） |
| `IsCoreParkingSupported` | `bool` | 停放不支持时隐藏/禁用该行（硬闸门） | ✅ **已实现**（v1.1 立项，v1.2 落地） |
| `IsFMaxSupported` | `bool` | FMax 通道不支持时置灰输入（硬闸门） | ✅ **已实现**（v1.1 立项，v1.2 落地） |

**v1.1 变更记录（2026-11-01，人类批准）**：

| 提案属性 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| `IsCoreParkingSupported` | `bool` | 停放不支持时隐藏/禁用该行（硬闸门） | ✅ **已并入正文 2.3 并实现**（v1.2 落地；默认 `false`；电源策略读取成功后按 `TryReadCoreParking` 的 `parkOk` 赋值；`PowerAvailable=false` 时必为 `false`） |
| `IsFMaxSupported` | `bool` | FMax 通道不支持时置灰输入（硬闸门） | ✅ **已并入正文 2.2 并实现**（v1.2 落地；默认 `false`；SMU 快照读取成功后按 `snapshot.FMaxMHz > 0` 赋值） |

**仍在候选区（未批准，前端不得绑定）**：

| 提案属性 | 类型 | 用途 | 现状替代方案 | 状态 |
| :--- | :--- | :--- | :--- | :--- |
| `PhysicalCoreCount` | `int` | 空态 / 汇总文案直接取核数 | `Cores.Count`（等价，仅便利性） | 可选，不急 |

> 后续任何新增属性 / 命令 / 重命名，必须走本流程：人类批准 → 本文档登记（属性名、类型、默认值、绑定模式、后端赋值时机）→ bump 契约版本号 → 同步告知前后端两个 AI。严禁单方面改动。
