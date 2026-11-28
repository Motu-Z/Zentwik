import 'package:material_ui/material_ui.dart';

enum CardWidthClass { compact, regular, wide }

enum CardHeightClass { unbounded, tight, regular, tall }

/// 外壳通过 [LayoutBuilder] 下发给卡片正文的实际尺寸合同。
///
/// 卡片禁止用 `span` 或 `MediaQuery` 猜尺寸：同一 span 在不同列数下宽度完全不同，
/// 时间线折叠/隐藏/调宽也会直接改变主工作区宽度。
@immutable
class CardMetrics {
  const CardMetrics({
    required this.bodyWidth,
    required this.bodyHeight,
  });

  /// 找不到 [_CardMetricsScope] 时的兜底：按 `compact` + `unbounded` 处理，
  /// 确保卡片脱离首页单独排版或单测时不抛异常。
  static const CardMetrics unknown = CardMetrics(
    bodyWidth: 0,
    bodyHeight: null,
  );

  // 按「正文宽」分档。
  static const double widthCompactMax = 220; // < 220 -> compact
  static const double widthWideMin = 380; // >= 380 -> wide

  // 按「正文高」分档；kMinBoardItemHeight(96) 扣除标题 20 + 间距 12 + 内边距 32 ≈ 32。
  static const double heightTightMax = 112; // < 112 -> tight
  static const double heightTallMin = 320; // >= 320 -> tall

  /// 正文可用宽（已扣掉卡片内边距，但不含外壳标题行）。
  final double bodyWidth;

  /// 正文可用高；null 表示自适应（高度由内容决定，约束无界）。
  final double? bodyHeight;

  CardWidthClass get widthClass {
    if (bodyWidth < widthCompactMax) {
      return CardWidthClass.compact;
    }
    if (bodyWidth >= widthWideMin) {
      return CardWidthClass.wide;
    }
    return CardWidthClass.regular;
  }

  CardHeightClass get heightClass {
    final h = bodyHeight;
    if (h == null) {
      return CardHeightClass.unbounded;
    }
    if (h < heightTightMax) {
      return CardHeightClass.tight;
    }
    if (h >= heightTallMin) {
      return CardHeightClass.tall;
    }
    return CardHeightClass.regular;
  }

  static CardMetrics of(BuildContext context) {
    final scope =
        context.dependOnInheritedWidgetOfExactType<_CardMetricsScope>();
    return scope?.metrics ?? unknown;
  }

  static Widget scope({
    Key? key,
    required CardMetrics metrics,
    required Widget child,
  }) {
    return _CardMetricsScope(
      key: key,
      metrics: metrics,
      child: child,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is CardMetrics &&
          bodyWidth == other.bodyWidth &&
          bodyHeight == other.bodyHeight;

  @override
  int get hashCode => Object.hash(bodyWidth, bodyHeight);

  @override
  String toString() => 'CardMetrics(w=$bodyWidth, h=$bodyHeight)';
}

class _CardMetricsScope extends InheritedWidget {
  const _CardMetricsScope({
    super.key,
    required this.metrics,
    required super.child,
  });

  final CardMetrics metrics;

  @override
  bool updateShouldNotify(_CardMetricsScope oldWidget) =>
      metrics != oldWidget.metrics;
}
