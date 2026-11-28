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
    // 设置里的推送间隔改动要立刻重建订阅，否则用户改完看不到任何变化。
    appState.addListener(_onAppStateChanged);
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

  /// 已生效的订阅间隔，用于避免同一组值重复下发订阅。
  int? _appliedPoll;
  int? _appliedSensor;

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
          unawaited(_refreshThenSubscribe());
        } else {
          _currentClient = client;
          appState.attach(client);
          unawaited(_refreshThenSubscribe());
        }
      }
    } else {
      // 断开后下次连上必须重新订阅，间隔相同也要重发。
      _appliedPoll = null;
      _appliedSensor = null;
    }
    notifyListeners();
  }

  /// 先拉快照拿到服务端设置，再按设置里的间隔订阅遥测。
  ///
  /// 直接拿 [pollIntervalMs] 硬订阅会让设置页那两个输入框变成摆设：
  /// 值写进了 `state.json`，实际推送节奏还是默认值。
  Future<void> _refreshThenSubscribe() async {
    try {
      await appState.refresh();
    } on Object {
      // 刷新失败也要继续订阅，退回默认间隔，界面不能因为一次超时没有数据。
    }
    await _applySubscription();
  }

  void _onAppStateChanged() {
    unawaited(_applySubscription());
  }

  Future<void> _applySubscription() async {
    final client = _currentClient;
    if (client == null || status != ConnectionStatuses.connected) {
      return;
    }

    final settings = appState.settings;
    final poll = settings?.monitorIntervalMs ?? pollIntervalMs;
    final sensor = settings?.sensorIntervalMs ?? sensorIntervalMs;
    if (poll == _appliedPoll && sensor == _appliedSensor) {
      return;
    }

    // 先记账再发：订阅失败重试会在下一次设置变化或重连时发生，
    // 而不是每次遥测帧都重发一轮订阅。
    _appliedPoll = poll;
    _appliedSensor = sensor;

    try {
      await client.subscribeTelemetry(
        intervalMs: poll,
        sensorIntervalMs: sensor,
      );
    } on Object {
      // 订阅失败不改变连接状态：界面仍可用 agent.status 等快照。
    }
  }

  @override
  void dispose() {
    _statusSub?.cancel();
    appState.removeListener(_onAppStateChanged);
    unawaited(connection.dispose());
    appState.dispose();
    super.dispose();
  }
}
