import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/tokens.dart';
import '../widgets/card_empty.dart';

class CpuTemperatureCard extends StatelessWidget {
  const CpuTemperatureCard({
    super.key,
    this.telemetry,
  });

  final TelemetryDto? telemetry;

  Color _colorForTemperature(double? value) {
    if (value == null || !value.isFinite) {
      return ZentwikTokens.textSecondary;
    }
    if (value < 70) {
      return ZentwikTokens.success;
    }
    if (value < 85) {
      return ZentwikTokens.accent;
    }
    return ZentwikTokens.danger;
  }

  @override
  Widget build(BuildContext context) {
    final current = telemetry;
    if (current == null) {
      return const CardEmptyHint('暂无数据');
    }

    final temp = current.temperatureCelsius;
    return Text(
      formatCelsius(temp),
      style: TextStyle(
        fontSize: ZentwikTokens.fontTitle,
        fontWeight: FontWeight.w600,
        color: _colorForTemperature(temp),
      ),
    );
  }
}
