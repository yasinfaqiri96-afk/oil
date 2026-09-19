import 'package:flutter/material.dart';

import '../../../../core/format/formatters.dart';
import '../../../../core/l10n/app_strings.dart';
import '../../../../core/theme/app_colors.dart';
import '../../../../shared/widgets/section_card.dart';
import '../../../../shared/widgets/status_chip.dart';
import '../../domain/dashboard_summary.dart';

class ShipmentsSection extends StatelessWidget {
  const ShipmentsSection({super.key, required this.shipments});

  final List<ActiveShipment> shipments;

  @override
  Widget build(BuildContext context) {
    return SectionCard(
      title: AppStrings.activeShipments,
      trailing: StatusChip(label: Formatters.count(shipments.length)),
      children: shipments.isEmpty
          ? [
              Padding(
                padding: const EdgeInsets.all(AppSpacing.lg),
                child: Text(AppStrings.noActiveShipments, style: Theme.of(context).textTheme.bodySmall),
              ),
            ]
          : [
              for (var i = 0; i < shipments.length; i++) ...[
                if (i > 0) const Divider(),
                _ShipmentRow(shipment: shipments[i]),
              ],
            ],
    );
  }
}

class _ShipmentRow extends StatelessWidget {
  const _ShipmentRow({required this.shipment});

  final ActiveShipment shipment;

  @override
  Widget build(BuildContext context) {
    final textTheme = Theme.of(context).textTheme;

    return Padding(
      padding: const EdgeInsets.all(AppSpacing.lg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(shipment.code, style: textTheme.titleMedium),
              ),
              StatusChip(label: shipment.statusText, tone: StatusTone.info),
            ],
          ),
          const SizedBox(height: AppSpacing.xs),
          Text(shipment.subtitle, style: textTheme.bodySmall),
          const SizedBox(height: AppSpacing.md),
          ClipRRect(
            borderRadius: BorderRadius.circular(4),
            child: LinearProgressIndicator(value: shipment.progressPercent / 100, minHeight: 6),
          ),
          const SizedBox(height: AppSpacing.sm),
          Row(
            children: [
              Expanded(
                child: Text(
                  '${Formatters.quantityMt(shipment.loadedMt)} / '
                  '${Formatters.quantityMt(shipment.totalMt)} ${AppStrings.unitMt}',
                  style: textTheme.bodySmall,
                ),
              ),
              Text(Formatters.percent(shipment.progressPercent), style: textTheme.labelMedium),
            ],
          ),
        ],
      ),
    );
  }
}
