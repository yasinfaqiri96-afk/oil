import 'package:flutter/material.dart';

import '../../core/theme/app_colors.dart';
import '../../core/theme/app_semantic_colors.dart';

class KpiCard extends StatelessWidget {
  const KpiCard({
    super.key,
    required this.label,
    required this.value,
    required this.icon,
    required this.caption,
    this.unit,
    this.status,
  });

  final String label;
  final String value;
  final IconData icon;
  final String caption;
  final String? unit;
  final Widget? status;

  @override
  Widget build(BuildContext context) {
    final textTheme = Theme.of(context).textTheme;
    final muted = context.semantic.textMuted;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.lg),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                Icon(icon, size: 18, color: muted),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Text(label, maxLines: 1, overflow: TextOverflow.ellipsis, style: textTheme.labelLarge),
                ),
              ],
            ),
            const SizedBox(height: AppSpacing.md),
            Row(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Flexible(
                  child: FittedBox(
                    fit: BoxFit.scaleDown,
                    alignment: AlignmentDirectional.centerStart,
                    // Numbers stay LTR so signs and separators read correctly inside RTL.
                    child: Text(
                      value,
                      textDirection: TextDirection.ltr,
                      maxLines: 1,
                      style: textTheme.headlineSmall?.copyWith(
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ),
                ),
                if (unit != null) ...[
                  const SizedBox(width: AppSpacing.xs),
                  Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: Text(unit!, style: textTheme.labelMedium),
                  ),
                ],
              ],
            ),
            const SizedBox(height: AppSpacing.xs),
            Row(
              children: [
                Expanded(
                  child: Text(caption, maxLines: 1, overflow: TextOverflow.ellipsis, style: textTheme.bodySmall),
                ),
                if (status != null) status!,
              ],
            ),
          ],
        ),
      ),
    );
  }
}
