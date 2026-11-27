import 'json_helpers.dart';

class CapabilityDto {
  const CapabilityDto({
    this.id,
    this.state,
    this.detail,
    this.requires = const [],
  });

  factory CapabilityDto.fromJson(Map<String, Object?> json) {
    return CapabilityDto(
      id: jsonString(json['id']),
      state: jsonString(json['state']),
      detail: jsonString(json['detail']),
      requires: jsonStringList(json['requires']) ?? const [],
    );
  }

  final String? id;
  final String? state;
  final String? detail;
  final List<String> requires;
}
