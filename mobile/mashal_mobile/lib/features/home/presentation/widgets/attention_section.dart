import 'package:flutter/material.dart';

import '../../../../core/format/formatters.dart';
import '../../../../core/l10n/app_strings.dart';
import '../../../../core/theme/app_colors.dart';
import '../../../../core/theme/app_semantic_colors.dart';
import '../../../../shared/widgets/section_card.dart';
import '../../../../shared/widgets/status_chip.dart';
import '../../domain/dashboard_summary.dart';

class AttentionSection extends StatelessWidget {
  const AttentionSection({super.key, required this.alerts, required this.pendingActions});

  final List<DashboardAlert> alerts;
  final List<PendingAction> pendingActions;

  @override
  Widget build(BuildContext context) {
    final actions = pendingActions.where((a) => a.count > 0).toList();
    final tiles = <Widget>[
      for (final alert in alerts) _AlertTile(alert: alert),
      for (final action in actions) _PendingTile(action: action),
    ];

    return SectionCard(
      title: AppStrings.alertsAndPending,
      children: tiles.isEmpty
          ? [
              Padding(
                padding: const EdgeInsets.all(AppSpacing.lg),
                child: Text(AppStrings.nothingPending, style: Theme.of(context).textTheme.bodySmall),
              ),
            ]
          : [
              for (var i = 0; i < tiles.length; i++) ...[
                if (i > 0) const Divider(indent: 56),
                tiles[i],
              ],
            ],
    );
  }
}

class _AlertTile extends StatelessWidget {
  const _AlertTile({required this.alert});

  final DashboardAlert alert;

  @override
  Widget build(BuildContext context) {
    final colors = context.semantic;
    final (icon, color) = switch (alert.severity) {
      AlertSeverity.critical => (Icons.error_outline_rounded, colors.error),
      AlertSeverity.warning => (Icons.warning_amber_rounded, colors.warning),
      AlertSeverity.info => (Icons.info_outline_rounded, colors.info),
    };

    return ListTile(
      leading: Icon(icon, color: color),
      title: Text(alert.title),
      subtitle: Text(alert.message, maxLines: 2, overflow: TextOverflow.ellipsis),
    );
  }
}

class _PendingTile extends StatelessWidget {
  const _PendingTile({required this.action});

  final PendingAction action;

  static IconData _iconFor(String code) => switch (code) {
        'loadings_without_receipt' => Icons.move_to_inbox_outlined,
        'receipts_without_allocation' => Icons.call_split_rounded,
        'sales_without_payment' => Icons.payments_outlined,
        'contracts_without_final_price' => Icons.price_change_outlined,
        'loadings_without_customs' => Icons.assignment_late_outlined,
        'shortage' || 'excess_shortage' => Icons.water_drop_outlined,
        'sarraf_rate_difference' => Icons.currency_exchange_rounded,
        _ => Icons.pending_actions_outlined,
      };

  @override
  Widget build(BuildContext context) {
    return ListTile(
      leading: Icon(_iconFor(action.code)),
      title: Text(action.title),
      trailing: StatusChip(label: Formatters.count(action.count), tone: StatusTone.info),
    );
  }
}
