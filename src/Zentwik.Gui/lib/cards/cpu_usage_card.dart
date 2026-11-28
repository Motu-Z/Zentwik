import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/tokens.dart';
import '../widgets/card_empty.dart';

class CpuUsageCard extends StatelessWidget {
  const CpuUsageCard({
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

    final percent = current.cpuTotalPercent;
    if (percent == null || !percent.isFinite || percent < 0) {
      return const Text(
        kMissingValue,
        style: TextStyle(
          fontSize: ZentwikTokens.fontTitle,
          fontWeight: FontWeight.w600,
          color: ZentwikTokens.textSecondary,
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
          style: const TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            fontWeight: FontWeight.w600,
            color: ZentwikTokens.textPrimary,
          ),
        ),
        const SizedBox(height: ZentwikTokens.spaceSm),
        LinearProgressIndicator(
          value: progress,
          minHeight: 4,
          backgroundColor: ZentwikTokens.border,
          color: ZentwikTokens.accent,
        ),
      ],
    );
  }
}
