import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

class PackagePowerCard extends StatelessWidget {
  const PackagePowerCard({
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
    final layout = resolvePackagePower(metrics: metrics);
    final valueFontSize =
        layout.displayFont ? ZentwikTokens.fontDisplay : ZentwikTokens.fontTitle;

    final detail = current.sensorDetail;
    final subtitle =
        (detail != null && detail.isNotEmpty) ? detail : '封装总功耗（单位：瓦特）';

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatWatts(current.packagePowerWatts),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: TextStyle(
            fontSize: valueFontSize,
            fontWeight: FontWeight.w600,
            color: palette.textPrimary,
          ),
        ),
        if (layout.showSubtitle) ...[
          SizedBox(height: layout.gap),
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
