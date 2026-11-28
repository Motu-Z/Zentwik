import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';

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

    final percent = current.cpuTotalPercent;
    if (percent == null || !percent.isFinite || percent < 0) {
      return Text(
        kMissingValue,
        style: TextStyle(
          fontSize: ZentwikTokens.fontTitle,
          fontWeight: FontWeight.w600,
          color: palette.textSecondary,
        ),
      );
    }

    final progress = (percent / 100.0).clamp(0.0, 1.0);
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatPercent(percent),
          style: TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            fontWeight: FontWeight.w600,
            color: palette.textPrimary,
          ),
        ),
        const SizedBox(height: ZentwikTokens.spaceSm),
        LinearProgressIndicator(
          value: progress,
          minHeight: 4,
          backgroundColor: palette.border,
          color: palette.accent,
        ),
      ],
    );
  }
}
