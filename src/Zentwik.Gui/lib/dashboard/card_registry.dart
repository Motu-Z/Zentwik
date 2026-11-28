import 'package:flutter/material.dart';

import '../theme/tokens.dart';
import 'dashboard_layout.dart';

typedef DashboardCardBuilder = Widget Function(
  BuildContext context,
  DashboardCard card,
);

/// 尚未接入具体实现的卡片占位视图。
class PlaceholderCard extends StatelessWidget {
  const PlaceholderCard({
    super.key,
    required this.card,
  });

  final DashboardCard card;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: ZentwikTokens.spaceSm),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            card.kind.label,
            style: const TextStyle(
              fontSize: ZentwikTokens.fontSubtitle,
              fontWeight: FontWeight.w600,
              color: ZentwikTokens.textPrimary,
            ),
          ),
          const SizedBox(height: ZentwikTokens.spaceXs),
          const Text(
            '尚未接入',
            style: TextStyle(
              fontSize: ZentwikTokens.fontCaption,
              color: ZentwikTokens.textSecondary,
            ),
          ),
        ],
      ),
    );
  }
}

/// 按 `DashboardKind.wire` 查找卡片构建器；未注册时回落到 [PlaceholderCard]。
class CardRegistry {
  const CardRegistry([this.builders = const {}]);

  final Map<String, DashboardCardBuilder> builders;

  Widget buildCard(BuildContext context, DashboardCard card) {
    final builder = builders[card.kind.wire];
    if (builder != null) {
      return builder(context, card);
    }
    return PlaceholderCard(card: card);
  }
}
