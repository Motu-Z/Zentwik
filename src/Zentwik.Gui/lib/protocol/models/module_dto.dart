import 'json_helpers.dart';

class ModuleDto {
  const ModuleDto({
    this.id,
    this.name,
    this.state,
    this.detail,
    this.capabilities = const [],
    this.optional,
    this.enabled,
  });

  factory ModuleDto.fromJson(Map<String, Object?> json) {
    return ModuleDto(
      id: jsonString(json['id']),
      name: jsonString(json['name']),
      state: jsonString(json['state']),
      detail: jsonString(json['detail']),
      capabilities: jsonStringList(json['capabilities']) ?? const [],
      optional: jsonBool(json['optional']),
      enabled: jsonBool(json['enabled']),
    );
  }

  final String? id;
  final String? name;
  final String? state;
  final String? detail;
  final List<String> capabilities;
  final bool? optional;
  final bool? enabled;
}
