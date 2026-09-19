import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../app/router.dart';
import '../../../core/format/formatters.dart';
import '../../../core/l10n/app_strings.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/theme/app_colors.dart';
import '../../../shared/widgets/error_state_view.dart';
import '../../../shared/widgets/inline_notice.dart';
import '../../../shared/widgets/status_chip.dart';
import '../application/dashboard_providers.dart';
import '../domain/dashboard_summary.dart';
import 'widgets/attention_section.dart';
import 'widgets/kpi_grid.dart';
import 'widgets/shipments_section.dart';

class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final summary = ref.watch(dashboardSummaryProvider);
    final data = summary.valueOrNull;
    final attention = data?.attentionCount ?? 0;

    final Widget body;
    if (data != null) {
      body = RefreshIndicator(
        onRefresh: () async {
          try {
            await ref.refresh(dashboardSummaryProvider.future);
          } on Object {
            // The failure stays in the provider state and is shown as a notice above the data.
          }
        },
        child: _DashboardBody(
          summary: data,
          refreshError: summary.hasError ? summary.error : null,
        ),
      );
    } else if (summary.hasError) {
      body = ErrorStateView(
        error: summary.error!,
        onRetry: () => ref.invalidate(dashboardSummaryProvider),
      );
    } else {
      body = const Center(child: CircularProgressIndicator());
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text(AppStrings.navHome),
        actions: [
          IconButton(
            tooltip: AppStrings.notifications,
            onPressed: () => context.go(AppRoutes.notifications),
            icon: Badge(
              isLabelVisible: attention > 0,
              label: Text(Formatters.count(attention)),
              child: const Icon(Icons.notifications_none_rounded),
            ),
          ),
          const SizedBox(width: AppSpacing.sm),
        ],
      ),
      body: body,
    );
  }
}

class _DashboardBody extends StatelessWidget {
  const _DashboardBody({required this.summary, this.refreshError});

  final DashboardSummary summary;
  final Object? refreshError;

  @override
  Widget build(BuildContext context) {
    final error = refreshError;
    final isOffline = error is ApiException && error.kind == ApiErrorKind.network;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(AppSpacing.lg, 0, AppSpacing.lg, AppSpacing.xl),
      children: [
        Text(
          '${AppStrings.businessDate}: ${summary.businessDate}  ·  '
          '${AppStrings.updatedAt}: ${Formatters.localTime(summary.asOfUtc)}',
          style: Theme.of(context).textTheme.bodySmall,
        ),
        const SizedBox(height: AppSpacing.md),
        if (error != null) ...[
          InlineNotice(
            message: isOffline ? AppStrings.offlineStale : AppStrings.refreshFailed,
            tone: StatusTone.warning,
            icon: isOffline ? Icons.cloud_off_rounded : null,
          ),
          const SizedBox(height: AppSpacing.md),
        ],
        KpiGrid(summary: summary),
        const SizedBox(height: AppSpacing.lg),
        AttentionSection(alerts: summary.alerts, pendingActions: summary.openPendingActions),
        const SizedBox(height: AppSpacing.lg),
        ShipmentsSection(shipments: summary.activeShipments),
      ],
    );
  }
}
