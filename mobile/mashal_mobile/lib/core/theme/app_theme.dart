import 'package:flutter/material.dart';

import 'app_colors.dart';
import 'app_semantic_colors.dart';

abstract final class AppTheme {
  static const fontFamily = 'Vazirmatn';

  static ThemeData light() => _build(
        brightness: Brightness.light,
        canvas: AppColors.canvas,
        semantic: AppSemanticColors.light,
        scheme: ColorScheme.fromSeed(
          seedColor: AppColors.primary,
          brightness: Brightness.light,
        ).copyWith(
          primary: AppColors.primary,
          onPrimary: Colors.white,
          secondary: AppColors.action,
          onSecondary: Colors.white,
          error: AppColors.error,
          surface: AppColors.paper,
          onSurface: AppColors.textPrimary,
          onSurfaceVariant: AppColors.textSecondary,
          outline: AppColors.divider,
          outlineVariant: AppColors.divider,
        ),
      );

  static ThemeData dark() => _build(
        brightness: Brightness.dark,
        canvas: AppColors.darkCanvas,
        semantic: AppSemanticColors.dark,
        scheme: ColorScheme.fromSeed(
          seedColor: AppColors.primary,
          brightness: Brightness.dark,
        ).copyWith(
          primary: AppColors.darkPrimary,
          onPrimary: AppColors.darkCanvas,
          secondary: AppColors.darkAction,
          onSecondary: AppColors.darkCanvas,
          error: AppColors.darkError,
          surface: AppColors.darkPaper,
          onSurface: AppColors.darkTextPrimary,
          onSurfaceVariant: AppColors.darkTextSecondary,
          outline: AppColors.darkDivider,
          outlineVariant: AppColors.darkDivider,
        ),
      );

  static ThemeData _build({
    required Brightness brightness,
    required ColorScheme scheme,
    required Color canvas,
    required AppSemanticColors semantic,
  }) {
    final base = ThemeData(
      useMaterial3: true,
      brightness: brightness,
      colorScheme: scheme,
      fontFamily: fontFamily,
      scaffoldBackgroundColor: canvas,
      visualDensity: VisualDensity.standard,
    );
    final text = base.textTheme.apply(
      bodyColor: scheme.onSurface,
      displayColor: scheme.onSurface,
      fontFamily: fontFamily,
    );

    WidgetStateProperty<T> bySelection<T>(T selected, T idle) =>
        WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? selected : idle,
        );

    return base.copyWith(
      textTheme: text.copyWith(
        headlineSmall: text.headlineSmall?.copyWith(fontSize: 22, fontWeight: FontWeight.w700),
        titleLarge: text.titleLarge?.copyWith(fontSize: 20, fontWeight: FontWeight.w700),
        titleMedium: text.titleMedium?.copyWith(fontSize: 16, fontWeight: FontWeight.w700),
        bodyMedium: text.bodyMedium?.copyWith(fontSize: 14, height: 1.5),
        bodySmall: text.bodySmall?.copyWith(fontSize: 12, color: semantic.textMuted),
        labelLarge: text.labelLarge?.copyWith(fontSize: 13, color: semantic.textMuted),
        labelMedium: text.labelMedium?.copyWith(fontSize: 12, color: semantic.textMuted),
      ),
      appBarTheme: AppBarTheme(
        backgroundColor: canvas,
        foregroundColor: scheme.onSurface,
        elevation: 0,
        scrolledUnderElevation: 0,
        surfaceTintColor: Colors.transparent,
        centerTitle: false,
        titleTextStyle: text.titleLarge?.copyWith(fontSize: 20, fontWeight: FontWeight.w700),
      ),
      cardTheme: CardThemeData(
        color: scheme.surface,
        elevation: 0,
        margin: EdgeInsets.zero,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AppSpacing.radius),
          side: BorderSide(color: semantic.divider),
        ),
      ),
      dividerTheme: DividerThemeData(color: semantic.divider, thickness: 1, space: 1),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: scheme.surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        height: 68,
        indicatorColor: scheme.primary.withValues(alpha: 0.10),
        labelTextStyle: bySelection(
          TextStyle(fontFamily: fontFamily, fontSize: 12, fontWeight: FontWeight.w700, color: scheme.primary),
          TextStyle(fontFamily: fontFamily, fontSize: 12, fontWeight: FontWeight.w500, color: semantic.textMuted),
        ),
        iconTheme: bySelection(
          IconThemeData(size: 24, color: scheme.primary),
          IconThemeData(size: 24, color: semantic.textMuted),
        ),
      ),
      navigationRailTheme: NavigationRailThemeData(
        backgroundColor: scheme.surface,
        indicatorColor: scheme.primary.withValues(alpha: 0.10),
        selectedIconTheme: IconThemeData(color: scheme.primary),
        unselectedIconTheme: IconThemeData(color: semantic.textMuted),
        selectedLabelTextStyle: TextStyle(fontFamily: fontFamily, fontSize: 12, fontWeight: FontWeight.w700, color: scheme.primary),
        unselectedLabelTextStyle: TextStyle(fontFamily: fontFamily, fontSize: 12, color: semantic.textMuted),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: scheme.surface,
        contentPadding: const EdgeInsets.symmetric(horizontal: AppSpacing.lg, vertical: AppSpacing.md),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(AppSpacing.radiusSm),
          borderSide: BorderSide(color: semantic.divider),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(AppSpacing.radiusSm),
          borderSide: BorderSide(color: semantic.divider),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(AppSpacing.radiusSm),
          borderSide: BorderSide(color: scheme.primary, width: 1.5),
        ),
      ),
      listTileTheme: ListTileThemeData(
        iconColor: semantic.textMuted,
        contentPadding: const EdgeInsets.symmetric(horizontal: AppSpacing.lg),
        titleTextStyle: text.bodyMedium?.copyWith(fontSize: 15, fontWeight: FontWeight.w600),
        subtitleTextStyle: text.bodySmall?.copyWith(color: semantic.textMuted),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: scheme.secondary,
          foregroundColor: scheme.onSecondary,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(AppSpacing.radiusSm)),
        ),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: scheme.primary,
        linearTrackColor: semantic.neutralSurface,
      ),
      extensions: [semantic],
    );
  }
}
