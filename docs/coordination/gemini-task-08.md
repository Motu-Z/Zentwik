# 任务 08：让主题真的能换（palette 运行时化）

上一轮那四个下拉（语言/主题/强调色/界面缩放）现在只写配置、改不动外观，因为：

- `ZentwikApp` 恒用 `buildZentwikTheme()`，没有 `themeMode`；
- `ZentwikTokens` 是 `const` 静态字段，全仓库直接引用 `ZentwikTokens.textPrimary` 这种写法，
  颜色在编译期就定死了，运行时换不了。

本轮把这个基础拆掉重建。**这是本轮唯一目标，不要顺手改别的页面逻辑。**

## 允许修改

`lib/theme/**`、`lib/widgets/**`、`lib/shell/**`、`lib/pages/**`、`lib/cards/**`、
`lib/dashboard/**`、`lib/app.dart`、以及对应测试。

## 不得修改

`lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、`lib/main.dart`、
`pubspec.yaml`、任何 `.cs`；不得改既有 71 例测试的断言（断言里如果引用了 token 常量，
改为引用新的等价入口即可，语义不许变）。

## A. 新的配色入口

1. `ZentwikTokens` **只保留尺寸类**：`space*`、`radius*`、`font*`。颜色全部迁出。
2. 新增 `ZentwikPalette extends ThemeExtension<ZentwikPalette>`，字段与原颜色 token 一一对应
   （`background` / `surface` / `border` / `textPrimary` / `textSecondary` / `textTertiary` /
   `accent` / `danger` / `success`）。
3. 两套亮度 × 四套强调色，共 8 个组合，用工厂构造，不写散落的 `Color(0x...)`：
   - 暗色：现有值（`#0E1117` / `#161B22` / `#2B313B` / `#E6EDF3` / `#8B949E` / `#6E7681`）
   - 亮色：背景 `#FFFFFF`、surface `#F6F8FA`、border `#D0D7DE`、
     textPrimary `#1F2328`、textSecondary `#59636E`、textTertiary `#818B98`
   - 强调色：`blue` `#4C8DFF`、`violet` `#A371F7`、`teal` `#39C5CF`、`amber` `#D29922`
     （亮色下 `amber` 与 `teal` 要压暗一档保证对比度，自己定具体值，但要在注释里写原因）
   - `danger` / `success` 两套亮度各给一个值，亮色下不要用暗色那份（对比度不够）
4. 扩展便捷访问：`extension ZentwikThemeContext on BuildContext { ZentwikPalette get palette }`，
   从 `Theme.of(context).extension<ZentwikPalette>()!` 取，`null` 时抛带说明的 `StateError`
   （说明「widget 跑在 buildZentwikTheme 之外」）。

## B. 主题构建

`buildZentwikTheme({Brightness brightness = Brightness.dark, String accent = 'blue', double textScale = 1.0})`
返回 `ThemeData`，`extensions: [palette]`，`colorScheme` 从 palette 派生，
`textTheme` 由 `textScale` 乘系数（`MediaQuery` 那套另说，见 D）。

未知 `accent` 字符串回落 `blue`，不要抛异常——设置文件可能是旧版本写的。

## C. 全量迁移

把 `lib/**` 里所有 `ZentwikTokens.<颜色>` 引用改成 `context.palette.<同名>`。
`const TextStyle(color: ZentwikTokens.textPrimary)` 这类会因为不再 const 而报错，
去掉 `const` 即可；`const` 丢失是可接受的代价，不要为了保 `const` 造两套颜色。

`lib/cards/**` 的九个卡片都要迁（它们现在是 `const` 一堆，改完自然散开）。

## D. 缩放与接线

1. `lib/app.dart`：`ZentwikApp` 从 `AppStateScope` 读 `settings`，用
   `theme: buildZentwikTheme(brightness: 由 settings.theme 决定, accent: settings.accent ?? 'blue')`
   构建主题；`settings` 为 `null` 时用暗色 + `blue`。
2. `uiScale`：用 `MediaQuery` 覆盖子树——`textScaler: TextScaler.linear(scale)`。
   这是文字与行高缩放，**不是**像素级缩放；在设置页把标签从「界面缩放」改成
   「文字缩放」，文案说明「影响字号与行高，不影响布局比例」。
3. 主题切换必须即时生效：`settings` 变化要能触发 `ZentwikApp` 重建（它已在
   `AppStateScope` 之下读 `settings` 的话，用 `AnimatedBuilder`/`ValueListenable` 接）。
   如果发现 `ZentwikApp` 层级拿不到更新（`appState` 在它内部创建 scope 且自身不变），
   可以调整 `lib/app.dart` 内部结构，但**不得**改 `lib/runtime/app_host.dart`。

## E. 设置页收尾

删掉上一轮加的那句「语言、主题、强调色与界面缩放会写入配置，但界面渲染尚未接入……」，
换成只针对语言的说明：「界面文案暂只有中文，该选项会写入配置供后续版本使用。」

## 验证

推 `feat/gui-protocol`，读这次 push 的 Actions（`Zentwik Next | GUI (Flutter)`），
`Analyze` 与 `Test` 都必须绿。

新增测试至少覆盖：

- `buildZentwikTheme` 两套亮度 + 四强调色的 palette 取值互不相同，未知 accent 回落 `blue`；
- `context.palette` 在 `buildZentwikTheme` 之外抛 `StateError`（用 `expect(..., throwsStateError)`）；
- 一个代表卡片（`CpuUsageCard`）在亮色 palette 下取到的是亮色 `textPrimary`——
  断言渲染出的 `Text` 的 `style.color`，不要断言截图。
