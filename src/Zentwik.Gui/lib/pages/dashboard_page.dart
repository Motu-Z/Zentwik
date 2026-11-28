import 'package:material_ui/material_ui.dart';

import '../dashboard/board_view.dart';
import '../dashboard/card_registry.dart';
import '../theme/theme.dart';
import '../timeline/timeline_panel.dart';
import '../workspace/workspace.dart';

/// 首页：主工作区（自由组件）+ 全局时间线（独立区域）。
///
/// 两块**各自独立滚动**，所以主工作区任何一张卡变高都不会把时间线整体下推。
/// 时间线隐藏时整块不渲染，空间直接还给主工作区。
class DashboardPage extends StatelessWidget {
  const DashboardPage({
    super.key,
    required this.workspace,
    required this.registry,
  });

  final Workspace workspace;
  final CardRegistry registry;

  /// 窄窗口下时间线自动折成窄轨：这时它已经在挤占主工作区的可读宽度了。
  static const double timelineAutoCollapseWidth = 900;

  @override
  Widget build(BuildContext context) {
    // 页面自己订阅工作区，不依赖外层替它套 AnimatedBuilder ——
    // 否则单独使用这个页面时，编辑开关和布局改动都不会重建。
    return ListenableBuilder(
      listenable: workspace,
      builder: (context, _) => _buildBody(context),
    );
  }

  Widget _buildBody(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final collapsed = workspace.timelineCollapsed ||
            constraints.maxWidth < timelineAutoCollapseWidth;
        final boardWidth = constraints.maxWidth -
            workspace.timelineOccupiedWidth -
            ZentwikTokens.spaceLg * 2;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            _Toolbar(
              editing: workspace.editing,
              onToggleEditing: () => workspace.setEditing(!workspace.editing),
              onAdd: () => _addComponent(context),
              onShowTimeline:
                  workspace.timelineHidden ? workspace.toggleTimelineHidden : null,
            ),
            Expanded(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Expanded(
                    child: BoardView(
                      board: workspace.board,
                      registry: registry,
                      editing: workspace.editing,
                      onMove: workspace.move,
                      onRemove: workspace.remove,
                      onResizeSpan: (kind, span) => workspace.resizeSpan(
                        kind,
                        span,
                        columns: BoardView.columnsFor(boardWidth),
                      ),
                      onSetHeight: workspace.setHeight,
                      onAdd: () => _addComponent(context),
                    ),
                  ),
                  if (workspace.timelineVisible)
                    TimelinePanel(
                      events: workspace.events,
                      collapsed: collapsed,
                      width: workspace.timelineWidth,
                      onToggleCollapsed: workspace.toggleTimelineCollapsed,
                      onHide: workspace.toggleTimelineHidden,
                      onWidthChanged: workspace.setTimelineWidth,
                    ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }

  void _addComponent(BuildContext context) {
    showAddComponentSheet(
      context,
      addable: workspace.board.addable,
      onPick: workspace.add,
    );
  }
}

/// 首页工具栏。
///
/// 按 `§2.8` 只保留一个主操作：平时是「编辑布局」，编辑态是「完成」。
/// 页面标题刻意不做大 —— 导航栏已经标明了当前位置（`§2.6`）。
class _Toolbar extends StatelessWidget {
  const _Toolbar({
    required this.editing,
    required this.onToggleEditing,
    required this.onAdd,
    this.onShowTimeline,
  });

  final bool editing;
  final VoidCallback onToggleEditing;
  final VoidCallback onAdd;
  final VoidCallback? onShowTimeline;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return SizedBox(
      height: 44,
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: ZentwikTokens.spaceLg,
        ),
        child: Row(
          children: [
            if (editing)
              Expanded(
                child: Text(
                  '拖动标题左侧把手移动，拖卡片右/下边缘调整宽高',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: ZentwikTokens.fontCaption,
                    color: palette.textTertiary,
                  ),
                ),
              )
            else
              const Spacer(),
            if (!editing) ...[
              if (onShowTimeline != null)
                IconButton(
                  tooltip: '显示时间线',
                  onPressed: onShowTimeline,
                  iconSize: 16,
                  color: palette.textTertiary,
                  icon: const Icon(Icons.vertical_split_outlined),
                ),
              TextButton.icon(
                onPressed: onToggleEditing,
                icon: const Icon(Icons.dashboard_customize_outlined, size: 16),
                label: const Text('编辑布局'),
                style: TextButton.styleFrom(
                  foregroundColor: palette.textSecondary,
                  textStyle: const TextStyle(
                    fontSize: ZentwikTokens.fontCaption,
                  ),
                ),
              ),
            ] else ...[
              OutlinedButton.icon(
                onPressed: onAdd,
                icon: const Icon(Icons.add, size: 16),
                label: const Text('添加组件'),
              ),
              const SizedBox(width: ZentwikTokens.spaceSm),
              FilledButton(
                onPressed: onToggleEditing,
                child: const Text('完成'),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
