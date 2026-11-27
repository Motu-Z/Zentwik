@Tags(['smoke'])
library;

import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/platform/win32_pipe_channel.dart';

/// 真机冒烟：连本机正在运行的服务，只做只读操作（ping / 订阅一帧 / 退订）。
///
/// 服务未运行时整组跳过，不判失败。
void main() {
  if (!Platform.isWindows) {
    test('非 Windows 环境跳过', () {}, skip: '命名管道仅 Windows');
    return;
  }

  late Win32PipeChannel channel;
  var reachable = true;

  setUp(() async {
    channel = Win32PipeChannel();
    try {
      await channel.connect();
    } on PipeConnectException {
      reachable = false;
    }
  });

  tearDown(() async {
    await channel.close();
  });

  test('ping 往返，响应 id 与请求一致', () async {
    if (!reachable) {
      return;
    }

    await channel.writeFrame(Uint8List.fromList(utf8.encode('{"v":2,"id":7,"op":"agent.ping"}')));
    final body = await channel.readFrame();
    expect(body, isNotNull);

    final json = jsonDecode(utf8.decode(body!)) as Map<String, Object?>;
    expect(json['id'], 7);
    expect(json['op'], 'agent.ping');
    expect(json['ok'], true);
    expect(json['isEvent'], false);
  }, timeout: const Timeout(Duration(seconds: 10)));

  test('订阅后能收到事件帧，退订后连接仍可用', () async {
    if (!reachable) {
      return;
    }

    await channel.writeFrame(Uint8List.fromList(utf8.encode(
      '{"v":2,"id":11,"op":"telemetry.subscribe","payload":{"intervalMs":500,"sensorIntervalMs":0}}',
    )));

    final ackBody = await channel.readFrame();
    final ack = jsonDecode(utf8.decode(ackBody!)) as Map<String, Object?>;
    expect(ack['ok'], true);
    expect(ack['id'], 11);
    final effective = (ack['data']! as Map<String, Object?>)['intervalMs'];
    expect(effective, isA<int>());

    final eventBody = await channel.readFrame();
    final event = jsonDecode(utf8.decode(eventBody!)) as Map<String, Object?>;
    expect(event['id'], 0, reason: '事件帧 id 恒为 0');
    expect(event['op'], 'telemetry.sample');
    expect(event['isEvent'], true);

    final data = event['data']! as Map<String, Object?>;
    expect(data['cpuTotalPercent'], isA<num>());
    expect(data['processorUtilization'], isA<List<Object?>>());
    expect((data['processorUtilization']! as List).length, greaterThan(0));

    await channel.writeFrame(Uint8List.fromList(utf8.encode('{"v":2,"id":12,"op":"telemetry.unsubscribe"}')));
    final unsubBody = await channel.readFrame();
    final unsub = jsonDecode(utf8.decode(unsubBody!)) as Map<String, Object?>;
    expect(unsub['ok'], true);
    expect(unsub['id'], 12);
  }, timeout: const Timeout(Duration(seconds: 15)));

  test('关闭后读帧返回 null，写帧抛异常', () async {
    if (!reachable) {
      return;
    }

    await channel.close();
    expect(await channel.readFrame(), isNull);
    expect(
      () => channel.writeFrame(Uint8List.fromList(utf8.encode('{"v":2,"id":1,"op":"agent.ping"}'))),
      throwsA(isA<PipeClosedException>()),
    );
  }, timeout: const Timeout(Duration(seconds: 10)));
}
