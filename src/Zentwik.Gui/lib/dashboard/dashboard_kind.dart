enum DashboardKind {
  cpuUsage('cpuUsage', 'CPU 占用', 1, 1, 2),
  cpuTemperature('cpuTemperature', '封装温度', 1, 1, 2),
  packagePower('packagePower', '封装功耗', 1, 1, 2),
  coreClocks('coreClocks', '逐核频率', 2, 2, 4),
  processorUsage('processorUsage', '逐逻辑处理器占用', 2, 2, 4),
  memory('memory', '内存', 1, 1, 2),
  processCount('processCount', '进程数', 1, 1, 2),
  ledgerSummary('ledgerSummary', '账本概览', 2, 2, 4),
  moduleStatus('moduleStatus', '模块状态', 1, 1, 2);

  const DashboardKind(
    this.wire,
    this.label,
    this.defaultSpan,
    this.minSpan,
    this.maxSpan,
  );

  final String wire;
  final String label;
  final int defaultSpan;
  final int minSpan;
  final int maxSpan;

  static DashboardKind? tryParse(String wire) {
    for (final kind in values) {
      if (kind.wire == wire) {
        return kind;
      }
    }
    return null;
  }
}
