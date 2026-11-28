import 'package:material_ui/material_ui.dart';

import 'palette.dart';
import 'tokens.dart';

export 'palette.dart';
export 'tokens.dart';

/// 构建基于 [ZentwikPalette] 与 [ZentwikTokens] 的 Material 3 主题。
ThemeData buildZentwikTheme({
  Brightness brightness = Brightness.dark,
  String accent = 'blue',
  double textScale = 1.0,
}) {
  final palette = ZentwikPalette.resolve(
    brightness: brightness,
    accent: accent,
  );
  final onAccent =
      brightness == Brightness.dark ? palette.textPrimary : Colors.white;

  final colorScheme = ColorScheme(
    brightness: brightness,
    primary: palette.accent,
    onPrimary: onAccent,
    secondary: palette.accent,
    onSecondary: onAccent,
    error: palette.danger,
    onError: onAccent,
    surface: palette.surface,
    onSurface: palette.textPrimary,
    onSurfaceVariant: palette.textSecondary,
    outline: palette.border,
  );

  return ThemeData(
    useMaterial3: true,
    brightness: brightness,
    colorScheme: colorScheme,
    scaffoldBackgroundColor: palette.background,
    cardColor: palette.surface,
    dividerColor: palette.border,
    extensions: [palette],
    textTheme: TextTheme(
      titleLarge: TextStyle(
        fontSize: ZentwikTokens.fontTitle * textScale,
        fontWeight: FontWeight.w600,
        color: palette.textPrimary,
      ),
      titleMedium: TextStyle(
        fontSize: ZentwikTokens.fontSubtitle * textScale,
        fontWeight: FontWeight.w600,
        color: palette.textPrimary,
      ),
      bodyMedium: TextStyle(
        fontSize: ZentwikTokens.fontBody * textScale,
        color: palette.textPrimary,
      ),
      bodySmall: TextStyle(
        fontSize: ZentwikTokens.fontCaption * textScale,
        color: palette.textSecondary,
      ),
    ),
  );
}
