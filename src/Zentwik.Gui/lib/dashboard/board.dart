import 'dashboard_kind.dart';

/// 主工作区组件数量上限：再多滚动就会失控，也挡住损坏文件里的垃圾数据。
const int kMaxBoardItems = 16;

/// 显式高度的下限，低于它卡片只能显示一行标题。
const double kMinBoardItemHeight = 96.0;

/// 一个已放置的组件。
///
/// [span] 是列跨度（存的是用户意图，渲染时按当前列数收敛）；
/// [height] 为 null 表示**自适应**——由内容决定高度，这是默认行为，
/// 避免为了填格子而制造空白。
class BoardItem {
  const BoardItem({
    required this.kind,
    required this.span,
    this.height,
  });

  final DashboardKind kind;
  final int span;
  final double? height;

  bool get autoHeight => height == null;

  BoardItem copyWith({int? span, double? height, bool clearHeight = false}) {
    return BoardItem(
      kind: kind,
      span: span ?? this.span,
      height: clearHeight ? null : (height ?? this.height),
    );
  }

  Map<String, Object?> toJson() => <String, Object?>{
        'kind': kind.wire,
        'span': span,
        if (height != null) 'height': height,
      };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is BoardItem &&
          kind == other.kind &&
          span == other.span &&
          height == other.height;

  @override
  int get hashCode => Object.hash(kind, span, height);

  @override
  String toString() => 'BoardItem(${kind.wire}, span=$span, h=$height)';
}

/// 首页主工作区的布局：**只是有序列表**。
///
/// 位置不是存下来的坐标 —— 坐标由 masonry 按顺序装箱算出来。
/// 这样窗口宽度一变，布局自动重排，不需要迁移旧坐标。
class DashboardBoard {
  const DashboardBoard(this.items);

  /// 首次启动的布局：只放**脉搏带 + 状态摘要**这几张。
  ///
  /// 刻意不是全集：逐核频率、逐逻辑处理器、内存、进程数都需要用户自己添加。
  /// 首页不该默认堆满实时监控指标（设计规范 §3.1「顶部脉搏带 + 扩展槽位」，
  /// 也对应「监控是可选功能」这条产品约束）。
  factory DashboardBoard.defaults() {
    return DashboardBoard(
      List<BoardItem>.unmodifiable([
        const BoardItem(kind: DashboardKind.cpuUsage, span: 1),
        const BoardItem(kind: DashboardKind.cpuTemperature, span: 1),
        const BoardItem(kind: DashboardKind.packagePower, span: 1),
        const BoardItem(kind: DashboardKind.moduleStatus, span: 1),
        const BoardItem(kind: DashboardKind.ledgerSummary, span: 2),
      ]),
    );
  }

  /// 容错解析：未知类型、超限、重复一律丢弃，不让坏文件把首页搞崩。
  static DashboardBoard? fromJson(Object? json) {
    if (json is! Map) {
      return null;
    }
    final rawItems = json['items'];
    if (rawItems is! List) {
      return null;
    }

    final seen = <DashboardKind>{};
    final parsed = <BoardItem>[];
    for (final raw in rawItems) {
      if (raw is! Map) {
        continue;
      }
      final wire = raw['kind'];
      if (wire is! String) {
        continue;
      }
      final kind = DashboardKind.tryParse(wire);
      if (kind == null || !seen.add(kind)) {
        continue;
      }
      final rawSpan = raw['span'];
      final span = (rawSpan is num ? rawSpan.toInt() : kind.defaultSpan)
          .clamp(kind.minSpan, kind.maxSpan)
          .toInt();
      final rawHeight = raw['height'];
      final height = rawHeight is num
          ? (rawHeight.toDouble() < kMinBoardItemHeight
              ? kMinBoardItemHeight
              : rawHeight.toDouble())
          : null;
      parsed.add(BoardItem(kind: kind, span: span, height: height));
      if (parsed.length >= kMaxBoardItems) {
        break;
      }
    }

    if (parsed.isEmpty) {
      return null;
    }
    return DashboardBoard(List<BoardItem>.unmodifiable(parsed));
  }

  final List<BoardItem> items;

  Set<DashboardKind> get kinds => {for (final item in items) item.kind};

  bool contains(DashboardKind kind) => kinds.contains(kind);

  /// 还没放到首页上的组件，用于「添加」面板。
  List<DashboardKind> get addable => [
        for (final kind in DashboardKind.values)
          if (!contains(kind)) kind,
      ];

  DashboardBoard add(DashboardKind kind) {
    if (contains(kind) || items.length >= kMaxBoardItems) {
      return this;
    }
    return DashboardBoard(
      List<BoardItem>.unmodifiable([
        ...items,
        BoardItem(kind: kind, span: kind.defaultSpan),
      ]),
    );
  }

  DashboardBoard remove(DashboardKind kind) {
    final next = [
      for (final item in items)
        if (item.kind != kind) item,
    ];
    if (next.length == items.length) {
      return this;
    }
    return DashboardBoard(List<BoardItem>.unmodifiable(next));
  }

  /// 把第 [from] 项插到 [to] 处；`to` 是移除后列表中的目标下标。
  DashboardBoard move(int from, int to) {
    if (from < 0 || from >= items.length) {
      return this;
    }
    final target = to.clamp(0, items.length - 1);
    if (from == target) {
      return this;
    }
    final next = List<BoardItem>.of(items);
    final moved = next.removeAt(from);
    next.insert(target, moved);
    return DashboardBoard(List<BoardItem>.unmodifiable(next));
  }

  DashboardBoard resizeSpan(DashboardKind kind, int span, {int? columns}) {
    final index = items.indexWhere((item) => item.kind == kind);
    if (index < 0) {
      return this;
    }
    final limit = kind.spanLimit(columns ?? kind.maxSpan);
    final next = (span < kind.minSpan ? kind.minSpan : span)
        .clamp(kind.minSpan, limit)
        .toInt();
    if (next == items[index].span) {
      return this;
    }
    final updated = List<BoardItem>.of(items);
    updated[index] = updated[index].copyWith(span: next);
    return DashboardBoard(List<BoardItem>.unmodifiable(updated));
  }

  DashboardBoard setHeight(DashboardKind kind, double? height) {
    final index = items.indexWhere((item) => item.kind == kind);
    if (index < 0) {
      return this;
    }
    final updated = List<BoardItem>.of(items);
    if (height == null) {
      updated[index] = updated[index].copyWith(clearHeight: true);
    } else {
      final clamped = height < kMinBoardItemHeight
          ? kMinBoardItemHeight
          : height.roundToDouble();
      if (clamped == items[index].height) {
        return this;
      }
      updated[index] = updated[index].copyWith(height: clamped);
    }
    return DashboardBoard(List<BoardItem>.unmodifiable(updated));
  }

  /// 一键回到自适应高度（清掉所有手工高度）。
  DashboardBoard clearHeights() {
    if (items.every((item) => item.autoHeight)) {
      return this;
    }
    return DashboardBoard(
      List<BoardItem>.unmodifiable([
        for (final item in items) item.copyWith(clearHeight: true),
      ]),
    );
  }

  Map<String, Object?> toJson() => <String, Object?>{
        'version': 1,
        'items': [for (final item in items) item.toJson()],
      };

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) {
      return true;
    }
    if (other is! DashboardBoard || other.items.length != items.length) {
      return false;
    }
    for (var i = 0; i < items.length; i++) {
      if (items[i] != other.items[i]) {
        return false;
      }
    }
    return true;
  }

  @override
  int get hashCode => Object.hashAll(items);
}
