import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/l10n/app_strings.dart';
import '../../../shared/widgets/feature_list_screen.dart';
import '../../../shared/widgets/placeholder_screen.dart';
import '../../auth/application/auth_controller.dart';

class FinanceScreen extends ConsumerWidget {
  const FinanceScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final capabilities = ref.watch(currentUserProvider)?.capabilities;
    if (capabilities == null || (!capabilities.finance && !capabilities.reports)) {
      return const PlaceholderScreen(
        title: AppStrings.navFinance,
        message: AppStrings.noAccessSection,
        icon: Icons.lock_outline_rounded,
      );
    }

    return FeatureListScreen(
      title: AppStrings.navFinance,
      sections: [
        FeatureSection(title: AppStrings.finance, entries: [
          if (capabilities.reports)
            const FeatureEntry(icon: Icons.request_quote_outlined, title: AppStrings.receivables, subtitle: AppStrings.receivablesSubtitle),
          if (capabilities.finance) ...const [
            FeatureEntry(icon: Icons.account_balance_outlined, title: AppStrings.cashPosition, subtitle: AppStrings.cashPositionSubtitle),
            FeatureEntry(icon: Icons.swap_horiz_rounded, title: AppStrings.payments, subtitle: AppStrings.paymentsSubtitle),
          ],
        ]),
      ],
    );
  }
}
