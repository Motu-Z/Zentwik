/// Zentwik 尺寸与排版 Token（颜色已迁至 `ZentwikPalette`）。
abstract final class ZentwikTokens {
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

  // 字号（28 / 20 / 15 / 13 / 12）
  /// 单值卡在 wide / tall 档用的主数值字号。
  /// 与 fontTitle(20) 保持可分辨差距（refactoring-ui §4.1：相邻档差 <15% 肉眼分不出）。
  static const double fontDisplay = 28.0;
  static const double fontTitle = 20.0;
  static const double fontSubtitle = 15.0;
  static const double fontBody = 13.0;
  static const double fontCaption = 12.0;
}
