import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';
import 'package:zentwik_gui/pages/dashboard_page.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  Widget wrapPage({
    required Size size,
    required DashboardLayout layout,
    required ValueChanged<DashboardLayout> onLayoutChanged,
  }) {
    return MediaQuery(
      data: MediaQueryData(size: size),
      child: MaterialApp(
        theme: buildZentwikTheme(),
        home: Scaffold(
          body: SizedBox(
            width: size.width,
            height: size.height,
            child: DashboardPage(
              layout: layout,
              onLayoutChanged: onLayoutChanged,
            ),
          ),
        ),
      ),
    );
  }

  testWidgets('宽屏渲染全部可见卡片且拖拽触发 onLayoutChanged', (tester) async {
    final layout = DashboardLayout.normalize([
      const DashboardCard(kind: DashboardKind.cpuUsage, span: 2),
      const DashboardCard(kind: DashboardKind.cpuTemperature, span: 2),
      const DashboardCard(
        kind: DashboardKind.memory,
        span: 1,
        visible: false,
      ),
    ]);

    DashboardLayout? updated;
    await tester.pumpWidget(
      wrapPage(
        size: const Size(1200, 800),
        layout: layout,
        onLayoutChanged: (next) => updated = next,
      ),
    );

    expect(find.byType(DashboardCardShell), findsNWidgets(2));
    expect(find.text(DashboardKind.memory.label), findsNothing);
    expect(find.byType(ReorderableListView), findsNothing);

    final firstCenter =
        tester.getCenter(find.text(DashboardKind.cpuUsage.label).first);
    final secondCenter =
        tester.getCenter(find.text(DashboardKind.cpuTemperature.label).first);

    await tester.dragFrom(firstCenter, secondCenter - firstCenter);
    await tester.pumpAndSettle();

    expect(updated, isNotNull);
    expect(
      updated!.cards.map((c) => c.kind).toList(),
      [
        DashboardKind.cpuTemperature,
        DashboardKind.cpuUsage,
        DashboardKind.memory,
      ],
    );
  });

  testWidgets('窄屏渲染为单列 ReorderableListView', (tester) async {
    final layout = DashboardLayout.normalize([
      const DashboardCard(kind: DashboardKind.cpuUsage, span: 2),
      const DashboardCard(kind: DashboardKind.memory, span: 1),
    ]);

    await tester.pumpWidget(
      wrapPage(
        size: const Size(600, 800),
        layout: layout,
        onLayoutChanged: (_) {},
      ),
    );

    expect(find.byType(ReorderableListView), findsOneWidget);
    expect(find.byType(DashboardCardShell), findsNWidgets(2));
  });

  testWidgets('全部卡片隐藏时显示空布局占位提示', (tester) async {
    final hiddenAll = DashboardLayout.normalize([
      const DashboardCard(
        kind: DashboardKind.cpuUsage,
        span: 1,
        visible: false,
      ),
    ]);

    await tester.pumpWidget(
      wrapPage(
        size: const Size(1200, 800),
        layout: hiddenAll,
        onLayoutChanged: (_) {},
      ),
    );

    expect(find.text('暂无可显示的仪表盘卡片'), findsOneWidget);
    expect(find.byType(DashboardCardShell), findsNothing);
  });
}
