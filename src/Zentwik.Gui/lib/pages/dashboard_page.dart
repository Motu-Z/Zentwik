import 'package:material_ui/material_ui.dart';

import '../dashboard/card_registry.dart';
import '../dashboard/dashboard_grid.dart';
import '../dashboard/dashboard_layout.dart';
import '../theme/tokens.dart';

/// 统一卡片外壳：背景、圆角、边框、内边距与标题行（右侧保留 resize 手柄位）。
class DashboardCardShell extends StatelessWidget {
  const DashboardCardShell({
    super.key,
    required this.card,
    required this.child,
    this.onResize,
    this.dragHandle,
  });

  final DashboardCard card;
  final Widget child;
  final VoidCallback? onResize;
  final Widget? dragHandle;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: ZentwikTokens.surface,
        borderRadius: BorderRadius.circular(ZentwikTokens.radiusMd),
        border: Border.all(color: ZentwikTokens.border),
      ),
      padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              if (dragHandle != null) ...[
                dragHandle!,
                const SizedBox(width: ZentwikTokens.spaceXs),
              ],
              Expanded(
                child: Text(
                  card.kind.label,
                  style: const TextStyle(
                    fontSize: ZentwikTokens.fontSubtitle,
                    fontWeight: FontWeight.w600,
                    color: ZentwikTokens.textPrimary,
                  ),
                ),
              ),
              IconButton(
                onPressed: onResize,
                tooltip: onResize == null ? null : '调整卡片宽度',
                constraints: const BoxConstraints(
                  minWidth: 28,
                  minHeight: 28,
                ),
                padding: EdgeInsets.zero,
                icon: Icon(
                  Icons.open_in_full,
                  size: 14,
                  color: onResize != null
                      ? ZentwikTokens.textSecondary
                      : ZentwikTokens.border,
                ),
              ),
            ],
          ),
          const SizedBox(height: ZentwikTokens.spaceSm),
          child,
        ],
      ),
    );
  }
}

/// Dashboard 页面骨架：纯展示与交互回调分发，不直接修改外部状态。
class DashboardPage extends StatelessWidget {
  const DashboardPage({
    super.key,
    required this.layout,
    required this.onLayoutChanged,
    this.registry = const CardRegistry(),
    this.onResizeCard,
  });

  static const double wideBreakpoint = 900.0;
  static const int wideColumns = 4;

  final DashboardLayout layout;
  final ValueChanged<DashboardLayout> onLayoutChanged;
  final CardRegistry registry;
  final ValueChanged<DashboardCard>? onResizeCard;

  @override
  Widget build(BuildContext context) {
    final visibleCards = layout.cards
        .where((card) => card.visible)
        .toList(growable: false);

    if (visibleCards.isEmpty) {
      return const Center(
        child: Padding(
          padding: EdgeInsets.all(ZentwikTokens.spaceXl),
          child: Text(
            '暂无可显示的仪表盘卡片',
            style: TextStyle(
              fontSize: ZentwikTokens.fontBody,
              color: ZentwikTokens.textSecondary,
            ),
          ),
        ),
      );
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        if (constraints.maxWidth >= wideBreakpoint) {
          return _buildWideGrid(context, visibleCards);
        }
        return _buildNarrowList(context, visibleCards);
      },
    );
  }

  Widget _buildWideGrid(
    BuildContext context,
    List<DashboardCard> visibleCards,
  ) {
    final rows = packIntoRows(
      visibleCards.map((card) => card.span).toList(growable: false),
      wideColumns,
    );

    var flatIndex = 0;
    final rowWidgets = <Widget>[];

    for (var r = 0; r < rows.length; r++) {
      final row = rows[r];
      var usedColumns = 0;
      final rowChildren = <Widget>[];

      for (var c = 0; c < row.length; c++) {
        final cell = row[c];
        final cardIndex = flatIndex++;
        final card = visibleCards[cardIndex];
        usedColumns += cell.span;

        if (c > 0) {
          rowChildren.add(const SizedBox(width: ZentwikTokens.spaceMd));
        }
        rowChildren.add(
          Expanded(
            flex: cell.span,
            child: _WideDraggableCard(
              index: cardIndex,
              card: card,
              onReorder: (fromVisible, toVisible) =>
                  _reorderVisible(visibleCards, fromVisible, toVisible),
              child: DashboardCardShell(
                card: card,
                onResize:
                    onResizeCard == null ? null : () => onResizeCard!(card),
                dragHandle: const Icon(
                  Icons.drag_indicator,
                  size: 16,
                  color: ZentwikTokens.textSecondary,
                ),
                child: registry.buildCard(context, card),
              ),
            ),
          ),
        );
      }

      if (usedColumns < wideColumns) {
        rowChildren.add(Spacer(flex: wideColumns - usedColumns));
      }

      if (r > 0) {
        rowWidgets.add(const SizedBox(height: ZentwikTokens.spaceMd));
      }
      rowWidgets.add(
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: rowChildren,
        ),
      );
    }

    return SingleChildScrollView(
      padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: rowWidgets,
      ),
    );
  }

  Widget _buildNarrowList(
    BuildContext context,
    List<DashboardCard> visibleCards,
  ) {
    return ReorderableListView.builder(
      padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
      buildDefaultDragHandles: false,
      itemCount: visibleCards.length,
      // onReorderItem 内部已处理「移除后目标下标前移」，不要再手工减一。
      onReorderItem: (oldIndex, newIndex) {
        _reorderVisible(visibleCards, oldIndex, newIndex);
      },
      itemBuilder: (context, index) {
        final card = visibleCards[index];
        return Padding(
          key: ValueKey(card.kind.wire),
          padding: const EdgeInsets.only(bottom: ZentwikTokens.spaceMd),
          child: DashboardCardShell(
            card: card,
            onResize: onResizeCard == null ? null : () => onResizeCard!(card),
            dragHandle: ReorderableDragStartListener(
              index: index,
              child: const Icon(
                Icons.drag_indicator,
                size: 16,
                color: ZentwikTokens.textSecondary,
              ),
            ),
            child: registry.buildCard(context, card),
          ),
        );
      },
    );
  }

  void _reorderVisible(
    List<DashboardCard> visibleCards,
    int fromVisible,
    int toVisible,
  ) {
    if (fromVisible < 0 ||
        fromVisible >= visibleCards.length ||
        toVisible < 0 ||
        toVisible >= visibleCards.length ||
        fromVisible == toVisible) {
      return;
    }

    final fromKind = visibleCards[fromVisible].kind;
    final toKind = visibleCards[toVisible].kind;
    final fromIndex = layout.cards.indexWhere((c) => c.kind == fromKind);
    final toIndex = layout.cards.indexWhere((c) => c.kind == toKind);
    if (fromIndex < 0 || toIndex < 0) {
      return;
    }
    onLayoutChanged(layout.reorder(fromIndex, toIndex));
  }
}

class _WideDraggableCard extends StatelessWidget {
  const _WideDraggableCard({
    required this.index,
    required this.card,
    required this.onReorder,
    required this.child,
  });

  final int index;
  final DashboardCard card;
  final void Function(int from, int to) onReorder;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return DragTarget<int>(
      onWillAcceptWithDetails: (details) => details.data != index,
      onAcceptWithDetails: (details) => onReorder(details.data, index),
      builder: (context, candidateData, rejectedData) {
        return Draggable<int>(
          data: index,
          feedback: Material(
            color: Colors.transparent,
            child: SizedBox(
              width: 280,
              child: Opacity(opacity: 0.85, child: child),
            ),
          ),
          childWhenDragging: Opacity(opacity: 0.4, child: child),
          child: child,
        );
      },
    );
  }
}
