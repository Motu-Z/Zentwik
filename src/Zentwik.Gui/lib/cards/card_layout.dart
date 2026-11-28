import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';

import '../dashboard/card_metrics.dart';
import '../theme/theme.dart';

/// 逐核频率单行估算高度（文字 16px + 行间距 4px）。
const int kCoreClockRowHeight = 20;

/// 账本分组与模块状态单行估算高度。
const double kSummaryListRowHeight = 22.0;

// ── D.1 CPU 占用 ──────────────────────────────────────────────

class CpuUsageLayout {
  const CpuUsageLayout({
    required this.inlineBar,
    required this.showStats,
    required this.displayFont,
    required this.gap,
  });

  final bool inlineBar;
  final bool showStats;
  final bool displayFont;
  final double gap;
}

CpuUsageLayout resolveCpuUsage({
  required CardMetrics metrics,
  required bool hasProcessorStats,
}) {
  final w = metrics.widthClass;
  final h = metrics.heightClass;
  final isTight = h == CardHeightClass.tight;
  final isTall = h == CardHeightClass.tall;
  return CpuUsageLayout(
    inlineBar: w != CardWidthClass.compact,
    showStats: !isTight && w == CardWidthClass.wide && hasProcessorStats,
    displayFont: isTall,
    gap: isTight ? ZentwikTokens.spaceXs : ZentwikTokens.spaceSm,
  );
}

// ── D.2 封装温度 ──────────────────────────────────────────────

({Color color, String label}) describeTemperature(
  ZentwikPalette palette,
  double? celsius,
) {
  if (celsius == null || !celsius.isFinite) {
    return (color: palette.textSecondary, label: '');
  }
  if (celsius < 70) {
    return (color: palette.success, label: '正常');
  }
  if (celsius < 85) {
    return (color: palette.accent, label: '偏高');
  }
  return (color: palette.danger, label: '过热');
}

class CpuTemperatureLayout {
  const CpuTemperatureLayout({
    required this.showLabel,
    required this.displayFont,
  });

  final bool showLabel;
  final bool displayFont;
}

CpuTemperatureLayout resolveCpuTemperature({required CardMetrics metrics}) {
  final w = metrics.widthClass;
  final h = metrics.heightClass;
  return CpuTemperatureLayout(
    showLabel: w != CardWidthClass.compact && h != CardHeightClass.tight,
    displayFont: h == CardHeightClass.tall,
  );
}

// ── D.3 封装功耗 ──────────────────────────────────────────────

class PackagePowerLayout {
  const PackagePowerLayout({
    required this.showSubtitle,
    required this.displayFont,
    required this.gap,
  });

  final bool showSubtitle;
  final bool displayFont;
  final double gap;
}

PackagePowerLayout resolvePackagePower({required CardMetrics metrics}) {
  final h = metrics.heightClass;
  final isTight = h == CardHeightClass.tight;
  return PackagePowerLayout(
    showSubtitle: !isTight,
    displayFont: h == CardHeightClass.tall,
    gap: isTight ? 0 : ZentwikTokens.spaceXs,
  );
}

// ── D.4 内存 ──────────────────────────────────────────────────

enum MemoryLayoutMode { compactStacked, splitRow, tightMinimal, tallDisplay }

class MemoryLayout {
  const MemoryLayout({
    required this.mode,
    required this.gap,
  });

  final MemoryLayoutMode mode;
  final double gap;
}

MemoryLayout resolveMemory({required CardMetrics metrics}) {
  final w = metrics.widthClass;
  final h = metrics.heightClass;
  if (h == CardHeightClass.tight) {
    return const MemoryLayout(
      mode: MemoryLayoutMode.tightMinimal,
      gap: ZentwikTokens.spaceXs,
    );
  }
  if (h == CardHeightClass.tall) {
    return const MemoryLayout(
      mode: MemoryLayoutMode.tallDisplay,
      gap: ZentwikTokens.spaceSm,
    );
  }
  if (w == CardWidthClass.compact) {
    return const MemoryLayout(
      mode: MemoryLayoutMode.compactStacked,
      gap: ZentwikTokens.spaceXs,
    );
  }
  return const MemoryLayout(
    mode: MemoryLayoutMode.splitRow,
    gap: ZentwikTokens.spaceSm,
  );
}

// ── D.5 进程数 ────────────────────────────────────────────────

class ProcessCountLayout {
  const ProcessCountLayout({
    required this.showSubtitle,
    required this.showProcessorCount,
    required this.displayFont,
  });

  final bool showSubtitle;
  final bool showProcessorCount;
  final bool displayFont;
}

ProcessCountLayout resolveProcessCount({
  required CardMetrics metrics,
  required bool hasProcessorCount,
}) {
  final w = metrics.widthClass;
  final h = metrics.heightClass;
  final isTight = h == CardHeightClass.tight;
  return ProcessCountLayout(
    showSubtitle: !isTight,
    showProcessorCount:
        !isTight && w != CardWidthClass.compact && hasProcessorCount,
    displayFont: h == CardHeightClass.tall,
  );
}

// ── D.6 逐核频率 ──────────────────────────────────────────────

class CoreClocksLayout {
  const CoreClocksLayout({
    required this.columns,
    required this.dense,
    required this.rowHeight,
    required this.visibleRows,
    required this.hiddenCores,
  });

  final int columns;
  final bool dense;
  final int rowHeight;
  final int visibleRows;
  final int hiddenCores;
}

CoreClocksLayout resolveCoreClocks({
  required CardMetrics metrics,
  required int coreCount,
  bool hasDistinctCcds = false,
}) {
  if (coreCount <= 0) {
    return const CoreClocksLayout(
      columns: 1,
      dense: false,
      rowHeight: kCoreClockRowHeight,
      visibleRows: 0,
      hiddenCores: 0,
    );
  }

  final bodyWidth = metrics.bodyWidth;
  final widthCap = bodyWidth < 300
      ? 1
      : (bodyWidth < 560 ? 2 : 4);

  final bodyHeight = metrics.bodyHeight;
  int columns;
  int visibleRows;
  int hiddenCores;

  if (bodyHeight == null) {
    columns = widthCap;
    visibleRows = (coreCount / columns).ceil().clamp(1, coreCount);
    hiddenCores = 0;
  } else {
    columns = 1;
    var rowsNeeded = (coreCount / columns).ceil();
    if (rowsNeeded * kCoreClockRowHeight > bodyHeight) {
      for (final candidate in const [2, 4]) {
        if (candidate > widthCap) {
          break;
        }
        columns = candidate;
        rowsNeeded = (coreCount / columns).ceil();
        if (rowsNeeded * kCoreClockRowHeight <= bodyHeight) {
          break;
        }
      }
    }

    if (rowsNeeded * kCoreClockRowHeight <= bodyHeight) {
      visibleRows = rowsNeeded;
      hiddenCores = 0;
    } else {
      final usableHeight = math.max(0.0, bodyHeight - kCoreClockRowHeight);
      visibleRows = (usableHeight ~/ kCoreClockRowHeight).clamp(1, rowsNeeded);
      final shown = visibleRows * columns;
      hiddenCores = (coreCount - shown).clamp(0, coreCount);
    }
  }

  const colGap = ZentwikTokens.spaceMd;
  final colWidth = bodyWidth <= 0
      ? double.infinity
      : (bodyWidth - (columns - 1) * colGap) / columns;
  final dense = !hasDistinctCcds && colWidth < 180;

  return CoreClocksLayout(
    columns: columns,
    dense: dense,
    rowHeight: kCoreClockRowHeight,
    visibleRows: visibleRows,
    hiddenCores: hiddenCores,
  );
}

// ── D.7 逻辑处理器 ────────────────────────────────────────────

class ProcessorUsageLayout {
  const ProcessorUsageLayout({
    required this.barWidth,
    required this.barHeight,
    required this.visibleCount,
    required this.hiddenCount,
  });

  final double barWidth;
  final double barHeight;
  final int visibleCount;
  final int hiddenCount;
}

ProcessorUsageLayout resolveProcessorUsage({
  required CardMetrics metrics,
  required int count,
}) {
  if (count <= 0) {
    return const ProcessorUsageLayout(
      barWidth: 8.0,
      barHeight: 28.0,
      visibleCount: 0,
      hiddenCount: 0,
    );
  }

  const gap = ZentwikTokens.spaceXs;
  final bodyWidth = metrics.bodyWidth;
  final bodyHeight = metrics.bodyHeight;

  if (bodyWidth <= 0) {
    return ProcessorUsageLayout(
      barWidth: 8.0,
      barHeight: bodyHeight == null
          ? 28.0
          : bodyHeight.clamp(20.0, 120.0).floorToDouble(),
      visibleCount: count,
      hiddenCount: 0,
    );
  }

  var barWidth = ((bodyWidth - (count - 1) * gap) / count)
      .floorToDouble()
      .clamp(4.0, 14.0);
  var perRow = ((bodyWidth + gap) / (barWidth + gap)).floor().clamp(1, count);
  var rows = (count / perRow).ceil();

  if (bodyHeight == null) {
    return ProcessorUsageLayout(
      barWidth: barWidth,
      barHeight: 28.0,
      visibleCount: count,
      hiddenCount: 0,
    );
  }

  final availForBars = bodyHeight - (rows - 1) * gap;
  if (availForBars / rows >= 20.0) {
    final barHeight = (availForBars / rows).clamp(20.0, 120.0).floorToDouble();
    return ProcessorUsageLayout(
      barWidth: barWidth,
      barHeight: barHeight,
      visibleCount: count,
      hiddenCount: 0,
    );
  }

  // 行数过多时先将条宽收窄到下限 4px。
  barWidth = 4.0;
  perRow = ((bodyWidth + gap) / (barWidth + gap)).floor().clamp(1, count);
  rows = (count / perRow).ceil();
  final shrunkAvail = bodyHeight - (rows - 1) * gap;
  if (shrunkAvail / rows >= 20.0) {
    final barHeight = (shrunkAvail / rows).clamp(20.0, 120.0).floorToDouble();
    return ProcessorUsageLayout(
      barWidth: barWidth,
      barHeight: barHeight,
      visibleCount: count,
      hiddenCount: 0,
    );
  }

  // 仍放不下：减少显示条数并在末尾显示 +N。
  const footerReserve = 20.0;
  final usableHeight = math.max(20.0, bodyHeight - footerReserve);
  final maxRows =
      ((usableHeight + gap) / (20.0 + gap)).floor().clamp(1, rows);
  final visibleCount = (maxRows * perRow).clamp(1, count);
  final hiddenCount = (count - visibleCount).clamp(0, count);
  final actualRows = (visibleCount / perRow).ceil();
  final barHeight = ((usableHeight - (actualRows - 1) * gap) / actualRows)
      .clamp(20.0, 120.0)
      .floorToDouble();

  return ProcessorUsageLayout(
    barWidth: barWidth,
    barHeight: barHeight,
    visibleCount: visibleCount,
    hiddenCount: hiddenCount,
  );
}

// ── D.8 调度账本 ──────────────────────────────────────────────

class LedgerSummaryLayout {
  const LedgerSummaryLayout({
    required this.splitColumns,
    required this.maxGroups,
    required this.hiddenGroups,
  });

  final bool splitColumns;
  final int maxGroups;
  final int hiddenGroups;
}

LedgerSummaryLayout resolveLedgerSummary({
  required CardMetrics metrics,
  required int groupCount,
}) {
  if (groupCount <= 0) {
    return LedgerSummaryLayout(
      splitColumns: metrics.widthClass == CardWidthClass.wide,
      maxGroups: 0,
      hiddenGroups: 0,
    );
  }

  final w = metrics.widthClass;
  final h = metrics.heightClass;
  final splitColumns = w == CardWidthClass.wide;

  if (h == CardHeightClass.tight) {
    return LedgerSummaryLayout(
      splitColumns: splitColumns,
      maxGroups: 0,
      hiddenGroups: groupCount,
    );
  }

  final widthCap = switch (w) {
    CardWidthClass.compact => 3,
    CardWidthClass.regular => 5,
    CardWidthClass.wide => groupCount,
  };

  final bodyHeight = metrics.bodyHeight;
  int maxGroups;
  if (bodyHeight == null) {
    maxGroups = math.min(widthCap, groupCount);
  } else {
    final headerCost = splitColumns ? 0.0 : 36.0;
    final fit = math.max(0, ((bodyHeight - headerCost) / kSummaryListRowHeight).floor());
    final target = math.min(widthCap, groupCount);
    // 被**宽度上限**截断时同样会追加「还有 N 类」尾注，尾注也是一行。
    // 只看 fit >= target 会漏算这一行，于是尾注被挤出可视区（被外壳的
    // 滚动兜住，不会报 overflow，所以 widget 测试查不出来）。按总行数预算。
    final requiredRows = target + (target < groupCount ? 1 : 0);
    if (requiredRows <= fit) {
      maxGroups = target;
    } else {
      // 放不下就整行让给尾注，宁少显示一组也不让尾注越界。
      maxGroups = math.max(0, fit - 1);
    }
  }

  return LedgerSummaryLayout(
    splitColumns: splitColumns,
    maxGroups: maxGroups,
    hiddenGroups: (groupCount - maxGroups).clamp(0, groupCount),
  );
}

// ── D.9 模块状态 ──────────────────────────────────────────────

class ModuleStatusLayout {
  const ModuleStatusLayout({
    required this.visibleRows,
    required this.hiddenModules,
    required this.showFooter,
  });

  final int visibleRows;
  final int hiddenModules;

  /// 尾注「还有 N 个模块」是否真的画得下。
  ///
  /// 高度只够一行时，一条真实模块比一行「还有 N 个」更有用，尾注让位。
  /// 不能让卡片自己用 `hiddenModules > 0` 判断——那会把尾注挤出可视区。
  final bool showFooter;
}

ModuleStatusLayout resolveModuleStatus({
  required CardMetrics metrics,
  required int moduleCount,
}) {
  if (moduleCount <= 0) {
    return const ModuleStatusLayout(
      visibleRows: 0,
      hiddenModules: 0,
      showFooter: false,
    );
  }

  final bodyHeight = metrics.bodyHeight;
  if (bodyHeight == null) {
    return ModuleStatusLayout(
      visibleRows: moduleCount,
      hiddenModules: 0,
      showFooter: false,
    );
  }

  final fit = (bodyHeight / kSummaryListRowHeight).floor();
  if (fit >= moduleCount) {
    return ModuleStatusLayout(
      visibleRows: moduleCount,
      hiddenModules: 0,
      showFooter: false,
    );
  }

  final canAffordFooter = fit >= 2;
  final visibleRows = canAffordFooter
      ? math.min(fit - 1, moduleCount)
      : fit.clamp(0, moduleCount);
  return ModuleStatusLayout(
    visibleRows: visibleRows,
    hiddenModules: moduleCount - visibleRows,
    showFooter: canAffordFooter,
  );
}
