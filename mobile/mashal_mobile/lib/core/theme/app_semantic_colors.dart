import 'package:flutter/material.dart';

import 'app_colors.dart';

@immutable
class AppSemanticColors extends ThemeExtension<AppSemanticColors> {
  const AppSemanticColors({
    required this.success,
    required this.warning,
    required this.error,
    required this.info,
    required this.textMuted,
    required this.divider,
    required this.neutralSurface,
  });

  static const light = AppSemanticColors(
    success: AppColors.success,
    warning: AppColors.warning,
    error: AppColors.error,
    info: AppColors.info,
    textMuted: AppColors.textSecondary,
    divider: AppColors.divider,
    neutralSurface: AppColors.neutralSurface,
  );

  static const dark = AppSemanticColors(
    success: AppColors.darkSuccess,
    warning: AppColors.darkWarning,
    error: AppColors.darkError,
    info: AppColors.darkInfo,
    textMuted: AppColors.darkTextSecondary,
    divider: AppColors.darkDivider,
    neutralSurface: AppColors.darkNeutralSurface,
  );

  final Color success;
  final Color warning;
  final Color error;
  final Color info;
  final Color textMuted;
  final Color divider;
  final Color neutralSurface;

  @override
  AppSemanticColors copyWith({
    Color? success,
    Color? warning,
    Color? error,
    Color? info,
    Color? textMuted,
    Color? divider,
    Color? neutralSurface,
  }) {
    return AppSemanticColors(
      success: success ?? this.success,
      warning: warning ?? this.warning,
      error: error ?? this.error,
      info: info ?? this.info,
      textMuted: textMuted ?? this.textMuted,
      divider: divider ?? this.divider,
      neutralSurface: neutralSurface ?? this.neutralSurface,
    );
  }

  @override
  AppSemanticColors lerp(ThemeExtension<AppSemanticColors>? other, double t) {
    if (other is! AppSemanticColors) return this;
    return AppSemanticColors(
      success: Color.lerp(success, other.success, t)!,
      warning: Color.lerp(warning, other.warning, t)!,
      error: Color.lerp(error, other.error, t)!,
      info: Color.lerp(info, other.info, t)!,
      textMuted: Color.lerp(textMuted, other.textMuted, t)!,
      divider: Color.lerp(divider, other.divider, t)!,
      neutralSurface: Color.lerp(neutralSurface, other.neutralSurface, t)!,
    );
  }
}

extension SemanticColorsContext on BuildContext {
  AppSemanticColors get semantic => Theme.of(this).extension<AppSemanticColors>()!;
}
