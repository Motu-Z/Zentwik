class DashboardCell {
  const DashboardCell({
    required this.column,
    required this.span,
  });

  final int column;
  final int span;

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is DashboardCell &&
          column == other.column &&
          span == other.span;

  @override
  int get hashCode => Object.hash(column, span);
}

List<List<DashboardCell>> packIntoRows(List<int> spans, int columns) {
  if (columns <= 0 || spans.isEmpty) {
    return const [];
  }

  final rows = <List<DashboardCell>>[];
  var currentRow = <DashboardCell>[];
  var used = 0;

  for (final rawSpan in spans) {
    final span = rawSpan > columns ? columns : (rawSpan < 1 ? 1 : rawSpan);
    if (used + span > columns && currentRow.isNotEmpty) {
      rows.add(List.unmodifiable(currentRow));
      currentRow = <DashboardCell>[];
      used = 0;
    }
    currentRow.add(DashboardCell(column: used, span: span));
    used += span;
  }

  if (currentRow.isNotEmpty) {
    rows.add(List.unmodifiable(currentRow));
  }
  return List.unmodifiable(rows);
}
