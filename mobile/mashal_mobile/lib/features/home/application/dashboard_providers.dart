import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/providers.dart';
import '../data/dashboard_repository.dart';
import '../domain/dashboard_summary.dart';

final dashboardRepositoryProvider = Provider<DashboardRepository>(
  (ref) => RemoteDashboardRepository(ref.watch(apiClientProvider)),
);

/// On a failed refresh Riverpod keeps the previous value, so the screen can keep showing
/// the last good data with an offline/failed notice.
final dashboardSummaryProvider = FutureProvider.autoDispose<DashboardSummary>(
  (ref) => ref.watch(dashboardRepositoryProvider).fetchSummary(),
);
