import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/ledger_entry.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';

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

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatCount(list.length),
          style: TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            fontWeight: FontWeight.w600,
            color: palette.textPrimary,
          ),
        ),
        if (grouped.isNotEmpty) ...[
          const SizedBox(height: ZentwikTokens.spaceSm),
          for (final item in grouped.entries)
            Padding(
              padding: const EdgeInsets.only(bottom: ZentwikTokens.spaceXs),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Text(
                    item.key,
                    style: TextStyle(
                      fontSize: ZentwikTokens.fontCaption,
                      color: palette.textSecondary,
                    ),
                  ),
                  Text(
                    formatCount(item.value),
                    style: TextStyle(
                      fontSize: ZentwikTokens.fontCaption,
                      color: palette.textPrimary,
                    ),
                  ),
                ],
              ),
            ),
        ],
      ],
    );
  }
}
