import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';
import 'package:zentwik_gui/protocol/models/settings_dto.dart';
import 'package:zentwik_gui/shell/shell_page.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  Widget buildShell({
    required int selectedIndex,
    required ValueChanged<int> onSelected,
    SettingsDto? settings,
  }) {
    return MaterialApp(
      theme: buildZentwikTheme(),
      home: ShellPage(
        selectedIndex: selectedIndex,
        onDestinationSelected: onSelected,
        layout: DashboardLayout.defaults,
        onLayoutChanged: (_) {},
        settings: settings,
      ),
    );
  }

  testWidgets('宽屏显示 8 项导航文字，点击第二项后显示占位页', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1200, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    var selected = 0;
    await tester.pumpWidget(
      StatefulBuilder(
        builder: (context, setState) {
          return buildShell(
            selectedIndex: selected,
            onSelected: (index) => setState(() => selected = index),
          );
        },
      ),
    );

    for (final label in const [
      '仪表盘',
      '进程',
      '场景',
      'CPU 调校',
      '电源',
      '模块',
      '设置',
      '关于',
    ]) {
      expect(find.text(label), findsOneWidget);
    }

    await tester.tap(find.text('进程'));
    await tester.pump();
    expect(find.text('该页面尚未接入'), findsOneWidget);
  });

  testWidgets('窄屏只显示图标，不显示导航文字', (tester) async {
    await tester.binding.setSurfaceSize(const Size(700, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      buildShell(
        selectedIndex: 0,
        onSelected: (_) {},
      ),
    );

    expect(find.text('进程'), findsNothing);
    expect(find.text('设置'), findsNothing);
    expect(find.byIcon(Icons.list_alt_outlined), findsOneWidget);
  });

  testWidgets('点击设置导航项后显示设置页而非尚未接入', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1200, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    var selected = 0;
    await tester.pumpWidget(
      StatefulBuilder(
        builder: (context, setState) {
          return buildShell(
            selectedIndex: selected,
            onSelected: (index) => setState(() => selected = index),
            settings: const SettingsDto(
              monitorIntervalMs: 1000,
              sensorIntervalMs: 2000,
            ),
          );
        },
      ),
    );

    await tester.tap(find.text('设置'));
    await tester.pump();

    expect(find.text('该页面尚未接入'), findsNothing);
    expect(find.text('设置'), findsNWidgets(2));
  });
}
