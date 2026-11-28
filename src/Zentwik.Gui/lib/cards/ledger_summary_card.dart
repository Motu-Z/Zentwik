import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/ledger_entry.dart';
import '../theme/tokens.dart';

class LedgerSummaryCard extends StatelessWidget {
  const LedgerSummaryCard({
    super.key,
    this.entries,
  });

  final List<LedgerEntry>? entries;

  @override
  Widget build(BuildContext context) {
    final list = entries;
    if (list == null) {
      return const Text(
        kMissingValue,
        style: TextStyle(
          fontSize: ZentwikTokens.fontTitle,
          color: ZentwikTokens.textSecondary,
        ),
      );
    }

    final grouped = <String, int>{};
    for (final entry in list) {
      final target = entry.target ?? '';
      final dot = target.indexOf('.');
      final namespace = dot > 0 ? target.substring(0, dot) : (target.isEmpty ? 'other' : target);
      grouped[namespace] = (grouped[namespace] ?? 0) + 1;
    }

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatCount(list.length),
          style: const TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            fontWeight: FontWeight.w600,
            color: ZentwikTokens.textPrimary,
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
                    style: const TextStyle(
                      fontSize: ZentwikTokens.fontCaption,
                      color: ZentwikTokens.textSecondary,
                    ),
                  ),
                  Text(
                    formatCount(item.value),
                    style: const TextStyle(
                      fontSize: ZentwikTokens.fontCaption,
                      color: ZentwikTokens.textPrimary,
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
