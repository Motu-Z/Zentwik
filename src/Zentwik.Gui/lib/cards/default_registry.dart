import 'package:material_ui/material_ui.dart';

import '../dashboard/card_registry.dart';
import '../dashboard/dashboard_kind.dart';
import '../protocol/models/models.dart';
import 'core_clocks_card.dart';
import 'cpu_temperature_card.dart';
import 'cpu_usage_card.dart';
import 'ledger_summary_card.dart';
import 'memory_card.dart';
import 'module_status_card.dart';
import 'package_power_card.dart';
import 'process_count_card.dart';
import 'processor_usage_card.dart';

/// 向卡片子树注入纯展示数据，不直接依赖 `AppState` 或发起协议请求。
class DashboardDataProvider extends InheritedWidget {
  const DashboardDataProvider({
    super.key,
    this.telemetry,
    this.modules,
    this.ledgerEntries,
    required super.child,
  });

  final TelemetryDto? telemetry;
  final List<ModuleDto>? modules;
  final List<LedgerEntry>? ledgerEntries;

  static DashboardDataProvider? maybeOf(BuildContext context) {
    return context.dependOnInheritedWidgetOfExactType<DashboardDataProvider>();
  }

  @override
  bool updateShouldNotify(DashboardDataProvider oldWidget) {
    return telemetry != oldWidget.telemetry ||
        modules != oldWidget.modules ||
        ledgerEntries != oldWidget.ledgerEntries;
  }
}

/// 将 9 种默认 [DashboardKind] 映射到对应卡片内容体。
CardRegistry defaultCardRegistry() {
  return CardRegistry({
    DashboardKind.cpuUsage.wire: (context, _) => CpuUsageCard(
          telemetry: DashboardDataProvider.maybeOf(context)?.telemetry,
        ),
    DashboardKind.cpuTemperature.wire: (context, _) => CpuTemperatureCard(
          telemetry: DashboardDataProvider.maybeOf(context)?.telemetry,
        ),
    DashboardKind.packagePower.wire: (context, _) => PackagePowerCard(
          telemetry: DashboardDataProvider.maybeOf(context)?.telemetry,
        ),
    DashboardKind.coreClocks.wire: (context, _) => CoreClocksCard(
          telemetry: DashboardDataProvider.maybeOf(context)?.telemetry,
        ),
    DashboardKind.processorUsage.wire: (context, _) => ProcessorUsageCard(
          telemetry: DashboardDataProvider.maybeOf(context)?.telemetry,
        ),
    DashboardKind.memory.wire: (context, _) => MemoryCard(
          telemetry: DashboardDataProvider.maybeOf(context)?.telemetry,
        ),
    DashboardKind.processCount.wire: (context, _) => ProcessCountCard(
          telemetry: DashboardDataProvider.maybeOf(context)?.telemetry,
        ),
    DashboardKind.ledgerSummary.wire: (context, _) => LedgerSummaryCard(
          entries: DashboardDataProvider.maybeOf(context)?.ledgerEntries,
        ),
    DashboardKind.moduleStatus.wire: (context, _) => ModuleStatusCard(
          modules: DashboardDataProvider.maybeOf(context)?.modules,
        ),
  });
}
