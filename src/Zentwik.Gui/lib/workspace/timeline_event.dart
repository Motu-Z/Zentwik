import 'package:material_ui/material_ui.dart';

/// 事件严重度，只影响时间线左侧圆点的颜色与文字语气。
enum TimelineSeverity { info, success, warning, danger }

/// 事件来源分类。分类只用于图标与文案，不参与布局。
enum TimelineCategory {
  connection('连接', Icons.link_outlined),
  module('模块', Icons.extension_outlined),
  setting('设置', Icons.tune_outlined),
  scenario('场景', Icons.auto_awesome_outlined),
  ledger('调度', Icons.receipt_long_outlined),
  system('系统', Icons.info_outline);

  const TimelineCategory(this.label, this.icon);

  final String label;
  final IconData icon;
}

/// 一条时间线事件。
///
/// 刻意保持「一句话 + 可选细节」的形态：时间线只记**发生了什么**，
/// 详细运行记录归独立的日志页。高频传感器采样不允许进这里。
@immutable
class TimelineEvent {
  const TimelineEvent({
    required this.id,
    required this.at,
    required this.category,
    required this.severity,
    required this.title,
    this.detail,
  });

  final int id;
  final DateTime at;
  final TimelineCategory category;
  final TimelineSeverity severity;
  final String title;
  final String? detail;

  /// 去重键：同一类别 + 同一标题相邻出现时只保留一条，
  /// 否则断线重连会把时间线刷成一堵墙。
  String get signature => '${category.name}|$severity|$title|${detail ?? ''}';

  String get timeLabel {
    final h = at.hour.toString().padLeft(2, '0');
    final m = at.minute.toString().padLeft(2, '0');
    final s = at.second.toString().padLeft(2, '0');
    return '$h:$m:$s';
  }
}
