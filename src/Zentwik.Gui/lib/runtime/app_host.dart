import 'dart:async';

import 'package:material_ui/material_ui.dart';

import '../app.dart';
import '../cards/cards.dart';
import '../dashboard/card_registry.dart';
import '../protocol/errors.dart';
import '../protocol/models/ledger_entry.dart';
import '../protocol/models/settings_dto.dart';
import '../shell/shell_page.dart';
import '../state/connection_state.dart';
import '../workspace/timeline_event.dart';
import '../workspace/workspace.dart';
import 'agent_runtime.dart';

/// 应用根组件：持有工作区与导航选中项，把运行时状态喂给无状态的 ShellPage。
///
/// 这里只做装配，不写业务：卡片数据由 `DashboardDataProvider` 注入，
/// 连接状态由 `AgentRuntime` 广播，首页布局与时间线由 [Workspace] 持有。
class ZentwikRoot extends StatefulWidget {
  const ZentwikRoot({
    super.key,
    required this.runtime,
    this.workspace,
  });

  final AgentRuntime runtime;

  /// 外部注入的工作区（测试用）；为空时自己建一个并读本地布局。
  final Workspace? workspace;

  @override
  State<ZentwikRoot> createState() => _ZentwikRootState();
}

class _ZentwikRootState extends State<ZentwikRoot> {
  late final Workspace _workspace = widget.workspace ?? Workspace();
  late final CardRegistry _registry = defaultCardRegistry();
  int _selectedIndex = 0;

  String? _lastStatus;
  String? _lastError;

  /// 账本快照：只在内容真变化时记事件，避免每秒刷新刷屏。
  Map<String, String>? _ledgerSnapshot;

  @override
  void initState() {
    super.initState();
    unawaited(_workspace.load());
    _lastStatus = widget.runtime.status;
    _lastError = widget.runtime.appState.errorMessage;
    widget.runtime.addListener(_onRuntimeChanged);
    widget.runtime.appState.addListener(_onAppStateChanged);
    unawaited(widget.runtime.start());
  }

  @override
  void dispose() {
    widget.runtime.removeListener(_onRuntimeChanged);
    widget.runtime.appState.removeListener(_onAppStateChanged);
    super.dispose();
  }

  /// 只把**状态跳变**记进时间线，而不是每次通知。
  void _onRuntimeChanged() {
    final status = widget.runtime.status;
    if (status == _lastStatus) {
      return;
    }
    _lastStatus = status;

    switch (status) {
      case ConnectionStatuses.connected:
        _workspace.record(
          category: TimelineCategory.connection,
          severity: TimelineSeverity.success,
          title: '已连接到 Agent 服务',
        );
      case ConnectionStatuses.reconnecting:
        _workspace.record(
          category: TimelineCategory.connection,
          severity: TimelineSeverity.warning,
          title: '连接中断，正在重连',
          detail: '第 ${widget.runtime.reconnectAttempt} 次尝试',
        );
      case ConnectionStatuses.failed:
        _workspace.record(
          category: TimelineCategory.connection,
          severity: TimelineSeverity.danger,
          title: '连接 Agent 服务失败',
          detail: widget.runtime.appState.errorMessage,
        );
      case ConnectionStatuses.disconnected:
        _workspace.record(
          category: TimelineCategory.connection,
          severity: TimelineSeverity.info,
          title: '已断开与 Agent 服务的连接',
        );
    }
  }

  /// 错误提示与账本变更。
  ///
  /// 这里**不记录遥测**：时间线是「发生了什么」，不是采样流水账。
  void _onAppStateChanged() {
    final appState = widget.runtime.appState;

    final error = appState.errorMessage;
    if (error != _lastError) {
      _lastError = error;
      if (error != null && error.trim().isNotEmpty) {
        _workspace.record(
          category: TimelineCategory.system,
          severity: TimelineSeverity.danger,
          title: '操作失败',
          detail: error,
        );
      }
    }

    _trackLedger(appState.ledger);
  }

  void _trackLedger(List<LedgerEntry> entries) {
    final snapshot = <String, String>{
      for (final entry in entries)
        (entry.target ?? '#'): '${entry.actualValue ?? '-'}|${entry.updatedAt ?? ''}',
    };
    final previous = _ledgerSnapshot;
    _ledgerSnapshot = snapshot;
    // 首次拿到账本只建立基线，不补记历史，否则一开机就是几十条。
    if (previous == null) {
      return;
    }
    for (final entry in entries) {
      final key = entry.target ?? '#';
      if (previous[key] == snapshot[key]) {
        continue;
      }
      final status = entry.statusEnum;
      _workspace.record(
        category: TimelineCategory.ledger,
        severity: switch (status) {
          LedgerStatus.failed => TimelineSeverity.danger,
          LedgerStatus.reverted => TimelineSeverity.warning,
          _ => TimelineSeverity.info,
        },
        title: '调度目标变更：$key',
        detail: [
          if (entry.previousValue case final from? when from.isNotEmpty)
            '原值 $from',
          if (entry.actualValue case final to? when to.isNotEmpty) '现值 $to',
          if (entry.source case final source? when source.isNotEmpty)
            '来源 $source',
        ].join(' · '),
      );
    }
  }

  Future<void> _toggleModule(String id, bool enable) async {
    final appState = widget.runtime.appState;
    final name = appState.modules
        .firstWhere(
          (module) => module.id == id,
          orElse: () => appState.modules.first,
        )
        .name;
    final ok = await appState.setModuleEnabled(id, enable);
    _workspace.record(
      category: TimelineCategory.module,
      severity: ok ? TimelineSeverity.success : TimelineSeverity.danger,
      title: '${enable ? '启用' : '停用'}模块 ${name ?? id}',
      detail: ok ? null : appState.errorMessage,
    );
  }

  Future<void> _saveSettings(SettingsPatch patch) async {
    final appState = widget.runtime.appState;
    final ok = await appState.saveSettings(patch);
    _workspace.record(
      category: TimelineCategory.setting,
      severity: ok ? TimelineSeverity.success : TimelineSeverity.danger,
      title: ok ? '设置已保存' : '设置保存失败',
      detail: ok ? _describePatch(patch) : appState.errorMessage,
    );
  }

  String _describePatch(SettingsPatch patch) {
    final parts = <String>[
      if (patch.language != null) '语言 ${patch.language}',
      if (patch.theme != null) '主题 ${patch.theme}',
      if (patch.accent != null) '强调色 ${patch.accent}',
      if (patch.uiScale != null) '界面缩放 ${patch.uiScale}',
      if (patch.monitorIntervalMs != null)
        '监控间隔 ${patch.monitorIntervalMs} ms',
      if (patch.sensorIntervalMs != null)
        '传感器间隔 ${patch.sensorIntervalMs} ms',
    ];
    return parts.isEmpty ? '未指定变更项' : parts.join(' · ');
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
              workspace: _workspace,
              registry: _registry,
              connectionStatus: widget.runtime.status,
              reconnectAttempt: widget.runtime.reconnectAttempt,
              onRetry: () => unawaited(widget.runtime.retry()),
              errorMessage: appState.errorMessage,
              modules: appState.modules,
              busyModuleId: appState.busyModuleId,
              onToggleModule: (id, enable) =>
                  unawaited(_toggleModule(id, enable)),
              settings: appState.settings,
              onSettingsChanged: (patch) =>
                  unawaited(_saveSettings(patch)),
              agentStatus: appState.status,
            ),
          );
        },
      ),
    );
  }
}
