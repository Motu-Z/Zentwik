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
  final List<Uint8List> _queue = [];
  Completer<Uint8List?>? _waiter;
  bool _closed = false;

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
    _closed = true;
    final waiter = _waiter;
    if (waiter != null && !waiter.isCompleted) {
      _waiter = null;
      waiter.complete(null);
    }
    await super.close();
  }
}

void main() {
  test('TelemetryState 使用 SubscriptionAck 生效间隔算超时并维护样本缓存', () async {
    final channel = _TestFrameChannel();
    final client = ProtocolClient(channel);
    await client.connect();

    final state = TelemetryState(client);
    final subFuture = state.startSubscription(
      intervalMs: 100,
      sensorIntervalMs: 200,
    );
    await Future<void>.delayed(Duration.zero);

    // 服务端把 100ms 夹到下限 500ms
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
}
