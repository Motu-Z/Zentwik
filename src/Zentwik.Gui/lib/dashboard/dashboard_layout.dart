import 'dashboard_kind.dart';

const int kMaxDashboardCards = 12;

class DashboardCard {
  const DashboardCard({
    required this.kind,
    required this.span,
    this.visible = true,
  });

  final DashboardKind kind;
  final int span;
  final bool visible;

  DashboardCard copyWith({int? span, bool? visible}) {
    final nextSpan = span == null
        ? this.span
        : span.clamp(kind.minSpan, kind.maxSpan).toInt();
    return DashboardCard(
      kind: kind,
      span: nextSpan,
      visible: visible ?? this.visible,
    );
  }

  Map<String, Object?> toJson() => <String, Object?>{
        'kind': kind.wire,
        'span': span,
        'visible': visible,
      };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is DashboardCard &&
          kind == other.kind &&
          span == other.span &&
          visible == other.visible;

  @override
  int get hashCode => Object.hash(kind, span, visible);
}

class DashboardLayout {
  const DashboardLayout(this.cards);

  factory DashboardLayout.normalize(Iterable<DashboardCard> input) {
    final seen = <DashboardKind>{};
    final normalized = <DashboardCard>[];

    for (final card in input) {
      final clampedSpan =
          card.span.clamp(card.kind.minSpan, card.kind.maxSpan).toInt();
      if (!seen.add(card.kind)) {
        continue;
      }
      normalized.add(
        DashboardCard(
          kind: card.kind,
          span: clampedSpan,
          visible: card.visible,
        ),
      );
      if (normalized.length >= kMaxDashboardCards) {
        break;
      }
    }

    if (normalized.isEmpty) {
      return defaults;
    }
    return DashboardLayout(List.unmodifiable(normalized));
  }

  factory DashboardLayout.fromJson(Object? json) {
    if (json is! Map<String, Object?>) {
      return defaults;
    }
    final rawCards = json['cards'];
    if (rawCards is! List) {
      return defaults;
    }

    final parsed = <DashboardCard>[];
    for (final item in rawCards) {
      if (item is! Map<String, Object?>) {
        return defaults;
      }
      final rawKind = item['kind'];
      if (rawKind is! String) {
        continue;
      }
      final kind = DashboardKind.tryParse(rawKind);
      if (kind == null) {
        continue;
      }
      final rawSpan = item['span'];
      final span = rawSpan is num ? rawSpan.toInt() : kind.defaultSpan;
      final rawVisible = item['visible'];
      final visible = rawVisible is bool ? rawVisible : true;
      parsed.add(
        DashboardCard(
          kind: kind,
          span: span,
          visible: visible,
        ),
      );
    }

    return DashboardLayout.normalize(parsed);
  }

  final List<DashboardCard> cards;

  static DashboardLayout get defaults => DashboardLayout(
        List.unmodifiable(
          DashboardKind.values
              .map(
                (kind) => DashboardCard(
                  kind: kind,
                  span: kind.defaultSpan,
                ),
              )
              .toList(growable: false),
        ),
      );

  DashboardLayout resize(DashboardKind kind, int span) {
    final index = cards.indexWhere((card) => card.kind == kind);
    if (index < 0) {
      return this;
    }
    final updated = List<DashboardCard>.of(cards);
    updated[index] = updated[index].copyWith(span: span);
    return DashboardLayout(List.unmodifiable(updated));
  }

  DashboardLayout reorder(int from, int to) {
    if (from < 0 ||
        from >= cards.length ||
        to < 0 ||
        to >= cards.length ||
        from == to) {
      return this;
    }
    final updated = List<DashboardCard>.of(cards);
    final moved = updated.removeAt(from);
    updated.insert(to, moved);
    return DashboardLayout(List.unmodifiable(updated));
  }

  DashboardLayout remove(DashboardKind kind) {
    final next = cards.where((card) => card.kind != kind).toList(growable: false);
    if (next.length == cards.length) {
      return this;
    }
    if (next.isEmpty) {
      return defaults;
    }
    return DashboardLayout(List.unmodifiable(next));
  }

  DashboardLayout add(DashboardKind kind) {
    if (cards.any((card) => card.kind == kind) ||
        cards.length >= kMaxDashboardCards) {
      return this;
    }
    return DashboardLayout(
      List.unmodifiable([
        ...cards,
        DashboardCard(kind: kind, span: kind.defaultSpan),
      ]),
    );
  }

  DashboardLayout setVisible(DashboardKind kind, bool visible) {
    final index = cards.indexWhere((card) => card.kind == kind);
    if (index < 0) {
      return this;
    }
    final updated = List<DashboardCard>.of(cards);
    updated[index] = updated[index].copyWith(visible: visible);
    return DashboardLayout(List.unmodifiable(updated));
  }

  Map<String, Object?> toJson() => <String, Object?>{
        'version': 1,
        'cards': cards.map((card) => card.toJson()).toList(growable: false),
      };

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) return true;
    if (other is! DashboardLayout || other.cards.length != cards.length) {
      return false;
    }
    for (var i = 0; i < cards.length; i++) {
      if (cards[i] != other.cards[i]) {
        return false;
      }
    }
    return true;
  }

  @override
  int get hashCode => Object.hashAll(cards);
}
