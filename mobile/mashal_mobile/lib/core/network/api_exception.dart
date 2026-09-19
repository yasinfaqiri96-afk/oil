import 'package:dio/dio.dart';

import '../l10n/app_strings.dart';
import '../utils/json.dart';

/// What the app should do with a failed request, derived from the server ProblemDetails `code`.
enum ApiErrorKind {
  network,
  unauthorized,
  sessionExpired,
  forbidden,
  validation,
  businessRule,
  conflict,
  notFound,
  rateLimited,
  unavailable,
  server,
  unknown,
}

/// Server errors are RFC 7807 ProblemDetails with `code`, `detail` (Dari/Persian) and `traceId`.
class ApiException implements Exception {
  const ApiException({
    required this.kind,
    required this.message,
    this.statusCode,
    this.code,
    this.fieldErrors = const {},
  });

  factory ApiException.fromDio(DioException error) {
    final inner = error.error;
    if (inner is ApiException) return inner;

    final response = error.response;
    if (response == null) {
      return const ApiException(kind: ApiErrorKind.network, message: AppStrings.networkError);
    }

    final data = response.data;
    final body = data is Map<String, dynamic> ? data : const <String, dynamic>{};
    final code = asNullableString(body['code']);
    final kind = kindFor(response.statusCode, code);
    final detail = asNullableString(body['detail']);

    return ApiException(
      kind: kind,
      message: (detail == null || detail.isEmpty) ? messageFor(kind) : detail,
      statusCode: response.statusCode,
      code: code,
      fieldErrors: _fieldErrors(body['errors']),
    );
  }

  final ApiErrorKind kind;
  final String message;
  final int? statusCode;
  final String? code;
  final Map<String, List<String>> fieldErrors;

  bool get isSessionEnded => kind == ApiErrorKind.sessionExpired;

  static ApiErrorKind kindFor(int? statusCode, String? code) {
    switch (code) {
      case 'token_expired' || 'session_revoked' || 'refresh_token_invalid' || 'refresh_token_reused':
        return ApiErrorKind.sessionExpired;
      case 'unauthorized' || 'invalid_credentials':
        return ApiErrorKind.unauthorized;
      case 'forbidden':
        return ApiErrorKind.forbidden;
      case 'validation' || 'idempotency_key_required' || 'idempotency_key_invalid':
        return ApiErrorKind.validation;
      case 'business_rule':
        return ApiErrorKind.businessRule;
      case 'conflict' || 'duplicate_request':
        return ApiErrorKind.conflict;
      case 'not_found' || 'method_not_allowed':
        return ApiErrorKind.notFound;
      case 'account_locked' || 'rate_limited':
        return ApiErrorKind.rateLimited;
      case 'mobile_auth_unavailable':
        return ApiErrorKind.unavailable;
      case 'server_error':
        return ApiErrorKind.server;
    }

    return switch (statusCode) {
      400 => ApiErrorKind.validation,
      401 => ApiErrorKind.unauthorized,
      403 => ApiErrorKind.forbidden,
      404 => ApiErrorKind.notFound,
      409 => ApiErrorKind.conflict,
      422 => ApiErrorKind.businessRule,
      429 => ApiErrorKind.rateLimited,
      503 => ApiErrorKind.unavailable,
      final int status when status >= 500 => ApiErrorKind.server,
      _ => ApiErrorKind.unknown,
    };
  }

  static String messageFor(ApiErrorKind kind) => switch (kind) {
        ApiErrorKind.network => AppStrings.networkError,
        ApiErrorKind.unauthorized => AppStrings.invalidCredentials,
        ApiErrorKind.sessionExpired => AppStrings.sessionExpired,
        ApiErrorKind.forbidden => AppStrings.accessDenied,
        ApiErrorKind.validation => AppStrings.validationError,
        ApiErrorKind.businessRule => AppStrings.businessRuleError,
        ApiErrorKind.conflict => AppStrings.conflictError,
        ApiErrorKind.notFound => AppStrings.notFoundError,
        ApiErrorKind.rateLimited => AppStrings.rateLimitedError,
        ApiErrorKind.unavailable => AppStrings.unavailableError,
        ApiErrorKind.server => AppStrings.serverError,
        ApiErrorKind.unknown => AppStrings.genericError,
      };

  static Map<String, List<String>> _fieldErrors(Object? value) {
    if (value is! Map<String, dynamic>) return const {};
    return {for (final entry in value.entries) entry.key: asStringList(entry.value)};
  }

  @override
  String toString() => 'ApiException($statusCode, $code, $kind)';
}
