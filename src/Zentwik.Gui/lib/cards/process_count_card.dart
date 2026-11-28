import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

class ProcessCountCard extends StatelessWidget {
  const ProcessCountCard({
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
    final procCount = current.processorCount;
    final layout = resolveProcessCount(
      metrics: metrics,
      hasProcessorCount: procCount != null && procCount > 0,
    );
    final valueFontSize =
        layout.displayFont ? ZentwikTokens.fontDisplay : ZentwikTokens.fontTitle;

    final subtitle = layout.showProcessorCount
        ? '进程 ${formatCount(current.processCount)} · 逻辑处理器 ${formatCount(procCount)}'
        : '含受保护进程';

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatCount(current.processCount),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: TextStyle(
            fontSize: valueFontSize,
            fontWeight: FontWeight.w600,
            color: palette.textPrimary,
          ),
        ),
        if (layout.showSubtitle) ...[
          const SizedBox(height: ZentwikTokens.spaceXs),
          Text(
            subtitle,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: palette.textSecondary,
            ),
          ),
        ],
      ],
    );
  }
}
