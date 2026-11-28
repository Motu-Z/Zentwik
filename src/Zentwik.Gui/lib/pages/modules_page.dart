import 'package:material_ui/material_ui.dart';

import '../format/units.dart';
import '../protocol/errors.dart';
import '../protocol/models/module_dto.dart';
import '../theme/theme.dart';

/// 模块管理页：纯展示与切换回调，不直接发起协议请求。
///
/// 只有 `optional` 模块能启停——内置模块后端直接回 `module_not_optional`，
/// 所以这里不给它们画开关（按了必然失败，等于骗用户）。
class ModulesPage extends StatelessWidget {
  const ModulesPage({
    super.key,
    this.modules,
    this.onToggle,
    this.busyModuleId,
  });

  final List<ModuleDto>? modules;

  /// 第二参数是「点击后应处的状态」，不是当前状态。
  final void Function(String id, bool enable)? onToggle;
  final String? busyModuleId;

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
      return Center(
        child: Text(
          kMissingValue,
          style: TextStyle(
            fontSize: ZentwikTokens.fontTitle,
            color: palette.textSecondary,
          ),
        ),
      );
    }

    if (list.isEmpty) {
      return Center(
        child: Text(
          '无模块',
          style: TextStyle(
            fontSize: ZentwikTokens.fontBody,
            color: palette.textSecondary,
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
        final isOptional = module.optional == true;
        final enabled = module.enabled ?? false;
        final canToggle = isBusy == false && onToggle != null && id != null;

        return Container(
          padding: const EdgeInsets.symmetric(
            horizontal: ZentwikTokens.spaceLg,
            vertical: ZentwikTokens.spaceMd,
          ),
          decoration: BoxDecoration(
            color: palette.surface,
            borderRadius: BorderRadius.circular(ZentwikTokens.radiusMd),
            border: Border.all(color: palette.border),
          ),
          child: Row(
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  color: _dotColor(palette, module),
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
                      style: TextStyle(
                        fontSize: ZentwikTokens.fontSubtitle,
                        fontWeight: FontWeight.w600,
                        color: palette.textPrimary,
                      ),
                    ),
                    const SizedBox(height: ZentwikTokens.spaceXs),
                    Text(
                      _statusLabel(module),
                      style: TextStyle(
                        fontSize: ZentwikTokens.fontCaption,
                        color: palette.textSecondary,
                      ),
                    ),
                    if (module.detail case final detail?
                        when detail.trim().isNotEmpty) ...[
                      const SizedBox(height: ZentwikTokens.spaceXs),
                      Text(
                        detail,
                        style: TextStyle(
                          fontSize: ZentwikTokens.fontCaption,
                          color: palette.textTertiary,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
              if (!isOptional)
                Text(
                  '内置',
                  style: TextStyle(
                    fontSize: ZentwikTokens.fontCaption,
                    color: palette.textTertiary,
                  ),
                )
              else if (isBusy)
                const SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              else
                Switch(
                  value: enabled,
                  onChanged: canToggle ? (_) => onToggle!(id, !enabled) : null,
                ),
            ],
          ),
        );
      },
    );
  }
}
