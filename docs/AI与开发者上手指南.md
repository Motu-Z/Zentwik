# Zentwik 上手与开发指南（AI / 开发者速览）

> **定位**：这是给其他 AI 与开发者的一段式速览入口。读完本文件即可安全上手；细节以引用的权威文件为准。
> **最后校准**：2026-11-01（breeze 设计度量对齐完成）。

---

## 0. 这是什么

**Zentwik** —— AMD Ryzen 处理器轻量化本地调校工具（Curve Optimizer 逐核偏移、FMax 频率上限、功耗墙 PPT/TDC/EDC/温度墙、基于 CPU Sets 的进程绑核）。

- **仓库**：`https://github.com/Motu-Z/Zentwik.git`，分支 `main`。唯一工作副本在 `D:/Projects/Zentwik/`。
- **形态**：WinUI 3 原生桌面应用（无边框窗口 + Mica 材质 + 自定义标题栏）。
- **运行依赖**：.NET 10 Runtime、PawnIO（SMU 访问）；缺位时回退 `WinIo32.dll` 端口访问。

---

## 1. 技术栈与工程布局

| 项 | 说明 |
| :--- | :--- |
| UI 框架 | **WinUI 3**（WindowsAppSDK 2.6.4） |
| 语言/运行时 | C# + .NET 10，XAML 声明界面 |
| 绑定 | 全程 `{x:Bind}` 编译期绑定；默认 `OneWay`，输入控件显式 `TwoWay` |
| 主题 | Dark（基准）/ Light / HighContrast 三套 `ThemeDictionaries`；所有「面」带透明度以透出 Mica |

```
Zentwik/
├─ Zentwik.App/              # UI 层
│  ├─ App.xaml               # 合并 Themes/Tokens.xaml + Controls.xaml（顺序：先 Tokens 后 Controls）
│  ├─ Themes/
│  │  ├─ Tokens.xaml        # 色彩 + 尺寸 token（强调色、圆角、内边距、控件高、滑块尺寸）
│  │  ├─ Typography.xaml    # 字号阶梯样式（AppPageTitle / AppLabel / AppBody ...）
│  │  └─ Controls.xaml      # 原生控件覆盖样式 + 17 个 breeze 启发的新基元组件
│  ├─ Views/                # 页面 .xaml + 后台 .xaml.cs
│  ├─ ViewModels/           # MVVM 视图模型（TuningViewModel 等）
│  ├─ Controls/             # 自绘控件代码（如 AppComboBox.cs）
│  └─ Helpers/ThemeController.cs  # 运行时按用户配置覆盖强调色
├─ Zentwik.Core/            # 业务/服务层（ISmuReader、ProcessorPowerTuning、TuningStore ...）
└─ UI_Contract.md           # ★唯一法定契约（前后端接口定义）
```

**编译红线：0 警告 0 错误。** 任何引入警告的写法都要消掉再提交。

---

## 2. 开发模式（契约驱动 + 前后端分离）

| 规则 | 约束 |
| :--- | :--- |
| 法定契约 | **`UI_Contract.md`（当前 v1.5，人类批准）** 是前端 (XAML) 与后端 (ViewModel) 通信的唯一法定契约。不得擅改其中属性/命令；要改须先与人类商讨并通知另一方。 |
| 前后端边界 | 约定：**前端 AI 只改 `.xaml`，后端 AI 只改 `.cs`（含 `Views/*.xaml.cs`）**，互不越界。当前为单 AI 模式，用户授权可同时改两端，但仍建议按此分区以保清晰。 |
| 页面装配 | ViewModel 由外壳**显式 `new` 并注入**（不走 `Frame.Navigate`）；构造参数全部来自 Core 服务注入，UI 层禁止自行 `new`。 |
| 初始化 | 页面 `Loaded` 调 `await ViewModel.InitializeAsync()`，只执行一次（切页回来不重读 SMU）。 |
| 线程 | SMU 访问在 ViewModel 内 `Task.Run` 到线程池，`await` 后续体回 UI 线程；UI 层不做额外封送。 |

> 反向生成契约的源码清单见 `UI_Contract.md` 顶部「反向生成来源」。

---

## 3. 设计系统（breeze-shell 移植 + 初音青强调色）

### 3.1 设计来源与边界

- **breeze-shell**（朋友项目）是 C++ Canvas 引擎（NanoVG/ThorVG）+ 自研 React 渲染器，**技术栈与 Zentwik 零交集，代码不可复用**。
- 我们**仅移植其设计语言**：视觉度量（圆角、内边距、控件高、字号）与组件形态（页头、InfoBar、分段控件、设置卡、开关…）。度量实测自 `src/shell/script/ts/src/config_page/{constants.ts,components/Fluent.tsx}`。
- 强调色按本产品规范走 **初音青 `#39C5BB`**，不沿用 breeze 的 `#60CDFF`。

### 3.2 强调色（全链路 token 化）

根级（在 `ThemeDictionaries` 之外，确保最先被查到，且可被 `ThemeController` 运行时覆盖）：

| 键 | 值 | 用途 |
| :--- | :--- | :--- |
| `SystemAccentColor` / `SystemAccentColorBrush` | `#FF39C5BB` | 语义锚点 |
| `AppAccentBrush` | `#FF39C5BB` | 主强调画刷（前景/边框/滑块） |
| `AppAccentHoverBrush` | `#FF5BD4CC` | 悬停 |
| `AppAccentPressedBrush` | `#FF2BA89F` | 按下 |
| `AppAccentSoftBrush` | `#2639C5BB` | 浅底（选中底、InfoBar 成功底） |
| `AppAccentBorderBrush` | `#4D39C5BB` | 强调描边 |
| `AppOnAccentBrush` | `#FF04222F` | 强调底上的文字 |

**铁律**：`Info / Warn / Error / Success` 等**语义色不跟随强调色**，换强调色后错误不能变紫。

### 3.3 度量 token 表（已对齐 breeze）

> 全部定义在 `Tokens.xaml` 尺寸区。改视觉先改这里，不要写死到样式里。

**圆角**

| Token | 值 | breeze 来源 |
| :--- | :--- | :--- |
| `AppCardCornerRadius` | **7** | Card / SettingsCard / Expander / ChoiceCard 全用 7 |
| `AppControlCornerRadius` | **4** | Button / ComboBox / NumberBox / Segment 全用 4 |
| `AppSegmentedCornerRadius` | **6** | 分段控件容器 |
| `AppComboCornerRadius` | **4** | ComboBox（原 6，已对齐） |
| `AppCaptionCornerRadius` | 6 | 标题栏按钮（窗口 chrome，breeze 无此概念，保留） |

**间距 / 内边距**

| Token | 值 | 说明 |
| :--- | :--- | :--- |
| `AppContentPadding` | **28,24,28,24** | 页面内容外边距（breeze `PAGE_PADDING=28`） |
| `AppCardPadding` | **16,14** | 卡片内边距（breeze Card 14 / SettingsCard 16） |
| `AppCardSpacing` | 16 | 卡片间纵向间距 |

**控件高度**

| Token | 值 | breeze 来源 |
| :--- | :--- | :--- |
| `AppControlHeight` | 32 | 标准控件 |
| `AppSmallControlHeight` | 26 | 小按钮 / 芯片（breeze Chip 26） |
| `AppComboHeight` | **32** | ComboBox（原 34，已对齐） |
| `AppInputHeight` | **32** | 输入框 / NumberBox（原 34，已对齐） |
| `AppIconButtonSize` | 36 | 图标按钮 |
| `AppSidebarWidth` | 220 | 侧栏（breeze `SIDEBAR_WIDTH=220`） |
| `AppTitleBarHeight` | 48 | 自定义标题栏 |
| `AppCaptionButtonSize` | 32 | 标题栏按钮 |
| `AppNavItemHeight` | 40 | 导航项 |

**滑块尺寸**（覆盖 WinUI 原生主题资源，不动模板）

| Token | 值 |
| :--- | :--- |
| `SliderTrackThemeHeight` | 2 |
| `SliderHorizontalThumbWidth` / `Height` | 14 |
| `SliderPreContentMargin` / `PostContentMargin` | 12 |

### 3.4 字号阶梯（`Typography.xaml`）

唯一字体族：`Segoe UI Variable Text, Microsoft YaHei UI`；数字 / 时间戳走 `Consolas` 等宽。

| 样式键 | 字号 | 字重 | 用途 |
| :--- | :--- | :--- | :--- |
| `AppPageTitle` | 28 | Bold | 页面大标题 |
| `AppCpuName` | 18 | Bold | 首屏 CPU 型号锚点 |
| `AppCardTitle` | 16 | SemiBold | 卡片标题 |
| `AppLabel` / `AppNavLabel` | 14 | Semi/Normal | 控件标签 / 导航项 |
| `AppBody` | 13 | Normal | 正文、按钮文字 |
| `AppDesc` / `AppMeta` / `AppTableHeader` | 12 | Normal | 辅助说明 / 路径时间戳 / 表头 |

### 3.5 已落地的 breeze 基元组件（`Controls.xaml`）

| 组件 | 关键用法 |
| :--- | :--- |
| `AppPageHeader` + `AppPageHeaderTitle/Desc/Actions` | 页面头部三件套 |
| `AppInfoBar` + `AppInfoBarWarning/Error/Success` | 内联通知（圆角 6、内边距 14） |
| `AppExpander` | 可折叠分组（圆角随 `AppCardCornerRadius`） |
| `AppSegmentedControl` + `AppSegmentedItem` | 分段选择；内部放若干 `RadioButton`（`GroupName` 互斥），容器圆角 6、内项圆角 4 |
| `AppChoiceCard` | 可选卡片，左侧 3px 青色竖条 = 选中（与 ComboBox 选中语言一致） |
| `AppProgressBar` | 高度 4、圆角 2、强调色填充 |
| `AppSettingsCard` + `AppSettingsLabel/Desc` | 设置行卡片（左标签+描述 / 右控件），内边距 16,14、下间距 8 |
| `AppToggleSwitch` | 原生模板已青色化，此处仅统一样式 |
| `AppComboBox` | 自绘下拉宽度对齐 + 关闭态文本通道（`controls/AppComboBox.cs`） |

> 添加新控件：在 `Controls.xaml` 写 `Style`，**颜色一律绑 `ThemeResource` 画刷、尺寸一律引用上面 token，禁止写死字面量**。

---

## 4. XAML 红线（必看，避免编译/运行炸）

1. **禁止独立 `<Color>` 元素**。WinUI 3 工具链对 `<Color x:Key=...>` 会报 `WMC0011` 警告、甚至 `XLS0414` 硬错误（别名 `using:Microsoft.UI.Xaml.Media` 也解析不到）。**一律用 `<SolidColorBrush Color="#..." />`**——这是全库几十处稳定写法。
2. **颜色只走画刷**：`Foreground` / `BorderBrush` / `Background` 绑画刷，绝不绑 `Color` 类型。
3. **`{ThemeResource}` vs `{StaticResource}`**：主题相关（明暗不同的值）用 `ThemeResource`；根级强调色写在 `ThemeDictionaries` 之外，用 `ThemeResource` 也能正确查到。
4. **不跨字典 `StaticResource` 引用 `ControlTemplate` 内资源**（运行期崩溃）；改用 `ThemeResource` 或内联。
5. 始终维持 **0 警告 0 错误**。

---

## 5. 如何新增（控件 / 页面）

- **新控件**：`Controls.xaml` 加 `Style`，颜色绑 token 画刷、尺寸引 token；原生控件用 `BasedOn` 覆盖而非整模板重写（除非契约允许）。
- **新页面**：`Views/XxxPage.xaml` + `XxxPage.xaml.cs` + `ViewModels/XxxViewModel.cs`；ViewModel 构造函数注入 Core 服务，页面外壳 `new` 注入并调 `InitializeAsync()`；绑定走 `x:Bind`。
- **改 ViewModel 属性/命令**：先读 `UI_Contract.md` 对应版本；若需改契约字段，**必须人类批准**后再动两端。

---

## 6. 提交约定（来自此前会话，沿用）

- 工作分支 `feat/logic`；合入 `main` 用 **`git push origin HEAD:refs/heads/main`**（快进）。
  ⚠️ **不要用字面 `git push origin main`**——本仓 `main` 曾领先本地，字面推送会回退远端提交。
- **不提交** `.ai_backup_*/`（AI 物理备份）与 `build/zip_portable.py`（已移出 Git 追踪，仅留本地）。
- 提交信息用 `feat(theme)` / `fix` / `chore` 前缀，简述改动。

---

## 7. 相关文档索引

| 文件 | 角色 | 注意 |
| :--- | :--- | :--- |
| `UI_Contract.md` | ★唯一法定契约（前后端接口） | **最高优先级**，改前必读 |
| `docs/AI与开发者上手指南.md` | 本文件（统合速览） | 当前权威入口 |
| `docs/UI-设计规范.md` | ⚠️ **WPF 时代旧规范**（强调色 `#38BDF8`、卡片圆角 10、侧栏 208px、字号 L1=26） | **已被本指南取代**，仅供追溯，**勿照此实现** |
| `docs/项目规范与开发手册.md` | 工程规范与开发手册 | 历史交接 |
| `docs/全局交接文档.md` / `docs/S5-前后端分离开局提示词.md` | 阶段交接 | 历史交接 |
| `README.md` | 面向用户的使用说明 | 注意仍称 “Zenwik”，与仓库名 Zentwik 略有出入 |
| `CHANGELOG.md` | 版本更新日志 | — |
</content>
</invoke>
