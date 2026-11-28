import 'package:material_ui/material_ui.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/dashboard/card_registry.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  testWidgets('未知 wire 回落 PlaceholderCard，已注册 wire 命中对应 builder',
      (tester) async {
    final registry = CardRegistry({
      DashboardKind.cpuUsage.wire: (context, card) =>
          Text('custom:${card.kind.wire}'),
    });

    const cpuCard = DashboardCard(kind: DashboardKind.cpuUsage, span: 1);
    const memoryCard = DashboardCard(kind: DashboardKind.memory, span: 1);

    await tester.pumpWidget(
      MaterialApp(
        theme: buildZentwikTheme(),
        home: Scaffold(
          body: Column(
            children: [
              Builder(builder: (ctx) => registry.buildCard(ctx, cpuCard)),
              Builder(builder: (ctx) => registry.buildCard(ctx, memoryCard)),
            ],
          ),
        ),
      ),
    );

    expect(find.text('custom:cpuUsage'), findsOneWidget);
    expect(find.byType(PlaceholderCard), findsOneWidget);
    // 标题由 DashboardCardShell 渲染，占位体里不再重复卡片名。
    expect(find.text(DashboardKind.memory.label), findsNothing);
    expect(find.text('尚未接入'), findsOneWidget);
  });
}
