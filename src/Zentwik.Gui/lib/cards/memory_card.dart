import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';

class MemoryCard extends StatelessWidget {
  const MemoryCard({
    super.key,
    this.telemetry,
  });

  final TelemetryDto? telemetry;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final current = telemetry;
    if (current == null) {
      return const CardEmptyHint('暂无数据');
    }

    final total = current.memoryTotalBytes;
    final available = current.memoryAvailableBytes;
    if (total == null || total <= 0 || available == null || available < 0) {
      return Text(
        kMissingValue,
        style: TextStyle(
          fontSize: ZentwikTokens.fontTitle,
          fontWeight: FontWeight.w600,
          color: palette.textSecondary,
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
              style: TextStyle(
                fontSize: ZentwikTokens.fontSubtitle,
                fontWeight: FontWeight.w600,
                color: palette.textPrimary,
              ),
            ),
            Text(
              formatPercent(percent),
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: palette.textSecondary,
              ),
            ),
          ],
        ),
        const SizedBox(height: ZentwikTokens.spaceSm),
        LinearProgressIndicator(
          value: ratio,
          minHeight: 4,
          backgroundColor: palette.border,
          color: palette.accent,
        ),
      ],
    );
  }
}
