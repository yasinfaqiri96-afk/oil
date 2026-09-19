import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/l10n/app_strings.dart';
import '../../../shared/widgets/feature_list_screen.dart';
import '../../../shared/widgets/placeholder_screen.dart';
import '../../auth/application/auth_controller.dart';
import '../../auth/domain/mobile_user.dart';

class OperationsScreen extends ConsumerWidget {
  const OperationsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final capabilities = ref.watch(currentUserProvider)?.capabilities ?? MobileCapabilities.none;
    if (!capabilities.operations && !capabilities.inventory) {
      return const PlaceholderScreen(
        title: AppStrings.navOperations,
        message: AppStrings.noAccessSection,
        icon: Icons.lock_outline_rounded,
      );
    }

    return FeatureListScreen(
      title: AppStrings.navOperations,
      sections: [
        if (capabilities.operations)
          const FeatureSection(title: AppStrings.loadFlow, entries: [
            FeatureEntry(icon: Icons.oil_barrel_outlined, title: AppStrings.loading, subtitle: AppStrings.loadingSubtitle),
            FeatureEntry(icon: Icons.local_shipping_outlined, title: AppStrings.transport, subtitle: AppStrings.transportSubtitle),
            FeatureEntry(icon: Icons.fact_check_outlined, title: AppStrings.receipt, subtitle: AppStrings.receiptSubtitle),
            FeatureEntry(icon: Icons.water_drop_outlined, title: AppStrings.losses, subtitle: AppStrings.lossesSubtitle),
          ]),
        if (capabilities.inventory)
          const FeatureSection(title: AppStrings.stock, entries: [
            FeatureEntry(icon: Icons.propane_tank_outlined, title: AppStrings.inventoryTanks, subtitle: AppStrings.inventoryTanksSubtitle),
          ]),
      ],
    );
  }
}
