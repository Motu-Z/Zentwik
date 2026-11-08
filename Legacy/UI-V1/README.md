# Legacy / UI-V1 —— 旧调校页留档

> 归档时间：2026-11-03
> 归档原因：UI 从零重构第一阶段（视觉基座与静态外壳）。旧调校页的视觉全部废弃，
> 但其中 **988 行的业务逻辑**（CO 逐核网格动态生成、FMax 步进、方案重命名 Popup、
> Toast、电源策略读写、SMU 探测）在本轮被判定为「有价值、下一轮接回」。

## 这里面有什么

| 文件 | 行数 | 内容 |
|---|---|---|
| `TuningView.xaml` | 270 | 旧调校页视觉（基于已删除的 `Themes/Shell.xaml` 与根 `Styles.xaml`） |
| `TuningView.xaml.cs` | ~988 | 全部业务逻辑：IPC 调用、tuning.json 读写、CO 网格生成、电源策略三件套（值 / Supported / Error） |

## 它不参与编译

- 本目录**没有 `.csproj`**，`Zentwik.slnx` 里也没有登记它 → MSBuild 根本不会扫到这里。
- `Legacy/Directory.Build.props` 是 0 字节空文件，不会把任何 `.cs` 链接回 UI 工程。
- `Legacy/WPF/` 与 `Legacy/WinUI3/` 是更早的整工程归档，同样不在 slnx 内。

## 下一轮怎么接回

新壳（`Zentwik.UI/Views/TuningView.xaml` + `Zentwik.UI/ViewModels/TuningViewModel.cs`）
已经预留了全部接入口：

1. **CO 网格** —— 新壳用 `ItemsControl` 绑定 `TuningViewModel.CoreMargins`（`ObservableCollection<CoreMarginItem>`），
   ± 按钮绑 `StepCoreUpCommand` / `StepCoreDownCommand`，输入框绑 `Margin`。
   旧代码里那段「手工 new 16 个 Border + TextBox + Button」的循环可以整段丢掉。
2. **FMax** —— `FMaxMHz` 属性 + `StepFMaxUpCommand` / `StepFMaxDownCommand` 已就位，
   旧逻辑只需把 IPC 调用挂进命令体。
3. **电源策略** —— `EppIndex` / `BoostIndex` / `MaxStateIndex` / `CStateEnabled` / `AspmIndex`
   与配套的 `*Supported` 布尔，对应旧代码里的「值 + Supported + Error」三件套。
4. **数据通道** —— `TuningViewModel` 本轮是**纯净**的：没有 IPC、没有 `Zentwik.Core` 依赖。
   下一轮把 `App.Tuning` / `WorkerPipeClient` 注进去即可。

## 接回时的两个坑

- 旧代码用 `FindResource("NumberInput")` / `("StepButton")` / `("NumberBoxBorder")` 动态取样式。
  这三个键在新的 `Themes/Styles.xaml` 里**保留为别名**（`NumberInput`→`NumberField`、
  `StepButton`→`ButtonStepper`、`NumberBoxBorder`→`SurfaceInset`），接回时不会因为
  「找不到资源」而崩在运行期。
- 旧的 `PopupBg` / `CardBorder` 画刷键也仍在 `Themes/Colors.Dark.xaml` 里，
  旧代码的 `FindResource("PopupBg")` 不会失效。

## 不要动这里

本目录是历史留档，不参与构建、不参与静态检查。任何修改都不会影响产品，
也不会被下一轮接回的逻辑自动继承 —— 接回时请以新壳为准重新落一遍。
