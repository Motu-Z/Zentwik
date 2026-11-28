import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';

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
      return Text(
        kMissingValue,
        style: TextStyle(
          fontSize: ZentwikTokens.fontTitle,
          color: palette.textSecondary,
        ),
      );
    }

    return Wrap(
      spacing: ZentwikTokens.spaceXs,
      runSpacing: ZentwikTokens.spaceXs,
      children: [
        for (final pct in usage) _ProcessorBar(percent: pct),
      ],
    );
  }
}

class _ProcessorBar extends StatelessWidget {
  const _ProcessorBar({required this.percent});

  final double percent;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    const double totalHeight = 28.0;
    final ratio = !percent.isFinite ? 0.0 : (percent / 100.0).clamp(0.0, 1.0);
    final fillHeight = totalHeight * ratio;

    return Container(
      width: 8,
      height: totalHeight,
      alignment: Alignment.bottomCenter,
      decoration: BoxDecoration(
        color: palette.border,
        borderRadius: BorderRadius.circular(2),
      ),
      child: Container(
        width: 8,
        height: fillHeight,
        decoration: BoxDecoration(
          color: palette.accent,
          borderRadius: BorderRadius.circular(2),
        ),
      ),
    );
  }
}
