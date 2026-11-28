import 'package:material_ui/material_ui.dart';

import '../theme/theme.dart';
import 'board.dart';

typedef DashboardCardBuilder = Widget Function(
  BuildContext context,
  BoardItem item,
);

/// 尚未接入具体实现的组件占位。
///
/// 标题由 `DashboardCardShell` 统一渲染，这里只给状态说明，避免同一标题出现两次。
class PlaceholderCard extends StatelessWidget {
  const PlaceholderCard({
    super.key,
    required this.item,
  });

  final BoardItem item;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: ZentwikTokens.spaceSm),
      child: Text(
        '尚未接入',
        style: TextStyle(
          fontSize: ZentwikTokens.fontCaption,
          color: context.palette.textSecondary,
        ),
      ),
    );
  }
}

/// 按 `DashboardKind.wire` 查找组件内容构建器；未注册时回落到 [PlaceholderCard]。
///
/// 注册表只解决「这个类型画什么」，不解决尺寸和放置 —— 那是布局的事。
class CardRegistry {
  const CardRegistry([this.builders = const {}]);

  final Map<String, DashboardCardBuilder> builders;

  bool isRegistered(BoardItem item) => builders.containsKey(item.kind.wire);

  Widget buildCard(BuildContext context, BoardItem item) {
    final builder = builders[item.kind.wire];
    if (builder != null) {
      return builder(context, item);
    }
    return PlaceholderCard(item: item);
  }
}
