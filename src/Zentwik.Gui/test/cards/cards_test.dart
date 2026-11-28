import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/cards/cards.dart';
import 'package:zentwik_gui/dashboard/dashboard_kind.dart';
import 'package:zentwik_gui/dashboard/dashboard_layout.dart';
import 'package:zentwik_gui/format/units.dart';
import 'package:zentwik_gui/protocol/models/models.dart';
import 'package:zentwik_gui/theme/theme.dart';

void main() {
  Widget wrap(Widget child) {
    return MaterialApp(
      theme: buildZentwikTheme(),
      home: Scaffold(body: SingleChildScrollView(child: child)),
    );
  }

  testWidgets('九张卡在全 null 数据下均显示 — 且不抛异常', (tester) async {
    await tester.pumpWidget(
      wrap(
        const Column(
          children: [
            CpuUsageCard(),
            CpuTemperatureCard(),
            PackagePowerCard(),
            CoreClocksCard(),
            ProcessorUsageCard(),
            MemoryCard(),
            ProcessCountCard(),
            LedgerSummaryCard(),
            ModuleStatusCard(),
          ],
        ),
      ),
    );

    expect(find.text(kMissingValue), findsNWidgets(9));
  });

  testWidgets('MemoryCard 在总量为 0 时不除零并显示 —', (tester) async {
    await tester.pumpWidget(
      wrap(
        const MemoryCard(
          telemetry: TelemetryDto(
            memoryTotalBytes: 0,
            memoryAvailableBytes: 0,
          ),
        ),
      ),
    );

    // 总量为 0 属「无数据」：整卡只出一个缺失标记，不画空进度条。
    expect(find.text(kMissingValue), findsOneWidget);
  });

  testWidgets('九张卡在有数据时关键文案格式正确，defaultCardRegistry 可正常构建',
      (tester) async {
    const telemetry = TelemetryDto(
      cpuTotalPercent: 12.34,
      temperatureCelsius: 64.5,
      packagePowerWatts: 35.2,
      memoryTotalBytes: 33485860864,
      memoryAvailableBytes: 16735488410,
      processCount: 312,
      coreClocks: [
        CoreClockDto(ccd: 0, core: 3, megahertz: 4450),
      ],
      processorUtilization: [10.0, 25.0],
    );

    const modules = [
      ModuleDto(
        id: 'monitoring',
        name: '监控',
        state: 'ready',
        enabled: true,
      ),
    ];

    const entries = [
      LedgerEntry(target: 'power.active', status: 0),
      LedgerEntry(target: 'power.min', status: 0),
      LedgerEntry(target: 'cpu.co', status: 0),
    ];

    final registry = defaultCardRegistry();

    await tester.pumpWidget(
      wrap(
        DashboardDataProvider(
          telemetry: telemetry,
          modules: modules,
          ledgerEntries: entries,
          child: Column(
            children: [
              for (final kind in DashboardKind.values)
                Builder(
                  builder: (context) => registry.buildCard(
                    context,
                    DashboardCard(kind: kind, span: kind.defaultSpan),
                  ),
                ),
            ],
          ),
        ),
      ),
    );

    expect(find.text('12.3%'), findsOneWidget);
    expect(find.text('64.5 °C'), findsOneWidget);
    expect(find.text('35.2 W'), findsOneWidget);
    expect(find.text('CCD0-C03'), findsOneWidget);
    expect(find.text('4.45 GHz'), findsOneWidget);
    expect(find.textContaining('15.6 GB'), findsOneWidget);
    expect(find.text('312'), findsOneWidget);
    expect(find.text('含受保护进程'), findsOneWidget);
    expect(find.text('3'), findsOneWidget);
    expect(find.text('power'), findsOneWidget);
    expect(find.text('监控'), findsOneWidget);
    expect(find.text('就绪'), findsOneWidget);
  });
}
