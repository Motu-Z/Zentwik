import 'package:material_ui/material_ui.dart';

import 'tokens.dart';

export 'tokens.dart';

/// 构建基于 [ZentwikTokens] 的暗色 Material 3 主题。
ThemeData buildZentwikTheme() {
  const colorScheme = ColorScheme.dark(
    primary: ZentwikTokens.accent,
    onPrimary: ZentwikTokens.textPrimary,
    secondary: ZentwikTokens.accent,
    onSecondary: ZentwikTokens.textPrimary,
    surface: ZentwikTokens.surface,
    onSurface: ZentwikTokens.textPrimary,
    onSurfaceVariant: ZentwikTokens.textSecondary,
    error: ZentwikTokens.danger,
    onError: ZentwikTokens.textPrimary,
    outline: ZentwikTokens.border,
  );

  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    colorScheme: colorScheme,
    scaffoldBackgroundColor: ZentwikTokens.background,
    cardColor: ZentwikTokens.surface,
    dividerColor: ZentwikTokens.border,
    textTheme: const TextTheme(
      titleLarge: TextStyle(
        fontSize: ZentwikTokens.fontTitle,
        fontWeight: FontWeight.w600,
        color: ZentwikTokens.textPrimary,
      ),
      titleMedium: TextStyle(
        fontSize: ZentwikTokens.fontSubtitle,
        fontWeight: FontWeight.w600,
        color: ZentwikTokens.textPrimary,
      ),
      bodyMedium: TextStyle(
        fontSize: ZentwikTokens.fontBody,
        color: ZentwikTokens.textPrimary,
      ),
      bodySmall: TextStyle(
        fontSize: ZentwikTokens.fontCaption,
        color: ZentwikTokens.textSecondary,
      ),
    ),
  );
}
