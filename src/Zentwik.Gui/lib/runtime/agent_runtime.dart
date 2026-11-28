import 'dart:async';

import 'package:flutter/foundation.dart';

import '../platform/win32_pipe_channel.dart';
import '../protocol/protocol.dart';
import '../state/state.dart';

/// 把连接状态机与聚合状态接在一起。
///
/// 职责边界：
/// - 只负责「连、断、切客户端、拉快照、订阅遥测」；
/// - 不碰窗口、托盘、持久化；
/// - 重连成功时把 [AppState] 切到新客户端（旧客户端不可复用）。
class AgentRuntime extends ChangeNotifier {
  AgentRuntime({
    ProtocolClient Function()? clientFactory,
    this.pollIntervalMs = 1000,
    this.sensorIntervalMs = 2000,
  }) {
    final factory = clientFactory ?? _defaultClientFactory;

    // 首连的客户端同时交给 AppState 做占位，避免界面在连上前没有状态对象。
    ProtocolClient? handoff = factory();
    final firstClient = handoff;
    appState = AppState(firstClient);

    connection = AgentConnectionState(() {
      if (handoff != null) {
        final client = handoff!;
        handoff = null;
        return client;
      }
      return factory();
    });

    _statusSub = connection.changes.listen(_onStatusChanged);
    _currentClient = firstClient;
  }

  static ProtocolClient _defaultClientFactory() =>
      ProtocolClient(Win32PipeChannel());

  late final AgentConnectionState connection;
  late final AppState appState;

  /// 遥测推送间隔（毫秒），传感器（温度/功耗/逐核频率）由后端按自己的节奏夹取。
  final int pollIntervalMs;
  final int sensorIntervalMs;

  StreamSubscription<String>? _statusSub;
  ProtocolClient? _currentClient;

  String get status => connection.status;
  int get reconnectAttempt => connection.attempt;

  /// 启动连接。失败会进 `reconnecting`，由状态机按退避重试。
  Future<void> start() => connection.connect();

  Future<void> retry() async {
    await connection.disconnect();
    await connection.connect();
  }

  void _onStatusChanged(String status) {
    if (status == ConnectionStatuses.connected) {
      final client = connection.client;
      if (client != null) {
        if (identical(client, _currentClient)) {
          // 首连：AppState 已经持有这个客户端，只需拉快照与订阅。
          unawaited(appState.refresh());
        } else {
          _currentClient = client;
          appState.attach(client);
        }
        unawaited(_subscribe(client));
      }
    }
    notifyListeners();
  }

  Future<void> _subscribe(ProtocolClient client) async {
    try {
      await client.subscribeTelemetry(
        intervalMs: pollIntervalMs,
        sensorIntervalMs: sensorIntervalMs,
      );
    } on Object {
      // 订阅失败不改变连接状态：下次推送间隔到期由后端决定，
      // 这里只丢一帧数据源，界面仍可用 agent.status 等快照。
    }
  }

  @override
  void dispose() {
    _statusSub?.cancel();
    unawaited(connection.dispose());
    appState.dispose();
    super.dispose();
  }
}
