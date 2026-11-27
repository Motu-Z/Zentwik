# 任务 02：Dashboard 布局引擎（纯 Dart，无 Widget）

**前置**：任务 01 已合入 `next`（`lib/protocol/`、`lib/state/` 存在）。
**执行者**：Gemini（Linux）。
**审查与 Windows 验证**：WorkBuddy。

---

## 0. 一句话

实现**布局模型 + 增删改排序算法 + 序列化**，使「用户自定义首页卡片」这件事在模型层完整成立。**不写任何 Widget、不写渲染、不写拖拽手势**——那些要等设计 token 和 Windows 侧验证。

## 1. 边界（越界即返工）

| 可以做 | 不可以做 |
|---|---|
| 新增 `lib/dashboard/` 下的模型与算法 | 新建任何 `Widget` / `State` / `Page`（本轮完全不碰 UI） |
| 新增 `test/dashboard/` 下的单测 | 修改 `lib/platform/**`、`lib/protocol/**`、`lib/state/**` |
| 读 `docs/coordination/` 与 `docs/protocol/gui-contract.md` | 改任何 `.cs`、`pubspec.yaml`（不加依赖）、`scripts/` |
| `flutter analyze` / `flutter test` | `flutter build windows`（Linux 无 MSVC） |

## 2. 卡片清单（第一版，写死在这个范围）

每一张卡片有两个属性：`span`（跨列数，1–4）与 `minSpan`/`maxSpan`（允许范围）。

| kind（wire 值） | 标题（中文） | 默认 span | 允许 span | 可删除 |
|---|---|---|---|---|
| `cpuUsage` | CPU 占用 | 1 | 1–2 | 是 |
| `cpuTemperature` | 封装温度 | 1 | 1–2 | 是 |
| `packagePower` | 封装功耗 | 1 | 1–2 | 是 |
| `coreClocks` | 逐核频率 | 2 | 2–4 | 是 |
| `processorUsage` | 逐逻辑处理器占用 | 2 | 2–4 | 是 |
| `memory` | 内存 | 1 | 1–2 | 是 |
| `processCount` | 进程数 | 1 | 1–2 | 是 |
| `ledgerSummary` | 账本概览 | 2 | 2–4 | 是 |
| `moduleStatus` | 模块状态 | 1 | 1–2 | 是 |

**不可删除的卡片不存在**（与 FlClash 不同：它必须保留启动按钮，我们没有这个约束）。但**布局不能为空**——全部删完时自动回落默认布局。

## 3. 交付物

### 3.1 `lib/dashboard/dashboard_kind.dart`

```dart
enum DashboardKind {
  cpuUsage('cpuUsage', 'CPU 占用', 1, 1, 2),
  ...
  ;
  const DashboardKind(this.wire, this.label, this.defaultSpan, this.minSpan, this.maxSpan);
  final String wire;
  final String label;
  final int defaultSpan;
  final int minSpan;
  final int maxSpan;

  static DashboardKind? tryParse(String wire);   // 未知值返回 null，不抛
}
```

要求：

- `wire` 是持久化与跨端契约的唯一标识，**不要用 `name` 或 `index`**（枚举改名会破坏已存布局）
- `label` 是界面兜底文案；正式文案后续走 `lib/l10n`，本轮不接

### 3.2 `lib/dashboard/dashboard_layout.dart`

```dart
class DashboardCard {
  const DashboardCard({required this.kind, required this.span, this.visible = true});
  final DashboardKind kind;
  final int span;        // 已 clamp 到 [kind.minSpan, kind.maxSpan]
  final bool visible;
  DashboardCard copyWith({int? span, bool? visible});
}

class DashboardLayout {
  const DashboardLayout(this.cards);
  final List<DashboardCard> cards;      // 顺序即展示顺序；只含 visible 与不可见项，不剔除
  static DashboardLayout get defaults;  // 见 §2 默认 span，全部 visible
}
```

必须提供：

| 方法 | 语义 |
|---|---|
| `DashboardLayout.normalize(Iterable<DashboardCard>)` | 去重（同 kind 保留首次）、`span` clamp 到合法区间、**空列表回落 `defaults`**、上限 12 张 |
| `resize(kind, span)` | clamp 后返回新实例；未知 kind 原样返回 |
| `reorder(from, to)` | 越界不抛，返回原实例；否则 `removeAt` + `insert` |
| `remove(kind)` | 删后为空 → `defaults` |
| `add(kind)` | 已存在则原样返回；否则追加默认 span |
| `setVisible(kind, visible)` | 全部隐藏时**不**回落默认（隐藏 ≠ 删除） |
| `toJson()` / `fromJson(Object?)` | 见 §4 |

**全部返回新实例，不改自身**（模型不可变）。`==` 与 `hashCode` 按内容实现，便于测试断言。

### 3.3 `lib/dashboard/dashboard_grid.dart`

纯函数，不依赖 Widget 层：

```dart
class DashboardCell { final int column; final int span; }
List<List<DashboardCell>> packIntoRows(List<int> spans, int columns);
```

规则：

- 按顺序贪心填行，一行内 `span` 求和 ≤ `columns` 就继续放，超出就换行
- 单张 `span > columns` 时按 `columns` 处理（宽屏 2 列时 4 跨度卡占满整行）
- 返回结构保证：每行 `span` 之和 ≤ `columns`；行顺序与输入顺序一致；所有卡恰好出现一次

### 3.4 `test/dashboard/`

至少 12 例，覆盖：

1. `defaults` 非空且所有 span 在允许区间
2. `normalize` 去重保留首次出现
3. `normalize` clamp 超出区间的 span（含 0 与负数）
4. `normalize` 空输入 → `defaults`
5. `resize` 超上限夹到 `maxSpan`，超下限夹到 `minSpan`
6. `reorder` 正向、反向、`from == to`、越界
7. `remove` 到空 → `defaults`；`add` 重复 → 原实例
8. `setVisible(false)` 全部隐藏后仍是空可见列表，不回落默认
9. `toJson`/`fromJson` 往返相等
10. `fromJson` 遇未知 `wire` 跳过该项，不抛
11. `fromJson` 遇缺 `span`/缺 `visible` 用默认值与 `true`
12. `packIntoRows`：2 列下 `[2,1,1]` → `[[2],[1,1]]`；4 列下 `[2,1,1]` → `[[2,1,1]]`；`[4,4]` 在 2 列下 → `[[2],[2]]`

## 4. 持久化格式（写死在测试里，不许自创字段名）

```json
{
  "version": 1,
  "cards": [
    {"kind": "cpuUsage", "span": 1, "visible": true},
    {"kind": "coreClocks", "span": 2, "visible": false}
  ]
}
```

- `version` 缺失按 1 处理；未来版本 > 1 时**保守处理：读得懂的部分照读，不认识的 kind 丢弃**，不抛
- `visible` 缺失按 `true`
- `span` 缺失按该 kind 的 `defaultSpan`
- 顶层不是 Map、`cards` 不是 List、单项不是 Map → 一律回落 `defaults`，不抛异常

**这一层是本地偏好，不进后端协议**。不要加任何 `AgentConnection` / `request()` 调用。

## 5. 自检

```bash
cd src/Zentwik.Gui
flutter analyze      # 0 issues（warning 也算失败）
flutter test         # 全绿
```

## 6. 报告格式

```
## 改了哪些文件
## 自检输出
## 未决问题
```

不写"已完成""效果良好"。只放可核对的输出。

## 7. 已知坑

- 不要用 `DashboardKind.values.indexOf` 做持久化 id —— 顺序变了所有用户布局会错位，一律用 `wire`
- `normalize` 里"去重保首次"和"clamp"的先后顺序会影响结果：先 clamp 再去重
- 测试里不要断言 `toString()`，只断言字段
