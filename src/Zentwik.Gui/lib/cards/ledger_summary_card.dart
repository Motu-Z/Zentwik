import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/models/ledger_entry.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

class LedgerSummaryCard extends StatelessWidget {
  const LedgerSummaryCard({
    super.key,
    this.entries,
  });

  final List<LedgerEntry>? entries;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final list = entries;
    if (list == null) {
      return const CardEmptyHint('暂无数据');
    }

    final grouped = <String, int>{};
    for (final entry in list) {
      final target = entry.target ?? '';
      final dot = target.indexOf('.');
      final namespace = dot > 0
          ? target.substring(0, dot)
          : (target.isEmpty ? 'other' : target);
      grouped[namespace] = (grouped[namespace] ?? 0) + 1;
    }

    final metrics = CardMetrics.of(context);
    final layout = resolveLedgerSummary(
      metrics: metrics,
      groupCount: grouped.length,
    );

    final countWidget = Text(
      formatCount(list.length),
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
      style: TextStyle(
        fontSize: metrics.heightClass == CardHeightClass.tall
            ? ZentwikTokens.fontDisplay
            : ZentwikTokens.fontTitle,
        fontWeight: FontWeight.w600,
        color: palette.textPrimary,
      ),
    );

    final shown = grouped.entries.take(layout.maxGroups).toList(growable: false);
    final groupList = Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final item in shown)
          Padding(
            padding: const EdgeInsets.only(bottom: ZentwikTokens.spaceXs),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Expanded(
                  child: Text(
                    item.key,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: ZentwikTokens.fontCaption,
                      color: palette.textSecondary,
                    ),
                  ),
                ),
                const SizedBox(width: ZentwikTokens.spaceSm),
                Text(
                  formatCount(item.value),
                  maxLines: 1,
                  style: TextStyle(
                    fontSize: ZentwikTokens.fontCaption,
                    color: palette.textPrimary,
                  ),
                ),
              ],
            ),
          ),
        if (layout.hiddenGroups > 0 && layout.maxGroups > 0)
          Text(
            '还有 ${layout.hiddenGroups} 类',
            maxLines: 1,
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: palette.textTertiary,
            ),
          ),
      ],
    );

    if (layout.splitColumns && (shown.isNotEmpty || layout.hiddenGroups > 0)) {
      return Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(width: 88, child: countWidget),
          const SizedBox(width: ZentwikTokens.spaceLg),
          Expanded(child: groupList),
        ],
      );
    }

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        countWidget,
        if (shown.isNotEmpty || (layout.hiddenGroups > 0 && layout.maxGroups > 0)) ...[
          const SizedBox(height: ZentwikTokens.spaceSm),
          groupList,
        ],
      ],
    );
  }
}
