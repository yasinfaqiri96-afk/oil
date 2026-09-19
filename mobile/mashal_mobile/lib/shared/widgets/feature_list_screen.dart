import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_colors.dart';
import 'status_chip.dart';

class FeatureEntry {
  const FeatureEntry({
    required this.icon,
    required this.title,
    this.subtitle = '',
    this.path,
    this.onTap,
  });

  final IconData icon;
  final String title;
  final String subtitle;

  /// Route to open. When both [path] and [onTap] are null the entry is shown as not available yet.
  final String? path;
  final VoidCallback? onTap;
}

class FeatureSection {
  const FeatureSection({required this.title, required this.entries});

  final String title;
  final List<FeatureEntry> entries;
}

/// Section menu used by the module tabs until each feature has its own screen.
class FeatureListScreen extends StatelessWidget {
  const FeatureListScreen({super.key, required this.title, required this.sections, this.header});

  final String title;
  final List<FeatureSection> sections;
  final Widget? header;

  @override
  Widget build(BuildContext context) {
    final isRtl = Directionality.of(context) == TextDirection.rtl;

    return Scaffold(
      appBar: AppBar(title: Text(title)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(AppSpacing.lg, AppSpacing.sm, AppSpacing.lg, AppSpacing.xl),
        children: [
          if (header != null) header!,
          for (final section in sections) ...[
            Padding(
              padding: const EdgeInsets.fromLTRB(AppSpacing.xs, AppSpacing.md, AppSpacing.xs, AppSpacing.sm),
              child: Text(section.title, style: Theme.of(context).textTheme.labelLarge),
            ),
            Card(
              clipBehavior: Clip.antiAlias,
              child: Column(
                children: [
                  for (var i = 0; i < section.entries.length; i++) ...[
                    if (i > 0) const Divider(indent: 56),
                    _EntryTile(entry: section.entries[i], isRtl: isRtl),
                  ],
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _EntryTile extends StatelessWidget {
  const _EntryTile({required this.entry, required this.isRtl});

  final FeatureEntry entry;
  final bool isRtl;

  @override
  Widget build(BuildContext context) {
    final path = entry.path;
    final onTap = entry.onTap ?? (path == null ? null : () => context.go(path));

    return ListTile(
      leading: Icon(entry.icon),
      title: Text(entry.title),
      subtitle: entry.subtitle.isEmpty ? null : Text(entry.subtitle),
      trailing: onTap == null
          ? const StatusChip(label: AppStrings.comingSoon)
          : Icon(isRtl ? Icons.chevron_left_rounded : Icons.chevron_right_rounded),
      onTap: onTap,
    );
  }
}
