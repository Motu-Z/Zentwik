import 'package:material_ui/material_ui.dart';

import '../dashboard/card_registry.dart';
import '../dashboard/dashboard_layout.dart';
import '../pages/about_page.dart';
import '../pages/dashboard_page.dart';
import '../pages/modules_page.dart';
import '../pages/settings_page.dart';
import '../protocol/models/agent_status_dto.dart';
import '../protocol/models/module_dto.dart';
import '../protocol/models/settings_dto.dart';
import '../state/connection_state.dart';
import '../theme/tokens.dart';
import 'connection_banner.dart';

class _ShellDestination {
  const _ShellDestination(this.label, this.icon);

  final String label;
  final IconData icon;
}

const List<_ShellDestination> _destinations = [
  _ShellDestination('仪表盘', Icons.dashboard_outlined),
  _ShellDestination('进程', Icons.list_alt_outlined),
  _ShellDestination('场景', Icons.auto_awesome_outlined),
  _ShellDestination('CPU 调校', Icons.tune_outlined),
  _ShellDestination('电源', Icons.bolt_outlined),
  _ShellDestination('模块', Icons.extension_outlined),
  _ShellDestination('设置', Icons.settings_outlined),
  _ShellDestination('关于', Icons.info_outline),
];

/// 主壳无状态页面：左侧固定 8 项导航 + 顶部连接横幅 + 内容区。
class ShellPage extends StatelessWidget {
  const ShellPage({
    super.key,
    this.selectedIndex = 0,
    required this.onDestinationSelected,
    required this.layout,
    required this.onLayoutChanged,
    this.registry = const CardRegistry(),
    this.onResizeCard,
    this.connectionStatus = ConnectionStatuses.disconnected,
    this.reconnectAttempt = 0,
    this.onRetry,
    this.modules,
    this.busyModuleId,
    this.onToggleModule,
    this.settings,
    this.onSettingsChanged,
    this.settingsReadOnly = false,
    this.agentStatus,
  });

  static const double wideBreakpoint = 900.0;

  final int selectedIndex;
  final ValueChanged<int> onDestinationSelected;
  final DashboardLayout layout;
  final ValueChanged<DashboardLayout> onLayoutChanged;
  final CardRegistry registry;
  final ValueChanged<DashboardCard>? onResizeCard;
  final String connectionStatus;
  final int reconnectAttempt;
  final VoidCallback? onRetry;
  final List<ModuleDto>? modules;
  final String? busyModuleId;
  final ValueChanged<String>? onToggleModule;
  final SettingsDto? settings;
  final ValueChanged<SettingsPatch>? onSettingsChanged;
  final bool settingsReadOnly;
  final AgentStatusDto? agentStatus;

  Widget _buildContent() {
    return switch (selectedIndex) {
      0 => DashboardPage(
          layout: layout,
          onLayoutChanged: onLayoutChanged,
          registry: registry,
          onResizeCard: onResizeCard,
        ),
      5 => ModulesPage(
          modules: modules,
          busyModuleId: busyModuleId,
          onToggle: onToggleModule,
        ),
      6 => SettingsPage(
          settings: settings,
          onChange: onSettingsChanged,
          readOnly: settingsReadOnly,
        ),
      7 => AboutPage(
          status: agentStatus,
          connectionStatus: connectionStatus,
        ),
      _ => const Center(
          child: Text(
            '该页面尚未接入',
            style: TextStyle(
              fontSize: ZentwikTokens.fontBody,
              color: ZentwikTokens.textSecondary,
            ),
          ),
        ),
    };
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: LayoutBuilder(
        builder: (context, constraints) {
          final isWide = constraints.maxWidth >= wideBreakpoint;
          return Row(
            children: [
              _ShellNavRail(
                extended: isWide,
                selectedIndex: selectedIndex,
                onSelected: onDestinationSelected,
              ),
              const VerticalDivider(width: 1, thickness: 1),
              Expanded(
                child: Column(
                  children: [
                    ConnectionBanner(
                      status: connectionStatus,
                      attempt: reconnectAttempt,
                      onRetry: onRetry,
                    ),
                    Expanded(child: _buildContent()),
                  ],
                ),
              ),
            ],
          );
        },
      ),
    );
  }
}

class _ShellNavRail extends StatelessWidget {
  const _ShellNavRail({
    required this.extended,
    required this.selectedIndex,
    required this.onSelected,
  });

  final bool extended;
  final int selectedIndex;
  final ValueChanged<int> onSelected;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: extended ? 168 : 56,
      color: ZentwikTokens.surface,
      padding: const EdgeInsets.symmetric(vertical: ZentwikTokens.spaceMd),
      child: Column(
        children: [
          for (var i = 0; i < _destinations.length; i++)
            _NavItem(
              destination: _destinations[i],
              extended: extended,
              selected: i == selectedIndex,
              onTap: () => onSelected(i),
            ),
        ],
      ),
    );
  }
}

class _NavItem extends StatelessWidget {
  const _NavItem({
    required this.destination,
    required this.extended,
    required this.selected,
    required this.onTap,
  });

  final _ShellDestination destination;
  final bool extended;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final color =
        selected ? ZentwikTokens.accent : ZentwikTokens.textSecondary;

    return InkWell(
      onTap: onTap,
      child: Container(
        height: 40,
        padding: EdgeInsets.symmetric(
          horizontal: extended ? ZentwikTokens.spaceLg : 0,
        ),
        alignment: extended ? Alignment.centerLeft : Alignment.center,
        decoration: BoxDecoration(
          color: selected
              ? ZentwikTokens.accent.withValues(alpha: 0.12)
              : Colors.transparent,
        ),
        child: extended
            ? Row(
                children: [
                  Icon(destination.icon, size: 18, color: color),
                  const SizedBox(width: ZentwikTokens.spaceMd),
                  Expanded(
                    child: Text(
                      destination.label,
                      style: TextStyle(
                        fontSize: ZentwikTokens.fontBody,
                        color: selected
                            ? ZentwikTokens.textPrimary
                            : ZentwikTokens.textSecondary,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              )
            : Tooltip(
                message: destination.label,
                child: Icon(destination.icon, size: 18, color: color),
              ),
      ),
    );
  }
}
