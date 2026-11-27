import 'dart:async';

import 'package:flutter/foundation.dart';

import '../protocol/protocol.dart';

/// 服务端环形缓冲最大帧数（契约 §6 / §7）。
const int kMaxTelemetryHistorySamples = 3600;

/// 管理遥测订阅、生效间隔与历史环形缓存。
class TelemetryState extends ChangeNotifier {
  TelemetryState(this._client);

  final ProtocolClient _client;
  final List<TelemetryDto> _samples = [];

  StreamSubscription<TelemetryDto>? _subscription;
  bool _subscribed = false;
  int? _effectiveIntervalMs;
  int? _effectiveSensorIntervalMs;
  String? _errorMessage;

  bool get isSubscribed => _subscribed;
  int? get effectiveIntervalMs => _effectiveIntervalMs;
  int? get effectiveSensorIntervalMs => _effectiveSensorIntervalMs;
  String? get errorMessage => _errorMessage;

  /// 根据 `SubscriptionAck` 返回的生效间隔计算超时阈值（契约 §6）。
  Duration get expectedSampleTimeout {
    final ms = _effectiveIntervalMs ?? 1000;
    return Duration(milliseconds: ms * 3);
  }

  List<TelemetryDto> get samples => List.unmodifiable(_samples);
  TelemetryDto? get latest => _samples.isEmpty ? null : _samples.last;

  Future<void> startSubscription({
    int intervalMs = 1000,
    int? sensorIntervalMs,
  }) async {
    try {
      final ack = await _client.subscribeTelemetry(
        intervalMs: intervalMs,
        sensorIntervalMs: sensorIntervalMs,
      );
      _effectiveIntervalMs = ack.intervalMs ?? intervalMs;
      _effectiveSensorIntervalMs = ack.sensorIntervalMs ?? sensorIntervalMs;
      _subscribed = true;
      _errorMessage = null;

      await _subscription?.cancel();
      _subscription = _client.telemetry.listen(
        _onSample,
        onDone: () {
          _subscribed = false;
          notifyListeners();
        },
      );
      notifyListeners();
    } on ProtocolException catch (error) {
      _errorMessage = error.userMessage;
      notifyListeners();
    }
  }

  Future<void> stopSubscription() async {
    await _subscription?.cancel();
    _subscription = null;
    _subscribed = false;
    try {
      await _client.unsubscribe();
    } catch (_) {
      // 忽略断线后的退订错误。
    }
    notifyListeners();
  }

  Future<void> loadHistory({int limit = 60}) async {
    final bounded = limit.clamp(1, kMaxTelemetryHistorySamples);
    try {
      final history = await _client.getTelemetryHistory(limit: bounded);
      _samples
        ..clear()
        ..addAll(history.samples);
      _trimToCapacity();
      _errorMessage = null;
      notifyListeners();
    } on ProtocolException catch (error) {
      _errorMessage = error.userMessage;
      notifyListeners();
    }
  }

  void _onSample(TelemetryDto sample) {
    _samples.add(sample);
    _trimToCapacity();
    notifyListeners();
  }

  void _trimToCapacity() {
    final excess = _samples.length - kMaxTelemetryHistorySamples;
    if (excess > 0) {
      _samples.removeRange(0, excess);
    }
  }

  @override
  void dispose() {
    _subscription?.cancel();
    super.dispose();
  }
}
