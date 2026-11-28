import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/tokens.dart';

class ProcessCountCard extends StatelessWidget {
  const ProcessCountCard({
    super.key,
    this.telemetry,
  });

  final TelemetryDto? telemetry;

  @override
  Widget build(BuildContext context) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          formatCount(telemetry?.processCount),
          style: const TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            fontWeight: FontWeight.w600,
            color: ZentwikTokens.textPrimary,
          ),
        ),
        const SizedBox(height: ZentwikTokens.spaceXs),
        const Text(
          '含受保护进程',
          style: TextStyle(
            fontSize: ZentwikTokens.fontCaption,
            color: ZentwikTokens.textSecondary,
          ),
        ),
      ],
    );
  }
}
