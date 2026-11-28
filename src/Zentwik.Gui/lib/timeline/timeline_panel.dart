import 'package:material_ui/material_ui.dart';

import '../theme/theme.dart';
import '../workspace/timeline_event.dart';
import '../workspace/workspace.dart';

/// 全局时间线：首页右侧的独立区域。
///
/// 三条硬约束：
/// - **不是普通组件**：不参与主工作区的自由排列，位置固定。
/// - **独立滚动**：主工作区某张卡变高，不会把它整体往下挤。
/// - **可折叠 / 可隐藏 / 可调宽**，折叠后只剩窄轨，隐藏后完全让位给主工作区。
class TimelinePanel extends StatelessWidget {
  const TimelinePanel({
    super.key,
    required this.events,
    required this.collapsed,
    required this.width,
    required this.onToggleCollapsed,
    required this.onHide,
    required this.onWidthChanged,
  });

  final List<TimelineEvent> events;
  final bool collapsed;
  final double width;
  final VoidCallback onToggleCollapsed;
  final VoidCallback onHide;
  final ValueChanged<double> onWidthChanged;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    if (collapsed) {
      return SizedBox(
        width: Workspace.kTimelineCollapsedWidth,
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: palette.surface,
            border: Border(left: BorderSide(color: palette.border)),
          ),
          child: Column(
            children: [
              const SizedBox(height: ZentwikTokens.spaceSm),
              IconButton(
                tooltip: '展开时间线',
                onPressed: onToggleCollapsed,
                iconSize: 16,
                color: palette.textTertiary,
                icon: const Icon(Icons.chevron_left),
              ),
              Expanded(
                child: RotatedBox(
                  quarterTurns: 3,
                  child: Center(
                    child: Text(
                      events.isEmpty ? '暂无事件' : '${events.length} 条事件',
                      maxLines: 1,
                      style: TextStyle(
                        fontSize: ZentwikTokens.fontCaption,
                        color: palette.textTertiary,
                      ),
                    ),
                  ),
                ),
              ),
              IconButton(
                tooltip: '隐藏时间线',
                onPressed: onHide,
                iconSize: 15,
                color: palette.textTertiary,
                icon: const Icon(Icons.close),
              ),
              const SizedBox(height: ZentwikTokens.spaceSm),
            ],
          ),
        ),
      );
    }

    return SizedBox(
      width: width,
      child: Stack(
        children: [
          DecoratedBox(
            decoration: BoxDecoration(
              color: palette.surface,
              border: Border(left: BorderSide(color: palette.border)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _Header(
                  count: events.length,
                  onToggleCollapsed: onToggleCollapsed,
                  onHide: onHide,
                ),
                Divider(height: 1, thickness: 1, color: palette.border),
                Expanded(
                  child: events.isEmpty
                      ? _EmptyTimeline()
                      : ListView.separated(
                          padding: const EdgeInsets.symmetric(
                            horizontal: ZentwikTokens.spaceLg,
                            vertical: ZentwikTokens.spaceMd,
                          ),
                          itemCount: events.length,
                          separatorBuilder: (_, _) => const SizedBox(
                            height: ZentwikTokens.spaceMd,
                          ),
                          itemBuilder: (context, index) =>
                              _EventTile(event: events[index]),
                        ),
                ),
              ],
            ),
          ),
          // 左边缘调宽。热区只有 6px，不抢内容。
          Positioned(
            left: 0,
            top: 0,
            bottom: 0,
            width: 6,
            child: MouseRegion(
              cursor: SystemMouseCursors.resizeLeftRight,
              child: GestureDetector(
                behavior: HitTestBehavior.translucent,
                // 向左拖 → 变宽，所以取反。
                onHorizontalDragUpdate: (details) =>
                    onWidthChanged(width - details.delta.dx),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _Header extends StatelessWidget {
  const _Header({
    required this.count,
    required this.onToggleCollapsed,
    required this.onHide,
  });

  final int count;
  final VoidCallback onToggleCollapsed;
  final VoidCallback onHide;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return SizedBox(
      height: 40,
      child: Row(
        children: [
          const SizedBox(width: ZentwikTokens.spaceLg),
          Text(
            '事件',
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              fontWeight: FontWeight.w600,
              letterSpacing: 0.2,
              color: palette.textSecondary,
            ),
          ),
          if (count > 0) ...[
            const SizedBox(width: ZentwikTokens.spaceSm),
            Text(
              '$count',
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: palette.textTertiary,
              ),
            ),
          ],
          const Spacer(),
          IconButton(
            tooltip: '折叠时间线',
            onPressed: onToggleCollapsed,
            iconSize: 16,
            color: palette.textTertiary,
            icon: const Icon(Icons.chevron_right),
          ),
          IconButton(
            tooltip: '隐藏时间线',
            onPressed: onHide,
            iconSize: 15,
            color: palette.textTertiary,
            icon: const Icon(Icons.close),
          ),
          const SizedBox(width: ZentwikTokens.spaceXs),
        ],
      ),
    );
  }
}

class _EmptyTimeline extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.history_toggle_off,
              size: 22,
              color: palette.textTertiary,
            ),
            const SizedBox(height: ZentwikTokens.spaceSm),
            Text(
              '还没有事件',
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: palette.textSecondary,
              ),
            ),
            const SizedBox(height: ZentwikTokens.spaceXs),
            Text(
              '连接、模块、调度与设置的变更会记在这里',
              textAlign: TextAlign.center,
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: palette.textTertiary,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _EventTile extends StatelessWidget {
  const _EventTile({required this.event});

  final TimelineEvent event;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final accent = _severityColor(palette);
    final showLabel =
        event.severity == TimelineSeverity.warning ||
            event.severity == TimelineSeverity.danger;

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // 图标形状本身携带严重度，颜色只是辅助 —— 只靠颜色对色盲不可读（`§5.7`）。
        Icon(_severityIcon, size: 14, color: accent),
        const SizedBox(width: ZentwikTokens.spaceSm),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      event.title,
                      style: TextStyle(
                        fontSize: ZentwikTokens.fontCaption,
                        fontWeight: FontWeight.w600,
                        height: 1.3,
                        color: palette.textPrimary,
                      ),
                    ),
                  ),
                  if (showLabel) ...[
                    const SizedBox(width: ZentwikTokens.spaceXs),
                    Text(
                      event.severity == TimelineSeverity.danger ? '异常' : '注意',
                      style: TextStyle(
                        fontSize: ZentwikTokens.fontCaption,
                        color: accent,
                      ),
                    ),
                  ],
                ],
              ),
              if (event.detail case final detail? when detail.isNotEmpty) ...[
                const SizedBox(height: 2),
                Text(
                  detail,
                  maxLines: 3,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: ZentwikTokens.fontCaption,
                    height: 1.35,
                    color: palette.textSecondary,
                  ),
                ),
              ],
              const SizedBox(height: ZentwikTokens.spaceXs),
              Text(
                '${event.timeLabel} · ${event.category.label}',
                style: TextStyle(
                  fontSize: ZentwikTokens.fontCaption,
                  color: palette.textTertiary,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  IconData get _severityIcon => switch (event.severity) {
        TimelineSeverity.info => Icons.circle_outlined,
        TimelineSeverity.success => Icons.check_circle_outline,
        TimelineSeverity.warning => Icons.error_outline,
        TimelineSeverity.danger => Icons.cancel_outlined,
      };

  Color _severityColor(ZentwikPalette palette) => switch (event.severity) {
        TimelineSeverity.info => palette.textTertiary,
        TimelineSeverity.success => palette.success,
        TimelineSeverity.warning => palette.warning,
        TimelineSeverity.danger => palette.danger,
      };
}
