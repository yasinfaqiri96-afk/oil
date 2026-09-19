import 'package:flutter/material.dart';

import '../../core/theme/app_colors.dart';

class SectionCard extends StatelessWidget {
  const SectionCard({super.key, required this.title, required this.children, this.trailing});

  final String title;
  final List<Widget> children;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(AppSpacing.lg, AppSpacing.md, AppSpacing.lg, AppSpacing.sm),
            child: Row(
              children: [
                Expanded(child: Text(title, style: Theme.of(context).textTheme.titleMedium)),
                if (trailing != null) trailing!,
              ],
            ),
          ),
          const Divider(),
          ...children,
        ],
      ),
    );
  }
}
