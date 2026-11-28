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
