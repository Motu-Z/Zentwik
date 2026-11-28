import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';

void main() {
  group('DashboardBoard', () {
    test('默认布局非空、无重复、跨度在允许区间内', () {
      final board = DashboardBoard.defaults();
      expect(board.items, isNotEmpty);
      expect(board.kinds.length, board.items.length, reason: '不应出现重复组件');
      for (final item in board.items) {
        expect(
          item.span,
          inInclusiveRange(item.kind.minSpan, item.kind.maxSpan),
          reason: '${item.kind.wire} 的跨度越界',
        );
        expect(item.autoHeight, isTrue, reason: '默认应为自适应高度');
      }
    });

    test('添加与移除：重复添加无效，移除后 addable 才重新出现', () {
      final board = DashboardBoard.defaults().remove(DashboardKind.cpuUsage);
      expect(board.contains(DashboardKind.cpuUsage), isFalse);
      expect(
        board.addable,
        contains(DashboardKind.cpuUsage),
        reason: '移除后应回到可添加列表',
      );

      final added = board.add(DashboardKind.cpuUsage);
      expect(added.items.length, board.items.length + 1);
      expect(added.items.last.kind, DashboardKind.cpuUsage);

      // 已在首页上，再添加应当原样返回（同一实例）。
      expect(identical(added.add(DashboardKind.cpuUsage), added), isTrue);
    });

    test('move 是插入而非交换，且越界目标被收敛', () {
      final board = DashboardBoard(const [
        BoardItem(kind: DashboardKind.cpuUsage, span: 1),
        BoardItem(kind: DashboardKind.memory, span: 1),
        BoardItem(kind: DashboardKind.processCount, span: 1),
      ]);

      final moved = board.move(0, 2);
      expect(
        moved.items.map((item) => item.kind),
        [
          DashboardKind.memory,
          DashboardKind.processCount,
          DashboardKind.cpuUsage,
        ],
      );

      final clamped = board.move(0, 99);
      expect(clamped.items.last.kind, DashboardKind.cpuUsage);
      expect(
        identical(board.move(1, 1), board),
        isTrue,
        reason: '原地不动应返回同一实例',
      );
    });

    test('resizeSpan 受组件上下限与当前列数双重约束', () {
      // 自己搭一张含「逐核频率」的板子：它已不在默认布局里。
      final board = DashboardBoard(const [
        BoardItem(kind: DashboardKind.coreClocks, span: 2),
      ]);

      // 两列窗口里，4 跨组件最多只能到 2。
      final narrow = board.resizeSpan(DashboardKind.coreClocks, 4, columns: 2);
      expect(
        narrow.items.firstWhere((i) => i.kind == DashboardKind.coreClocks).span,
        2,
      );

      final wide = board.resizeSpan(DashboardKind.coreClocks, 4, columns: 4);
      expect(
        wide.items.firstWhere((i) => i.kind == DashboardKind.coreClocks).span,
        4,
      );

      // 低于 minSpan 的请求被抬回下限。
      final tooSmall = board.resizeSpan(DashboardKind.coreClocks, 1);
      expect(
        tooSmall.items
            .firstWhere((i) => i.kind == DashboardKind.coreClocks)
            .span,
        DashboardKind.coreClocks.minSpan,
      );
    });

    test('setHeight 低于下限时抬到下限，null 恢复自适应', () {
      final base = DashboardBoard(const [
        BoardItem(kind: DashboardKind.memory, span: 1),
      ]);
      var board = base.setHeight(DashboardKind.memory, 10);
      expect(
        board.items.firstWhere((i) => i.kind == DashboardKind.memory).height,
        kMinBoardItemHeight,
      );

      board = board.setHeight(DashboardKind.memory, 220.4);
      expect(
        board.items.firstWhere((i) => i.kind == DashboardKind.memory).height,
        220.0,
        reason: '高度取整，避免落盘出现长小数',
      );

      board = board.setHeight(DashboardKind.memory, null);
      expect(
        board.items.firstWhere((i) => i.kind == DashboardKind.memory).autoHeight,
        isTrue,
      );
    });

    test('clearHeights 一次清掉所有手工高度', () {
      final board = DashboardBoard.defaults()
          .setHeight(DashboardKind.cpuUsage, 180)
          .setHeight(DashboardKind.cpuTemperature, 160);
      expect(board.clearHeights().items.every((i) => i.autoHeight), isTrue);
    });

    test('toJson → fromJson 往返相等（含跨度与显式高度）', () {
      final board = DashboardBoard.defaults()
          .move(0, 3)
          .resizeSpan(DashboardKind.cpuUsage, 3, columns: 4)
          .setHeight(DashboardKind.moduleStatus, 168);

      final restored = DashboardBoard.fromJson(board.toJson());
      expect(restored, board);
    });

    test('fromJson 丢弃未知类型与重复项、收敛越界值、空结果返回 null', () {
      final restored = DashboardBoard.fromJson({
        'items': [
          {'kind': 'cpuUsage', 'span': 99},
          {'kind': 'cpuUsage', 'span': 1},
          {'kind': 'notAKind', 'span': 1},
          {'kind': 'memory', 'span': 1, 'height': -5},
        ],
      })!;

      expect(restored.items.length, 2, reason: '重复与未知类型都应被丢弃');
      expect(restored.items[0].span, DashboardKind.cpuUsage.maxSpan);
      expect(restored.items[1].height, kMinBoardItemHeight);

      expect(DashboardBoard.fromJson({'items': []}), isNull);
      expect(DashboardBoard.fromJson({'nope': 1}), isNull);
      expect(DashboardBoard.fromJson(null), isNull);
    });
  });

  group('packMasonry', () {
    test('一张卡变高不会把其他列一起推下去', () {
      final rects = packMasonry(
        spans: const [1, 1, 1],
        heights: const [300, 80, 80],
        columns: 3,
        columnWidth: 100,
        spacing: 10,
      );

      // 第一张很高，但另外两张仍然贴顶 —— 这是固定行布局做不到的。
      expect(rects[0], const Rect.fromLTWH(0, 0, 100, 300));
      expect(rects[1], const Rect.fromLTWH(110, 0, 100, 80));
      expect(rects[2], const Rect.fromLTWH(220, 0, 100, 80));
    });

    test('后续项落到最短列，而不是简单按行铺开', () {
      final rects = packMasonry(
        spans: const [1, 1, 1],
        heights: const [100, 50, 80],
        columns: 2,
        columnWidth: 100,
        spacing: 10,
      );

      expect(rects[0], const Rect.fromLTWH(0, 0, 100, 100));
      expect(rects[1], const Rect.fromLTWH(110, 0, 100, 50));
      // 第二列底部（50+10）比第一列（100+10）低，所以第三张进第二列。
      expect(rects[2].left, 110);
      expect(rects[2].top, 60);
    });

    test('跨列项宽度含中间间距，且起始列不超过可用范围', () {
      final rects = packMasonry(
        spans: const [1, 1, 2],
        heights: const [100, 100, 60],
        columns: 2,
        columnWidth: 100,
        spacing: 10,
      );

      expect(rects[2].width, 210, reason: '两列 + 一个间距');
      expect(rects[2].left, 0);
      expect(rects[2].top, 110, reason: '要等跨到的两列都空出来');
    });

    test('跨度大于列数时被收敛，不产生负宽度或越界', () {
      final rects = packMasonry(
        spans: const [4, 1],
        heights: const [50, 50],
        columns: 2,
        columnWidth: 100,
        spacing: 10,
      );

      expect(rects[0].width, 210);
      expect(rects[0].left, 0);
      expect(rects[1].top, 60);
    });

    test('空输入返回空，负高度按 0 处理', () {
      expect(
        packMasonry(
          spans: const [],
          heights: const [],
          columns: 2,
          columnWidth: 100,
          spacing: 10,
        ),
        isEmpty,
      );

      final rects = packMasonry(
        spans: const [1],
        heights: const [-10],
        columns: 1,
        columnWidth: 100,
        spacing: 10,
      );
      expect(rects.single.height, 0);
    });

    test('总高度取最深一列的底边', () {
      final rects = packMasonry(
        spans: const [1, 1],
        heights: const [100, 140],
        columns: 2,
        columnWidth: 100,
        spacing: 10,
      );
      expect(masonryExtentHeight(rects), 140);
      expect(masonryExtentHeight(const []), 0);
    });
  });
}
