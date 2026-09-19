import 'package:flutter/material.dart';

import '../../core/l10n/app_strings.dart';
import '../../core/network/api_exception.dart';
import '../../core/theme/app_colors.dart';
import '../../core/theme/app_semantic_colors.dart';

/// Full-screen error with a retry action; offline and no-access get their own icon.
class ErrorStateView extends StatelessWidget {
  const ErrorStateView({super.key, required this.error, required this.onRetry});

  final Object error;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final failure = error;
    final apiError = failure is ApiException ? failure : null;
    final kind = apiError?.kind ?? ApiErrorKind.unknown;

    final icon = switch (kind) {
      ApiErrorKind.network => Icons.cloud_off_rounded,
      ApiErrorKind.forbidden => Icons.lock_outline_rounded,
      _ => Icons.error_outline_rounded,
    };

    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(AppSpacing.xl),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 40, color: context.semantic.textMuted),
            const SizedBox(height: AppSpacing.md),
            Text(apiError?.message ?? AppStrings.genericError, textAlign: TextAlign.center),
            if (kind != ApiErrorKind.forbidden) ...[
              const SizedBox(height: AppSpacing.lg),
              FilledButton(onPressed: onRetry, child: const Text(AppStrings.retry)),
            ],
          ],
        ),
      ),
    );
  }
}
