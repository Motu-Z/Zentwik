import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/errors.dart';
import '../protocol/models/module_dto.dart';
import '../theme/tokens.dart';

/// 模块管理页：纯展示与切换回调，不直接发起协议请求。
class ModulesPage extends StatelessWidget {
  const ModulesPage({
    super.key,
    this.modules,
    this.onToggle,
    this.busyModuleId,
  });

  final List<ModuleDto>? modules;
  final ValueChanged<String>? onToggle;
  final String? busyModuleId;

  Color _dotColor(ModuleDto module) {
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
    if (list == null) {
      return const Center(
        child: Text(
          kMissingValue,
          style: TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            color: ZentwikTokens.textSecondary,
          ),
        ),
      );
    }

    if (list.isEmpty) {
      return const Center(
        child: Text(
          '无模块',
          style: TextStyle(
            fontSize: ZentwikTokens.fontBody,
            color: ZentwikTokens.textSecondary,
          ),
        ),
      );
    }

    return ListView.separated(
      padding: const EdgeInsets.all(ZentwikTokens.spaceLg),
      itemCount: list.length,
      separatorBuilder: (_, _) => const SizedBox(height: ZentwikTokens.spaceSm),
      itemBuilder: (context, index) {
        final module = list[index];
        final id = module.id;
        final isBusy = busyModuleId != null && busyModuleId == id;
        final canToggle = !isBusy && onToggle != null && id != null;

        return Container(
          padding: const EdgeInsets.symmetric(
            horizontal: ZentwikTokens.spaceLg,
            vertical: ZentwikTokens.spaceMd,
          ),
          decoration: BoxDecoration(
            color: ZentwikTokens.surface,
            borderRadius: BorderRadius.circular(ZentwikTokens.radiusMd),
            border: Border.all(color: ZentwikTokens.border),
          ),
          child: Row(
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  color: _dotColor(module),
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: ZentwikTokens.spaceMd),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      module.name ?? id ?? kMissingValue,
                      style: const TextStyle(
                        fontSize: ZentwikTokens.fontSubtitle,
                        fontWeight: FontWeight.w600,
                        color: ZentwikTokens.textPrimary,
                      ),
                    ),
                    const SizedBox(height: ZentwikTokens.spaceXs),
                    Text(
                      _statusLabel(module),
                      style: const TextStyle(
                        fontSize: ZentwikTokens.fontCaption,
                        color: ZentwikTokens.textSecondary,
                      ),
                    ),
                  ],
                ),
              ),
              if (isBusy)
                const SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              else
                Switch(
                  value: module.enabled ?? false,
                  onChanged: canToggle ? (_) => onToggle!(id) : null,
                ),
            ],
          ),
        );
      },
    );
  }
}
