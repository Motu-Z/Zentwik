/// 数据缺失占位符（后端约定：读不到就是 `null`，不是 0）。
const String kMissingValue = '—';

String formatPercent(double? value) {
  if (value == null || !value.isFinite || value < 0) {
    return kMissingValue;
  }
  return '${value.toStringAsFixed(1)}%';
}

String formatCelsius(double? value) {
  if (value == null || !value.isFinite) {
    return kMissingValue;
  }
  return '${value.toStringAsFixed(1)} °C';
}

String formatWatts(double? value) {
  if (value == null || !value.isFinite || value < 0) {
    return kMissingValue;
  }
  return '${value.toStringAsFixed(1)} W';
}

String formatGigahertz(double? megahertz) {
  if (megahertz == null || !megahertz.isFinite || megahertz < 0) {
    return kMissingValue;
  }
  return '${(megahertz / 1000.0).toStringAsFixed(2)} GHz';
}

String formatBytes(int? bytes) {
  if (bytes == null || bytes < 0) {
    return kMissingValue;
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  var value = bytes.toDouble();
  var unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }
  if (unitIndex == 0) {
    return '$bytes B';
  }
  return '${value.toStringAsFixed(1)} ${units[unitIndex]}';
}

String formatCount(int? value) {
  if (value == null || value < 0) {
    return kMissingValue;
  }
  return '$value';
}

/// 把秒数折成「X 天 X 小时 X 分」，前导 0 段省略；不足 1 分钟按秒显示。
///
/// 代理是常驻服务，`uptimeSeconds` 动辄几十万，直接印秒数没人读得懂。
String formatUptime(int? seconds) {
  if (seconds == null || seconds < 0) {
    return kMissingValue;
  }
  if (seconds < 60) {
    return '$seconds 秒';
  }

  final days = seconds ~/ 86400;
  final hours = (seconds % 86400) ~/ 3600;
  final minutes = (seconds % 3600) ~/ 60;

  final parts = <String>[
    if (days > 0) '$days 天',
    if (days > 0 || hours > 0) '$hours 小时',
    if (days == 0) '$minutes 分',
  ];
  return parts.join(' ');
}
