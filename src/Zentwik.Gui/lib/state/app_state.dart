import 'dart:async';

import 'package:flutter/foundation.dart';

import '../protocol/protocol.dart';

/// 遥测事件本地缓存上限（保留最近 60 帧）。
const int kAppStateTelemetryCapacity = 60;

/// 聚合服务状态、能力列表、模块列表与遥测流缓存，仅持有 [ProtocolClient]。
class AppState extends ChangeNotifier {
  AppState(this._client) {
    _telemetrySub = _client.telemetry.listen(_onTelemetry);
  }

  final ProtocolClient _client;
  final List<TelemetryDto> _telemetryHistory = [];

  StreamSubscription<TelemetryDto>? _telemetrySub;
  Future<void>? _refreshFuture;
  bool _disposed = false;

  AgentStatusDto? _status;
  List<CapabilityDto> _capabilities = const [];
  List<ModuleDto> _modules = const [];
  ProtocolException? _lastError;
  String? _lastErrorMessage;

  AgentStatusDto? get status => _status;
  List<CapabilityDto> get capabilities => _capabilities;
  List<ModuleDto> get modules => _modules;
  TelemetryDto? get telemetry =>
      _telemetryHistory.isEmpty ? null : _telemetryHistory.last;
  List<TelemetryDto> get telemetryHistory =>
      List.unmodifiable(_telemetryHistory);
  ProtocolException? get lastError => _lastError;

  /// 非协议层错误（超时、客户端已关闭等）的文本描述，与 [lastError] 互补。
  String? get lastErrorMessage => _lastErrorMessage;

  /// 并发刷新 `agent.status`、`capabilities.list`、`modules.list`。
  ///
  /// 单个请求失败不影响其余项更新，失败项保留旧缓存并记录 `lastError`；
  /// 并发多次调用去重，同一时刻只跑一轮。
  Future<void> refresh() {
    return _refreshFuture ??= _refreshInternal();
  }

  Future<void> _refreshInternal() async {
    ProtocolException? roundError;
    String? roundErrorMessage;

    void capture(Object error) {
      if (error is ProtocolException) {
        roundError = error;
      } else {
        roundErrorMessage = error.toString();
      }
    }

    try {
      await Future.wait([
        _client.getStatus().then((value) {
          _status = value;
        }).catchError(capture),
        _client.listCapabilities().then((value) {
          _capabilities = value;
        }).catchError(capture),
        _client.listModules().then((value) {
          _modules = value;
        }).catchError(capture),
      ]);

      _lastError = roundError;
      _lastErrorMessage = roundErrorMessage;
      _notify();
    } finally {
      _refreshFuture = null;
    }
  }

  void _onTelemetry(TelemetryDto sample) {
    if (_disposed) {
      return;
    }
    _telemetryHistory.add(sample);
    if (_telemetryHistory.length > kAppStateTelemetryCapacity) {
      _telemetryHistory.removeRange(
        0,
        _telemetryHistory.length - kAppStateTelemetryCapacity,
      );
    }
    _notify();
  }

  void _notify() {
    if (_disposed) {
      return;
    }
    notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _telemetrySub?.cancel();
    _telemetrySub = null;
    super.dispose();
  }
}
