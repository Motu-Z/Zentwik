/// 手写 JSON 解析辅助函数，保持缺失字段为 `null`，不把缺失静默转为 0 或空串。
int? jsonInt(Object? value) => value is num ? value.toInt() : null;

double? jsonDouble(Object? value) => value is num ? value.toDouble() : null;

bool? jsonBool(Object? value) => value is bool ? value : null;

String? jsonString(Object? value) => value is String ? value : null;

/// 解析带偏移的时间戳；保留 `DateTime.parse` 原始语义，不调用 `toUtc()` 覆盖。
DateTime? jsonDateTime(Object? value) {
  if (value is! String) return null;
  return DateTime.tryParse(value);
}

List<String>? jsonStringList(Object? value) {
  if (value is! List) return null;
  return value.whereType<String>().toList(growable: false);
}

List<double>? jsonDoubleList(Object? value) {
  if (value is! List) return null;
  return value
      .whereType<num>()
      .map((item) => item.toDouble())
      .toList(growable: false);
}
