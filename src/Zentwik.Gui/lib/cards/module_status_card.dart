import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../format/units.dart';
import '../protocol/errors.dart';
import '../protocol/models/module_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';
import 'card_layout.dart';

class ModuleStatusCard extends StatelessWidget {
  const ModuleStatusCard({
    super.key,
    this.modules,
  });

  final List<ModuleDto>? modules;

  Color _dotColor(ZentwikPalette palette, ModuleDto module) {
    if (module.enabled == false) {
      return palette.textSecondary;
    }
    return switch (module.state) {
      ModuleStates.ready => palette.success,
      ModuleStates.degraded => palette.accent,
      ModuleStates.unavailable => palette.textSecondary,
      _ => palette.textSecondary,
    };
  }

  String _statusLabel(ModuleDto module) {
    if (module.enabled == false) {
      return '未启用';
    }
    return switch (module.state) {
      ModuleStates.ready => '就绪',
      ModuleStates.degraded => '降级',
      ModuleStates.unavailable => '不可用',
      _ => module.state ?? kMissingValue,
    };
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final list = modules;
    if (list == null) {
      return const CardEmptyHint('暂无数据');
    }
    if (list.isEmpty) {
      return const CardEmptyHint('无模块');
    }

    final metrics = CardMetrics.of(context);
    final layout = resolveModuleStatus(
      metrics: metrics,
      moduleCount: list.length,
    );

    final shown = list.take(layout.visibleRows).toList(growable: false);

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (var i = 0; i < shown.length; i++) ...[
          if (i > 0) const SizedBox(height: ZentwikTokens.spaceXs),
          Row(
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  color: _dotColor(palette, shown[i]),
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: ZentwikTokens.spaceSm),
              Expanded(
                child: Text(
                  shown[i].name ?? shown[i].id ?? kMissingValue,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: ZentwikTokens.fontBody,
                    color: palette.textPrimary,
                  ),
                ),
              ),
              const SizedBox(width: ZentwikTokens.spaceSm),
              Text(
                _statusLabel(shown[i]),
                maxLines: 1,
                style: TextStyle(
                  fontSize: ZentwikTokens.fontCaption,
                  color: palette.textSecondary,
                ),
              ),
            ],
          ),
        ],
        if (layout.showFooter) ...[
          const SizedBox(height: ZentwikTokens.spaceXs),
          Text(
            '还有 ${layout.hiddenModules} 个模块',
            maxLines: 1,
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
