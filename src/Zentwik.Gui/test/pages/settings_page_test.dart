import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/format/units.dart';
import 'package:zentwik_gui/pages/settings_page.dart';
import 'package:zentwik_gui/protocol/models/settings_dto.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  Widget wrap(Widget child) {
    return MaterialApp(
      theme: buildZentwikTheme(),
      home: Scaffold(body: child),
    );
  }

  testWidgets('settings 为 null 时整页显示 —', (tester) async {
    await tester.pumpWidget(wrap(const SettingsPage()));
    expect(find.text(kMissingValue), findsOneWidget);
  });

  testWidgets('越界输入被夹取、回显并给出提示', (tester) async {
    SettingsPatch? captured;
    const settings = SettingsDto(
      language: 'zh-CN',
      theme: 'dark',
      accent: '#4C8DFF',
      uiScale: 1.0,
      monitorIntervalMs: 1000,
      sensorIntervalMs: 2000,
    );

    await tester.pumpWidget(
      wrap(
        SettingsPage(
          settings: settings,
          onChange: (patch) => captured = patch,
        ),
      ),
    );

    final monitorField = find.byType(TextField).first;
    await tester.enterText(monitorField, '100');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pump();

    expect(captured?.monitorIntervalMs, 500);
    expect(find.text('500'), findsOneWidget);
    expect(find.textContaining('允许范围为 500–5000 ms'), findsOneWidget);
  });

  testWidgets('非数字输入回退到原值并提示，不提交任何设置', (tester) async {
    SettingsPatch? captured;
    const settings = SettingsDto(monitorIntervalMs: 1000, sensorIntervalMs: 2000);

    await tester.pumpWidget(
      wrap(
        SettingsPage(
          settings: settings,
          onChange: (patch) => captured = patch,
        ),
      ),
    );

    await tester.enterText(find.byType(TextField).first, 'abc');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pump();

    expect(captured, isNull);
    expect(find.text('1000'), findsOneWidget);
    expect(find.textContaining('需为 500–5000 之间的整数'), findsOneWidget);
  });

  testWidgets('失焦即提交，不必按回车', (tester) async {
    SettingsPatch? captured;
    const settings = SettingsDto(monitorIntervalMs: 1000, sensorIntervalMs: 2000);

    await tester.pumpWidget(
      wrap(
        SettingsPage(
          settings: settings,
          onChange: (patch) => captured = patch,
        ),
      ),
    );

    await tester.enterText(find.byType(TextField).first, '1500');
    await tester.pump();
    expect(captured, isNull);

    // 焦点移到另一个输入框 => 第一个失焦 => 应提交。
    await tester.tap(find.byType(TextField).last);
    await tester.pump();

    expect(captured?.monitorIntervalMs, 1500);
  });

  testWidgets('readOnly 为 true 时输入框禁用', (tester) async {
    const settings = SettingsDto(
      monitorIntervalMs: 1000,
      sensorIntervalMs: 2000,
    );

    await tester.pumpWidget(
      wrap(
        const SettingsPage(
          settings: settings,
          readOnly: true,
        ),
      ),
    );

    final fields = tester.widgetList<TextField>(find.byType(TextField));
    for (final field in fields) {
      expect(field.enabled, isFalse);
    }
  });
}
