import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';

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

    final detail = current.sensorDetail;
    final subtitle =
        (detail != null && detail.isNotEmpty) ? detail : '封装总功耗（单位：瓦特）';

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatWatts(current.packagePowerWatts),
          style: TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            fontWeight: FontWeight.w600,
            color: palette.textPrimary,
          ),
        ),
        const SizedBox(height: ZentwikTokens.spaceXs),
        Text(
          subtitle,
          style: TextStyle(
            fontSize: ZentwikTokens.fontCaption,
            color: palette.textSecondary,
          ),
        ),
      ],
    );
  }
}
