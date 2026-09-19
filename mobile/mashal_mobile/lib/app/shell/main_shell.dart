import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/l10n/app_strings.dart';

class _Destination {
  const _Destination(this.label, this.icon, this.selectedIcon);

  final String label;
  final IconData icon;
  final IconData selectedIcon;
}

const _destinations = [
  _Destination(AppStrings.navHome, Icons.space_dashboard_outlined, Icons.space_dashboard_rounded),
  _Destination(AppStrings.navOperations, Icons.local_shipping_outlined, Icons.local_shipping_rounded),
  _Destination(AppStrings.navSales, Icons.receipt_long_outlined, Icons.receipt_long_rounded),
  _Destination(AppStrings.navFinance, Icons.account_balance_wallet_outlined, Icons.account_balance_wallet_rounded),
  _Destination(AppStrings.navMore, Icons.more_horiz_rounded, Icons.more_horiz_rounded),
];

/// Bottom navigation on phones, navigation rail on tablets.
class MainShell extends StatelessWidget {
  const MainShell({super.key, required this.navigationShell});

  static const double railBreakpoint = 840;

  final StatefulNavigationShell navigationShell;

  void _onSelect(int index) {
    navigationShell.goBranch(
      index,
      initialLocation: index == navigationShell.currentIndex,
    );
  }

  @override
  Widget build(BuildContext context) {
    final useRail = MediaQuery.sizeOf(context).width >= railBreakpoint;

    if (useRail) {
      return Scaffold(
        body: Row(
          children: [
            NavigationRail(
              selectedIndex: navigationShell.currentIndex,
              onDestinationSelected: _onSelect,
              labelType: NavigationRailLabelType.all,
              destinations: [
                for (final d in _destinations)
                  NavigationRailDestination(
                    icon: Icon(d.icon),
                    selectedIcon: Icon(d.selectedIcon),
                    label: Text(d.label),
                  ),
              ],
            ),
            const VerticalDivider(width: 1),
            Expanded(child: navigationShell),
          ],
        ),
      );
    }

    return Scaffold(
      body: navigationShell,
      bottomNavigationBar: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Divider(height: 1),
          NavigationBar(
            selectedIndex: navigationShell.currentIndex,
            onDestinationSelected: _onSelect,
            destinations: [
              for (final d in _destinations)
                NavigationDestination(
                  icon: Icon(d.icon),
                  selectedIcon: Icon(d.selectedIcon),
                  label: d.label,
                ),
            ],
          ),
        ],
      ),
    );
  }
}
