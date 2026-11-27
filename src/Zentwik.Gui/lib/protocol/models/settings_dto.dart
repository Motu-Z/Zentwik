import 'json_helpers.dart';

class SettingsDto {
  const SettingsDto({
    this.language,
    this.theme,
    this.accent,
    this.uiScale,
    this.monitorIntervalMs,
    this.sensorIntervalMs,
  });

  factory SettingsDto.fromJson(Map<String, Object?> json) {
    return SettingsDto(
      language: jsonString(json['language']),
      theme: jsonString(json['theme']),
      accent: jsonString(json['accent']),
      uiScale: jsonDouble(json['uiScale']),
      monitorIntervalMs: jsonInt(json['monitorIntervalMs']),
      sensorIntervalMs: jsonInt(json['sensorIntervalMs']),
    );
  }

  final String? language;
  final String? theme;
  final String? accent;
  final double? uiScale;
  final int? monitorIntervalMs;
  final int? sensorIntervalMs;
}

/// `settings.set` 部分更新载荷，只序列化非 `null` 字段。
class SettingsPatch {
  const SettingsPatch({
    this.language,
    this.theme,
    this.accent,
    this.uiScale,
    this.monitorIntervalMs,
    this.sensorIntervalMs,
  });

  final String? language;
  final String? theme;
  final String? accent;
  final double? uiScale;
  final int? monitorIntervalMs;
  final int? sensorIntervalMs;

  Map<String, Object?> toJson() {
    final map = <String, Object?>{};
    if (language != null) map['language'] = language;
    if (theme != null) map['theme'] = theme;
    if (accent != null) map['accent'] = accent;
    if (uiScale != null) map['uiScale'] = uiScale;
    if (monitorIntervalMs != null) {
      map['monitorIntervalMs'] = monitorIntervalMs;
    }
    if (sensorIntervalMs != null) {
      map['sensorIntervalMs'] = sensorIntervalMs;
    }
    return map;
  }
}
