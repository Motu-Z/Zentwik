import 'dart:math' as math;

import 'package:flutter/rendering.dart';
import 'package:flutter/widgets.dart';

/// 纯函数的跨列装箱：给定每项跨度与高度，算出每项在内容坐标系里的位置。
///
/// 规则（这是「某项变高不牵连其他列」的全部秘密）：
/// 维护每列已用到的底部 y，放置一项时在允许的起始列区间里找
/// **跨列范围内最高点最低** 的那个起点，然后只把这 [span] 列的底部推到
/// 该项下方。其余列一列都不动，所以不存在「整行被最高卡撑开」。
List<Rect> packMasonry({
  required List<int> spans,
  required List<double> heights,
  required int columns,
  required double columnWidth,
  required double spacing,
}) {
  assert(spans.length == heights.length, 'span 与 height 必须一一对应');
  if (columns <= 0 || spans.isEmpty) {
    return const <Rect>[];
  }

  final extent = List<double>.filled(columns, 0);
  final rects = <Rect>[];

  for (var i = 0; i < spans.length; i++) {
    final span = spans[i].clamp(1, columns).toInt();
    final height = heights[i] < 0 ? 0.0 : heights[i];

    var bestY = double.infinity;
    var bestColumn = 0;
    for (var start = 0; start + span <= columns; start++) {
      var y = 0.0;
      for (var c = start; c < start + span; c++) {
        y = math.max(y, extent[c]);
      }
      // 用容差而不是严格小于：浮点误差下同高的列仍取最左，
      // 否则同样的布局在两次计算间会左右跳动。
      if (y < bestY - 0.01) {
        bestY = y;
        bestColumn = start;
      }
    }
    if (!bestY.isFinite) {
      // 列数比 span 还少（列数刚变小的一帧）。
      bestY = extent.reduce(math.max);
      bestColumn = 0;
    }

    final width = columnWidth * span + spacing * (span - 1);
    rects.add(Rect.fromLTWH(
      bestColumn * (columnWidth + spacing),
      bestY,
      math.max(0, width),
      height,
    ));

    final bottom = bestY + height + spacing;
    for (var c = bestColumn; c < bestColumn + span; c++) {
      extent[c] = bottom;
    }
  }

  return rects;
}

/// 内容总高度：最后一行的底边（扣掉尾部间距）。
double masonryExtentHeight(List<Rect> rects) {
  if (rects.isEmpty) {
    return 0;
  }
  var bottom = 0.0;
  for (final rect in rects) {
    bottom = math.max(bottom, rect.bottom);
  }
  return bottom;
}

/// 每项的跨列与显式高度，通过 `ParentDataWidget` 传给 [MasonryGrid]。
class MasonryCell extends ParentDataWidget<MasonryParentData> {
  const MasonryCell({
    super.key,
    required this.span,
    this.height,
    required super.child,
  });

  /// 占几列；渲染时会被收敛到 `1..columns`。
  final int span;

  /// 显式高度；null 表示由内容决定（自适应）。
  final double? height;

  @override
  void applyParentData(RenderObject renderObject) {
    final data = renderObject.parentData! as MasonryParentData;
    var dirty = false;
    if (data.span != span) {
      data.span = span;
      dirty = true;
    }
    if (data.height != height) {
      data.height = height;
      dirty = true;
    }
    if (dirty) {
      final parent = renderObject.parent;
      if (parent != null) {
        parent.markNeedsLayout();
      }
    }
  }

  @override
  Type get debugTypicalAncestorWidgetClass => MasonryGrid;
}

class MasonryParentData extends ContainerBoxParentData<RenderBox> {
  int span = 1;
  double? height;
}

/// 变高、跨列、列间独立堆叠的布局。
///
/// 与 `Row` + `Expanded` 的区别：同一行里最高的卡片不会把邻居拉高，
/// 邻居也不共享行高 —— 每张卡只占自己那一列的垂直区间。
class MasonryGrid extends MultiChildRenderObjectWidget {
  const MasonryGrid({
    super.key,
    required this.columns,
    required this.spacing,
    super.children,
  });

  final int columns;
  final double spacing;

  @override
  RenderMasonry createRenderObject(BuildContext context) => RenderMasonry(
        columns: columns,
        spacing: spacing,
      );

  @override
  void updateRenderObject(BuildContext context, RenderMasonry renderObject) {
    renderObject
      ..columns = columns
      ..spacing = spacing;
  }
}

class RenderMasonry extends RenderBox
    with
        ContainerRenderObjectMixin<RenderBox, MasonryParentData>,
        RenderBoxContainerDefaultsMixin<RenderBox, MasonryParentData> {
  RenderMasonry({required int columns, required double spacing})
      // 命名参数不能以下划线开头，无法用 initializing formal 赋给私有字段。
      // ignore: prefer_initializing_formals
      : _columns = columns,
        // ignore: prefer_initializing_formals
        _spacing = spacing;

  int _columns;
  int get columns => _columns;
  set columns(int value) {
    if (_columns == value) {
      return;
    }
    _columns = value;
    markNeedsLayout();
  }

  double _spacing;
  double get spacing => _spacing;
  set spacing(double value) {
    if (_spacing == value) {
      return;
    }
    _spacing = value;
    markNeedsLayout();
  }

  @override
  void setupParentData(RenderBox child) {
    if (child.parentData is! MasonryParentData) {
      child.parentData = MasonryParentData();
    }
  }

  @override
  void performLayout() {
    final columns = _columns < 1 ? 1 : _columns;
    final width = constraints.hasBoundedWidth
        ? constraints.maxWidth
        : constraints.minWidth;
    final spacing = _spacing;
    final columnWidth =
        math.max(0.0, (width - spacing * (columns - 1)) / columns);

    // 第一遍：按各自跨度定宽测量真实高度。
    // 必须先量后放 —— 装箱依赖实际高度，预估高度会让列间出现错位。
    final cells = <({RenderBox child, int span, double? height})>[];
    RenderBox? child = firstChild;
    while (child != null) {
      final data = child.parentData! as MasonryParentData;
      final span = data.span.clamp(1, columns).toInt();
      final cellWidth = columnWidth * span + spacing * (span - 1);
      final fixedHeight = data.height;
      child.layout(
        fixedHeight == null
            ? BoxConstraints.tightFor(width: cellWidth)
            : BoxConstraints.tightFor(
                width: cellWidth,
                height: math.max(0, fixedHeight),
              ),
        parentUsesSize: true,
      );
      cells.add((child: child, span: span, height: fixedHeight));
      child = data.nextSibling;
    }

    // 第二遍：装箱并落位。
    final rects = packMasonry(
      spans: [for (final cell in cells) cell.span],
      heights: [for (final cell in cells) cell.child.size.height],
      columns: columns,
      columnWidth: columnWidth,
      spacing: spacing,
    );
    for (var i = 0; i < cells.length; i++) {
      final data = cells[i].child.parentData! as MasonryParentData;
      data.offset = rects[i].topLeft;
    }

    final height = masonryExtentHeight(rects);
    size = constraints.constrain(Size(width, height));
  }

  @override
  Size computeDryLayout(BoxConstraints constraints) {
    final width = constraints.hasBoundedWidth
        ? constraints.maxWidth
        : constraints.minWidth;
    return constraints.constrain(Size(width, 0));
  }

  @override
  double computeMinIntrinsicWidth(double height) => 0;

  @override
  double computeMaxIntrinsicWidth(double height) => 0;

  @override
  void paint(PaintingContext context, Offset offset) {
    defaultPaint(context, offset);
  }

  @override
  bool hitTestChildren(BoxHitTestResult result, {required Offset position}) {
    return defaultHitTestChildren(result, position: position);
  }
}
