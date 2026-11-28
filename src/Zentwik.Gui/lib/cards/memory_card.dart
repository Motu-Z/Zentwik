import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/tokens.dart';

class MemoryCard extends StatelessWidget {
  const MemoryCard({
    super.key,
    this.telemetry,
  });

  final TelemetryDto? telemetry;

  @override
  Widget build(BuildContext context) {
    final total = telemetry?.memoryTotalBytes;
    final available = telemetry?.memoryAvailableBytes;
    final hasValid =
        total != null && total > 0 && available != null && available >= 0;

    final int? used = hasValid ? (total - available).clamp(0, total) : null;
    final double? percent =
        (used != null && total > 0) ? (used / total) * 100.0 : null;
    final double ratio =
        (used != null && total > 0) ? (used / total).clamp(0.0, 1.0) : 0.0;

    final summary = hasValid
        ? '${formatBytes(used)} / ${formatBytes(total)}'
        : kMissingValue;

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
