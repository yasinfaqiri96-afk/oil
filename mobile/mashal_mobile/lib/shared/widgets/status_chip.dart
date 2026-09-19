import 'package:flutter/material.dart';

import '../../core/theme/app_colors.dart';
import '../../core/theme/app_semantic_colors.dart';

enum StatusTone { neutral, info, success, warning, critical }

class StatusChip extends StatelessWidget {
  const StatusChip({super.key, required this.label, this.tone = StatusTone.neutral});

  final String label;
  final StatusTone tone;

  @override
  Widget build(BuildContext context) {
    final colors = context.semantic;
    final color = switch (tone) {
      StatusTone.neutral => colors.textMuted,
      StatusTone.info => colors.info,
      StatusTone.success => colors.success,
      StatusTone.warning => colors.warning,
      StatusTone.critical => colors.error,
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: AppSpacing.sm, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(
        label,
        maxLines: 1,
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
              color: color,
              fontWeight: FontWeight.w700,
            ),
      ),
    );
  }
}
