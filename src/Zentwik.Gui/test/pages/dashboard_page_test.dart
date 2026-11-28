import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';
import 'package:zentwik_gui/pages/dashboard_page.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  Widget wrapPage({
    required DashboardLayout layout,
    required ValueChanged<DashboardLayout> onLayoutChanged,
  }) {
    return MaterialApp(
      theme: buildZentwikTheme(),
      home: Scaffold(
        body: DashboardPage(
          layout: layout,
          onLayoutChanged: onLayoutChanged,
        ),
      ),
    );
  }

  /// 用真实的窗口尺寸驱动 `LayoutBuilder`（`MediaQuery` 改尺寸影响不到约束）。
  Future<void> setSurface(WidgetTester tester, Size size) async {
    await tester.binding.setSurfaceSize(size);
    addTearDown(() => tester.binding.setSurfaceSize(null));
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
    await setSurface(tester, const Size(1200, 800));
    await tester.pumpWidget(
      wrapPage(
        layout: layout,
        onLayoutChanged: (next) => updated = next,
      ),
    );

    expect(find.byType(DashboardCardShell), findsNWidgets(2));
    expect(
      find.descendant(
        of: find.byType(DashboardCardShell),
        matching: find.text(DashboardKind.memory.label),
      ),
      findsNothing,
    );
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

  testWidgets('窄屏渲染为单列 ReorderableListView 且不显示 resize 手柄',
      (tester) async {
    final layout = DashboardLayout.normalize([
      const DashboardCard(kind: DashboardKind.cpuUsage, span: 2),
      const DashboardCard(kind: DashboardKind.memory, span: 1),
    ]);

    await setSurface(tester, const Size(600, 800));
    await tester.pumpWidget(
      wrapPage(
        layout: layout,
        onLayoutChanged: (_) {},
      ),
    );

    expect(find.byType(ReorderableListView), findsOneWidget);
    expect(find.byType(DashboardCardShell), findsNWidgets(2));
    expect(find.byIcon(Icons.open_in_full), findsNothing);
    expect(find.byIcon(Icons.visibility_off_outlined), findsNWidgets(2));
  });

  testWidgets('全部卡片隐藏时显示空布局占位提示', (tester) async {
    final hiddenAll = DashboardLayout.normalize([
      const DashboardCard(
        kind: DashboardKind.cpuUsage,
        span: 1,
        visible: false,
      ),
    ]);

    await setSurface(tester, const Size(1200, 800));
    await tester.pumpWidget(
      wrapPage(
        layout: hiddenAll,
        onLayoutChanged: (_) {},
      ),
    );

    expect(find.text('暂无可显示的仪表盘卡片'), findsOneWidget);
    expect(find.byType(DashboardCardShell), findsNothing);
  });

  testWidgets('宽屏点击 resize 在 minSpan..maxSpan 循环，隐藏后可从顶部恢复',
      (tester) async {
    var layout = DashboardLayout.normalize([
      const DashboardCard(kind: DashboardKind.cpuUsage, span: 1),
      const DashboardCard(kind: DashboardKind.memory, span: 1),
    ]);

    await setSurface(tester, const Size(1200, 800));
    await tester.pumpWidget(
      StatefulBuilder(
        builder: (context, setState) {
          return wrapPage(
            layout: layout,
            onLayoutChanged: (next) => setState(() => layout = next),
          );
        },
      ),
    );

    expect(find.text('已隐藏的卡片：'), findsNothing);

    await tester.tap(find.byIcon(Icons.open_in_full).first);
    await tester.pump();
    expect(layout.cards.first.span, 2);

    await tester.tap(find.byIcon(Icons.open_in_full).first);
    await tester.pump();
    expect(layout.cards.first.span, 1);

    await tester.tap(find.byIcon(Icons.visibility_off_outlined).first);
    await tester.pump();
    expect(find.byType(DashboardCardShell), findsOneWidget);
    expect(find.text('已隐藏的卡片：'), findsOneWidget);
    expect(find.widgetWithText(ActionChip, DashboardKind.cpuUsage.label),
        findsOneWidget);

    await tester
        .tap(find.widgetWithText(ActionChip, DashboardKind.cpuUsage.label));
    await tester.pump();
    expect(find.byType(DashboardCardShell), findsNWidgets(2));
    expect(find.text('已隐藏的卡片：'), findsNothing);
  });
}
