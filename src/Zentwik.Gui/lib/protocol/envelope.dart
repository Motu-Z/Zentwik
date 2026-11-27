import 'dart:convert';

/// 协议版本号，与服务端 `ProtocolInfo.Version` 一致。
const int kProtocolVersion = 2;

/// 客户端发往服务端的请求信封。
class RequestEnvelope {
  const RequestEnvelope({
    this.v = kProtocolVersion,
    required this.id,
    required this.op,
    this.payload,
  });

  final int v;
  final int id;
  final String op;
  final Map<String, Object?>? payload;

  /// 序列化为 JSON Map。`payload` 为 `null` 时省略该键，不写入 `"payload": null`。
  Map<String, Object?> toJson() {
    final map = <String, Object?>{
      'v': v,
      'id': id,
      'op': op,
    };
    if (payload != null) {
      map['payload'] = payload;
    }
    return map;
  }

  String encode() => jsonEncode(toJson());
}

/// 服务端返回的响应或事件信封。
///
/// 服务端会附带计算属性 `isEvent`，客户端解码时忽略该字段，
/// 统一以 `id == 0` 判定事件帧。
class ResponseEnvelope {
  const ResponseEnvelope({
    required this.v,
    required this.id,
    required this.op,
    required this.ok,
    this.error,
    this.detail,
    this.data,
  });

  factory ResponseEnvelope.fromJson(Map<String, Object?> json) {
    return ResponseEnvelope(
      v: (json['v'] as num?)?.toInt() ?? 0,
      id: (json['id'] as num?)?.toInt() ?? 0,
      op: json['op'] as String? ?? '',
      ok: json['ok'] as bool? ?? false,
      error: json['error'] as String?,
      detail: json['detail'] as String?,
      data: json['data'],
    );
  }

  factory ResponseEnvelope.decode(String source) {
    final decoded = jsonDecode(source);
    if (decoded is! Map<String, Object?>) {
      throw const FormatException('响应信封必须是 JSON 对象');
    }
    return ResponseEnvelope.fromJson(decoded);
  }

  final int v;
  final int id;
  final String op;
  final bool ok;
  final String? error;
  final String? detail;
  final Object? data;

  /// 事件帧判定：`id == 0`。
  bool get isEvent => id == 0;
}
