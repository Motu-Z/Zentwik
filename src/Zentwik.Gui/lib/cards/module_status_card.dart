import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/errors.dart';
import '../protocol/models/module_dto.dart';
import '../theme/theme.dart';
import '../widgets/card_empty.dart';

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

    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var i = 0; i < list.length; i++) ...[
          if (i > 0) const SizedBox(height: ZentwikTokens.spaceXs),
          Row(
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  color: _dotColor(palette, list[i]),
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: ZentwikTokens.spaceSm),
              Expanded(
                child: Text(
                  list[i].name ?? list[i].id ?? kMissingValue,
                  style: TextStyle(
                    fontSize: ZentwikTokens.fontBody,
                    color: palette.textPrimary,
                  ),
                ),
              ),
              Text(
                _statusLabel(list[i]),
                style: TextStyle(
                  fontSize: ZentwikTokens.fontCaption,
                  color: palette.textSecondary,
                ),
              ),
            ],
          ),
        ],
      ],
    );
  }
}
