import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/format/units.dart';
import 'package:zentwik_gui/pages/modules_page.dart';
import 'package:zentwik_gui/protocol/models/module_dto.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  Widget wrap(Widget child) {
    return MaterialApp(
      theme: buildZentwikTheme(),
      home: Scaffold(body: child),
    );
  }

  testWidgets('modules 为 null 时显示 —，空列表显示无模块', (tester) async {
    await tester.pumpWidget(wrap(const ModulesPage()));
    expect(find.text(kMissingValue), findsOneWidget);

    await tester.pumpWidget(wrap(const ModulesPage(modules: [])));
    expect(find.text('无模块'), findsOneWidget);
  });

  testWidgets('三种状态文案正确且点击回调带上模块 id', (tester) async {
    String? toggledId;
    const modules = [
      ModuleDto(id: 'monitoring', name: '监控', state: 'ready', enabled: true),
      ModuleDto(id: 'tuning', name: '调校', state: 'degraded', enabled: true),
      ModuleDto(id: 'power', name: '功耗墙', state: 'unavailable', enabled: true),
      ModuleDto(id: 'idle', name: '备用', state: 'unavailable', enabled: false),
    ];

    await tester.pumpWidget(
      wrap(
        ModulesPage(
          modules: modules,
          onToggle: (id) => toggledId = id,
        ),
      ),
    );

    expect(find.text('就绪'), findsOneWidget);
    expect(find.text('降级'), findsOneWidget);
    expect(find.text('不可用'), findsOneWidget);
    expect(find.text('未启用'), findsOneWidget);

    await tester.tap(find.byType(Switch).first);
    expect(toggledId, 'monitoring');
  });

  testWidgets('busyModuleId 命中的行显示进度指示并禁用点击', (tester) async {
    String? toggledId;
    const modules = [
      ModuleDto(id: 'monitoring', name: '监控', state: 'ready', enabled: true),
      ModuleDto(id: 'tuning', name: '调校', state: 'ready', enabled: true),
    ];

    await tester.pumpWidget(
      wrap(
        ModulesPage(
          modules: modules,
          busyModuleId: 'monitoring',
          onToggle: (id) => toggledId = id,
        ),
      ),
    );

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(find.byType(Switch), findsOneWidget);

    await tester.tap(find.byType(Switch).first);
    expect(toggledId, 'tuning');
  });
}
