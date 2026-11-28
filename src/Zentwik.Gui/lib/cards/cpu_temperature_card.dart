import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';

class CpuTemperatureCard extends StatelessWidget {
  const CpuTemperatureCard({
    super.key,
    this.telemetry,
  });

  final TelemetryDto? telemetry;

  Color _colorForTemperature(ZentwikPalette palette, double? value) {
    if (value == null || !value.isFinite) {
      return palette.textSecondary;
    }
    if (value < 70) {
      return palette.success;
    }
    if (value < 85) {
      return palette.accent;
    }
    return palette.danger;
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
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
        color: _colorForTemperature(palette, temp),
      ),
    );
  }
}
