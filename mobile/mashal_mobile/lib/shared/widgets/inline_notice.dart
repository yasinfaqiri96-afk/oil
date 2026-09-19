import 'package:flutter/material.dart';

import '../../core/theme/app_colors.dart';
import '../../core/theme/app_semantic_colors.dart';
import 'status_chip.dart';

class InlineNotice extends StatelessWidget {
  const InlineNotice({super.key, required this.message, this.tone = StatusTone.info, this.icon});

  final String message;
  final StatusTone tone;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final colors = context.semantic;
    final (color, defaultIcon) = switch (tone) {
      StatusTone.neutral => (colors.textMuted, Icons.info_outline_rounded),
      StatusTone.info => (colors.info, Icons.info_outline_rounded),
      StatusTone.success => (colors.success, Icons.check_circle_outline_rounded),
      StatusTone.warning => (colors.warning, Icons.warning_amber_rounded),
      StatusTone.critical => (colors.error, Icons.error_outline_rounded),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: AppSpacing.md, vertical: AppSpacing.sm),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(AppSpacing.radiusSm),
        border: Border.all(color: color.withValues(alpha: 0.25)),
      ),
      child: Row(
        children: [
          Icon(icon ?? defaultIcon, size: 18, color: color),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Text(
              message,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: color),
            ),
          ),
        ],
      ),
    );
  }
}
