import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/errors.dart';
import '../protocol/models/module_dto.dart';
import '../theme/tokens.dart';

class ModuleStatusCard extends StatelessWidget {
  const ModuleStatusCard({
    super.key,
    this.modules,
  });

  final List<ModuleDto>? modules;

  Color _dotColor(ModuleDto module) {
    // 后端语义：可选组件未启用时同样报 unavailable（见 ModuleCatalog），
    // 所以先看 enabled 再判 state，避免把「没装的组件」标成故障色。
    if (module.enabled == false) {
      return ZentwikTokens.textSecondary;
    }
    return switch (module.state) {
      ModuleStates.ready => ZentwikTokens.success,
      ModuleStates.degraded => ZentwikTokens.accent,
      ModuleStates.unavailable => ZentwikTokens.textSecondary,
      _ => ZentwikTokens.textSecondary,
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
    final list = modules;
    if (list == null || list.isEmpty) {
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
        for (var i = 0; i < list.length; i++) ...[
          if (i > 0) const SizedBox(height: ZentwikTokens.spaceXs),
          Row(
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  color: _dotColor(list[i]),
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: ZentwikTokens.spaceSm),
              Expanded(
                child: Text(
                  list[i].name ?? list[i].id ?? kMissingValue,
                  style: const TextStyle(
                    fontSize: ZentwikTokens.fontBody,
                    color: ZentwikTokens.textPrimary,
                  ),
                ),
              ),
              Text(
                _statusLabel(list[i]),
                style: const TextStyle(
                  fontSize: ZentwikTokens.fontCaption,
                  color: ZentwikTokens.textSecondary,
                ),
              ),
            ],
          ),
        ],
      ],
    );
  }
}
