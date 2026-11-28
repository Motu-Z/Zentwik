import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/cards/cpu_usage_card.dart';
import 'package:zentwik_gui/protocol/models/telemetry_dto.dart';
import 'package:zentwik_gui/shell/connection_banner.dart';
import 'package:zentwik_gui/state/connection_state.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  group('ZentwikTokens, ZentwikPalette & Theme', () {
    test('token 尺寸非空且暗色主题 scaffoldBackgroundColor 与调色板一致', () {
      expect(ZentwikTokens.spaceLg, 16.0);
      expect(ZentwikTokens.radiusMd, 10.0);
      expect(ZentwikTokens.fontBody, 13.0);

      final dark = ZentwikPalette.resolve();
      final theme = buildZentwikTheme();
      expect(theme.useMaterial3, isTrue);
      expect(theme.brightness, Brightness.dark);
      expect(theme.scaffoldBackgroundColor, dark.background);
      expect(theme.colorScheme.surface, dark.surface);
      expect(theme.colorScheme.primary, dark.accent);
      expect(theme.colorScheme.error, dark.danger);
      expect(theme.extension<ZentwikPalette>(), isNotNull);
    });

    test('支持暗亮双主题 × 四种强调色，未知强调色回退 blue', () {
      const accents = ['blue', 'violet', 'teal', 'amber'];
      final darkAccents = {
        for (final a in accents)
          ZentwikPalette.resolve(brightness: Brightness.dark, accent: a).accent,
      };
      final lightAccents = {
        for (final a in accents)
          ZentwikPalette.resolve(brightness: Brightness.light, accent: a).accent,
      };

      expect(darkAccents.length, 4);
      expect(lightAccents.length, 4);

      final darkBlue =
          ZentwikPalette.resolve(brightness: Brightness.dark, accent: 'blue');
      final lightBlue =
          ZentwikPalette.resolve(brightness: Brightness.light, accent: 'blue');
      expect(darkBlue.background, isNot(equals(lightBlue.background)));
      expect(
        ZentwikPalette.resolve(brightness: Brightness.dark, accent: 'unknown')
            .accent,
        darkBlue.accent,
      );
      expect(
        ZentwikPalette.resolve(brightness: Brightness.light, accent: 'unknown')
            .accent,
        lightBlue.accent,
      );
    });

    test('相同参数构建的主题彼此相等（否则 Theme 会在每帧遥测时判脏整棵树）', () {
      // Theme 用 ThemeData == 决定是否通知依赖者；ThemeExtension 默认按同一性比较，
      // 只要调色板不实现 ==，这里就会永远是 false，界面每秒被全量重建一次。
      expect(buildZentwikTheme(), buildZentwikTheme());
      expect(
        buildZentwikTheme(brightness: Brightness.light, accent: 'teal'),
        buildZentwikTheme(brightness: Brightness.light, accent: 'teal'),
      );
      expect(
        buildZentwikTheme(),
        isNot(buildZentwikTheme(accent: 'amber')),
      );
      expect(ZentwikPalette.resolve(), ZentwikPalette.resolve());
    });

    testWidgets('在 buildZentwikTheme 之外读取 context.palette 抛出 StateError',
        (tester) async {
      Object? caught;
      await tester.pumpWidget(
        MaterialApp(
          home: Builder(
            builder: (context) {
              try {
                context.palette;
              } catch (e) {
                caught = e;
              }
              return const SizedBox.shrink();
            },
          ),
        ),
      );
      expect(caught, isA<StateError>());
    });

    testWidgets('CpuUsageCard 在亮色主题下使用亮色调色板文字颜色', (tester) async {
      final lightPalette = ZentwikPalette.resolve(brightness: Brightness.light);
      final telemetry = TelemetryDto(cpuTotalPercent: 42.5);

      await tester.pumpWidget(
        MaterialApp(
          theme: buildZentwikTheme(brightness: Brightness.light),
          home: Scaffold(
            body: CpuUsageCard(telemetry: telemetry),
          ),
        ),
      );

      final textWidget = tester.widget<Text>(find.text('42.5%'));
      expect(textWidget.style?.color, lightPalette.textPrimary);
    });
  });

  group('ConnectionBanner', () {
    testWidgets('connected 与 disconnected 静默，reconnecting 显示次数，failed 显示重试',
        (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          theme: buildZentwikTheme(),
          home: const Scaffold(
            body: ConnectionBanner(status: ConnectionStatuses.connected),
          ),
        ),
      );
      expect(find.byType(Text), findsNothing);

      await tester.pumpWidget(
        MaterialApp(
          theme: buildZentwikTheme(),
          home: const Scaffold(
            body: ConnectionBanner(
              status: ConnectionStatuses.reconnecting,
              attempt: 3,
            ),
          ),
        ),
      );
      expect(find.textContaining('第 3 次'), findsOneWidget);

      var retried = false;
      await tester.pumpWidget(
        MaterialApp(
          theme: buildZentwikTheme(),
          home: Scaffold(
            body: ConnectionBanner(
              status: ConnectionStatuses.failed,
              onRetry: () => retried = true,
            ),
          ),
        ),
      );
      expect(find.text('重试'), findsOneWidget);
      await tester.tap(find.text('重试'));
      expect(retried, isTrue);
    });
  });
}
