import 'package:dio/dio.dart';

import '../auth/session_manager.dart';
import 'api_exception.dart';

/// Adds the bearer token, refreshes it when it is about to expire, and retries a request once
/// after a 401. A revoked session is never retried — the user is signed out.
class AuthInterceptor extends Interceptor {
  AuthInterceptor({required SessionManager session, required Dio dio})
      : _session = session,
        _dio = dio;

  static const _retriedKey = 'mashal.auth.retried';

  final SessionManager _session;
  final Dio _dio;

  @override
  Future<void> onRequest(RequestOptions options, RequestInterceptorHandler handler) async {
    try {
      final token = await _session.accessTokenForRequest();
      if (token != null) {
        options.headers['Authorization'] = 'Bearer $token';
      }
      handler.next(options);
    } on ApiException catch (error) {
      handler.reject(DioException(requestOptions: options, error: error));
    }
  }

  @override
  Future<void> onError(DioException err, ErrorInterceptorHandler handler) async {
    final options = err.requestOptions;
    if (err.response?.statusCode != 401 || options.extra[_retriedKey] == true) {
      handler.next(err);
      return;
    }

    if (ApiException.fromDio(err).code == 'session_revoked') {
      await _session.expire();
      handler.next(err);
      return;
    }

    try {
      final token = await _session.refresh();
      options.extra[_retriedKey] = true;
      options.headers['Authorization'] = 'Bearer $token';
      handler.resolve(await _dio.fetch<dynamic>(options));
    } on ApiException catch (error) {
      handler.reject(DioException(
        requestOptions: options,
        response: err.response,
        type: DioExceptionType.badResponse,
        error: error,
      ));
    } on DioException catch (error) {
      handler.reject(error);
    }
  }
}
