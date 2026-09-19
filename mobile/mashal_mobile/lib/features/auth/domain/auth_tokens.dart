import '../../../core/utils/json.dart';

class AuthTokens {
  const AuthTokens({
    required this.accessToken,
    required this.accessTokenExpiresAt,
    required this.refreshToken,
    required this.refreshTokenExpiresAt,
  });

  factory AuthTokens.fromJson(Map<String, dynamic> json) => AuthTokens(
        accessToken: asString(json['accessToken']),
        accessTokenExpiresAt: asUtcDateTime(json['accessTokenExpiresAtUtc']) ?? _epoch,
        refreshToken: asString(json['refreshToken']),
        refreshTokenExpiresAt: asUtcDateTime(json['refreshTokenExpiresAtUtc']) ?? _epoch,
      );

  static final DateTime _epoch = DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);

  final String accessToken;
  final DateTime accessTokenExpiresAt;
  final String refreshToken;
  final DateTime refreshTokenExpiresAt;

  bool get isComplete => accessToken.isNotEmpty && refreshToken.isNotEmpty;

  Map<String, dynamic> toJson() => {
        'accessToken': accessToken,
        'accessTokenExpiresAtUtc': accessTokenExpiresAt.toUtc().toIso8601String(),
        'refreshToken': refreshToken,
        'refreshTokenExpiresAtUtc': refreshTokenExpiresAt.toUtc().toIso8601String(),
      };
}
