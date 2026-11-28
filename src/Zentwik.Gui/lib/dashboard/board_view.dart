import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';

import '../theme/theme.dart';
import 'board.dart';
import 'card_registry.dart';
import 'card_shell.dart';
import 'dashboard_kind.dart';
import 'masonry.dart';

/// 首页主工作区。
///
/// 布局是「跨列 + 变高」的 masonry：每张卡只占自己那几列的垂直区间。
/// 一张卡变高只推下它跨列范围内的卡，不会把整行邻居一起拉伸 ——
/// 这正是固定行列布局做不到的地方。
///
/// 编辑态才暴露拖拽把手、双轴缩放手柄和菜单；平时零按钮（`§2.8` 一页一个主操作）。
/// 卡片目前是纯展示内容，所以编辑态不禁用卡内交互；将来卡片内加按钮时，
/// 需要在这里包一层 `AbsorbPointer` 并把把手提到 Stack 上层。
class BoardView extends StatefulWidget {
  const BoardView({
    super.key,
    required this.board,
    required this.registry,
    required this.editing,
    required this.onMove,
    required this.onRemove,
    required this.onResizeSpan,
    required this.onSetHeight,
    required this.onAdd,
    this.padding = const EdgeInsets.all(ZentwikTokens.spaceLg),
  });

  final DashboardBoard board;
  final CardRegistry registry;
  final bool editing;

  /// 把第 [from] 项移到 [to]（[to] 为移除后的目标下标）。
  final void Function(int from, int to) onMove;
  final ValueChanged<DashboardKind> onRemove;
  final void Function(DashboardKind kind, int span) onResizeSpan;

  /// null 表示恢复自适应高度。
  final void Function(DashboardKind kind, double? height) onSetHeight;
  final VoidCallback onAdd;

  final EdgeInsets padding;

  /// 列数随宽度变化。窄到 560 以下退化为单列，与折起时间线后的宽屏并列不冲突。
  static int columnsFor(double width) {
    if (width < 560) {
      return 1;
    }
    if (width < 880) {
      return 2;
    }
    if (width < 1240) {
      return 3;
    }
    return 4;
  }

  static const double spacing = ZentwikTokens.spaceLg;

  @override
  State<BoardView> createState() => _BoardViewState();
}

class _BoardViewState extends State<BoardView> {
  int? _draggingIndex;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final available = constraints.maxWidth - widget.padding.horizontal;
        final columns = BoardView.columnsFor(available);
        final items = widget.board.items;

        if (items.isEmpty) {
          return EmptyBoardHint(onAdd: widget.onAdd);
        }

        return SingleChildScrollView(
          padding: widget.padding,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              MasonryGrid(
                columns: columns,
                spacing: BoardView.spacing,
                children: [
                  for (var i = 0; i < items.length; i++)
                    MasonryCell(
                      key: ValueKey(items[i].kind.wire),
                      span: math.min(items[i].span, columns),
                      height: items[i].height,
                      child: _BoardSlot(
                        index: i,
                        item: items[i],
                        columns: columns,
                        registry: widget.registry,
                        editing: widget.editing,
                        onDragStarted: () => setState(() => _draggingIndex = i),
                        onDragEnded: () => setState(() => _draggingIndex = null),
                        onMove: widget.onMove,
                        onRemove: widget.onRemove,
                        onResizeSpan: widget.onResizeSpan,
                        onSetHeight: widget.onSetHeight,
                      ),
                    ),
                ],
              ),
              // 拖到卡片之外时的唯一收口：显式给一条落点，避免「松手没反应」。
              if (widget.editing && _draggingIndex != null)
                _AppendDropStrip(
                  onDrop: (from) => widget.onMove(from, items.length - 1),
                ),
            ],
          ),
        );
      },
    );
  }
}

/// 一张卡的编辑态包装：拖拽源、落点、双轴缩放手柄、菜单。
class _BoardSlot extends StatefulWidget {
  const _BoardSlot({
    required this.index,
    required this.item,
    required this.columns,
    required this.registry,
    required this.editing,
    required this.onDragStarted,
    required this.onDragEnded,
    required this.onMove,
    required this.onRemove,
    required this.onResizeSpan,
    required this.onSetHeight,
  });

  final int index;
  final BoardItem item;
  final int columns;
  final CardRegistry registry;
  final bool editing;
  final VoidCallback onDragStarted;
  final VoidCallback onDragEnded;
  final void Function(int from, int to) onMove;
  final ValueChanged<DashboardKind> onRemove;
  final void Function(DashboardKind kind, int span) onResizeSpan;
  final void Function(DashboardKind kind, double? height) onSetHeight;

  @override
  State<_BoardSlot> createState() => _BoardSlotState();
}

class _BoardSlotState extends State<_BoardSlot> {
  final GlobalKey _cardKey = GlobalKey();

  /// 落点在卡片上半（true）还是下半（false）。
  bool? _dropBefore;

  double _dx = 0;
  double _dy = 0;
  double _spanStart = 1;
  double _heightStart = 0;

  /// 一个列单位的宽度，在拖拽开始时固定：跨越过程中卡片自身变宽，
  /// 若每帧重算会形成「变宽→单位变大→目标变小」的抖动。
  double _unit = 1;

  int get _effectiveSpan => math.min(widget.item.span, widget.columns);

  double _measuredHeight() {
    final box = _cardKey.currentContext?.findRenderObject();
    return box is RenderBox && box.hasSize ? box.size.height : 0;
  }

  double _measuredWidth() {
    final box = _cardKey.currentContext?.findRenderObject();
    return box is RenderBox && box.hasSize ? box.size.width : 1;
  }

  void _beginSpanDrag() {
    _dx = 0;
    _spanStart = _effectiveSpan.toDouble();
    _unit = (_measuredWidth() + BoardView.spacing) / _effectiveSpan;
    if (_unit <= 0) {
      _unit = 1;
    }
  }

  void _updateSpanDrag(DragUpdateDetails details) {
    _dx += details.delta.dx;
    final kind = widget.item.kind;
    final limit = kind.spanLimit(widget.columns);
    final target = (_spanStart + _dx / _unit).round().clamp(kind.minSpan, limit);
    if (target != _effectiveSpan) {
      widget.onResizeSpan(kind, target);
    }
  }

  void _beginHeightDrag() {
    _dy = 0;
    _heightStart = widget.item.height ?? _measuredHeight();
  }

  void _updateHeightDrag(DragUpdateDetails details) {
    _dy += details.delta.dy;
    // 8px 吸附：手感和间距刻度一致，也避免每像素落一次盘。
    final raw = _heightStart + _dy;
    final snapped = (raw / 8).roundToDouble() * 8;
    final target = snapped < kMinBoardItemHeight
        ? kMinBoardItemHeight
        : snapped;
    if (target != widget.item.height) {
      widget.onSetHeight(widget.item.kind, target);
    }
  }

  Widget _grip(BuildContext context) {
    return MouseRegion(
      cursor: SystemMouseCursors.grab,
      child: Draggable<int>(
        data: widget.index,
        dragAnchorStrategy: pointerDragAnchorStrategy,
        onDragStarted: widget.onDragStarted,
        onDragEnd: (_) => widget.onDragEnded(),
        feedback: _DragPill(item: widget.item),
        child: SizedBox(
          width: 18,
          height: 20,
          child: Icon(
            Icons.drag_indicator,
            size: 15,
            color: context.palette.textTertiary,
          ),
        ),
      ),
    );
  }

  Widget _menu(BuildContext context) {
    final palette = context.palette;
    final kind = widget.item.kind;
    final limit = kind.spanLimit(widget.columns);
    return SizedBox(
      width: 20,
      height: 20,
      child: PopupMenuButton<_CardAction>(
        tooltip: '组件设置',
        padding: EdgeInsets.zero,
        splashRadius: 14,
        iconSize: 15,
        icon: Icon(Icons.more_vert, size: 15, color: palette.textTertiary),
        onSelected: (action) {
          switch (action) {
            case _CardAction.wider:
              widget.onResizeSpan(kind, (_effectiveSpan + 1).clamp(1, limit));
            case _CardAction.narrower:
              widget.onResizeSpan(
                kind,
                (_effectiveSpan - 1).clamp(kind.minSpan, limit),
              );
            case _CardAction.autoHeight:
              widget.onSetHeight(kind, null);
            case _CardAction.remove:
              widget.onRemove(kind);
          }
        },
        itemBuilder: (context) => [
          PopupMenuItem(
            value: _CardAction.wider,
            enabled: _effectiveSpan < limit,
            child: const Text('加宽一列'),
          ),
          PopupMenuItem(
            value: _CardAction.narrower,
            enabled: _effectiveSpan > kind.minSpan,
            child: const Text('缩窄一列'),
          ),
          PopupMenuItem(
            value: _CardAction.autoHeight,
            enabled: !widget.item.autoHeight,
            child: const Text('高度自适应'),
          ),
          const PopupMenuDivider(),
          const PopupMenuItem(
            value: _CardAction.remove,
            child: Text('移出主工作区'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    if (!widget.editing) {
      return RepaintBoundary(
        child: DashboardCardShell(
          key: _cardKey,
          item: widget.item,
          child: widget.registry.buildCard(context, widget.item),
        ),
      );
    }

    return DragTarget<int>(
      onWillAcceptWithDetails: (details) => details.data != widget.index,
      onMove: (details) {
        final box = _cardKey.currentContext?.findRenderObject();
        if (box is! RenderBox) {
          return;
        }
        final before =
            box.globalToLocal(details.offset).dy < box.size.height / 2;
        if (before != _dropBefore) {
          setState(() => _dropBefore = before);
        }
      },
      onLeave: (_) {
        if (_dropBefore != null) {
          setState(() => _dropBefore = null);
        }
      },
      onAcceptWithDetails: (details) {
        final from = details.data;
        final before = _dropBefore ?? true;
        setState(() => _dropBefore = null);
        // 落点在本卡上半 → 插到本卡之前；下半 → 插到本卡之后。
        var to = before ? widget.index : widget.index + 1;
        if (from < to) {
          to -= 1;
        }
        widget.onMove(from, to);
      },
      builder: (context, candidate, rejected) {
        final hovering = candidate.isNotEmpty;
        final card = DashboardCardShell(
          key: _cardKey,
          item: widget.item,
          leading: _grip(context),
          trailing: _menu(context),
          outlined: hovering,
          dimmed: hovering,
          child: widget.registry.buildCard(context, widget.item),
        );

        return Stack(
          children: [
            card,
            // 右侧拉宽：按列吸附。10px 热区，够宽但不至于挡住内容。
            Positioned(
              top: 0,
              bottom: 0,
              right: 0,
              width: 10,
              child: MouseRegion(
                cursor: SystemMouseCursors.resizeLeftRight,
                child: GestureDetector(
                  behavior: HitTestBehavior.translucent,
                  onHorizontalDragStart: (_) => _beginSpanDrag(),
                  onHorizontalDragUpdate: _updateSpanDrag,
                ),
              ),
            ),
            // 底部拉高：8px 吸附。
            Positioned(
              left: 0,
              right: 10,
              bottom: 0,
              height: 10,
              child: MouseRegion(
                cursor: SystemMouseCursors.resizeUpDown,
                child: GestureDetector(
                  behavior: HitTestBehavior.translucent,
                  onVerticalDragStart: (_) => _beginHeightDrag(),
                  onVerticalDragUpdate: _updateHeightDrag,
                ),
              ),
            ),
          ],
        );
      },
    );
  }
}

enum _CardAction { wider, narrower, autoHeight, remove }

/// 拖拽时跟随光标的胶囊，而不是整卡残影：尺寸恒定，不会因为卡片高度变化抖动。
class _DragPill extends StatelessWidget {
  const _DragPill({required this.item});

  final BoardItem item;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Material(
      color: Colors.transparent,
      child: Container(
        padding: const EdgeInsets.symmetric(
          horizontal: ZentwikTokens.spaceMd,
          vertical: ZentwikTokens.spaceSm,
        ),
        decoration: BoxDecoration(
          color: palette.surface,
          borderRadius: BorderRadius.circular(ZentwikTokens.radiusSm),
          border: Border.all(color: palette.accent),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(item.kind.icon, size: 14, color: palette.accent),
            const SizedBox(width: ZentwikTokens.spaceSm),
            Text(
              item.kind.label,
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                fontWeight: FontWeight.w600,
                color: palette.textPrimary,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// 拖到卡片之外时的落点：只在拖拽进行中出现，平时不占任何视觉。
class _AppendDropStrip extends StatelessWidget {
  const _AppendDropStrip({required this.onDrop});

  final ValueChanged<int> onDrop;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return DragTarget<int>(
      onAcceptWithDetails: (details) => onDrop(details.data),
      builder: (context, candidate, rejected) {
        final active = candidate.isNotEmpty;
        return Container(
          height: 56,
          margin: const EdgeInsets.only(top: ZentwikTokens.spaceLg),
          alignment: Alignment.center,
          decoration: BoxDecoration(
            color: palette.accent.withValues(alpha: active ? 0.16 : 0.06),
            borderRadius: BorderRadius.circular(ZentwikTokens.radiusMd),
            border: Border.all(
              color: palette.accent.withValues(alpha: active ? 0.6 : 0.2),
            ),
          ),
          child: Text(
            '拖到这里移到末尾',
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: active ? palette.accent : palette.textTertiary,
            ),
          ),
        );
      },
    );
  }
}

/// 「添加组件」面板：列出还没放到首页上的组件。
///
/// 用对话框而不是常驻列表：添加是低频操作，不该在首页常驻一块区域。
Future<void> showAddComponentSheet(
  BuildContext context, {
  required List<DashboardKind> addable,
  required ValueChanged<DashboardKind> onPick,
}) {
  return showDialog<void>(
    context: context,
    builder: (context) {
      final palette = context.palette;
      return AlertDialog(
        backgroundColor: palette.surface,
        title: const Text('添加组件'),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: ZentwikTokens.spaceLg,
          vertical: ZentwikTokens.spaceSm,
        ),
        content: SizedBox(
          // 固定宽度上限，不用百分比（`§3.4.2`）。
          width: 420,
          child: addable.isEmpty
              ? Padding(
                  padding: const EdgeInsets.symmetric(
                    vertical: ZentwikTokens.spaceXl,
                  ),
                  child: Text(
                    '所有组件都已经在首页上了',
                    style: TextStyle(
                      fontSize: ZentwikTokens.fontBody,
                      color: palette.textSecondary,
                    ),
                  ),
                )
              : SingleChildScrollView(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      for (final kind in addable)
                        ListTile(
                          contentPadding: EdgeInsets.zero,
                          leading: Icon(
                            kind.icon,
                            size: 18,
                            color: palette.textTertiary,
                          ),
                          title: Text(
                            kind.label,
                            style: TextStyle(
                              fontSize: ZentwikTokens.fontBody,
                              fontWeight: FontWeight.w600,
                              color: palette.textPrimary,
                            ),
                          ),
                          subtitle: Text(
                            kind.description,
                            style: TextStyle(
                              fontSize: ZentwikTokens.fontCaption,
                              color: palette.textTertiary,
                            ),
                          ),
                          onTap: () {
                            onPick(kind);
                            Navigator.of(context).pop();
                          },
                        ),
                    ],
                  ),
                ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('关闭'),
          ),
        ],
      );
    },
  );
}
