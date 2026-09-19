import 'package:flutter/material.dart';

/// Mirrors the canonical web tokens in `design-system/MASTER.md`.
abstract final class AppColors {
  static const primary = Color(0xFF173F73);
  static const primaryDark = Color(0xFF123258);
  static const action = Color(0xFF1877F2);
  static const canvas = Color(0xFFFCFCFC);
  static const paper = Color(0xFFFFFFFF);
  static const neutralSurface = Color(0xFFF5F7FA);
  static const textPrimary = Color(0xFF424242);
  static const textSecondary = Color(0xFF666B75);
  static const divider = Color(0xFFE5E7EB);
  static const success = Color(0xFF6EA152);
  static const warning = Color(0xFFF59E0B);
  static const error = Color(0xFFCC0000);
  static const info = Color(0xFF006EA6);

  // Dark mode (mobile only; calm, low-contrast surfaces).
  static const darkPrimary = Color(0xFF8DB2E3);
  static const darkAction = Color(0xFF5B9DF5);
  static const darkCanvas = Color(0xFF0F141B);
  static const darkPaper = Color(0xFF161D26);
  static const darkNeutralSurface = Color(0xFF1C2530);
  static const darkTextPrimary = Color(0xFFE4E7EB);
  static const darkTextSecondary = Color(0xFF9BA4AF);
  static const darkDivider = Color(0xFF263241);
  static const darkSuccess = Color(0xFF8BBF6E);
  static const darkWarning = Color(0xFFF7B23B);
  static const darkError = Color(0xFFFF6B6B);
  static const darkInfo = Color(0xFF4FA8D6);
}

abstract final class AppSpacing {
  static const double xs = 4;
  static const double sm = 8;
  static const double md = 12;
  static const double lg = 16;
  static const double xl = 24;
  static const double radius = 12;
  static const double radiusSm = 8;
}
