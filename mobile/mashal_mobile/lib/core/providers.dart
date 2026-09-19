import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../features/auth/data/auth_api.dart';
import 'auth/session_manager.dart';
import 'auth/token_store.dart';
import 'config/app_config.dart';
import 'network/api_client.dart';
import 'network/dio_factory.dart';

final appConfigProvider = Provider<AppConfig>((ref) => AppConfig.fromEnvironment);

/// Tests replace the transport; production uses Dio's default adapter.
final httpClientAdapterProvider = Provider<HttpClientAdapter?>((ref) => null);

final tokenStoreProvider = Provider<TokenStore>((ref) => SecureTokenStore());

/// Unauthenticated client for login/refresh/logout (no auth interceptor, no recursion).
final authApiProvider = Provider<AuthApi>((ref) {
  return AuthApi(createApiDio(
    ref.watch(appConfigProvider),
    adapter: ref.watch(httpClientAdapterProvider),
  ));
});

final sessionManagerProvider = Provider<SessionManager>((ref) {
  return SessionManager(
    store: ref.watch(tokenStoreProvider),
    authApi: ref.watch(authApiProvider),
  );
});

final apiClientProvider = Provider<ApiClient>((ref) {
  return ApiClient.withSession(
    config: ref.watch(appConfigProvider),
    session: ref.watch(sessionManagerProvider),
    adapter: ref.watch(httpClientAdapterProvider),
  );
});
