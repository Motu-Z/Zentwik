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
      accent: 'blue',
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
    const settings =
        SettingsDto(monitorIntervalMs: 1000, sensorIntervalMs: 2000);

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
    const settings =
        SettingsDto(monitorIntervalMs: 1000, sensorIntervalMs: 2000);

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

    await tester.tap(find.byType(TextField).last);
    await tester.pump();

    expect(captured?.monitorIntervalMs, 1500);
  });

  testWidgets('三个枚举下拉与缩放滑杆范围正确，且仅在变更后发出 SettingsPatch',
      (tester) async {
    SettingsPatch? captured;
    const settings = SettingsDto(
      language: 'zh-CN',
      theme: 'dark',
      accent: 'blue',
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

    expect(SettingsPage.languageOptions.keys, ['zh-CN', 'zh-TW', 'en-US']);
    expect(SettingsPage.themeOptions.keys, ['dark', 'light']);
    expect(
      SettingsPage.accentOptions.keys,
      ['blue', 'violet', 'teal', 'amber'],
    );

    final slider = tester.widget<Slider>(find.byType(Slider));
    expect(slider.min, 0.8);
    expect(slider.max, 1.6);
    expect(slider.divisions, 16);

    await tester.tap(find.text('蓝色'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('紫色').last);
    await tester.pumpAndSettle();

    expect(captured?.accent, 'violet');
  });

  testWidgets('readOnly 为 true 时全部输入、下拉与滑杆禁用', (tester) async {
    const settings = SettingsDto(
      language: 'zh-CN',
      theme: 'dark',
      accent: 'blue',
      uiScale: 1.0,
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

    for (final field in tester.widgetList<TextField>(find.byType(TextField))) {
      expect(field.enabled, isFalse);
    }
    for (final dropdown in tester.widgetList<DropdownButton<String>>(
      find.byType(DropdownButton<String>),
    )) {
      expect(dropdown.onChanged, isNull);
    }
    final slider = tester.widget<Slider>(find.byType(Slider));
    expect(slider.onChanged, isNull);
  });
}
