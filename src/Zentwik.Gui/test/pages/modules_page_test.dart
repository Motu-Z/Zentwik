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

  testWidgets('四种状态文案与 detail 都渲染出来', (tester) async {
    const modules = [
      ModuleDto(id: 'monitoring', name: '监控', state: 'ready', enabled: true),
      ModuleDto(id: 'tuning', name: '调校', state: 'degraded', enabled: true),
      ModuleDto(
        id: 'power',
        name: '电源',
        state: 'unavailable',
        detail: 'AMDFamily17 未加载。',
        enabled: true,
      ),
      ModuleDto(
        id: 'power-limits',
        name: '硬件功耗墙',
        state: 'unavailable',
        detail: '可选组件，未启用。',
        optional: true,
        enabled: false,
      ),
    ];

    await tester.pumpWidget(wrap(const ModulesPage(modules: modules)));

    expect(find.text('就绪'), findsOneWidget);
    expect(find.text('降级'), findsOneWidget);
    expect(find.text('不可用'), findsOneWidget);
    expect(find.text('未启用'), findsOneWidget);
    expect(find.text('AMDFamily17 未加载。'), findsOneWidget);
    expect(find.text('可选组件，未启用。'), findsOneWidget);
  });

  testWidgets('内置模块没有开关，可选模块点击回调带上 id 与目标状态', (tester) async {
    String? toggledId;
    bool? targetState;
    const modules = [
      ModuleDto(id: 'monitoring', name: '监控', state: 'ready', enabled: true),
      ModuleDto(
        id: 'power-limits',
        name: '硬件功耗墙',
        state: 'unavailable',
        optional: true,
        enabled: false,
      ),
    ];

    await tester.pumpWidget(
      wrap(
        ModulesPage(
          modules: modules,
          onToggle: (id, enable) {
            toggledId = id;
            targetState = enable;
          },
        ),
      ),
    );

    // 内置模块后端会拒绝启停，界面就不该给按钮。
    expect(find.text('内置'), findsOneWidget);
    expect(find.byType(Switch), findsOneWidget);

    await tester.tap(find.byType(Switch));
    expect(toggledId, 'power-limits');
    expect(targetState, isTrue);
  });

  testWidgets('enabled 为 false 的可选模块状态显示未启用', (tester) async {
    const modules = [
      ModuleDto(
        id: 'power-limits',
        name: '硬件功耗墙',
        state: 'unavailable',
        optional: true,
        enabled: false,
      ),
    ];

    await tester.pumpWidget(wrap(const ModulesPage(modules: modules)));
    expect(find.text('未启用'), findsOneWidget);
    expect(find.text('不可用'), findsNothing);
  });

  testWidgets('busyModuleId 命中的行显示进度指示，其余行仍可点击', (tester) async {
    String? toggledId;
    const modules = [
      ModuleDto(
        id: 'power-limits',
        name: '硬件功耗墙',
        state: 'unavailable',
        optional: true,
        enabled: false,
      ),
      ModuleDto(
        id: 'other',
        name: '另一个可选模块',
        state: 'ready',
        optional: true,
        enabled: true,
      ),
    ];

    await tester.pumpWidget(
      wrap(
        ModulesPage(
          modules: modules,
          busyModuleId: 'power-limits',
          onToggle: (id, _) => toggledId = id,
        ),
      ),
    );

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(find.byType(Switch), findsOneWidget);

    await tester.tap(find.byType(Switch));
    expect(toggledId, 'other');
  });
}
