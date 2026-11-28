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
    final totalBytes = telemetry?.memoryTotalBytes;
    final availableBytes = telemetry?.memoryAvailableBytes;
    // 总量未知或非正、可用量越界都视为「无数据」，不猜。
    final usable = totalBytes != null &&
        totalBytes > 0 &&
        availableBytes != null &&
        availableBytes >= 0;

    int? usedBytes;
    double? percent;
    var ratio = 0.0;
    if (usable) {
      usedBytes = (totalBytes - availableBytes).clamp(0, totalBytes);
      percent = (usedBytes / totalBytes) * 100.0;
      ratio = (usedBytes / totalBytes).clamp(0.0, 1.0);
    }

    final summary = usable
        ? '${formatBytes(usedBytes)} / ${formatBytes(totalBytes)}'
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
            // 无数据时只留一个「—」，不重复刷两遍缺失标记。
            if (usable)
              Text(
                formatPercent(percent),
                style: const TextStyle(
                  fontSize: ZentwikTokens.fontCaption,
                  color: ZentwikTokens.textSecondary,
                ),
              ),
          ],
        ),
        // 无数据时不画空进度条，否则会被读成「占用 0%」。
        if (usable) ...[
          const SizedBox(height: ZentwikTokens.spaceSm),
          LinearProgressIndicator(
            value: ratio,
            minHeight: 4,
            backgroundColor: ZentwikTokens.border,
            color: ZentwikTokens.accent,
          ),
        ],
      ],
    );
  }
}
