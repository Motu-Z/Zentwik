# 任务 09：卡片内容的自适应重排（只定逻辑，不调风格）

首页布局已经能自由排列、独立调宽高、持久化。但**卡片内部还是死的**：
每张卡都按"最窄情况"写死一套排布，拉到 2 跨、4 跨时不会变，显式调高后
要么上方一大片空白、要么内容被 `SingleChildScrollView` 静默切掉。

本轮只解决一件事：**卡片内部按自己拿到的实际尺寸改变信息组织方式**。
不换字体、不调配色、不加动画、不改布局系统。

## 允许修改

`lib/dashboard/**`、`lib/cards/**`、`lib/widgets/**`、`lib/theme/tokens.dart`
（仅允许新增一个字号常量，见 C.6）、以及对应测试。

## 不得修改

`lib/protocol/**`、`lib/state/**`、`lib/platform/**`、`lib/runtime/**`、
`lib/main.dart`、`lib/dashboard/board.dart` 的 JSON 结构、`pubspec.yaml`、
任何 `.cs`；不得改 `DashboardKind` 各类型的 `minSpan`/`maxSpan`；
不得改既有测试的断言语义（新增用例可以）。

---

## A. 先立一个"尺寸合同"

### A.1 卡片需要知道自己多大

现状：`DashboardCardShell` 把内容塞进 `Column`，内容只能靠隐式约束猜。
改为由外壳**显式下发**尺寸信息。

新建 `lib/dashboard/card_metrics.dart`：

```dart
enum CardWidthClass { compact, regular, wide }
enum CardHeightClass { unbounded, tight, regular, tall }

class CardMetrics {
  /// 正文可用宽（已扣掉卡片内边距，但**不含**外壳标题行）。
  final double bodyWidth;

  /// 正文可用高；null 表示自适应（高度由内容决定，约束无界）。
  final double? bodyHeight;

  CardWidthClass get widthClass;
  CardHeightClass get heightClass;
}
```

取值来源：`DashboardCardShell` 内部在正文外层加一个 `LayoutBuilder`，
读 `constraints.maxWidth` 与 `constraints.maxHeight`，再经
`InheritedWidget`（`_CardMetricsScope`）下发。

卡片一律通过 `CardMetrics.of(context)` 读取。

**`CardMetrics.of` 必须有兜底**：找不到 scope 时返回
`CardMetrics.unknown`（`bodyWidth = 0`、`bodyHeight = null`），
并在这种情况下按 `compact` + `unbounded` 处理。理由：卡片要能脱离首页单独
排版、单独测试，不能因为缺一个 InheritedWidget 就抛异常。

### A.2 断点数值（写死在这里，别处不许再出现）

```dart
// 按「正文宽」分档。推导依据（4 列布局、窗口 1440、时间线展开 320）：
//   1 跨 ≈ 255 卡宽 → 正文 223
//   2 跨 ≈ 526 卡宽 → 正文 494
//   3 列布局时 1 跨 ≈ 345 → 正文 313
static const double widthCompactMax = 220;   // < 220 → compact
static const double widthWideMin   = 380;    // ≥ 380 → wide

// 按「正文高」分档；kMinBoardItemHeight(96) 扣除标题 20 + 间距 12 + 内边距 32 ≈ 32
static const double heightTightMax   = 112;
static const double heightTallMin    = 320;
```

**禁止按 `span` 判断档位。** 同一个 span=2，4 列窗口是 ~706px、2 列窗口是
~345px，跨档完全不同。span 只能用于 `DashboardKind` 的合法范围校验。

### A.3 禁止用 MediaQuery 判断宽度

卡片内**不得**出现 `MediaQuery.of(context).size.width` /
`MediaQuery.sizeOf(context)`。

原因：时间线可以在窗口尺寸不变的情况下从 260 拖到 520、或直接隐藏，
主工作区可用宽度会变但 `MediaQuery` 数字不变。用窗口宽度判断，
这段时间卡片会保持错误的档位。这一条会写进验收（见 F.3）。

---

## B. 高度方向的统一策略（本轮最重要的规则）

**高度不足时的降级顺序，逐级尝试，前一级够用就不许用后一级：**

1. **压紧间距**——同一组间距 token 降一档（`spaceLg`→`spaceMd`→`spaceSm`→`spaceXs`）
2. **减少条目数**——列表类卡少显示几条，并在末尾显示 `+N 项`
3. **隐藏次要信息块**——只隐藏"解释性文字"，不隐藏数据本身
4. **内部滚动**——仅当 1–3 都做不到时才允许，且必须让用户看出还有内容
   （`Scrollbar` 或明确的下沿渐隐）

**严禁**：静默裁切、把字号降到 `fontCaption` 以下、用 `Stack` 叠盖、
把内容压成不可读的重叠。

**高度富余时的允许动作，按优先级：**

1. 列表类：多显示真实条目（不超过数据本身条数）
2. 单值类：数值换更大字号档（`fontTitle` → `fontDisplay`）
3. 都做不到 → 内容顶部对齐，剩余空间就是空白

**严禁**用装饰元素（分隔线、占位图形、无意义图标）去"填满"空白。
用户自己把卡拉高造成的空白是他选的；为了掩盖空白而加装饰才是问题。

---

## C. 实现纪律（会被 review 卡住的点）

1. **决策逻辑必须是纯函数**，与 Widget 分离，放在 `lib/cards/` 下
   （或 `lib/dashboard/`），签名形如：

   ```dart
   CoreClocksLayout resolveCoreClocks({
     required CardMetrics metrics,
     required int coreCount,
   });
   ```

   返回一个小 `class`（含 `columns` / `dense` / `visibleRows` 之类字段）。
   Widget 里只做 `final layout = resolveXxx(...)` 然后按字段分支。

   理由：这套逻辑有断点、有取整、有上下限夹取，靠 widget 测试覆盖太慢，
   纯函数可以逐档写单测。

2. 卡片保持 `StatelessWidget`，**不引入本地状态**。尺寸变化完全由外部约束驱动。

3. 所有卡片的条目数、列数计算都要**夹取到合法区间**：
   至少 1 条、至少 1 列；`coreCount`/`module` 列表为空时走空态，不要算出 0 列。

4. 列表类卡"显示 N 条 + 还有 M 项"里的 M 要**真实计算**，不许写死。

5. 卡片不得读取 `AppState` / `AgentRuntime`，数据仍由 `default_registry.dart` 注入。

6. `ZentwikTokens` **只允许新增一个常量**：

   ```dart
   /// 单值卡在 wide / tall 档用的主数值字号。
   /// 与 fontTitle(20) 保持可分辨差距（refactoring-ui §4.1：相邻档差 <15% 肉眼分不出）。
   static const double fontDisplay = 28.0;
   ```

   不许改其他任何 token 值，不许新增间距/圆角档。

7. 不许新增 pub 依赖。

---

## D. 九张卡各自的自适应契约

术语：`compact` / `regular` / `wide` 按 A.2 的**正文宽**；
`tight` / `regular` / `tall` 按 A.2 的**正文高**；`unbounded` = 自适应高度。

### D.1 `cpuUsage` — CPU 占用

可用字段：`cpuTotalPercent`、`processorUtilization`（逐逻辑处理器，可空）。

| 条件 | 行为 |
|---|---|
| compact | 数值 `fontTitle` 在上，进度条在下（现状） |
| regular / wide | 数值与进度条**同排**：数值左、条右 `Expanded` |
| wide + 有 `processorUtilization` | 数值下方加一行副信息：`峰值 87% · 均值 41%`（`fontCaption`、textTertiary） |
| tight | 只留数值 + 条，去掉副信息行 |
| tall | 允许数值升 `fontDisplay` |

**注意**：副信息的"均值"是**逻辑处理器**平均值（对 `processorUtilization`
求平均），不是物理核均值。文案必须写"逻辑处理器均值"或干脆写"均值"，
**不得**写"逐核均值"。**不得**改用 `coreClocks` 计算——那是 D.6 的数据。

### D.2 `cpuTemperature` — 封装温度

可用字段：`temperatureCelsius`。**没有** Tjmax/阈值字段，不许编。

现在颜色阈值（<70 success / <85 accent / ≥85 danger）散在卡里。把它抽成
纯函数，颜色与文字**必须同源**：

```dart
({Color color, String label}) describeTemperature(ZentwikPalette p, double? c)
// 正常 / 偏高 / 过热
```

| 条件 | 行为 |
|---|---|
| compact / tight | 只有数值（status 语不出现在窄卡，避免挤压） |
| regular / wide | 数值 + 同色系状态词（`fontCaption`），**文字与数值同色** |
| tall | 数值升 `fontDisplay` |

只靠颜色表达状态不可读（`§5.7`），所以 regular 以上必须带文字。

### D.3 `packagePower` — 封装功耗

可用字段：`packagePowerWatts`、`sensorDetail`（可空字符串）。

| 条件 | 行为 |
|---|---|
| tight | 只留数值 |
| compact / regular | 数值 + 副行（`sensorDetail` 非空则用它，否则用固定说明文案） |
| wide | 同上，但副行限 1 行 `ellipsis`；**不许**因为宽就把 `sensorDetail` 铺满两行 |
| tall | 数值升 `fontDisplay` |

`sensorDetail` 可能很长（服务端原样透传），必须 `maxLines: 1` + `ellipsis`，
且副行宽度不足时优先保数值不被挤。

### D.4 `memory` — 内存

可用字段：`memoryTotalBytes`、`memoryAvailableBytes`。

当前用 `Row(spaceBetween)` 放 `"12.3 GB / 31.9 GB"` 和 `"39%"`，
**1 跨窄卡会溢出**——这是本轮要修的实际缺陷。

| 条件 | 行为 |
|---|---|
| compact | 拆两行：第一行已用容量（`fontSubtitle`），第二行 `占用 39% · 共 31.9 GB`（`fontCaption`）；进度条在下 |
| regular / wide | 现状单行左右分置 |
| tight | 只留 `已用 / 总`（`fontSubtitle`）+ 条，去掉百分比文字（条本身已表达比例） |
| tall | 已用容量升 `fontDisplay`，总量降为副行 |

GB 文本绝对不许溢出：所有数值 `Text` 加 `maxLines: 1` + `ellipsis`，
必要时用 `FittedBox(fit: BoxFit.scaleDown)`，但**不得**缩到 `fontCaption` 以下。

### D.5 `processCount` — 进程数

可用字段：`processCount`、`processorCount`。**没有**线程数，不许编。

| 条件 | 行为 |
|---|---|
| compact | 数值 + 固定说明（现状） |
| regular / wide | 数值 + 一行两项指标：`进程 N · 逻辑处理器 M`（`fontCaption`） |
| tight | 只留数值 |
| tall | 数值升 `fontDisplay` |

### D.6 `coreClocks` — 逐核频率（本轮最复杂的一张）

可用字段：`coreClocks[i].ccd / .core / .megahertz`，`fMaxMhz`（构造参数）。

现在是 16 行 `Row(标签76 + 条 + 数值68)`，等于把最贵的面积用来重复
16 次同样的结构。要改成**二维自适应**：

```dart
class CoreClocksLayout {
  final int columns;   // 1 / 2 / 4
  final bool dense;    // 窄列时标签省略 CCD 前缀，只显示 C01
  final int rowHeight; // 用于高度预算
  final int visibleRows; // 高度不够时最多显示几行
}
```

决策顺序（**必须按这个顺序**，这是本轮的核心逻辑）：

1. **宽度先定上限**：`bodyWidth < 300` → 最多 1 列；
   `< 560` → 最多 2 列；`≥ 560` → 最多 4 列。
2. **高度再决定要不要加列**：估算
   `rowsNeeded = ceil(coreCount / columns)`，
   `neededHeight = rowsNeeded * rowHeight`。
   若 `bodyHeight != null && neededHeight > bodyHeight`，则**尝试加列**
   （1→2→4，受第 1 步上限约束）。
3. 加列后仍放不下 → `visibleRows = floor(bodyHeight / rowHeight)`（至少 1），
   末尾显示 `还有 N 核未显示`，**不要滚动**（滚动在这里极难用）。
4. `bodyHeight == null` → 不加列（按宽度给的列数），`visibleRows = rowsNeeded`，
   高度自适应。

**`rowHeight` 必须是命名常量**，不许散落魔法数。当前单行约 20px，
自己实测后定值并在测试里锁住。

`dense` 规则：列宽不足时标签从 `CCD0-C01` 退化为 `C01`。
退化后仍需能区分核（`ccd` 相同则 `C01` 唯一；若 `ccd` 为空则保留完整格式）。

**禁止**把这张卡改成热力网格或任何需要新数据的图形——那是风格轮的事。
本轮只允许"分行/分列/省略标签"。

### D.7 `processorUsage` — 逻辑处理器

可用字段：`processorUtilization`（`List<double>`，元素个数 = 逻辑处理器数）。

现在是 `Wrap` + 固定 8px 宽、28px 高的竖条。32 个逻辑处理器在 1 跨里会
挤成两行且高度不受控。

| 条件 | 行为 |
|---|---|
| 任意 | 竖条宽度自适应：`clamp((bodyWidth - gaps) / count, 4, 14)`，**向下取整**避免累计溢出 |
| `bodyHeight == null` | 条高固定 28（现状） |
| `bodyHeight != null` | 条高 `clamp(bodyHeight - 需要的行数间距, 20, 120)`，让它填满可用高 |
| 条数过多换行 | 允许 `Wrap` 换行，**但总高必须 ≤ bodyHeight**；放不下时逐步收窄条宽（4 是下限），仍放不下再减少显示条数并在末尾写 `+N` |

**不许**把多个逻辑处理器合并成一格（会失真）。**不许**用滚动。

### D.8 `ledgerSummary` — 调度账本

可用字段：`entries`（`LedgerEntry`：`target` / `previousValue` / `actualValue` /
`source` / `status` / `updatedAt`）。

现在：一个大数字 + 按 namespace 分组计数。宽而矮时上下堆叠浪费空间。

```dart
class LedgerSummaryLayout {
  final bool splitColumns;  // 总数字与分组列表左右分栏
  final int maxGroups;      // 最多显示几个分组，超出写 +N
  final int maxEntries;     // 可选，见下
}
```

| 条件 | 行为 |
|---|---|
| compact | 大数字在上，分组列表在下，`maxGroups = 3` |
| regular | 同上，`maxGroups = 5` |
| wide | `splitColumns = true`：左列大数字（固定宽），右列分组列表 |
| tight | 只留大数字，`maxGroups = 0` |
| `bodyHeight != null` | `maxGroups = floor((bodyHeight - 数字区高) / 行高)`，夹取 `0..分组总数`，超出写 `还有 N 类` |

**本轮不做**"宽档显示具体账本条目详情"——那是信息增强，不是自适应，
留给后续单独评估。

### D.9 `moduleStatus` — 模块状态

可用字段：`ModuleDto.id / name / enabled / state`。列表卡，逻辑同 D.8。

| 条件 | 行为 |
|---|---|
| 任意 | 每行：状态点 + 名称（`Expanded`）+ 状态词 |
| compact | 状态词已是 2–3 字（就绪/降级/不可用/未启用），**不需要**退化；已实测 1 跨 223px 放得下 |
| `bodyHeight != null` | `visibleRows = floor(bodyHeight / 行高)`（至少 1），末尾 `还有 N 个模块` |
| `bodyHeight == null` | 全显示 |

**不许**因为窄就把状态词换成纯色点（只靠颜色不可读，`§5.7`）。

---

## E. 空态与缺数据的一致处理

现有 `CardEmptyHint` 保留。但要注意区分三件事，不要混用：

| 情形 | 呈现 |
|---|---|
| 整份遥测还没到（`telemetry == null`） | `CardEmptyHint('暂无数据')` |
| 字段缺失/非法（NULL、负、非有限） | `kMissingValue`（`—`），字号与正常数值一致 |
| 列表为空（`modules.isEmpty` / `clocks.isEmpty`） | `CardEmptyHint('无模块')` 这类具体文案 |

**不许**在窄卡里把空态文案截断成半句——空态文案必须 `maxLines: 2` 以上且
随宽度自然换行。

---

## F. 验收标准

### F.1 纯函数单测（主力）

每个 `resolveXxx` 函数必须有边界用例：断点值 `-1`、`==`、`+1` 三档，
且覆盖 `bodyHeight == null` 与有限值两种情况。建议放在
`test/cards/adaptive_test.dart`。

必须覆盖的具体断言（举例）：
- `resolveCoreClocks(bodyWidth: 219, ...) .columns == 1`，`221` 时按规则升档
- 16 核 + `bodyWidth: 500`（2 列上限）+ `bodyHeight: 120` → 加列到 2 后仍不够 →
  `visibleRows` 有限且 > 0
- `resolveLedger(maxGroups)` 在分组只有 2 个时不会被算成 `+N`
- `processorUsage` 条宽在 32 核 + 223px 下不产生负值或 0

### F.2 Widget 层不溢出

写一组参数化 widget 测试：9 种 `DashboardKind` × span `{1,2,4}` ×
`height {null, 96, 400}`，每个组合 `pumpWidget` 后断言
`tester.takeException()` 为 `null`。

（`setSurfaceSize` 控制外层宽度，`BoardItem.height` 控制显式高度。）

### F.3 时间线调宽触发重排（关键回归）

新增一条 widget 测试：固定窗口尺寸不变，把时间线宽度从 260 调到 520，
断言某张卡片的 `CardMetrics.bodyWidth` 变小（通过一个测试用的探针 widget 读
`CardMetrics.of`）。这条能直接抓住"用 MediaQuery 判断宽度"的错误实现。

### F.4 构建与运行

- `flutter analyze --no-fatal-infos` 无 issue
- `flutter test --exclude-tags smoke` 全绿，且不少于现有 82 例
- `flutter build windows --debug` 通过
- 实机启动，Debug/Release 都出窗口

### F.5 实机手验清单（写进报告）

1. 逐核频率：1 跨（应自动收到 2 跨）与 4 跨的列数明显不同；
2. 逐核频率：把高度拖到 120 左右，出现 `还有 N 核未显示` 而不是滚动条；
3. 模块状态：拖高到能多显示几行时，条目数真的变多（不是空白变多）；
4. 内存卡：1 跨时 `12.3 GB / 31.9 GB` 不溢出（这是改动前的缺陷）；
5. 时间线从展开拖到折叠再到隐藏，卡片信息密度逐级上升且不闪错档。

---

## G. 交付报告要点

1. `CardMetrics` 的传递方式与兜底行为；
2. 九张卡的 `resolveXxx` 签名与断点决策表（表格形式）；
3. 高度降级四级顺序分别在哪几张卡里被真正用到；
4. F.1–F.3 的测试用例数与覆盖的边界；
5. 构建/运行结果，以及 F.5 手验逐条结论；
6. 明确列出：哪些卡在哪些档位下仍然没有差异化行为，以及原因
   （例如数据不足，不许留空话）。

---

## H. 明确不属于本轮（不要顺手做）

以下都是真实待办，但属于后续轮次，本轮**碰了会分散 review 注意力**：

- 趋势曲线 / sparkline（需要把 `TelemetryState.samples` 接进来）
- 进出场动画、视口外停帧（`TickerMode`）
- 数字等宽 `tabular-nums`
- 字号档位收敛（13 与 12 只差 8%，是全局问题）
- 时间线事件分类扩充（场景、接管）
- 逐核频率改热力网格（需要新的配色语义，属风格轮）
