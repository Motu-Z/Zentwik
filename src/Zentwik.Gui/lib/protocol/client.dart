import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import '../platform/frame_channel.dart';
import 'envelope.dart';
import 'errors.dart';
import 'models/models.dart';

/// 基于 [FrameChannel] 的协议客户端。
///
/// 负责请求自增 ID 配对、并发请求分发、超时控制、事件广播流与断线清理。
class ProtocolClient {
  ProtocolClient(
    this._channel, {
    this.requestTimeout = const Duration(seconds: 15),
  });

  final FrameChannel _channel;
  final Duration requestTimeout;

  final Map<int, Completer<ResponseEnvelope>> _pending = {};
  final StreamController<TelemetryDto> _telemetryController =
      StreamController<TelemetryDto>.broadcast();

  int _nextId = 1;
  bool _connected = false;
  bool _closed = false;
  Future<void>? _closeFuture;

  /// 遥测事件广播流，无人监听时不报错。
  Stream<TelemetryDto> get telemetry => _telemetryController.stream;

  bool get isConnected => _connected && !_closed;
  bool get isClosed => _closed;

  /// 建立连接并启动唯一的读循环。
  Future<void> connect({
    Duration timeout = const Duration(seconds: 3),
  }) async {
    if (_closed) {
      throw const ProtocolException(ErrorCodes.connectionClosed);
    }
    if (_connected) {
      return;
    }

    await _channel.connect(timeout: timeout);
    _connected = true;
    unawaited(_readLoop());
  }

  /// 发送协议请求并按 `id` 等待对应响应。
  ///
  /// 多个请求可并发在飞，不全局串行化；超时抛 [TimeoutException]，
  /// `ok == false` 或连接已关闭抛 [ProtocolException]。
  Future<ResponseEnvelope> request(
    String op, [
    Map<String, Object?>? payload,
  ]) async {
    if (_closed || !_connected) {
      throw const ProtocolException(ErrorCodes.connectionClosed);
    }

    final id = _nextId++;
    final completer = Completer<ResponseEnvelope>();
    _pending[id] = completer;

    final envelope = RequestEnvelope(id: id, op: op, payload: payload);
    final bytes = Uint8List.fromList(utf8.encode(envelope.encode()));

    try {
      await _channel.writeFrame(bytes);
    } catch (error) {
      _pending.remove(id);
      rethrow;
    }

    final response = await completer.future.timeout(
      requestTimeout,
      onTimeout: () {
        _pending.remove(id);
        throw TimeoutException('请求超时：$op', requestTimeout);
      },
    );

    if (!response.ok) {
      throw ProtocolException(
        response.error ?? ErrorCodes.internal,
        detail: response.detail,
        op: response.op,
      );
    }

    return response;
  }

  /// 发送 `telemetry.unsubscribe` 取消遥测订阅。
  Future<void> unsubscribe() async {
    if (_closed || !_connected) {
      return;
    }
    await request('telemetry.unsubscribe');
  }

  /// 幂等关闭：若仍连接则先尝试取消遥测订阅，再关闭底层通道与事件流。
  Future<void> close() {
    return _closeFuture ??= _closeInternal();
  }

  Future<void> _closeInternal() async {
    if (_connected && !_closed) {
      try {
        await request('telemetry.unsubscribe')
            .timeout(const Duration(seconds: 2));
      } catch (_) {
        // 关闭阶段尽力退订，忽略退订失败或超时。
      }
    }

    _handleDisconnect();
    await _channel.close();
  }

  Future<void> _readLoop() async {
    try {
      while (!_closed) {
        final frame = await _channel.readFrame();
        if (frame == null) {
          break;
        }
        _dispatchFrame(frame);
      }
    } catch (_) {
      // 读通道异常按对端断线处理，在 finally 中统一失败挂起请求。
    } finally {
      _handleDisconnect();
    }
  }

  void _dispatchFrame(Uint8List frame) {
    final ResponseEnvelope envelope;
    try {
      envelope = ResponseEnvelope.decode(utf8.decode(frame));
    } catch (_) {
      return;
    }

    if (envelope.isEvent) {
      if (envelope.op == 'telemetry.sample' &&
          envelope.data is Map<String, Object?>) {
        if (!_telemetryController.isClosed) {
          _telemetryController.add(
            TelemetryDto.fromJson(envelope.data! as Map<String, Object?>),
          );
        }
      }
      return;
    }

    final completer = _pending.remove(envelope.id);
    if (completer != null && !completer.isCompleted) {
      completer.complete(envelope);
    }
  }

  void _handleDisconnect() {
    if (_closed) {
      return;
    }
    _closed = true;
    _connected = false;

    if (_pending.isNotEmpty) {
      final pending = List<Completer<ResponseEnvelope>>.of(_pending.values);
      _pending.clear();
      for (final completer in pending) {
        if (!completer.isCompleted) {
          completer.completeError(
            const ProtocolException(ErrorCodes.connectionClosed),
          );
        }
      }
    }

    if (!_telemetryController.isClosed) {
      unawaited(_telemetryController.close());
    }
  }

  // --- 常用类型化便捷方法 ---

  Future<void> ping() => request('agent.ping');

  Future<AgentStatusDto> getStatus() async {
    final response = await request('agent.status');
    final data = response.data;
    if (data is! Map<String, Object?>) {
      throw const ProtocolException(
        ErrorCodes.protocolMalformed,
        op: 'agent.status',
      );
    }
    return AgentStatusDto.fromJson(data);
  }

  Future<List<CapabilityDto>> listCapabilities() async {
    final response = await request('capabilities.list');
    final data = response.data;
    if (data is! List) {
      return const [];
    }
    return data
        .whereType<Map<String, Object?>>()
        .map(CapabilityDto.fromJson)
        .toList(growable: false);
  }

  Future<List<ModuleDto>> listModules() async {
    final response = await request('modules.list');
    final data = response.data;
    if (data is! List) {
      return const [];
    }
    return data
        .whereType<Map<String, Object?>>()
        .map(ModuleDto.fromJson)
        .toList(growable: false);
  }

  Future<List<ModuleDto>> enableModule(String id) async {
    final response = await request('modules.enable', {'id': id});
    final data = response.data;
    if (data is! List) {
      return const [];
    }
    return data
        .whereType<Map<String, Object?>>()
        .map(ModuleDto.fromJson)
        .toList(growable: false);
  }

  Future<List<ModuleDto>> disableModule(String id) async {
    final response = await request('modules.disable', {'id': id});
    final data = response.data;
    if (data is! List) {
      return const [];
    }
    return data
        .whereType<Map<String, Object?>>()
        .map(ModuleDto.fromJson)
        .toList(growable: false);
  }

  Future<SubscriptionAck> subscribeTelemetry({
    required int intervalMs,
    int? sensorIntervalMs,
  }) async {
    final payload = <String, Object?>{'intervalMs': intervalMs};
    if (sensorIntervalMs != null) {
      payload['sensorIntervalMs'] = sensorIntervalMs;
    }
    final response = await request('telemetry.subscribe', payload);
    final data = response.data;
    if (data is! Map<String, Object?>) {
      return const SubscriptionAck();
    }
    return SubscriptionAck.fromJson(data);
  }

  Future<HistoryDto> getTelemetryHistory({int limit = 60}) async {
    final response = await request('telemetry.history', {'limit': limit});
    final data = response.data;
    if (data is! Map<String, Object?>) {
      return const HistoryDto();
    }
    return HistoryDto.fromJson(data);
  }

  Future<SettingsDto> getSettings() async {
    final response = await request('settings.get');
    final data = response.data;
    if (data is! Map<String, Object?>) {
      return const SettingsDto();
    }
    return SettingsDto.fromJson(data);
  }

  Future<SettingsDto> setSettings(SettingsPatch patch) async {
    final response = await request('settings.set', patch.toJson());
    final data = response.data;
    if (data is! Map<String, Object?>) {
      return const SettingsDto();
    }
    return SettingsDto.fromJson(data);
  }

  Future<List<LedgerEntry>> listLedger() async {
    final response = await request('ledger.list');
    final data = response.data;
    if (data is! List) {
      return const [];
    }
    return data
        .whereType<Map<String, Object?>>()
        .map(LedgerEntry.fromJson)
        .toList(growable: false);
  }
}
