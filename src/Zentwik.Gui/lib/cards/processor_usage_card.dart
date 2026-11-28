import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

class ProcessorUsageCard extends StatelessWidget {
  const ProcessorUsageCard({
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

    final usage = current.processorUtilization;
    if (usage == null || usage.isEmpty) {
      return const CardEmptyHint('无处理器数据');
    }

    final metrics = CardMetrics.of(context);
    final layout = resolveProcessorUsage(
      metrics: metrics,
      count: usage.length,
    );

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Wrap(
          spacing: ZentwikTokens.spaceXs,
          runSpacing: ZentwikTokens.spaceXs,
          children: [
            for (final pct in usage.take(layout.visibleCount))
              _ProcessorBar(
                percent: pct,
                width: layout.barWidth,
                height: layout.barHeight,
              ),
          ],
        ),
        if (layout.hiddenCount > 0) ...[
          const SizedBox(height: ZentwikTokens.spaceXs),
          Text(
            '+${layout.hiddenCount}',
            maxLines: 1,
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

class _ProcessorBar extends StatelessWidget {
  const _ProcessorBar({
    required this.percent,
    required this.width,
    required this.height,
  });

  final double percent;
  final double width;
  final double height;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final ratio = !percent.isFinite ? 0.0 : (percent / 100.0).clamp(0.0, 1.0);
    final fillHeight = height * ratio;

    return Container(
      width: width,
      height: height,
      alignment: Alignment.bottomCenter,
      decoration: BoxDecoration(
        color: palette.border,
        borderRadius: BorderRadius.circular(2),
      ),
      child: Container(
        width: width,
        height: fillHeight,
        decoration: BoxDecoration(
          color: palette.accent,
          borderRadius: BorderRadius.circular(2),
        ),
      ),
    );
  }
}
