import 'package:material_ui/material_ui.dart';

/// 首页主工作区可放置的组件类型。
///
/// 每一项只描述「这是什么、能多大」，不描述它长什么样 —— 内容由
/// `CardRegistry` 按 [wire] 映射，尺寸由 masonry 按 [minSpan]/[maxSpan] 收敛。
///
/// 时间线**不在这里**：它不是普通组件，由首页右侧独立区域承载。
enum DashboardKind {
  cpuUsage(
    wire: 'cpuUsage',
    label: 'CPU 占用',
    description: '总占用与逐核均值',
    icon: Icons.speed_outlined,
    defaultSpan: 1,
    minSpan: 1,
    maxSpan: 4,
  ),
  cpuTemperature(
    wire: 'cpuTemperature',
    label: '封装温度',
    description: 'Tctl/Tdie 与阈值距离',
    icon: Icons.thermostat_outlined,
    defaultSpan: 1,
    minSpan: 1,
    maxSpan: 2,
  ),
  packagePower(
    wire: 'packagePower',
    label: '封装功耗',
    description: '实际功耗与 PPT 占比',
    icon: Icons.bolt_outlined,
    defaultSpan: 1,
    minSpan: 1,
    maxSpan: 2,
  ),
  coreClocks(
    wire: 'coreClocks',
    label: '逐核频率',
    description: '每个物理核的当前频率',
    icon: Icons.memory_outlined,
    defaultSpan: 2,
    minSpan: 2,
    maxSpan: 4,
  ),
  processorUsage(
    wire: 'processorUsage',
    label: '逻辑处理器',
    description: '逐逻辑处理器占用',
    icon: Icons.grid_view_outlined,
    defaultSpan: 2,
    minSpan: 2,
    maxSpan: 4,
  ),
  memory(
    wire: 'memory',
    label: '内存',
    description: '已用容量与占用率',
    icon: Icons.storage_outlined,
    defaultSpan: 1,
    minSpan: 1,
    maxSpan: 2,
  ),
  processCount(
    wire: 'processCount',
    label: '进程数',
    description: '进程与线程计数',
    icon: Icons.list_alt_outlined,
    defaultSpan: 1,
    minSpan: 1,
    maxSpan: 2,
  ),
  ledgerSummary(
    wire: 'ledgerSummary',
    label: '调度账本',
    description: '最近被改写的调度目标',
    icon: Icons.receipt_long_outlined,
    defaultSpan: 2,
    minSpan: 2,
    maxSpan: 4,
  ),
  moduleStatus(
    wire: 'moduleStatus',
    label: '模块状态',
    description: '监控、调度等模块运行态',
    icon: Icons.extension_outlined,
    defaultSpan: 1,
    minSpan: 1,
    maxSpan: 2,
  );

  const DashboardKind({
    required this.wire,
    required this.label,
    required this.description,
    required this.icon,
    required this.defaultSpan,
    required this.minSpan,
    required this.maxSpan,
  });

  /// 持久化标识；改名会破坏用户已保存的布局。
  final String wire;
  final String label;
  final String description;
  final IconData icon;

  /// 首次添加时的列跨度。
  final int defaultSpan;
  final int minSpan;
  final int maxSpan;

  /// 跨列上限要收敛到当前列数：两列窗口里不存在 4 跨组件。
  /// 返回值只会缩小 [span] 的可用上限，不会把下限抬到超过列数。
  int spanLimit(int columns) {
    final limit = maxSpan < columns ? maxSpan : columns;
    return limit < minSpan ? minSpan : limit;
  }

  static DashboardKind? tryParse(String wire) {
    for (final kind in values) {
      if (kind.wire == wire) {
        return kind;
      }
    }
    return null;
  }
}
