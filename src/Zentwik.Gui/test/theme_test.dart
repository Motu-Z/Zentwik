import 'package:material_ui/material_ui.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/shell/connection_banner.dart';
import 'package:zentwik_gui/state/connection_state.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  group('ZentwikTokens & Theme', () {
    test('token 取值非空且暗色主题 scaffoldBackgroundColor 与 token 一致', () {
      expect(ZentwikTokens.background, isA<Color>());
      expect(ZentwikTokens.surface, isA<Color>());
      expect(ZentwikTokens.border, isA<Color>());
      expect(ZentwikTokens.textPrimary, isA<Color>());
      expect(ZentwikTokens.textSecondary, isA<Color>());
      expect(ZentwikTokens.accent, isA<Color>());
      expect(ZentwikTokens.danger, isA<Color>());
      expect(ZentwikTokens.success, isA<Color>());
      expect(ZentwikTokens.spaceLg, 16.0);
      expect(ZentwikTokens.radiusMd, 10.0);
      expect(ZentwikTokens.fontBody, 13.0);

      final theme = buildZentwikTheme();
      expect(theme.useMaterial3, isTrue);
      expect(theme.brightness, Brightness.dark);
      expect(theme.scaffoldBackgroundColor, ZentwikTokens.background);
      expect(theme.colorScheme.surface, ZentwikTokens.surface);
      expect(theme.colorScheme.primary, ZentwikTokens.accent);
      expect(theme.colorScheme.error, ZentwikTokens.danger);
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
