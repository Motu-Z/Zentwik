import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/platform/frame_channel.dart';
import 'package:zentwik_gui/protocol/protocol.dart';

Uint8List _encodeJson(Map<String, Object?> json) =>
    Uint8List.fromList(utf8.encode(jsonEncode(json)));

/// 可控异步内存通道：继承 [InMemoryFrameChannel]，在队列为空时挂起等待新帧或对端关闭，
/// 便于在测试中验证并发乱序响应、超时与对端断线。
class _ControllableFrameChannel extends InMemoryFrameChannel {
  _ControllableFrameChannel();

  final List<Uint8List> _asyncQueue = [];
  Completer<Uint8List?>? _waiter;
  bool _peerClosed = false;

  @override
  void push(Uint8List body) {
    if (_peerClosed) return;
    final waiter = _waiter;
    if (waiter != null && !waiter.isCompleted) {
      _waiter = null;
      waiter.complete(body);
    } else {
      _asyncQueue.add(body);
    }
  }

  void simulatePeerClose() {
    _peerClosed = true;
    final waiter = _waiter;
    if (waiter != null && !waiter.isCompleted) {
      _waiter = null;
      waiter.complete(null);
    }
  }

  @override
  Future<Uint8List?> readFrame() async {
    if (_asyncQueue.isNotEmpty) {
      return _asyncQueue.removeAt(0);
    }
    if (_peerClosed) {
      return null;
    }
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

void main() {
  group('RequestEnvelope & ResponseEnvelope', () {
    test('1. 请求编码：payload 缺失时 JSON 里没有 payload 键', () {
      const req = RequestEnvelope(id: 1, op: 'agent.status');
      final jsonMap = req.toJson();
      expect(jsonMap.containsKey('payload'), isFalse);
      expect(req.encode(), '{"v":2,"id":1,"op":"agent.status"}');

      const withPayload = RequestEnvelope(
        id: 2,
        op: 'telemetry.subscribe',
        payload: {'intervalMs': 1000, 'sensorIntervalMs': 2000},
      );
      expect(withPayload.toJson().containsKey('payload'), isTrue);
    });

    test('2. 响应解码：data 缺失、detail 为空串、isEvent 存在均能正常解析', () {
      final noData = ResponseEnvelope.decode(
        '{"v":2,"id":1,"op":"agent.ping","ok":true,"detail":"","isEvent":false}',
      );
      expect(noData.ok, isTrue);
      expect(noData.data, isNull);
      expect(noData.detail, '');
      expect(noData.isEvent, isFalse);

      final withData = ResponseEnvelope.decode(
        '{"v":2,"id":2,"op":"agent.status","ok":true,'
        '"data":{"version":"0.1.0","protocolVersion":2,"machineName":"MOTUIC-LAPTOP",'
        '"elevated":true,"runningAsService":true,"simulated":false,"processorCount":32,'
        '"physicalCoreCount":16,"processorGroups":1,'
        '"startedAt":"2026-11-27T23:49:47.7340302+08:00","uptimeSeconds":6879,'
        '"dataDirectory":"C:\\\\ProgramData\\\\Zentwik","planCount":0,"sceneCount":0,'
        '"ledgerEntryCount":2},"isEvent":false}',
      );
      final status =
          AgentStatusDto.fromJson(withData.data! as Map<String, Object?>);
      expect(status.machineName, 'MOTUIC-LAPTOP');
      expect(status.processorCount, 32);
      expect(status.activeSceneId, isNull);
      expect(status.startedAt, isNotNull);
    });
  });

  group('ProtocolClient', () {
    test('3. 事件帧：id == 0 进广播流，不消耗请求配对', () async {
      final channel = _ControllableFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();

      final received = <TelemetryDto>[];
      final sub = client.telemetry.listen(received.add);

      final statusFuture = client.request('agent.status');
      await Future<void>.delayed(Duration.zero);

      // 先推事件帧（id == 0），再推请求响应（id == 1）
      channel.push(
        _encodeJson({
          'v': 2,
          'id': 0,
          'op': 'telemetry.sample',
          'ok': true,
          'isEvent': true,
          'data': {
            'timestamp': '2026-11-27T17:44:34.816571+00:00',
            'cpuTotalPercent': 9.616295594504974,
            'memoryTotalBytes': 33485860864,
            'memoryAvailableBytes': 19368648704,
            'processCount': 354,
            'processorCount': 32,
          },
        }),
      );
      channel.push(
        _encodeJson({
          'v': 2,
          'id': 1,
          'op': 'agent.status',
          'ok': true,
          'data': {'version': '0.1.0'},
          'isEvent': false,
        }),
      );

      final response = await statusFuture;
      await Future<void>.delayed(Duration.zero);

      expect(response.id, 1);
      expect(received, hasLength(1));
      expect(received.first.cpuTotalPercent, closeTo(9.616295, 1e-5));

      await sub.cancel();
      await client.close();
    });

    test('4. 并发：两个 request 乱序返回，各自拿到自己的结果', () async {
      final channel = _ControllableFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();

      final first = client.request('agent.status');
      final second = client.request('settings.get');
      await Future<void>.delayed(Duration.zero);

      expect(channel.sent, hasLength(2));

      // 先回 id=2，再回 id=1
      channel.push(
        _encodeJson({
          'v': 2,
          'id': 2,
          'op': 'settings.get',
          'ok': true,
          'data': {'language': 'zh-CN', 'theme': 'dark'},
          'isEvent': false,
        }),
      );
      channel.push(
        _encodeJson({
          'v': 2,
          'id': 1,
          'op': 'agent.status',
          'ok': true,
          'data': {'machineName': 'MOTUIC-LAPTOP'},
          'isEvent': false,
        }),
      );

      final results = await Future.wait([first, second]);
      expect(results[0].op, 'agent.status');
      expect((results[0].data! as Map<String, Object?>)['machineName'],
          'MOTUIC-LAPTOP');
      expect(results[1].op, 'settings.get');
      expect(
          (results[1].data! as Map<String, Object?>)['language'], 'zh-CN');

      await client.close();
    });

    test('5. 错误：ok:false 抛 ProtocolException，code 与 detail 正确', () async {
      final channel = _ControllableFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();

      final future = client.request('plans.apply', {'id': 'missing'});
      await Future<void>.delayed(Duration.zero);

      channel.push(
        _encodeJson({
          'v': 2,
          'id': 1,
          'op': 'plans.apply',
          'ok': false,
          'error': 'plan.not_found',
          'detail': '方案不存在',
          'isEvent': false,
        }),
      );

      await expectLater(
        future,
        throwsA(
          isA<ProtocolException>()
              .having((e) => e.code, 'code', ErrorCodes.planNotFound)
              .having((e) => e.detail, 'detail', '方案不存在')
              .having((e) => e.op, 'op', 'plans.apply'),
        ),
      );

      expect(describe(ErrorCodes.planNotFound).contains('plan.not_found'),
          isFalse);

      await client.close();
    });

    test('6. 超时：不返回响应时抛 TimeoutException，且不泄漏挂起项', () async {
      final channel = _ControllableFrameChannel();
      final client = ProtocolClient(
        channel,
        requestTimeout: const Duration(milliseconds: 50),
      );
      await client.connect();

      await expectLater(
        client.request('agent.ping'),
        throwsA(isA<TimeoutException>()),
      );

      // 超时清理后，后续正常请求仍可正常完成
      final nextFuture = client.request('agent.ping');
      await Future<void>.delayed(Duration.zero);
      channel.push(
        _encodeJson({
          'v': 2,
          'id': 2,
          'op': 'agent.ping',
          'ok': true,
          'isEvent': false,
        }),
      );
      final resp = await nextFuture;
      expect(resp.ok, isTrue);

      await client.close();
    });

    test('7. 对端关闭：挂起请求全部失败、事件流关闭、后续请求立即失败', () async {
      final channel = _ControllableFrameChannel();
      final client = ProtocolClient(channel);
      await client.connect();

      var streamClosed = false;
      final sub = client.telemetry.listen(
        (_) {},
        onDone: () => streamClosed = true,
      );

      final pending1 = client.request('agent.status');
      final pending2 = client.request('modules.list');
      await Future<void>.delayed(Duration.zero);

      channel.simulatePeerClose();

      await expectLater(
        pending1,
        throwsA(
          isA<ProtocolException>()
              .having((e) => e.code, 'code', ErrorCodes.connectionClosed),
        ),
      );
      await expectLater(
        pending2,
        throwsA(
          isA<ProtocolException>()
              .having((e) => e.code, 'code', ErrorCodes.connectionClosed),
        ),
      );

      await Future<void>.delayed(Duration.zero);
      expect(streamClosed, isTrue);

      await expectLater(
        client.request('agent.ping'),
        throwsA(
          isA<ProtocolException>()
              .having((e) => e.code, 'code', ErrorCodes.connectionClosed),
        ),
      );

      await sub.cancel();
      await client.close();
    });
  });

  group('Enum & DTO decoding', () {
    test('8. 枚举映射：owner/status/kind 数字 <-> 语义值双向表', () {
      expect(LedgerOwner.fromWire(0), LedgerOwner.none);
      expect(LedgerOwner.fromWire(1), LedgerOwner.scene);
      expect(LedgerOwner.fromWire(2), LedgerOwner.user);
      expect(LedgerOwner.fromWire(3), LedgerOwner.plan);
      expect(LedgerOwner.fromWire(4), LedgerOwner.system);
      expect(LedgerOwner.none.wire, 0);
      expect(LedgerOwner.system.wire, 4);
      expect(LedgerOwner.fromWire(99), isNull);

      expect(LedgerStatus.fromWire(0), LedgerStatus.applied);
      expect(LedgerStatus.fromWire(1), LedgerStatus.reverted);
      expect(LedgerStatus.fromWire(2), LedgerStatus.failed);
      expect(LedgerStatus.fromWire(3), LedgerStatus.skipped);
      expect(LedgerStatus.fromWire(4), LedgerStatus.unknown);
      expect(LedgerStatus.unknown.wire, 4);

      expect(PlanKind.fromWire(0), PlanKind.cpuTuning);
      expect(PlanKind.fromWire(1), PlanKind.power);
      expect(PlanKind.fromWire(2), PlanKind.scheduling);
      expect(PlanKind.scheduling.wire, 2);

      final entry = LedgerEntry.fromJson({
        'target': 'power.active',
        'planId': 'probe-hidden',
        'previousValue': 'f60bedaa',
        'desiredValue': '8c5e7fda',
        'actualValue': 'f60bedaa',
        'source': 'plan:probe-hidden',
        'owner': 0,
        'priority': 2147483646,
        'status': 1,
        'updatedAt': '2026-11-26T14:56:37.2378083+00:00',
        'restorable': false,
        'processStartTicks': 0,
      });
      expect(entry.ownerEnum, LedgerOwner.none);
      expect(entry.statusEnum, LedgerStatus.reverted);
      expect(entry.restorable, isFalse);
    });

    test('9. TelemetryDto：temperatureCelsius 缺失为 null，coreClocks 缺失为空列表', () {
      final dto = TelemetryDto.fromJson({
        'timestamp': '2026-11-27T17:44:34.816571+00:00',
        'cpuTotalPercent': 10,
        'memoryTotalBytes': 33485860864,
        'memoryAvailableBytes': 19368648704,
        'processCount': 354,
        'processorCount': 32,
      });
      expect(dto.cpuTotalPercent, 10.0);
      expect(dto.temperatureCelsius, isNull);
      expect(dto.packagePowerWatts, isNull);
      expect(dto.coreClocks, isEmpty);
      expect(dto.processorUtilization, isNull);

      final full = TelemetryDto.fromJson({
        'timestamp': '2026-11-27T17:44:34.816571+00:00',
        'cpuTotalPercent': 9.616295594504974,
        'temperatureCelsius': 73.75,
        'packagePowerWatts': 34,
        'coreClocks': [
          {'ccd': 0, 'core': 0, 'megahertz': 310},
        ],
        'processorUtilization': [9.23, 0],
      });
      expect(full.temperatureCelsius, 73.75);
      expect(full.packagePowerWatts, 34.0);
      expect(full.coreClocks, hasLength(1));
      expect(full.coreClocks.first.megahertz, 310.0);
      expect(full.processorUtilization, [9.23, 0.0]);
    });
  });
}
