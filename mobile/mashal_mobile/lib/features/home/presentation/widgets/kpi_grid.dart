import 'package:flutter/material.dart';

import '../../../../core/format/formatters.dart';
import '../../../../core/l10n/app_strings.dart';
import '../../../../core/theme/app_colors.dart';
import '../../../../shared/widgets/kpi_card.dart';
import '../../../../shared/widgets/status_chip.dart';
import '../../domain/dashboard_summary.dart';

/// Shows only the KPIs the server sent; sections the user may not see are omitted.
class KpiGrid extends StatelessWidget {
  const KpiGrid({super.key, required this.summary});

  final DashboardSummary summary;

  @override
  Widget build(BuildContext context) {
    final cards = <Widget>[
      if (summary.inventory case final inventory?)
        KpiCard(
          label: AppStrings.kpiInventory,
          icon: Icons.inventory_2_outlined,
          value: Formatters.quantityMt(inventory.totalMt),
          unit: AppStrings.unitMt,
          caption: '${Formatters.count(inventory.lowStockTankCount)} ${AppStrings.lowStock}',
          status: inventory.lowStockTankCount > 0
              ? StatusChip(label: Formatters.count(inventory.lowStockTankCount), tone: StatusTone.warning)
              : null,
        ),
      if (summary.goodsInTransit case final transit?)
        KpiCard(
          label: AppStrings.kpiGoodsInTransit,
          icon: Icons.local_shipping_outlined,
          value: Formatters.quantityMt(transit.totalMt),
          unit: AppStrings.unitMt,
          caption: '${Formatters.count(transit.loadCount)} ${AppStrings.liveLoads}',
          status: transit.delayedCount > 0
              ? StatusChip(
                  label: '${Formatters.count(transit.delayedCount)} ${AppStrings.delayed}',
                  tone: StatusTone.warning,
                )
              : null,
        ),
      if (summary.todaySales case final sales?)
        KpiCard(
          label: AppStrings.kpiTodaySales,
          icon: Icons.point_of_sale_outlined,
          value: Formatters.money(sales.amountUsd),
          unit: AppStrings.unitUsd,
          caption: '${Formatters.count(sales.count)} ${AppStrings.salesCount}',
        ),
      if (summary.receivables case final receivables?)
        KpiCard(
          label: AppStrings.kpiReceivables,
          icon: Icons.request_quote_outlined,
          value: Formatters.money(receivables.totalUsd),
          unit: AppStrings.unitUsd,
          caption: '${Formatters.count(receivables.customerCount)} ${AppStrings.debtorCustomers}',
        ),
      if (summary.cashPosition case final cash?)
        KpiCard(
          label: AppStrings.kpiCashPosition,
          icon: Icons.account_balance_outlined,
          value: Formatters.money(cash.totalUsd),
          unit: AppStrings.unitUsd,
          caption: '${Formatters.count(cash.accountCount)} ${AppStrings.cashAccounts}',
        ),
      if (summary.todayProfit case final profit?)
        KpiCard(
          label: AppStrings.kpiTodayProfit,
          icon: Icons.trending_up_rounded,
          value: Formatters.money(profit.grossProfitUsd),
          unit: AppStrings.unitUsd,
          caption: '${Formatters.count(profit.costedSaleCount)}/'
              '${Formatters.count(profit.saleCount)} ${AppStrings.costedSales}',
          status: _confidenceChip(profit.confidence),
        ),
    ];

    return LayoutBuilder(
      builder: (context, constraints) {
        final columns = constraints.maxWidth >= 900 ? 3 : 2;
        const gap = AppSpacing.md;
        final itemWidth = (constraints.maxWidth - gap * (columns - 1)) / columns;

        return Wrap(
          spacing: gap,
          runSpacing: gap,
          children: [for (final card in cards) SizedBox(width: itemWidth, child: card)],
        );
      },
    );
  }

  static Widget? _confidenceChip(PnlConfidence confidence) => switch (confidence) {
        PnlConfidence.verified => null,
        PnlConfidence.estimated => const StatusChip(label: AppStrings.confidenceEstimated, tone: StatusTone.info),
        PnlConfidence.legacy => const StatusChip(label: AppStrings.confidenceLegacy),
        PnlConfidence.needsReview =>
          const StatusChip(label: AppStrings.confidenceNeedsReview, tone: StatusTone.warning),
      };
}
