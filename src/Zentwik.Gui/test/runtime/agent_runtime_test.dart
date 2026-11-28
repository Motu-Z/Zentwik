import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/platform/frame_channel.dart';
import 'package:zentwik_gui/protocol/protocol.dart';
import 'package:zentwik_gui/runtime/agent_runtime.dart';

/// 带等待者的内存通道：队列空时挂起读取，等 [push] 完成。
///
/// [InMemoryFrameChannel] 队列空即返回 `null`（模拟对端关闭），
/// 无法支撑「客户端提问、服务端作答」这种往返，故这里重写读取语义。
class _LoopbackChannel extends InMemoryFrameChannel {
  Completer<Uint8List?>? _waiter;
  bool _closed = false;

  /// 每写入一帧就回调一次，测试里当作「服务端收到请求」。
  void Function(Map<String, Object?> request)? onWrite;

  @override
  Future<void> writeFrame(Uint8List body) async {
    await super.writeFrame(body);
    final handler = onWrite;
    if (handler != null) {
      handler(jsonDecode(utf8.decode(body)) as Map<String, Object?>);
    }
  }

  @override
  void push(Uint8List body) {
    if (_closed) {
      return;
    }
    final waiter = _waiter;
    if (waiter != null && !waiter.isCompleted) {
      _waiter = null;
      waiter.complete(body);
    } else {
      super.push(body);
    }
  }

  @override
  Future<Uint8List?> readFrame() async {
    final immediate = await _takeQueued();
    if (immediate != null) {
      return immediate;
    }
    if (_closed) {
      return null;
    }
    final waiter = Completer<Uint8List?>();
    _waiter = waiter;
    return waiter.future;
  }

  Future<Uint8List?> _takeQueued() async {
    try {
      return await super.readFrame();
    } on StateError {
      return null;
    }
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

Uint8List _jsonBytes(Map<String, Object?> json) =>
    Uint8List.fromList(utf8.encode(jsonEncode(json)));

/// 最小服务端：按 op 回帧，并维护一份可被 `settings.set` 改写的设置。
class _FakeAgent {
  _FakeAgent(this.channel) {
    channel.onWrite = _handle;
  }

  final _LoopbackChannel channel;
  int monitorIntervalMs = 2500;
  int sensorIntervalMs = 3000;
  final List<Map<String, Object?>> subscribeRequests = [];

  void _handle(Map<String, Object?> request) {
    final id = (request['id'] as num).toInt();
    final op = request['op'] as String;
    final payload = request['payload'];

    if (op == 'telemetry.subscribe') {
      subscribeRequests.add(
        Map<String, Object?>.from(payload! as Map),
      );
      _reply(id, op, {
        'intervalMs': monitorIntervalMs,
        'sensorIntervalMs': sensorIntervalMs,
        'effectiveAt': '2026-11-28T01:00:00Z',
      });
      return;
    }

    if (op == 'settings.set') {
      final patch = payload! as Map;
      monitorIntervalMs =
          (patch['monitorIntervalMs'] as num?)?.toInt() ?? monitorIntervalMs;
      sensorIntervalMs =
          (patch['sensorIntervalMs'] as num?)?.toInt() ?? sensorIntervalMs;
      _reply(id, op, _settings());
      return;
    }

    final data = switch (op) {
      'agent.status' => {'version': '0.1.0'},
      'capabilities.list' => <Object?>[],
      'modules.list' => <Object?>[],
      'settings.get' => _settings(),
      'ledger.list' => <Object?>[],
      _ => <Object?>[],
    };
    _reply(id, op, data);
  }

  Map<String, Object?> _settings() => {
        'monitorIntervalMs': monitorIntervalMs,
        'sensorIntervalMs': sensorIntervalMs,
      };

  void _reply(int id, String op, Object? data) {
    channel.push(
      _jsonBytes({
        'v': 2,
        'id': id,
        'op': op,
        'ok': true,
        'isEvent': false,
        'data': data,
      }),
    );
  }
}

void main() {
  test('首连后按设置里的间隔订阅，而不是构造时的默认值', () async {
    final channel = _LoopbackChannel();
    final agent = _FakeAgent(channel);

    final runtime = AgentRuntime(
      clientFactory: () => ProtocolClient(channel),
      pollIntervalMs: 1000,
      sensorIntervalMs: 2000,
    );

    await runtime.start();
    // 等 refresh（5 个 op）+ 订阅走完。
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(Duration.zero);
    }

    expect(agent.subscribeRequests, isNotEmpty);
    expect(agent.subscribeRequests.first['intervalMs'], 2500);
    expect(agent.subscribeRequests.first['sensorIntervalMs'], 3000);

    runtime.dispose();
  });

  test('保存设置后立即按新间隔重订阅', () async {
    final channel = _LoopbackChannel();
    final agent = _FakeAgent(channel);

    final runtime = AgentRuntime(
      clientFactory: () => ProtocolClient(channel),
      pollIntervalMs: 1000,
      sensorIntervalMs: 2000,
    );

    await runtime.start();
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(Duration.zero);
    }
    final before = agent.subscribeRequests.length;

    final saved = await runtime.appState.saveSettings(
      const SettingsPatch(monitorIntervalMs: 500),
    );
    expect(saved, isTrue);
    for (var i = 0; i < 10; i++) {
      await Future<void>.delayed(Duration.zero);
    }

    expect(agent.subscribeRequests.length, greaterThan(before));
    expect(agent.subscribeRequests.last['intervalMs'], 500);

    runtime.dispose();
  });
}
