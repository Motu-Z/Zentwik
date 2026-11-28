import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/tokens.dart';

class CoreClocksCard extends StatelessWidget {
  const CoreClocksCard({
    super.key,
    this.telemetry,
    this.fMaxMhz = 4750.0,
  });

  final TelemetryDto? telemetry;
  final double fMaxMhz;

  @override
  Widget build(BuildContext context) {
    final clocks = telemetry?.coreClocks;
    if (clocks == null || clocks.isEmpty) {
      return const Text(
        kMissingValue,
        style: TextStyle(
          fontSize: ZentwikTokens.fontTitle,
          color: ZentwikTokens.textSecondary,
        ),
      );
    }

    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var i = 0; i < clocks.length; i++) ...[
          if (i > 0) const SizedBox(height: ZentwikTokens.spaceXs),
          _CoreClockRow(clock: clocks[i], fMaxMhz: fMaxMhz),
        ],
      ],
    );
  }
}

class _CoreClockRow extends StatelessWidget {
  const _CoreClockRow({
    required this.clock,
    required this.fMaxMhz,
  });

  final CoreClockDto clock;
  final double fMaxMhz;

  @override
  Widget build(BuildContext context) {
    final ccd = clock.ccd ?? 0;
    final core = (clock.core ?? 0).toString().padLeft(2, '0');
    final mhz = clock.megahertz;
    final ratio = (mhz == null || !mhz.isFinite || fMaxMhz <= 0)
        ? 0.0
        : (mhz / fMaxMhz).clamp(0.0, 1.0);

    return Row(
      children: [
        SizedBox(
          width: 76,
          child: Text(
            'CCD$ccd-C$core',
            style: const TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: ZentwikTokens.textSecondary,
            ),
          ),
        ),
        Expanded(
          child: LinearProgressIndicator(
            value: ratio,
            minHeight: 6,
            backgroundColor: ZentwikTokens.border,
            color: ZentwikTokens.accent,
          ),
        ),
        const SizedBox(width: ZentwikTokens.spaceSm),
        SizedBox(
          width: 68,
          child: Text(
            formatGigahertz(mhz),
            textAlign: TextAlign.right,
            style: const TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: ZentwikTokens.textPrimary,
            ),
          ),
        ),
      ],
    );
  }
}
