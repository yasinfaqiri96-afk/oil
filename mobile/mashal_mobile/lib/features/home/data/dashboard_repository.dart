import '../../../core/network/api_client.dart';
import '../domain/dashboard_summary.dart';

abstract interface class DashboardRepository {
  Future<DashboardSummary> fetchSummary();
}

class RemoteDashboardRepository implements DashboardRepository {
  const RemoteDashboardRepository(this._api);

  static const path = '/api/mobile/v1/dashboard';

  final ApiClient _api;

  @override
  Future<DashboardSummary> fetchSummary() async => DashboardSummary.fromJson(await _api.getJson(path));
}
