import 'package:material_ui/material_ui.dart';

import 'state/app_state.dart';
import 'theme/theme.dart';

/// 通过 [InheritedNotifier] 向子树提供 [AppState]。
class AppStateScope extends InheritedNotifier<AppState> {
  const AppStateScope({
    super.key,
    required AppState appState,
    required super.child,
  }) : super(notifier: appState);

  static AppState of(BuildContext context) {
    final scope =
        context.dependOnInheritedWidgetOfExactType<AppStateScope>();
    assert(scope != null, '未在 Widget 树中找到 AppStateScope');
    return scope!.notifier!;
  }

  static AppState? maybeOf(BuildContext context) {
    return context
        .dependOnInheritedWidgetOfExactType<AppStateScope>()
        ?.notifier;
  }
}

/// 应用根组件：响应 [AppState.settings] 实时切换主题、强调色与文字缩放。
class ZentwikApp extends StatelessWidget {
  const ZentwikApp({
    super.key,
    required this.appState,
    this.home,
  });

  final AppState appState;
  final Widget? home;

  @override
  Widget build(BuildContext context) {
    return AppStateScope(
      appState: appState,
      child: AnimatedBuilder(
        animation: appState,
        builder: (context, _) {
          final settings = appState.settings;
          final brightness = settings?.theme == 'light'
              ? Brightness.light
              : Brightness.dark;
          final accent = settings?.accent ?? 'blue';
          final scale = (settings?.uiScale ?? 1.0).clamp(0.8, 1.6).toDouble();

          return MaterialApp(
            title: 'Zentwik',
            theme: buildZentwikTheme(
              brightness: brightness,
              accent: accent,
            ),
            builder: (context, child) {
              return MediaQuery(
                data: MediaQuery.of(context).copyWith(
                  textScaler: TextScaler.linear(scale),
                ),
                child: child ?? const SizedBox.shrink(),
              );
            },
            home: home ?? const Scaffold(body: SizedBox.shrink()),
          );
        },
      ),
    );
  }
}
