import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/l10n/app_strings.dart';
import '../../../shared/widgets/feature_list_screen.dart';
import '../../../shared/widgets/placeholder_screen.dart';
import '../../auth/application/auth_controller.dart';

class SalesScreen extends ConsumerWidget {
  const SalesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final capabilities = ref.watch(currentUserProvider)?.capabilities;
    if (capabilities == null || !capabilities.sales) {
      return const PlaceholderScreen(
        title: AppStrings.navSales,
        message: AppStrings.noAccessSection,
        icon: Icons.lock_outline_rounded,
      );
    }

    return FeatureListScreen(
      title: AppStrings.navSales,
      sections: [
        FeatureSection(title: AppStrings.navSales, entries: [
          const FeatureEntry(icon: Icons.receipt_long_outlined, title: AppStrings.salesList, subtitle: AppStrings.salesListSubtitle),
          if (capabilities.manageData)
            const FeatureEntry(icon: Icons.add_card_outlined, title: AppStrings.newSale, subtitle: AppStrings.newSaleSubtitle),
          const FeatureEntry(icon: Icons.person_search_outlined, title: AppStrings.customerBalance, subtitle: AppStrings.customerBalanceSubtitle),
        ]),
      ],
    );
  }
}
