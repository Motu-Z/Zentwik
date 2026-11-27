import 'dart:async';
import 'dart:math' as math;

import '../protocol/protocol.dart';

/// 连接状态字符串常量（对外契约）。
abstract final class ConnectionStatuses {
  static const String disconnected = 'disconnected';
  static const String connecting = 'connecting';
  static const String connected = 'connected';
  static const String reconnecting = 'reconnecting';
  static const String failed = 'failed';
}

typedef BackoffStrategy = Duration Function(int attempt);
typedef DelayRunner = Future<void> Function(Duration delay);

Duration defaultReconnectBackoff(int attempt) {
  final exponent = attempt <= 1 ? 0 : attempt - 1;
  final ms = math.min(30000, 200 * (1 << exponent));
  return Duration(milliseconds: ms);
}

/// 连接状态机：`disconnected -> connecting -> connected -> reconnecting -> failed`。
class AgentConnectionState {
  AgentConnectionState(
    this._clientFactory, {
    BackoffStrategy? backoff,
    DelayRunner? sleep,
  })  : _backoff = backoff ?? defaultReconnectBackoff,
        _sleep = sleep;

  static const int maxReconnectAttempts = 6;

  final ProtocolClient Function() _clientFactory;
  final BackoffStrategy _backoff;
  final DelayRunner? _sleep;

  final StreamController<String> _changesController =
      StreamController<String>.broadcast();

  String _status = ConnectionStatuses.disconnected;
  int _attempt = 0;
  ProtocolClient? _client;
  Future<void>? _connectFuture;
  StreamSubscription<TelemetryDto>? _telemetrySub;
  Timer? _backoffTimer;
  Completer<bool>? _backoffCompleter;
  bool _manualDisconnect = false;
  bool _disposed = false;

  String get status => _status;
  int get attempt => _attempt;
  Stream<String> get changes => _changesController.stream;
  ProtocolClient? get client => _client;

  Future<void> connect() {
    if (_disposed) {
      return Future<void>.value();
    }
    if (_status == ConnectionStatuses.connected) {
      return Future<void>.value();
    }
    if (_status == ConnectionStatuses.connecting && _connectFuture != null) {
      return _connectFuture!;
    }

    _manualDisconnect = false;
    _cancelBackoffWait();
    _attempt = 0;
    _setStatus(ConnectionStatuses.connecting);

    final future = _connectInternal();
    _connectFuture = future;
    return future;
  }

  Future<void> _connectInternal() async {
    try {
      final client = _clientFactory();
      await client.connect();
      if (_manualDisconnect || _disposed) {
        await client.close();
        return;
      }
      _client = client;
      _attempt = 0;
      _attachClient(client);
      _setStatus(ConnectionStatuses.connected);
    } catch (_) {
      if (_manualDisconnect || _disposed) {
        return;
      }
      _startReconnect();
    } finally {
      _connectFuture = null;
    }
  }

  void _attachClient(ProtocolClient client) {
    _telemetrySub?.cancel();
    _telemetrySub = client.telemetry.listen(
      (_) {},
      onDone: _handleUnexpectedDisconnect,
    );
  }

  void _handleUnexpectedDisconnect() {
    if (_manualDisconnect || _disposed) {
      return;
    }
    if (_status == ConnectionStatuses.reconnecting ||
        _status == ConnectionStatuses.disconnected) {
      return;
    }
    _client = null;
    _startReconnect();
  }

  void _startReconnect() {
    if (_manualDisconnect || _disposed) {
      return;
    }
    _setStatus(ConnectionStatuses.reconnecting);
    unawaited(_reconnectLoop());
  }

  Future<void> _reconnectLoop() async {
    for (var i = 1; i <= maxReconnectAttempts; i++) {
      if (_manualDisconnect || _disposed) {
        return;
      }
      _attempt = i;
      final delay = _backoff(i);
      final cancelled = await _waitBackoff(delay);
      if (cancelled || _manualDisconnect || _disposed) {
        return;
      }

      try {
        final client = _clientFactory();
        await client.connect();
        if (_manualDisconnect || _disposed) {
          await client.close();
          return;
        }
        _client = client;
        _attempt = 0;
        _attachClient(client);
        _setStatus(ConnectionStatuses.connected);
        return;
      } catch (_) {
        // 继续下一次退避重试
      }
    }

    if (!_manualDisconnect && !_disposed) {
      _setStatus(ConnectionStatuses.failed);
    }
  }

  Future<bool> _waitBackoff(Duration delay) {
    final completer = Completer<bool>();
    _backoffCompleter = completer;

    final customSleep = _sleep;
    if (customSleep != null) {
      customSleep(delay).then((_) {
        if (!completer.isCompleted) {
          completer.complete(false);
        }
      }).catchError((Object _) {
        if (!completer.isCompleted) {
          completer.complete(true);
        }
      });
    } else {
      _backoffTimer = Timer(delay, () {
        if (!completer.isCompleted) {
          completer.complete(false);
        }
      });
    }

    return completer.future.whenComplete(() {
      if (identical(_backoffCompleter, completer)) {
        _backoffCompleter = null;
      }
      _backoffTimer?.cancel();
      _backoffTimer = null;
    });
  }

  void _cancelBackoffWait() {
    _backoffTimer?.cancel();
    _backoffTimer = null;
    final completer = _backoffCompleter;
    _backoffCompleter = null;
    if (completer != null && !completer.isCompleted) {
      completer.complete(true);
    }
  }

  Future<void> disconnect() async {
    _manualDisconnect = true;
    _connectFuture = null;
    _cancelBackoffWait();
    await _telemetrySub?.cancel();
    _telemetrySub = null;
    final currentClient = _client;
    _client = null;
    if (currentClient != null) {
      await currentClient.close();
    }
    _attempt = 0;
    _setStatus(ConnectionStatuses.disconnected);
  }

  Future<void> dispose() async {
    if (_disposed) {
      return;
    }
    await disconnect();
    _disposed = true;
    await _changesController.close();
  }

  void _setStatus(String next) {
    if (_status == next) {
      return;
    }
    _status = next;
    if (!_changesController.isClosed) {
      _changesController.add(next);
    }
  }
}
