import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/tokens.dart';

class PackagePowerCard extends StatelessWidget {
  const PackagePowerCard({
    super.key,
    this.telemetry,
  });

  final TelemetryDto? telemetry;

  @override
  Widget build(BuildContext context) {
    final detail = telemetry?.sensorDetail;
    final subtitle =
        (detail != null && detail.isNotEmpty) ? detail : '封装总功耗（单位：瓦特）';

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatWatts(telemetry?.packagePowerWatts),
          style: const TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            fontWeight: FontWeight.w600,
            color: ZentwikTokens.textPrimary,
          ),
        ),
        const SizedBox(height: ZentwikTokens.spaceXs),
        Text(
          subtitle,
          style: const TextStyle(
            fontSize: ZentwikTokens.fontCaption,
            color: ZentwikTokens.textSecondary,
          ),
        ),
      ],
    );
  }
}
