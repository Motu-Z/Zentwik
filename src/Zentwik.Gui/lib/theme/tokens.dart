import 'package:material_ui/material_ui.dart';

/// Zentwik 暗色主题设计 Token，所有颜色、间距、圆角与字号统一由此派生。
abstract final class ZentwikTokens {
  // 颜色（暗色优先）
  static const Color background = Color(0xFF0E1117);
  static const Color surface = Color(0xFF161B22);
  static const Color border = Color(0xFF2B313B);
  static const Color textPrimary = Color(0xFFE6EDF3);
  static const Color textSecondary = Color(0xFF8B949E);
  /// 第三级文字：说明、补充细节。比 [textSecondary] 再暗一档，用于不与主信息抢注意力。
  static const Color textTertiary = Color(0xFF6E7681);
  static const Color accent = Color(0xFF4C8DFF);
  static const Color danger = Color(0xFFF85149);
  static const Color success = Color(0xFF3FB950);

  // 间距（4 / 8 / 12 / 16 / 24）
  static const double spaceXs = 4.0;
  static const double spaceSm = 8.0;
  static const double spaceMd = 12.0;
  static const double spaceLg = 16.0;
  static const double spaceXl = 24.0;

  // 圆角（6 / 10 / 14）
  static const double radiusSm = 6.0;
  static const double radiusMd = 10.0;
  static const double radiusLg = 14.0;

  // 字号（20 / 15 / 13 / 12）
  static const double fontTitle = 20.0;
  static const double fontSubtitle = 15.0;
  static const double fontBody = 13.0;
  static const double fontCaption = 12.0;
}
