import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

class CpuTemperatureCard extends StatelessWidget {
  const CpuTemperatureCard({
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
    final layout = resolveCpuTemperature(metrics: metrics);
    final temp = current.temperatureCelsius;
    final info = describeTemperature(palette, temp);
    final valueFontSize =
        layout.displayFont ? ZentwikTokens.fontDisplay : ZentwikTokens.fontTitle;

    final valueWidget = Text(
      formatCelsius(temp),
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
      style: TextStyle(
        fontSize: valueFontSize,
        fontWeight: FontWeight.w600,
        color: info.color,
      ),
    );

    if (!layout.showLabel || info.label.isEmpty) {
      return valueWidget;
    }

    return Row(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.baseline,
      textBaseline: TextBaseline.alphabetic,
      children: [
        Flexible(child: valueWidget),
        const SizedBox(width: ZentwikTokens.spaceSm),
        Text(
          info.label,
          maxLines: 1,
          style: TextStyle(
            fontSize: ZentwikTokens.fontCaption,
            fontWeight: FontWeight.w600,
            color: info.color,
          ),
        ),
      ],
    );
  }
}
