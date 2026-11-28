import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/platform/frame_channel.dart';
import 'package:zentwik_gui/protocol/protocol.dart';
import 'package:zentwik_gui/state/state.dart';

Uint8List _jsonBytes(Map<String, Object?> json) =>
    Uint8List.fromList(utf8.encode(jsonEncode(json)));

class _TestFrameChannel extends InMemoryFrameChannel {
  _TestFrameChannel({this.failConnect = false});

  final bool failConnect;
  final List<Uint8List> _queue = [];
  Completer<Uint8List?>? _waiter;
  bool _closed = false;

  @override
  Future<void> connect({
    Duration timeout = const Duration(seconds: 3),
  }) async {
    if (failConnect) {
      throw const ProtocolException(ErrorCodes.connectionClosed);
    }
    await super.connect(timeout: timeout);
  }

  @override
  void push(Uint8List body) {
    if (_closed) return;
    final waiter = _waiter;
    if (waiter != null && !waiter.isCompleted) {
      _waiter = null;
      waiter.complete(body);
    } else {
      _queue.add(body);
    }
  }

  void simulatePeerClose() {
    _closed = true;
    final waiter = _waiter;
    if (waiter != null && !waiter.isCompleted) {
      _waiter = null;
      waiter.complete(null);
    }
  }

  @override
  Future<Uint8List?> readFrame() async {
    if (_queue.isNotEmpty) return _queue.removeAt(0);
    if (_closed) return null;
    final waiter = Completer<Uint8List?>();
    _waiter = waiter;
    return waiter.future;
  }

  @override
  Future<void> close() async {
    simulatePeerClose();
    await super.close();
  }
}

class _SlowConnectChannel extends _TestFrameChannel {
  _SlowConnectChannel(this._connectGate);

  final Completer<void> _connectGate;

  @override
  Future<void> connect({
    Duration timeout = const Duration(seconds: 3),
  }) async {
    await _connectGate.future;
    await super.connect(timeout: timeout);
  }
}

void main() {
  group('AgentConnectionState', () {
    test('1 & 2. 退避序列为 200/400/800/1600/3200/6400 ms，第 6 次失败后转 failed',
        () async {
      final recordedDelays = <Duration>[];
      var connectCalls = 0;

      final state = AgentConnectionState(
        () {
          connectCalls++;
          return ProtocolClient(_TestFrameChannel(failConnect: true));
        },
        sleep: (delay) async {
          recordedDelays.add(delay);
        },
      );

      final statuses = <String>[];
      final sub = state.changes.listen(statuses.add);

      await state.connect();
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);

      expect(
        recordedDelays.map((d) => d.inMilliseconds).toList(),
        [200, 400, 800, 1600, 3200, 6400],
      );
      expect(connectCalls, 7); // 1 initial + 6 reconnect attempts
      expect(state.status, ConnectionStatuses.failed);
      expect(state.attempt, 6);
      expect(
        statuses,
        [
          ConnectionStatuses.connecting,
          ConnectionStatuses.reconnecting,
          ConnectionStatuses.failed,
        ],
      );

      await sub.cancel();
      await state.dispose();
    });

    test('3. disconnect 在退避等待期间调用立即转 disconnected 且不残留定时器',
        () async {
      final sleepGate = Completer<void>();
      var factoryCalls = 0;

      final state = AgentConnectionState(
        () {
          factoryCalls++;
          return ProtocolClient(_TestFrameChannel(failConnect: true));
        },
        sleep: (_) => sleepGate.future,
      );

      await state.connect();
      await Future<void>.delayed(Duration.zero);
      expect(state.status, ConnectionStatuses.reconnecting);

      await state.disconnect();
      expect(state.status, ConnectionStatuses.disconnected);
      expect(factoryCalls, 1);

      sleepGate.complete();
      await Future<void>.delayed(Duration.zero);
      expect(factoryCalls, 1);
      expect(state.status, ConnectionStatuses.disconnected);

      await state.dispose();
    });

    test('4. connect 在 connected 时幂等，底层建连只发生一次', () async {
      var factoryCalls = 0;
      final state = AgentConnectionState(() {
        factoryCalls++;
        return ProtocolClient(_TestFrameChannel());
      });

      await state.connect();
      expect(state.status, ConnectionStatuses.connected);
      await state.connect();
      expect(factoryCalls, 1);

      await state.dispose();
    });

    test('5. connect 在 connecting 时返回同一个 Future', () async {
      final gate = Completer<void>();
      final state = AgentConnectionState(
        () => ProtocolClient(_SlowConnectChannel(gate)),
      );

      final first = state.connect();
      final second = state.connect();
      expect(identical(first, second), isTrue);
      expect(state.status, ConnectionStatuses.connecting);

      gate.complete();
      await first;
      expect(state.status, ConnectionStatuses.connected);

      await state.dispose();
    });

    test('6. disconnect 重复调用不抛异常', () async {
      final state = AgentConnectionState(
        () => ProtocolClient(_TestFrameChannel()),
      );

      await state.disconnect();
      await state.disconnect();
      expect(state.status, ConnectionStatuses.disconnected);

      await state.dispose();
    });

    test('7. 断线后自动进入 reconnecting 并恢复到 connected', () async {
      final firstChannel = _TestFrameChannel();
      final secondChannel = _TestFrameChannel();
      var index = 0;

      final state = AgentConnectionState(
        () {
          index++;
          return ProtocolClient(index == 1 ? firstChannel : secondChannel);
        },
        sleep: (_) async {},
      );

      final statuses = <String>[];
      final sub = state.changes.listen(statuses.add);

      await state.connect();
      expect(state.status, ConnectionStatuses.connected);

      firstChannel.simulatePeerClose();
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);

      expect(
        statuses,
        [
          ConnectionStatuses.connecting,
          ConnectionStatuses.connected,
          ConnectionStatuses.reconnecting,
          ConnectionStatuses.connected,
        ],
      );
      expect(state.status, ConnectionStatuses.connected);
      expect(state.attempt, 0);

      await sub.cancel();
      await state.dispose();
    });
  });

  group('AppState', () {
    test('8. refresh 成功时三块缓存都被填入', () async {
      final channel = _TestFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();

      final appState = AppState(client);
      final refreshFuture = appState.refresh();
      await Future<void>.delayed(Duration.zero);

      expect(channel.sent, hasLength(3));
      for (var i = 0; i < channel.sent.length; i++) {
        final req = jsonDecode(utf8.decode(channel.sent[i]))
            as Map<String, Object?>;
        final id = (req['id'] as num).toInt();
        final op = req['op'] as String;
        if (op == 'agent.status') {
          channel.push(
            _jsonBytes({
              'v': 2,
              'id': id,
              'op': op,
              'ok': true,
              'data': {'version': '0.1.0', 'machineName': 'MOTUIC-LAPTOP'},
              'isEvent': false,
            }),
          );
        } else if (op == 'capabilities.list') {
          channel.push(
            _jsonBytes({
              'v': 2,
              'id': id,
              'op': op,
              'ok': true,
              'data': [
                {'id': 'hardware.amd.smu', 'state': 'running'},
              ],
              'isEvent': false,
            }),
          );
        } else if (op == 'modules.list') {
          channel.push(
            _jsonBytes({
              'v': 2,
              'id': id,
              'op': op,
              'ok': true,
              'data': [
                {'id': 'monitoring', 'name': '监控', 'state': 'ready'},
              ],
              'isEvent': false,
            }),
          );
        }
      }

      await refreshFuture;
      expect(appState.status?.machineName, 'MOTUIC-LAPTOP');
      expect(appState.capabilities, hasLength(1));
      expect(appState.modules, hasLength(1));
      expect(appState.lastError, isNull);

      appState.dispose();
      await client.close();
    });

    test('9. refresh 中 modules.list 失败时其余两块更新、modules 保留旧值、lastError 非空',
        () async {
      final channel = _TestFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();
      final appState = AppState(client);

      // 先成功一轮，写入旧值
      final round1 = appState.refresh();
      await Future<void>.delayed(Duration.zero);
      for (final raw in channel.sent) {
        final req = jsonDecode(utf8.decode(raw)) as Map<String, Object?>;
        final id = (req['id'] as num).toInt();
        final op = req['op'] as String;
        final data = switch (op) {
          'agent.status' => {'version': '0.1.0', 'machineName': 'OLD'},
          'capabilities.list' => [
              {'id': 'cap.1', 'state': 'running'},
            ],
          _ => [
              {'id': 'monitoring', 'name': '监控', 'state': 'ready'},
            ],
        };
        channel.push(
          _jsonBytes({
            'v': 2,
            'id': id,
            'op': op,
            'ok': true,
            'data': data,
            'isEvent': false,
          }),
        );
      }
      await round1;
      expect(appState.modules, hasLength(1));

      // 第二轮让 modules.list 失败
      final sentBeforeRound2 = channel.sent.length;
      final round2 = appState.refresh();
      await Future<void>.delayed(Duration.zero);
      for (final raw in channel.sent.sublist(sentBeforeRound2)) {
        final req = jsonDecode(utf8.decode(raw)) as Map<String, Object?>;
        final id = (req['id'] as num).toInt();
        final op = req['op'] as String;
        if (op == 'modules.list') {
          channel.push(
            _jsonBytes({
              'v': 2,
              'id': id,
              'op': op,
              'ok': false,
              'error': ErrorCodes.internal,
              'detail': '模块查询失败',
              'isEvent': false,
            }),
          );
        } else if (op == 'agent.status') {
          channel.push(
            _jsonBytes({
              'v': 2,
              'id': id,
              'op': op,
              'ok': true,
              'data': {'version': '0.1.0', 'machineName': 'NEW'},
              'isEvent': false,
            }),
          );
        } else {
          channel.push(
            _jsonBytes({
              'v': 2,
              'id': id,
              'op': op,
              'ok': true,
              'data': [
                {'id': 'cap.1', 'state': 'running'},
                {'id': 'cap.2', 'state': 'loaded'},
              ],
              'isEvent': false,
            }),
          );
        }
      }

      await round2;
      expect(appState.status?.machineName, 'NEW');
      expect(appState.capabilities, hasLength(2));
      expect(appState.modules, hasLength(1));
      expect(appState.modules.first.id, 'monitoring');
      expect(appState.lastError?.code, ErrorCodes.internal);

      appState.dispose();
      await client.close();
    });

    test('10. refresh 并发两次只发一轮请求', () async {
      final channel = _TestFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();
      final appState = AppState(client);

      final f1 = appState.refresh();
      final f2 = appState.refresh();
      expect(identical(f1, f2), isTrue);
      await Future<void>.delayed(Duration.zero);
      expect(channel.sent, hasLength(3));

      for (final raw in channel.sent) {
        final req = jsonDecode(utf8.decode(raw)) as Map<String, Object?>;
        final id = (req['id'] as num).toInt();
        final op = req['op'] as String;
        channel.push(
          _jsonBytes({
            'v': 2,
            'id': id,
            'op': op,
            'ok': true,
            'data': op == 'agent.status' ? {'version': '0.1.0'} : <Object?>[],
            'isEvent': false,
          }),
        );
      }
      await f1;

      appState.dispose();
      await client.close();
    });

    test('11. 遥测事件超过 60 帧时丢弃最旧帧', () async {
      final channel = _TestFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();
      final appState = AppState(client);

      for (var i = 0; i < 65; i++) {
        channel.push(
          _jsonBytes({
            'v': 2,
            'id': 0,
            'op': 'telemetry.sample',
            'ok': true,
            'isEvent': true,
            'data': {
              'timestamp': '2026-11-27T17:44:34.816571+00:00',
              'cpuTotalPercent': i.toDouble(),
            },
          }),
        );
      }
      await Future<void>.delayed(Duration.zero);

      expect(appState.telemetryHistory, hasLength(60));
      expect(appState.telemetryHistory.first.cpuTotalPercent, 5.0);
      expect(appState.telemetry?.cpuTotalPercent, 64.0);

      appState.dispose();
      await client.close();
    });

    test('12. dispose 后到达的遥测帧不再通知监听者，也不抛异常', () async {
      final channel = _TestFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();
      final appState = AppState(client);
      var notifications = 0;
      appState.addListener(() => notifications++);

      appState.dispose();
      channel.push(
        _jsonBytes({
          'v': 2,
          'id': 0,
          'op': 'telemetry.sample',
          'ok': true,
          'isEvent': true,
          'data': {
            'timestamp': '2026-11-27T17:44:34.816571+00:00',
            'cpuTotalPercent': 1.0,
          },
        }),
      );
      await Future<void>.delayed(Duration.zero);

      expect(notifications, 0);
      await client.close();
    });
  });

  group('TelemetryState', () {
    test('使用 SubscriptionAck 生效间隔算超时并维护样本缓存', () async {
      final channel = _TestFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();

      final state = TelemetryState(client);
      final subFuture = state.startSubscription(
        intervalMs: 100,
        sensorIntervalMs: 200,
      );
      await Future<void>.delayed(Duration.zero);

      channel.push(
        _jsonBytes({
          'v': 2,
          'id': 1,
          'op': 'telemetry.subscribe',
          'ok': true,
          'data': {'intervalMs': 500, 'sensorIntervalMs': 500},
          'isEvent': false,
        }),
      );

      await subFuture;
      expect(state.isSubscribed, isTrue);
      expect(state.effectiveIntervalMs, 500);
      expect(state.expectedSampleTimeout, const Duration(milliseconds: 1500));

      channel.push(
        _jsonBytes({
          'v': 2,
          'id': 0,
          'op': 'telemetry.sample',
          'ok': true,
          'isEvent': true,
          'data': {
            'timestamp': '2026-11-27T17:44:34.816571+00:00',
            'cpuTotalPercent': 12.5,
          },
        }),
      );
      await Future<void>.delayed(Duration.zero);

      expect(state.samples, hasLength(1));
      expect(state.latest?.cpuTotalPercent, 12.5);

      state.dispose();
      await client.close();
    });
  });
}
