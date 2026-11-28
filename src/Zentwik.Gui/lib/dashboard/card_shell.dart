import 'package:material_ui/material_ui.dart';

import '../theme/theme.dart';
import 'board.dart';
import 'card_metrics.dart';

/// 首页卡片的统一外壳：背景、圆角、标题行。
///
/// 只负责「看起来是一张卡」，不含任何布局位置信息 —— 位置由 masonry 决定。
/// 按设计规范「去边框」：卡片靠与页面底色的明度差分离，只在编辑态加一圈描边
/// 作为**状态**，而不是给每张卡常驻第二层矩形框（`§8.5.2` 分隔只选一种）。
class DashboardCardShell extends StatelessWidget {
  const DashboardCardShell({
    super.key,
    required this.item,
    required this.child,
    this.leading,
    this.trailing,
    this.outlined = false,
    this.dimmed = false,
  });

  final BoardItem item;
  final Widget child;

  /// 标题行左侧（编辑模式下的拖拽把手）。
  final Widget? leading;

  /// 标题行右侧（编辑模式下的菜单按钮）。
  final Widget? trailing;

  /// 编辑态的描边；平时靠背景差即可，不加框。
  final bool outlined;

  /// 拖拽中留在原位的半透明占位。
  final bool dimmed;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final fixedHeight = item.height != null;

    // 标题是 label，不是 heading：小、灰、w600。数值才是主角（`§2.5.3`）。
    final heading = SizedBox(
      height: 20,
      child: Row(
        children: [
          ?leading,
          if (leading != null) const SizedBox(width: 2),
          // 图标面积比同字号文字大得多，必须比文字更淡（`§2.7.1`）。
          Icon(
            item.kind.icon,
            size: 14,
            color: palette.textTertiary,
          ),
          const SizedBox(width: ZentwikTokens.spaceSm),
          Expanded(
            child: Text(
              item.kind.label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                fontWeight: FontWeight.w600,
                letterSpacing: 0.2,
                color: palette.textSecondary,
              ),
            ),
          ),
          ?trailing,
        ],
      ),
    );

    final measuredBody = LayoutBuilder(
      builder: (context, constraints) {
        final width = constraints.maxWidth.isFinite ? constraints.maxWidth : 0.0;
        final height =
            fixedHeight && constraints.hasBoundedHeight ? constraints.maxHeight : null;
        final metrics = CardMetrics(
          bodyWidth: width,
          bodyHeight: height,
        );
        final content = CardMetrics.scope(
          metrics: metrics,
          child: child,
        );
        if (!fixedHeight) {
          return content;
        }
        // 显式高度下若极小边界仍超出：宁可让它滚，也不要裁掉半行数字。
        return SingleChildScrollView(
          primary: false,
          child: content,
        );
      },
    );

    final body = fixedHeight ? Expanded(child: measuredBody) : measuredBody;

    return Opacity(
      opacity: dimmed ? 0.4 : 1,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: palette.surface,
          borderRadius: BorderRadius.circular(ZentwikTokens.radiusMd),
          border: outlined
              ? Border.all(color: palette.accent.withValues(alpha: 0.55))
              : null,
        ),
        child: Padding(
          padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
          child: Column(
            mainAxisSize: fixedHeight ? MainAxisSize.max : MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              heading,
              const SizedBox(height: ZentwikTokens.spaceMd),
              body,
            ],
          ),
        ),
      ),
    );
  }
}

/// 空态：整个主工作区一张卡都没有时显示。
///
/// 空态是 P0 而非打磨（`§8.4`）：它带图标（保持原尺寸，不放大 3–4 倍）、
/// 一句说明，以及本页唯一的主操作。
class EmptyBoardHint extends StatelessWidget {
  const EmptyBoardHint({super.key, required this.onAdd, this.enabled = true});

  final VoidCallback onAdd;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            Icons.dashboard_customize_outlined,
            size: 28,
            color: palette.textTertiary,
          ),
          const SizedBox(height: ZentwikTokens.spaceMd),
          Text(
            '主工作区还没有组件',
            style: TextStyle(
              fontSize: ZentwikTokens.fontBody,
              color: palette.textSecondary,
            ),
          ),
          const SizedBox(height: ZentwikTokens.spaceSm),
          Text(
            '添加组件后可以拖动排列、调整宽高，布局会自动保存',
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: palette.textTertiary,
            ),
          ),
          const SizedBox(height: ZentwikTokens.spaceLg),
          FilledButton.icon(
            onPressed: enabled ? onAdd : null,
            icon: const Icon(Icons.add, size: 16),
            label: const Text('添加组件'),
          ),
        ],
      ),
    );
  }
}
