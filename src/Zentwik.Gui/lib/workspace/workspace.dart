import 'dart:async';

import 'package:flutter/foundation.dart';

import '../dashboard/board.dart';
import '../dashboard/dashboard_kind.dart';
import 'timeline_event.dart';
import 'workspace_store.dart';

/// 首页工作区状态：主工作区布局 + 全局时间线的视图状态。
///
/// 唯一一个可变状态源，界面只读它、只通过它改 —— 这样「拖完就走」
/// 才不会出现布局与时间线各存各的、重启后对不上的情况。
class Workspace extends ChangeNotifier {
  Workspace({
    WorkspaceStore? store,
    DateTime Function()? clock,
  })  : _store = store ?? FileWorkspaceStore(),
        _clock = clock ?? DateTime.now;

  /// 时间线宽度下限：再窄连 `12:34:56` 加标题都放不下。
  static const double kTimelineMinWidth = 260;
  static const double kTimelineMaxWidth = 520;
  static const double kTimelineDefaultWidth = 320;

  /// 时间线保留的事件条数上限；更早的交给日志页。
  static const int kMaxTimelineEvents = 120;

  final WorkspaceStore _store;
  final DateTime Function() _clock;

  DashboardBoard _board = DashboardBoard.defaults();
  final List<TimelineEvent> _events = <TimelineEvent>[];
  double _timelineWidth = kTimelineDefaultWidth;
  bool _timelineCollapsed = false;
  bool _timelineHidden = false;
  bool _editing = false;
  bool _loaded = false;
  int _nextEventId = 0;
  Timer? _saveTimer;

  DashboardBoard get board => _board;
  List<TimelineEvent> get events => List.unmodifiable(_events);
  double get timelineWidth => _timelineWidth;
  bool get timelineCollapsed => _timelineCollapsed;
  bool get timelineHidden => _timelineHidden;
  bool get editing => _editing;

  /// 时间线此刻是否真的占据宽度。
  bool get timelineVisible => !_timelineHidden;

  /// 折起来后只是一条窄轨；隐藏才是完全让位。
  double get timelineOccupiedWidth {
    if (_timelineHidden) {
      return 0;
    }
    return _timelineCollapsed ? kTimelineCollapsedWidth : _timelineWidth;
  }

  static const double kTimelineCollapsedWidth = 44;

  /// 从磁盘恢复。读不到或数据损坏时保留默认布局。
  Future<void> load() async {
    final raw = await _store.read();
    if (raw != null) {
      final restored = DashboardBoard.fromJson(raw['board']);
      if (restored != null) {
        _board = restored;
      }
      final timeline = raw['timeline'];
      if (timeline is Map) {
        final width = timeline['width'];
        if (width is num) {
          _timelineWidth = _clampWidth(width.toDouble());
        }
        final collapsed = timeline['collapsed'];
        if (collapsed is bool) {
          _timelineCollapsed = collapsed;
        }
        final hidden = timeline['hidden'];
        if (hidden is bool) {
          _timelineHidden = hidden;
        }
      }
    }
    _loaded = true;
    notifyListeners();
  }

  // ── 主工作区 ────────────────────────────────────────────────

  void setEditing(bool value) {
    if (_editing == value) {
      return;
    }
    _editing = value;
    notifyListeners();
  }

  void add(DashboardKind kind) {
    final next = _board.add(kind);
    if (identical(next, _board)) {
      return;
    }
    _board = next;
    _commit();
  }

  void remove(DashboardKind kind) {
    final next = _board.remove(kind);
    if (identical(next, _board)) {
      return;
    }
    _board = next;
    _commit();
  }

  void move(int from, int to) {
    final next = _board.move(from, to);
    if (identical(next, _board)) {
      return;
    }
    _board = next;
    _commit();
  }

  void resizeSpan(DashboardKind kind, int span, {int? columns}) {
    final next = _board.resizeSpan(kind, span, columns: columns);
    if (identical(next, _board)) {
      return;
    }
    _board = next;
    _commit();
  }

  void setHeight(DashboardKind kind, double? height) {
    final next = _board.setHeight(kind, height);
    if (identical(next, _board)) {
      return;
    }
    _board = next;
    _commit();
  }

  void clearHeights() {
    final next = _board.clearHeights();
    if (identical(next, _board)) {
      return;
    }
    _board = next;
    _commit();
  }

  void resetBoard() {
    _board = DashboardBoard.defaults();
    record(
      category: TimelineCategory.setting,
      severity: TimelineSeverity.info,
      title: '首页布局已恢复默认',
    );
  }

  // ── 时间线 ──────────────────────────────────────────────────

  void record({
    required TimelineCategory category,
    required TimelineSeverity severity,
    required String title,
    String? detail,
    DateTime? at,
  }) {
    final signature = '${category.name}|$severity|$title|${detail ?? ''}';
    // 相邻重复不入库：断线重连会连着抛同样几条，刷屏比没记录更糟。
    if (_events.isNotEmpty && _events.first.signature == signature) {
      return;
    }
    _events.insert(
      0,
      TimelineEvent(
        id: _nextEventId++,
        at: at ?? _clock(),
        category: category,
        severity: severity,
        title: title,
        detail: detail,
      ),
    );
    if (_events.length > kMaxTimelineEvents) {
      _events.removeRange(kMaxTimelineEvents, _events.length);
    }
    _commit();
  }

  void setTimelineWidth(double width) {
    final next = _clampWidth(width);
    if (next == _timelineWidth) {
      return;
    }
    _timelineWidth = next;
    if (_timelineCollapsed) {
      _timelineCollapsed = false;
    }
    _commit();
  }

  void toggleTimelineCollapsed() {
    _timelineCollapsed = !_timelineCollapsed;
    _commit();
  }

  void toggleTimelineHidden() {
    _timelineHidden = !_timelineHidden;
    if (!_timelineHidden && _timelineCollapsed && _timelineWidth <= 0) {
      _timelineWidth = kTimelineDefaultWidth;
    }
    _commit();
  }

  double _clampWidth(double width) {
    if (width < kTimelineMinWidth) {
      return kTimelineMinWidth;
    }
    if (width > kTimelineMaxWidth) {
      return kTimelineMaxWidth;
    }
    return width;
  }

  // ── 持久化 ──────────────────────────────────────────────────

  void _commit() {
    notifyListeners();
    _scheduleSave();
  }

  void _scheduleSave() {
    if (!_loaded) {
      // 还没读完就写会拿默认布局覆盖用户的真实布局。
      return;
    }
    _saveTimer?.cancel();
    // 拖拽缩放每帧都在改，攒一下再落盘。
    _saveTimer = Timer(const Duration(milliseconds: 400), () {
      unawaited(_store.write(toJson()));
    });
  }

  Map<String, Object?> toJson() => <String, Object?>{
        'version': 1,
        'board': _board.toJson(),
        'timeline': <String, Object?>{
          'width': _timelineWidth,
          'collapsed': _timelineCollapsed,
          'hidden': _timelineHidden,
        },
      };

  @override
  void dispose() {
    _saveTimer?.cancel();
    super.dispose();
  }
}
