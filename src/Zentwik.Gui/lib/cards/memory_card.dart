import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/tokens.dart';
import '../widgets/card_empty.dart';

class MemoryCard extends StatelessWidget {
  const MemoryCard({
    super.key,
    this.telemetry,
  });

  final TelemetryDto? telemetry;

  @override
  Widget build(BuildContext context) {
    final current = telemetry;
    if (current == null) {
      return const CardEmptyHint('暂无数据');
    }

    final total = current.memoryTotalBytes;
    final available = current.memoryAvailableBytes;
    if (total == null || total <= 0 || available == null || available < 0) {
      return const Text(
        kMissingValue,
        style: TextStyle(
          fontSize: ZentwikTokens.fontTitle,
          fontWeight: FontWeight.w600,
          color: ZentwikTokens.textSecondary,
        ),
      );
    }

    final used = (total - available).clamp(0, total);
    final ratio = (used / total).clamp(0.0, 1.0);
    final percent = ratio * 100.0;
    final summary = '${formatBytes(used)} / ${formatBytes(total)}';

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Text(
              summary,
              style: const TextStyle(
                fontSize: ZentwikTokens.fontSubtitle,
                fontWeight: FontWeight.w600,
                color: ZentwikTokens.textPrimary,
              ),
            ),
            Text(
              formatPercent(percent),
              style: const TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: ZentwikTokens.textSecondary,
              ),
            ),
          ],
        ),
        const SizedBox(height: ZentwikTokens.spaceSm),
        LinearProgressIndicator(
          value: ratio,
          minHeight: 4,
          backgroundColor: ZentwikTokens.border,
          color: ZentwikTokens.accent,
        ),
      ],
    );
  }
}
