import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../app/router.dart';
import '../../../core/l10n/app_strings.dart';
import '../../../core/theme/app_colors.dart';
import '../../../shared/widgets/feature_list_screen.dart';
import '../../auth/application/auth_controller.dart';
import '../../auth/domain/mobile_user.dart';

class MoreScreen extends ConsumerWidget {
  const MoreScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final user = ref.watch(currentUserProvider);

    return FeatureListScreen(
      title: AppStrings.navMore,
      header: user == null ? null : _ProfileCard(user: user),
      sections: [
        const FeatureSection(title: AppStrings.inbox, entries: [
          FeatureEntry(
            icon: Icons.task_alt_rounded,
            title: AppStrings.approvals,
            subtitle: AppStrings.approvalsSubtitle,
            path: AppRoutes.approvals,
          ),
          FeatureEntry(
            icon: Icons.notifications_none_rounded,
            title: AppStrings.notifications,
            subtitle: AppStrings.notificationsSubtitle,
            path: AppRoutes.notifications,
          ),
        ]),
        FeatureSection(title: AppStrings.account, entries: [
          FeatureEntry(
            icon: Icons.logout_rounded,
            title: AppStrings.signOut,
            onTap: () => _confirmSignOut(context, ref),
          ),
        ]),
      ],
    );
  }

  Future<void> _confirmSignOut(BuildContext context, WidgetRef ref) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text(AppStrings.signOut),
        content: const Text(AppStrings.signOutConfirm),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text(AppStrings.cancel),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text(AppStrings.signOut),
          ),
        ],
      ),
    );

    if (confirmed == true) {
      await ref.read(authControllerProvider.notifier).logout();
    }
  }
}

class _ProfileCard extends StatelessWidget {
  const _ProfileCard({required this.user});

  final MobileUser user;

  @override
  Widget build(BuildContext context) {
    final textTheme = Theme.of(context).textTheme;
    final company = user.company;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.lg),
        child: Row(
          children: [
            CircleAvatar(
              radius: 22,
              child: Text(user.displayName.isEmpty ? '?' : user.displayName.characters.first),
            ),
            const SizedBox(width: AppSpacing.md),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(user.displayName, style: textTheme.titleMedium),
                  const SizedBox(height: 2),
                  Text(
                    company == null ? user.role : '${user.role} · ${company.displayName}',
                    style: textTheme.bodySmall,
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
