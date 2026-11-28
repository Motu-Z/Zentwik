import 'package:flutter/material.dart';

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

/// 应用根组件：装配暗色主题与 [AppStateScope]。
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
      child: MaterialApp(
        title: 'Zentwik',
        theme: buildZentwikTheme(),
        home: home ?? const Scaffold(body: SizedBox.shrink()),
      ),
    );
  }
}
