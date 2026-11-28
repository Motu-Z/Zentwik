import 'dart:async';

import 'package:material_ui/material_ui.dart';

import '../app.dart';
import '../cards/cards.dart';
import '../dashboard/card_registry.dart';
import '../dashboard/dashboard_layout.dart';
import '../shell/shell_page.dart';
import 'agent_runtime.dart';

/// 应用根组件：持有布局与导航选中项，把运行时状态喂给无状态的 ShellPage。
///
/// 这里只做装配，不写业务：卡片数据由 `DashboardDataProvider` 注入，
/// 连接状态由 `AgentRuntime` 广播。
class ZentwikRoot extends StatefulWidget {
  const ZentwikRoot({
    super.key,
    required this.runtime,
    this.layout,
  });

  final AgentRuntime runtime;

  /// 外部注入的初始布局（后续接设置持久化）；为空时用默认布局。
  final DashboardLayout? layout;

  @override
  State<ZentwikRoot> createState() => _ZentwikRootState();
}

class _ZentwikRootState extends State<ZentwikRoot> {
  late DashboardLayout _layout = widget.layout ?? DashboardLayout.defaults;
  late final CardRegistry _registry = defaultCardRegistry();
  int _selectedIndex = 0;

  @override
  void initState() {
    super.initState();
    unawaited(widget.runtime.start());
  }

  @override
  Widget build(BuildContext context) {
    final appState = widget.runtime.appState;

    return ZentwikApp(
      appState: appState,
      home: AnimatedBuilder(
        // 连接状态与聚合数据都在动，两者合并后统一重建一次。
        animation: Listenable.merge([widget.runtime, appState]),
        builder: (context, _) {
          return DashboardDataProvider(
            telemetry: appState.telemetry,
            modules: appState.modules,
            ledgerEntries: appState.ledger,
            child: ShellPage(
              selectedIndex: _selectedIndex,
              onDestinationSelected: (index) =>
                  setState(() => _selectedIndex = index),
              layout: _layout,
              onLayoutChanged: (next) => setState(() => _layout = next),
              registry: _registry,
              connectionStatus: widget.runtime.status,
              reconnectAttempt: widget.runtime.reconnectAttempt,
              onRetry: () => unawaited(widget.runtime.retry()),
              errorMessage: appState.errorMessage,
              modules: appState.modules,
              busyModuleId: appState.busyModuleId,
              onToggleModule: (id, enable) =>
                  unawaited(appState.setModuleEnabled(id, enable)),
              settings: appState.settings,
              onSettingsChanged: (patch) =>
                  unawaited(appState.saveSettings(patch)),
              agentStatus: appState.status,
            ),
          );
        },
      ),
    );
  }
}
