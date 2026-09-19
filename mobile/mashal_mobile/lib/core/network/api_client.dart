import 'package:dio/dio.dart';

import '../auth/session_manager.dart';
import '../config/app_config.dart';
import 'api_exception.dart';
import 'auth_interceptor.dart';
import 'dio_factory.dart';

/// Authenticated HTTP client for `/api/mobile/v1`. Business rules stay on the server.
class ApiClient {
  ApiClient(this._dio);

  factory ApiClient.withSession({
    required AppConfig config,
    required SessionManager session,
    HttpClientAdapter? adapter,
  }) {
    final dio = createApiDio(config, adapter: adapter);
    dio.interceptors.add(AuthInterceptor(session: session, dio: dio));
    return ApiClient(dio);
  }

  final Dio _dio;

  Future<Map<String, dynamic>> getJson(String path, {Map<String, dynamic>? query}) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(path, queryParameters: query);
      return response.data ?? const {};
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }

  /// Every write sends one client-generated UUID for all retries of the same operation; the
  /// server rejects a second use with 409 `duplicate_request`, so a retry cannot post twice.
  Future<Map<String, dynamic>> postJson(
    String path,
    Object body, {
    required String idempotencyKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        path,
        data: body,
        options: Options(headers: {'Idempotency-Key': idempotencyKey}),
      );
      return response.data ?? const {};
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }
}
