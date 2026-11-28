import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/models/telemetry_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

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
    final palette = context.palette;
    final current = telemetry;
    if (current == null) {
      return const CardEmptyHint('暂无数据');
    }

    final clocks = current.coreClocks;
    if (clocks.isEmpty) {
      return const CardEmptyHint('无核心频率');
    }

    final ccdSet = <int?>{for (final c in clocks) c.ccd};
    final hasDistinctCcds = ccdSet.contains(null) || ccdSet.length > 1;
    final metrics = CardMetrics.of(context);
    final layout = resolveCoreClocks(
      metrics: metrics,
      coreCount: clocks.length,
      hasDistinctCcds: hasDistinctCcds,
    );

    final visibleCount =
        math.min(clocks.length, layout.visibleRows * layout.columns);

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (var r = 0; r < layout.visibleRows; r++) ...[
          if (r > 0) const SizedBox(height: ZentwikTokens.spaceXs),
          Row(
            children: [
              for (var c = 0; c < layout.columns; c++) ...[
                if (c > 0) const SizedBox(width: ZentwikTokens.spaceMd),
                Expanded(
                  child: (r * layout.columns + c) < visibleCount
                      ? _CoreClockCell(
                          clock: clocks[r * layout.columns + c],
                          fMaxMhz: fMaxMhz,
                          dense: layout.dense,
                        )
                      : const SizedBox.shrink(),
                ),
              ],
            ],
          ),
        ],
        if (layout.hiddenCores > 0) ...[
          const SizedBox(height: ZentwikTokens.spaceXs),
          Text(
            '还有 ${layout.hiddenCores} 核未显示',
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: palette.textTertiary,
            ),
          ),
        ],
      ],
    );
  }
}

class _CoreClockCell extends StatelessWidget {
  const _CoreClockCell({
    required this.clock,
    required this.fMaxMhz,
    required this.dense,
  });

  final CoreClockDto clock;
  final double fMaxMhz;
  final bool dense;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final ccd = clock.ccd;
    final core = (clock.core ?? 0).toString().padLeft(2, '0');
    final label = (dense && ccd != null) ? 'C$core' : 'CCD${ccd ?? 0}-C$core';
    final mhz = clock.megahertz;
    final ratio = (mhz == null || !mhz.isFinite || fMaxMhz <= 0)
        ? 0.0
        : (mhz / fMaxMhz).clamp(0.0, 1.0);

    return Row(
      children: [
        SizedBox(
          width: dense ? 34 : 72,
          child: Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: palette.textSecondary,
            ),
          ),
        ),
        Expanded(
          child: LinearProgressIndicator(
            value: ratio,
            minHeight: 6,
            backgroundColor: palette.border,
            color: palette.accent,
          ),
        ),
        const SizedBox(width: ZentwikTokens.spaceXs),
        SizedBox(
          width: dense ? 56 : 64,
          child: Text(
            formatGigahertz(mhz),
            maxLines: 1,
            textAlign: TextAlign.right,
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: palette.textPrimary,
            ),
          ),
        ),
      ],
    );
  }
}
