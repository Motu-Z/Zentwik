import 'package:flutter_test/flutter_test.dart';
import 'package:zentwik_gui/dashboard/dashboard.dart';

void main() {
  group('DashboardKind & DashboardLayout', () {
    test('1. defaults 非空且所有 span 在允许区间', () {
      final defaults = DashboardLayout.defaults;
      expect(defaults.cards, isNotEmpty);
      expect(defaults.cards, hasLength(DashboardKind.values.length));
      for (final card in defaults.cards) {
        expect(card.span, inInclusiveRange(card.kind.minSpan, card.kind.maxSpan));
        expect(card.visible, isTrue);
      }
    });

    test('2. normalize 去重保留首次出现', () {
      final layout = DashboardLayout.normalize([
        const DashboardCard(kind: DashboardKind.cpuUsage, span: 2),
        const DashboardCard(kind: DashboardKind.memory, span: 1),
        const DashboardCard(
          kind: DashboardKind.cpuUsage,
          span: 1,
          visible: false,
        ),
      ]);

      expect(layout.cards, hasLength(2));
      expect(layout.cards[0].kind, DashboardKind.cpuUsage);
      expect(layout.cards[0].span, 2);
      expect(layout.cards[0].visible, isTrue);
      expect(layout.cards[1].kind, DashboardKind.memory);
    });

    test('3. normalize clamp 超出区间的 span（含 0 与负数）', () {
      final layout = DashboardLayout.normalize([
        const DashboardCard(kind: DashboardKind.cpuUsage, span: 0),
        const DashboardCard(kind: DashboardKind.coreClocks, span: -5),
        const DashboardCard(kind: DashboardKind.memory, span: 99),
      ]);

      expect(layout.cards[0].span, DashboardKind.cpuUsage.minSpan);
      expect(layout.cards[1].span, DashboardKind.coreClocks.minSpan);
      expect(layout.cards[2].span, DashboardKind.memory.maxSpan);
    });

    test('4. normalize 空输入回落 defaults', () {
      expect(DashboardLayout.normalize(const []), DashboardLayout.defaults);
    });

    test('5. resize 超上限夹到 maxSpan，超下限夹到 minSpan', () {
      final layout = DashboardLayout.defaults;
      final tooLarge = layout.resize(DashboardKind.cpuUsage, 99);
      expect(
        tooLarge.cards.firstWhere((c) => c.kind == DashboardKind.cpuUsage).span,
        DashboardKind.cpuUsage.maxSpan,
      );

      final tooSmall = layout.resize(DashboardKind.coreClocks, 0);
      expect(
        tooSmall.cards.firstWhere((c) => c.kind == DashboardKind.coreClocks).span,
        DashboardKind.coreClocks.minSpan,
      );
    });

    test('6. reorder 正向、反向、from == to 与越界', () {
      final layout = DashboardLayout.normalize([
        const DashboardCard(kind: DashboardKind.cpuUsage, span: 1),
        const DashboardCard(kind: DashboardKind.cpuTemperature, span: 1),
        const DashboardCard(kind: DashboardKind.memory, span: 1),
      ]);

      final forward = layout.reorder(0, 2);
      expect(
        forward.cards.map((c) => c.kind).toList(),
        [
          DashboardKind.cpuTemperature,
          DashboardKind.memory,
          DashboardKind.cpuUsage,
        ],
      );

      final backward = forward.reorder(2, 0);
      expect(backward, layout);

      expect(identical(layout.reorder(1, 1), layout), isTrue);
      expect(identical(layout.reorder(-1, 1), layout), isTrue);
      expect(identical(layout.reorder(0, 99), layout), isTrue);
    });

    test('7. remove 到空回落 defaults，add 重复返回原实例', () {
      final single = DashboardLayout.normalize([
        const DashboardCard(kind: DashboardKind.cpuUsage, span: 1),
      ]);

      expect(single.remove(DashboardKind.cpuUsage), DashboardLayout.defaults);
      expect(identical(single.add(DashboardKind.cpuUsage), single), isTrue);

      final added = single.add(DashboardKind.memory);
      expect(added.cards, hasLength(2));
      expect(added.cards.last.kind, DashboardKind.memory);
      expect(added.cards.last.span, DashboardKind.memory.defaultSpan);
    });

    test('8. setVisible(false) 全部隐藏后不回落默认', () {
      var layout = DashboardLayout.normalize([
        const DashboardCard(kind: DashboardKind.cpuUsage, span: 1),
        const DashboardCard(kind: DashboardKind.memory, span: 1),
      ]);

      layout = layout.setVisible(DashboardKind.cpuUsage, false);
      layout = layout.setVisible(DashboardKind.memory, false);

      expect(layout.cards, hasLength(2));
      expect(layout.cards.where((c) => c.visible), isEmpty);
    });

    test('9. toJson / fromJson 往返相等', () {
      final original = DashboardLayout.normalize([
        const DashboardCard(kind: DashboardKind.cpuUsage, span: 2),
        const DashboardCard(
          kind: DashboardKind.coreClocks,
          span: 4,
          visible: false,
        ),
      ]);

      final decoded = DashboardLayout.fromJson(original.toJson());
      expect(decoded, original);
    });

    test('10. fromJson 遇未知 wire 跳过该项，不抛异常', () {
      final decoded = DashboardLayout.fromJson({
        'version': 2,
        'cards': [
          {'kind': 'unknownFutureCard', 'span': 2, 'visible': true},
          {'kind': 'cpuUsage', 'span': 2, 'visible': true},
        ],
      });

      expect(decoded.cards, hasLength(1));
      expect(decoded.cards.first.kind, DashboardKind.cpuUsage);
      expect(decoded.cards.first.span, 2);
    });

    test('11. fromJson 遇缺 span/visible 用默认值与 true，异常结构回落 defaults', () {
      final decoded = DashboardLayout.fromJson({
        'cards': [
          {'kind': 'coreClocks'},
        ],
      });
      expect(decoded.cards, hasLength(1));
      expect(decoded.cards.first.span, DashboardKind.coreClocks.defaultSpan);
      expect(decoded.cards.first.visible, isTrue);

      expect(DashboardLayout.fromJson('not a map'), DashboardLayout.defaults);
      expect(
        DashboardLayout.fromJson({'cards': 'not a list'}),
        DashboardLayout.defaults,
      );
      expect(
        DashboardLayout.fromJson({
          'cards': ['not a card map'],
        }),
        DashboardLayout.defaults,
      );
    });
  });

  group('packIntoRows', () {
    test('12. 2 列、4 列与超列宽卡片打包正确', () {
      expect(
        packIntoRows([2, 1, 1], 2),
        [
          [const DashboardCell(column: 0, span: 2)],
          [
            const DashboardCell(column: 0, span: 1),
            const DashboardCell(column: 1, span: 1),
          ],
        ],
      );

      expect(
        packIntoRows([2, 1, 1], 4),
        [
          [
            const DashboardCell(column: 0, span: 2),
            const DashboardCell(column: 2, span: 1),
            const DashboardCell(column: 3, span: 1),
          ],
        ],
      );

      expect(
        packIntoRows([4, 4], 2),
        [
          [const DashboardCell(column: 0, span: 2)],
          [const DashboardCell(column: 0, span: 2)],
        ],
      );
    });
  });
}
