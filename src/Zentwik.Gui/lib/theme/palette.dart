import 'package:material_ui/material_ui.dart';

/// 运行时配色扩展：支持暗色/亮色与四种强调色（blue/violet/teal/amber）。
class ZentwikPalette extends ThemeExtension<ZentwikPalette> {
  const ZentwikPalette({
    required this.background,
    required this.surface,
    required this.border,
    required this.textPrimary,
    required this.textSecondary,
    required this.textTertiary,
    required this.accent,
    required this.danger,
    required this.success,
  });

  factory ZentwikPalette.resolve({
    Brightness brightness = Brightness.dark,
    String accent = 'blue',
  }) {
    final isDark = brightness == Brightness.dark;
    final accentColor = _resolveAccent(isDark: isDark, accent: accent);

    if (isDark) {
      return ZentwikPalette(
        background: const Color(0xFF0E1117),
        surface: const Color(0xFF161B22),
        border: const Color(0xFF2B313B),
        textPrimary: const Color(0xFFE6EDF3),
        textSecondary: const Color(0xFF8B949E),
        textTertiary: const Color(0xFF6E7681),
        accent: accentColor,
        danger: const Color(0xFFF85149),
        success: const Color(0xFF3FB950),
      );
    }

    // 亮色下语义色（danger/success）与高饱和强调色均压暗一档，保证在白底上的可读对比度。
    return ZentwikPalette(
      background: const Color(0xFFFFFFFF),
      surface: const Color(0xFFF6F8FA),
      border: const Color(0xFFD0D7DE),
      textPrimary: const Color(0xFF1F2328),
      textSecondary: const Color(0xFF59636E),
      textTertiary: const Color(0xFF818B98),
      accent: accentColor,
      danger: const Color(0xFFCF222E),
      success: const Color(0xFF1A7F37),
    );
  }

  static Color _resolveAccent({
    required bool isDark,
    required String accent,
  }) {
    if (isDark) {
      return switch (accent) {
        'violet' => const Color(0xFFA371F7),
        'teal' => const Color(0xFF39C5CF),
        'amber' => const Color(0xFFD29922),
        _ => const Color(0xFF4C8DFF),
      };
    }

    // 亮色模式下将 teal 与 amber 压暗，避免浅背景上文字与进度条对比度不足。
    return switch (accent) {
      'violet' => const Color(0xFF8250DF),
      'teal' => const Color(0xFF0E8A94),
      'amber' => const Color(0xFF9A6700),
      _ => const Color(0xFF4C8DFF),
    };
  }

  final Color background;
  final Color surface;
  final Color border;
  final Color textPrimary;
  final Color textSecondary;
  final Color textTertiary;
  final Color accent;
  final Color danger;
  final Color success;

  @override
  ZentwikPalette copyWith({
    Color? background,
    Color? surface,
    Color? border,
    Color? textPrimary,
    Color? textSecondary,
    Color? textTertiary,
    Color? accent,
    Color? danger,
    Color? success,
  }) {
    return ZentwikPalette(
      background: background ?? this.background,
      surface: surface ?? this.surface,
      border: border ?? this.border,
      textPrimary: textPrimary ?? this.textPrimary,
      textSecondary: textSecondary ?? this.textSecondary,
      textTertiary: textTertiary ?? this.textTertiary,
      accent: accent ?? this.accent,
      danger: danger ?? this.danger,
      success: success ?? this.success,
    );
  }

  @override
  ZentwikPalette lerp(ThemeExtension<ZentwikPalette>? other, double t) {
    if (other is! ZentwikPalette) {
      return this;
    }
    return ZentwikPalette(
      background: Color.lerp(background, other.background, t)!,
      surface: Color.lerp(surface, other.surface, t)!,
      border: Color.lerp(border, other.border, t)!,
      textPrimary: Color.lerp(textPrimary, other.textPrimary, t)!,
      textSecondary: Color.lerp(textSecondary, other.textSecondary, t)!,
      textTertiary: Color.lerp(textTertiary, other.textTertiary, t)!,
      accent: Color.lerp(accent, other.accent, t)!,
      danger: Color.lerp(danger, other.danger, t)!,
      success: Color.lerp(success, other.success, t)!,
    );
  }
}

/// 便捷从当前 [BuildContext] 读取 [ZentwikPalette]。
extension ZentwikThemeContext on BuildContext {
  ZentwikPalette get palette {
    final ext = Theme.of(this).extension<ZentwikPalette>();
    if (ext == null) {
      throw StateError('ZentwikPalette 未找到：widget 跑在 buildZentwikTheme 之外');
    }
    return ext;
  }
}
