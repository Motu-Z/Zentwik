import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/cards/cards.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';
import 'package:zentwik_gui/pages/dashboard_page.dart';
import 'package:zentwik_gui/protocol/models/models.dart';
import 'package:zentwik_gui/theme/theme.dart';
import 'package:zentwik_gui/workspace/workspace.dart';
import 'package:zentwik_gui/workspace/workspace_store.dart';

void main() {
  group('CardMetrics 断点与兜底', () {
    test('unknown 兜底为 compact + unbounded', () {
      expect(CardMetrics.unknown.bodyWidth, 0);
      expect(CardMetrics.unknown.bodyHeight, isNull);
      expect(CardMetrics.unknown.widthClass, CardWidthClass.compact);
      expect(CardMetrics.unknown.heightClass, CardHeightClass.unbounded);
    });

    test('宽度与高度断点边界 (-1 / == / +1)', () {
      expect(
        const CardMetrics(bodyWidth: 219, bodyHeight: null).widthClass,
        CardWidthClass.compact,
      );
      expect(
        const CardMetrics(bodyWidth: 220, bodyHeight: null).widthClass,
        CardWidthClass.regular,
      );
      expect(
        const CardMetrics(bodyWidth: 379, bodyHeight: null).widthClass,
        CardWidthClass.regular,
      );
      expect(
        const CardMetrics(bodyWidth: 380, bodyHeight: null).widthClass,
        CardWidthClass.wide,
      );

      expect(
        const CardMetrics(bodyWidth: 300, bodyHeight: 111).heightClass,
        CardHeightClass.tight,
      );
      expect(
        const CardMetrics(bodyWidth: 300, bodyHeight: 112).heightClass,
        CardHeightClass.regular,
      );
      expect(
        const CardMetrics(bodyWidth: 300, bodyHeight: 319).heightClass,
        CardHeightClass.regular,
      );
      expect(
        const CardMetrics(bodyWidth: 300, bodyHeight: 320).heightClass,
        CardHeightClass.tall,
      );
    });

    testWidgets('缺 _CardMetricsScope 时 CardMetrics.of 不抛异常并返回 unknown',
        (tester) async {
      late CardMetrics captured;
      await tester.pumpWidget(
        MaterialApp(
          home: Builder(
            builder: (context) {
              captured = CardMetrics.of(context);
              return const SizedBox.shrink();
            },
          ),
        ),
      );
      expect(captured, CardMetrics.unknown);
    });
  });

  group('F.1 纯函数自适应决策单测', () {
    test('resolveCpuUsage 断点与统计行开关', () {
      final compact = resolveCpuUsage(
        metrics: const CardMetrics(bodyWidth: 219, bodyHeight: null),
        hasProcessorStats: true,
      );
      expect(compact.inlineBar, isFalse);
      expect(compact.showStats, isFalse);

      final regular = resolveCpuUsage(
        metrics: const CardMetrics(bodyWidth: 220, bodyHeight: null),
        hasProcessorStats: true,
      );
      expect(regular.inlineBar, isTrue);
      expect(regular.showStats, isFalse);

      final wide = resolveCpuUsage(
        metrics: const CardMetrics(bodyWidth: 380, bodyHeight: null),
        hasProcessorStats: true,
      );
      expect(wide.inlineBar, isTrue);
      expect(wide.showStats, isTrue);

      final wideTight = resolveCpuUsage(
        metrics: const CardMetrics(bodyWidth: 380, bodyHeight: 111),
        hasProcessorStats: true,
      );
      expect(wideTight.showStats, isFalse);

      final tall = resolveCpuUsage(
        metrics: const CardMetrics(bodyWidth: 220, bodyHeight: 320),
        hasProcessorStats: false,
      );
      expect(tall.displayFont, isTrue);
    });

    test('describeTemperature 与 resolveCpuTemperature 阈值及同源文案', () {
      final p = ZentwikPalette.resolve();
      expect(describeTemperature(p, 69.9), (color: p.success, label: '正常'));
      expect(describeTemperature(p, 70.0), (color: p.accent, label: '偏高'));
      expect(describeTemperature(p, 84.9), (color: p.accent, label: '偏高'));
      expect(describeTemperature(p, 85.0), (color: p.danger, label: '过热'));
      expect(describeTemperature(p, null), (color: p.textSecondary, label: ''));

      expect(
        resolveCpuTemperature(
          metrics: const CardMetrics(bodyWidth: 219, bodyHeight: null),
        ).showLabel,
        isFalse,
      );
      expect(
        resolveCpuTemperature(
          metrics: const CardMetrics(bodyWidth: 220, bodyHeight: 112),
        ).showLabel,
        isTrue,
      );
      expect(
        resolveCpuTemperature(
          metrics: const CardMetrics(bodyWidth: 380, bodyHeight: 111),
        ).showLabel,
        isFalse,
      );
      expect(
        resolveCpuTemperature(
          metrics: const CardMetrics(bodyWidth: 220, bodyHeight: 320),
        ).displayFont,
        isTrue,
      );
    });

    test('resolvePackagePower 高度降级与增高放大', () {
      expect(
        resolvePackagePower(
          metrics: const CardMetrics(bodyWidth: 200, bodyHeight: 111),
        ).showSubtitle,
        isFalse,
      );
      expect(
        resolvePackagePower(
          metrics: const CardMetrics(bodyWidth: 200, bodyHeight: 112),
        ).showSubtitle,
        isTrue,
      );
      expect(
        resolvePackagePower(
          metrics: const CardMetrics(bodyWidth: 200, bodyHeight: 320),
        ).displayFont,
        isTrue,
      );
    });

    test('resolveMemory 四档模式切换', () {
      expect(
        resolveMemory(
          metrics: const CardMetrics(bodyWidth: 219, bodyHeight: null),
        ).mode,
        MemoryLayoutMode.compactStacked,
      );
      expect(
        resolveMemory(
          metrics: const CardMetrics(bodyWidth: 220, bodyHeight: null),
        ).mode,
        MemoryLayoutMode.splitRow,
      );
      expect(
        resolveMemory(
          metrics: const CardMetrics(bodyWidth: 380, bodyHeight: 111),
        ).mode,
        MemoryLayoutMode.tightMinimal,
      );
      expect(
        resolveMemory(
          metrics: const CardMetrics(bodyWidth: 220, bodyHeight: 320),
        ).mode,
        MemoryLayoutMode.tallDisplay,
      );
    });

    test('resolveProcessCount 逻辑处理器展示规则', () {
      expect(
        resolveProcessCount(
          metrics: const CardMetrics(bodyWidth: 219, bodyHeight: null),
          hasProcessorCount: true,
        ).showProcessorCount,
        isFalse,
      );
      expect(
        resolveProcessCount(
          metrics: const CardMetrics(bodyWidth: 220, bodyHeight: null),
          hasProcessorCount: true,
        ).showProcessorCount,
        isTrue,
      );
      expect(
        resolveProcessCount(
          metrics: const CardMetrics(bodyWidth: 380, bodyHeight: 111),
          hasProcessorCount: true,
        ).showSubtitle,
        isFalse,
      );
    });

    test('resolveCoreClocks 宽/高二维自适应与边界', () {
      expect(
        resolveCoreClocks(
          metrics: const CardMetrics(bodyWidth: 299, bodyHeight: null),
          coreCount: 8,
        ).columns,
        1,
      );
      expect(
        resolveCoreClocks(
          metrics: const CardMetrics(bodyWidth: 300, bodyHeight: null),
          coreCount: 8,
        ).columns,
        2,
      );
      expect(
        resolveCoreClocks(
          metrics: const CardMetrics(bodyWidth: 560, bodyHeight: null),
          coreCount: 8,
        ).columns,
        4,
      );

      final constrained = resolveCoreClocks(
        metrics: const CardMetrics(bodyWidth: 500, bodyHeight: 120),
        coreCount: 16,
      );
      expect(constrained.columns, 2);
      expect(constrained.visibleRows, greaterThan(0));
      expect(constrained.visibleRows, lessThan(8));
      expect(constrained.hiddenCores, greaterThan(0));

      expect(
        resolveCoreClocks(
          metrics: const CardMetrics(bodyWidth: 300, bodyHeight: null),
          coreCount: 8,
        ).dense,
        isTrue,
      );
      expect(
        resolveCoreClocks(
          metrics: const CardMetrics(bodyWidth: 300, bodyHeight: null),
          coreCount: 8,
          hasDistinctCcds: true,
        ).dense,
        isFalse,
      );
      expect(
        resolveCoreClocks(
          metrics: const CardMetrics(bodyWidth: 300, bodyHeight: null),
          coreCount: 0,
        ).visibleRows,
        0,
      );
    });

    test('resolveProcessorUsage 条宽夹取、高度填充与超限截断', () {
      final crowded = resolveProcessorUsage(
        metrics: const CardMetrics(bodyWidth: 223, bodyHeight: null),
        count: 32,
      );
      expect(crowded.barWidth, greaterThanOrEqualTo(4.0));
      expect(crowded.barHeight, 28.0);
      expect(crowded.hiddenCount, 0);

      final tall = resolveProcessorUsage(
        metrics: const CardMetrics(bodyWidth: 400, bodyHeight: 120),
        count: 8,
      );
      expect(tall.barHeight, greaterThan(28.0));
      expect(tall.barHeight, lessThanOrEqualTo(120.0));

      final tight = resolveProcessorUsage(
        metrics: const CardMetrics(bodyWidth: 223, bodyHeight: 40),
        count: 32,
      );
      expect(tight.visibleCount, greaterThanOrEqualTo(1));
      expect(tight.hiddenCount, greaterThan(0));
    });

    test('resolveLedgerSummary 少分组不触发 +N 且宽卡分栏', () {
      final twoGroups = resolveLedgerSummary(
        metrics: const CardMetrics(bodyWidth: 200, bodyHeight: null),
        groupCount: 2,
      );
      expect(twoGroups.maxGroups, 2);
      expect(twoGroups.hiddenGroups, 0);

      final compactMany = resolveLedgerSummary(
        metrics: const CardMetrics(bodyWidth: 200, bodyHeight: null),
        groupCount: 6,
      );
      expect(compactMany.splitColumns, isFalse);
      expect(compactMany.maxGroups, 3);
      expect(compactMany.hiddenGroups, 3);

      final wide = resolveLedgerSummary(
        metrics: const CardMetrics(bodyWidth: 420, bodyHeight: null),
        groupCount: 6,
      );
      expect(wide.splitColumns, isTrue);
      expect(wide.maxGroups, 6);
      expect(wide.hiddenGroups, 0);

      final tight = resolveLedgerSummary(
        metrics: const CardMetrics(bodyWidth: 420, bodyHeight: 100),
        groupCount: 6,
      );
      expect(tight.maxGroups, 0);
    });

    test('resolveModuleStatus 显式高度截断并算剩余数量', () {
      final unbounded = resolveModuleStatus(
        metrics: const CardMetrics(bodyWidth: 220, bodyHeight: null),
        moduleCount: 6,
      );
      expect(unbounded.visibleRows, 6);
      expect(unbounded.hiddenModules, 0);

      final limited = resolveModuleStatus(
        metrics: const CardMetrics(bodyWidth: 220, bodyHeight: 70),
        moduleCount: 6,
      );
      expect(limited.visibleRows, lessThan(6));
      expect(limited.visibleRows, greaterThanOrEqualTo(1));
      expect(limited.hiddenModules, 6 - limited.visibleRows);
    });

    // 这两条锁一个不变量：**内容行数 + 尾注行数 必须 ≤ 可容纳行数**。
    //
    // 此前账本只在「高度不够」时给尾注留行，被**宽度上限**截断时漏算，
    // 于是尾注被挤出可视区；模块状态在 fit == 1 时强制塞 1 行再叠尾注，
    // 96px 最小高度下必超。两者都被外壳的 SingleChildScrollView 静默吸收，
    // widget 层的 takeException 查不出来，只能在这里扫高度区间。
    test('resolveLedgerSummary 尾注行必须计入可用高度', () {
      for (var h = 40.0; h <= 400.0; h += 4.0) {
        final layout = resolveLedgerSummary(
          metrics: CardMetrics(bodyWidth: 200, bodyHeight: h),
          groupCount: 6,
        );
        final fit = math.max(0, ((h - 36.0) / kSummaryListRowHeight).floor());
        final footerRows =
            (layout.hiddenGroups > 0 && layout.maxGroups > 0) ? 1 : 0;
        expect(
          layout.maxGroups + footerRows,
          lessThanOrEqualTo(fit),
          reason: 'h=$h 时显示 ${layout.maxGroups} 组 + 尾注 $footerRows 行，'
              '超出可容纳的 $fit 行',
        );
      }
    });

    test('resolveModuleStatus 尾注行必须计入可用高度', () {
      for (var h = 0.0; h <= 400.0; h += 4.0) {
        final layout = resolveModuleStatus(
          metrics: CardMetrics(bodyWidth: 200, bodyHeight: h),
          moduleCount: 6,
        );
        final fit = (h / kSummaryListRowHeight).floor();
        final footerRows = layout.showFooter ? 1 : 0;
        expect(
          layout.visibleRows + footerRows,
          lessThanOrEqualTo(fit),
          reason: 'h=$h 时显示 ${layout.visibleRows} 行 + 尾注 $footerRows 行，'
              '超出可容纳的 $fit 行',
        );
      }
    });
  });

  group('F.2 全矩阵 Widget 不溢出验证', () {
    testWidgets('9 种卡片 × 3 种跨度 × 3 种高度均无异常', (tester) async {
      final telemetry = TelemetryDto(
        cpuTotalPercent: 48.5,
        temperatureCelsius: 74.2,
        packagePowerWatts: 68.4,
        sensorDetail: 'AMD Ryzen 9 Package Power (SMU Telemetry Long Sensor Name)',
        memoryTotalBytes: 34359738368,
        memoryAvailableBytes: 17179869184,
        processCount: 286,
        processorCount: 16,
        coreClocks: [
          for (var i = 0; i < 16; i++)
            CoreClockDto(ccd: i ~/ 8, core: i % 8, megahertz: 4200 + i * 25),
        ],
        processorUtilization: [
          for (var i = 0; i < 16; i++) (i * 6.0) % 100,
        ],
      );
      const modules = [
        ModuleDto(id: 'm1', name: '核心监控模块', state: 'ready', enabled: true),
        ModuleDto(id: 'm2', name: '功耗墙控制', state: 'degraded', enabled: true),
        ModuleDto(id: 'm3', name: '温度保护', state: 'ready', enabled: true),
        ModuleDto(id: 'm4', name: '调度账本', state: 'ready', enabled: true),
        ModuleDto(id: 'm5', name: '可选组件', state: 'unavailable', enabled: false),
      ];
      const entries = [
        LedgerEntry(target: 'power.ppt', status: 0),
        LedgerEntry(target: 'cpu.co', status: 0),
        LedgerEntry(target: 'sched.boost', status: 0),
        LedgerEntry(target: 'mem.timing', status: 0),
        LedgerEntry(target: 'fan.curve', status: 0),
        LedgerEntry(target: 'io.prio', status: 0),
      ];
      final registry = defaultCardRegistry();

      for (final kind in DashboardKind.values) {
        for (final rawSpan in const [1, 2, 4]) {
          final span = rawSpan.clamp(kind.minSpan, kind.maxSpan);
          final cardWidth = span == 1
              ? 220.0
              : (span == 2 ? 460.0 : 920.0);
          for (final height in const <double?>[null, 96.0, 400.0]) {
            final item = BoardItem(kind: kind, span: span, height: height);
            await tester.pumpWidget(
              MaterialApp(
                theme: buildZentwikTheme(),
                home: Scaffold(
                  body: DashboardDataProvider(
                    telemetry: telemetry,
                    modules: modules,
                    ledgerEntries: entries,
                    child: SingleChildScrollView(
                      child: SizedBox(
                        width: cardWidth,
                        height: height,
                        child: Builder(
                          builder: (context) => DashboardCardShell(
                            item: item,
                            child: registry.buildCard(context, item),
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            );
            expect(
              tester.takeException(),
              isNull,
              reason: '${kind.wire} span=$span height=$height 发生异常',
            );
          }
        }
      }
    });
  });

  group('F.3 时间线调宽触发 CardMetrics 重算（不依赖 MediaQuery）', () {
    testWidgets('固定窗口尺寸下，时间线从 260 拖到 520 使卡片 bodyWidth 变小',
        (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final workspace = Workspace(store: MemoryWorkspaceStore());
      await workspace.load();

      double? measuredWidth;
      final registry = CardRegistry({
        for (final kind in DashboardKind.values)
          kind.wire: (context, item) {
            // 必须在**卡片自身**的 build context 里读：注册表拿到的 context 在
            // `_CardMetricsScope` 之上，读到的永远是 unknown(0)，这条断言会假绿/假红。
            return Builder(
              builder: (inner) {
                if (kind == DashboardKind.cpuUsage) {
                  measuredWidth = CardMetrics.of(inner).bodyWidth;
                }
                return Text('probe:${kind.wire}');
              },
            );
          },
      });

      await tester.pumpWidget(
        MaterialApp(
          theme: buildZentwikTheme(),
          home: Scaffold(
            body: DashboardPage(
              workspace: workspace,
              registry: registry,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      workspace.setTimelineWidth(260);
      await tester.pumpAndSettle();
      final widthAt260 = measuredWidth!;

      workspace.setTimelineWidth(520);
      await tester.pumpAndSettle();
      final widthAt520 = measuredWidth!;

      expect(widthAt520, lessThan(widthAt260));

      await tester.pump(const Duration(milliseconds: 500));
    });
  });
}
