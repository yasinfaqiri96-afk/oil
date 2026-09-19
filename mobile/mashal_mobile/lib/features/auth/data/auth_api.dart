import 'package:dio/dio.dart';

import '../../../core/l10n/app_strings.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/utils/json.dart';
import '../domain/auth_tokens.dart';
import '../domain/mobile_user.dart';

class AuthResult {
  const AuthResult({required this.tokens, required this.user});

  factory AuthResult.fromJson(Map<String, dynamic> json) => AuthResult(
        tokens: AuthTokens.fromJson(json),
        user: MobileUser.fromJson(asMap(json['user']) ?? const {}),
      );

  final AuthTokens tokens;
  final MobileUser user;
}

/// Login, refresh and logout. Uses a Dio instance without the auth interceptor.
class AuthApi {
  AuthApi(this._dio);

  static const loginPath = '/api/mobile/v1/auth/login';
  static const refreshPath = '/api/mobile/v1/auth/refresh';
  static const logoutPath = '/api/mobile/v1/auth/logout';

  final Dio _dio;

  Future<AuthResult> login({
    required String username,
    required String password,
    required String deviceId,
    String? deviceName,
  }) async {
    final json = await _post(loginPath, {
      'username': username,
      'password': password,
      'deviceId': deviceId,
      'deviceName': deviceName,
    });
    return AuthResult.fromJson(json);
  }

  Future<AuthResult> refresh(String refreshToken) async {
    final json = await _post(refreshPath, {'refreshToken': refreshToken});
    return AuthResult.fromJson(json);
  }

  /// Revokes this device's session on the server. Callers clear local tokens regardless.
  Future<void> logout({String? accessToken, String? refreshToken}) async {
    try {
      await _dio.post<void>(
        logoutPath,
        data: {'refreshToken': refreshToken},
        options: Options(headers: {
          if (accessToken != null) 'Authorization': 'Bearer $accessToken',
        }),
      );
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }

  Future<Map<String, dynamic>> _post(String path, Map<String, Object?> body) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(path, data: body);
      final data = response.data;
      if (data == null) {
        throw const ApiException(kind: ApiErrorKind.server, message: AppStrings.serverError);
      }
      return data;
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }
}
