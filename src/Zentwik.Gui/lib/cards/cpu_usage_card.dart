import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

class CpuUsageCard extends StatelessWidget {
  const CpuUsageCard({
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

    final metrics = CardMetrics.of(context);
    final utils = current.processorUtilization
        ?.where((v) => v.isFinite)
        .toList(growable: false);
    final hasStats = utils != null && utils.isNotEmpty;
    final layout = resolveCpuUsage(
      metrics: metrics,
      hasProcessorStats: hasStats,
    );
    final valueFontSize =
        layout.displayFont ? ZentwikTokens.fontDisplay : ZentwikTokens.fontTitle;

    final percent = current.cpuTotalPercent;
    if (percent == null || !percent.isFinite || percent < 0) {
      return Text(
        kMissingValue,
        style: TextStyle(
          fontSize: valueFontSize,
          fontWeight: FontWeight.w600,
          color: palette.textSecondary,
        ),
      );
    }

    final progress = (percent / 100.0).clamp(0.0, 1.0);
    final valueText = Text(
      formatPercent(percent),
      maxLines: 1,
      softWrap: false,
      style: TextStyle(
        fontSize: valueFontSize,
        fontWeight: FontWeight.w600,
        color: palette.textPrimary,
      ),
    );
    final bar = LinearProgressIndicator(
      value: progress,
      minHeight: 4,
      backgroundColor: palette.border,
      color: palette.accent,
    );

    String? statsText;
    if (layout.showStats && hasStats) {
      var peak = utils.first;
      var sum = 0.0;
      for (final v in utils) {
        if (v > peak) {
          peak = v;
        }
        sum += v;
      }
      final avg = sum / utils.length;
      statsText = '峰值 ${peak.round()}% · 均值 ${avg.round()}%';
    }

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (layout.inlineBar)
          Row(
            children: [
              valueText,
              const SizedBox(width: ZentwikTokens.spaceMd),
              Expanded(child: bar),
            ],
          )
        else ...[
          valueText,
          SizedBox(height: layout.gap),
          bar,
        ],
        if (statsText != null) ...[
          SizedBox(height: layout.gap),
          Text(
            statsText,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: palette.textTertiary,
            ),
          ),
        ],
      ],
    );
  }
}
