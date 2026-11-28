import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

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

    final metrics = CardMetrics.of(context);
    final layout = resolveMemory(metrics: metrics);

    final used = (total - available).clamp(0, total);
    final ratio = (used / total).clamp(0.0, 1.0);
    final percent = ratio * 100.0;
    final usedStr = formatBytes(used);
    final totalStr = formatBytes(total);
    final pctStr = formatPercent(percent);

    final bar = LinearProgressIndicator(
      value: ratio,
      minHeight: 4,
      backgroundColor: palette.border,
      color: palette.accent,
    );

    return switch (layout.mode) {
      MemoryLayoutMode.tightMinimal => Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '$usedStr / $totalStr',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: ZentwikTokens.fontSubtitle,
                fontWeight: FontWeight.w600,
                color: palette.textPrimary,
              ),
            ),
            SizedBox(height: layout.gap),
            bar,
          ],
        ),
      MemoryLayoutMode.compactStacked => Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              usedStr,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: ZentwikTokens.fontSubtitle,
                fontWeight: FontWeight.w600,
                color: palette.textPrimary,
              ),
            ),
            SizedBox(height: layout.gap),
            Text(
              '占用 $pctStr · 共 $totalStr',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: palette.textSecondary,
              ),
            ),
            SizedBox(height: layout.gap),
            bar,
          ],
        ),
      MemoryLayoutMode.tallDisplay => Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              usedStr,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: ZentwikTokens.fontDisplay,
                fontWeight: FontWeight.w600,
                color: palette.textPrimary,
              ),
            ),
            SizedBox(height: layout.gap),
            Text(
              '占用 $pctStr · 共 $totalStr',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: ZentwikTokens.fontCaption,
                color: palette.textSecondary,
              ),
            ),
            SizedBox(height: layout.gap),
            bar,
          ],
        ),
      MemoryLayoutMode.splitRow => Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Flexible(
                  child: Text(
                    '$usedStr / $totalStr',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: ZentwikTokens.fontSubtitle,
                      fontWeight: FontWeight.w600,
                      color: palette.textPrimary,
                    ),
                  ),
                ),
                const SizedBox(width: ZentwikTokens.spaceSm),
                Text(
                  pctStr,
                  maxLines: 1,
                  style: TextStyle(
                    fontSize: ZentwikTokens.fontCaption,
                    color: palette.textSecondary,
                  ),
                ),
              ],
            ),
            SizedBox(height: layout.gap),
            bar,
          ],
        ),
    };
  }
}
