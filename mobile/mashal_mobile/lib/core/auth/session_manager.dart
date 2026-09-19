import '../../features/auth/data/auth_api.dart';
import '../l10n/app_strings.dart';
import '../network/api_exception.dart';
import 'token_store.dart';

/// Owns the token lifecycle: hands out a valid access token, refreshes it once for many
/// concurrent callers, and ends the session when the server rejects the refresh token.
class SessionManager {
  SessionManager({
    required TokenStore store,
    required AuthApi authApi,
    DateTime Function()? now,
    this.onSessionExpired,
  })  : _store = store,
        _authApi = authApi,
        _now = now ?? DateTime.now;

  /// Refresh slightly before expiry so a request does not leave with a token that dies in flight.
  static const refreshMargin = Duration(seconds: 30);

  static const _expired = ApiException(
    kind: ApiErrorKind.sessionExpired,
    message: AppStrings.sessionExpired,
    statusCode: 401,
  );

  final TokenStore _store;
  final AuthApi _authApi;
  final DateTime Function() _now;

  void Function()? onSessionExpired;

  Future<String>? _refreshing;

  Future<String?> accessTokenForRequest() async {
    final session = await _store.read();
    if (session == null) return null;

    final threshold = _now().toUtc().add(refreshMargin);
    if (session.tokens.accessTokenExpiresAt.isAfter(threshold)) {
      return session.tokens.accessToken;
    }
    return refresh();
  }

  /// Single flight: concurrent 401 responses share one refresh call.
  Future<String> refresh() =>
      _refreshing ??= _refresh().whenComplete(() => _refreshing = null);

  Future<void> expire() async {
    await _store.clear();
    onSessionExpired?.call();
  }

  Future<String> _refresh() async {
    final session = await _store.read();
    if (session == null) {
      await expire();
      throw _expired;
    }

    try {
      final result = await _authApi.refresh(session.tokens.refreshToken);
      await _store.save(result.tokens, user: result.user);
      return result.tokens.accessToken;
    } on ApiException catch (error) {
      // Offline or server trouble does not end the session; the refresh can be retried later.
      if (error.kind == ApiErrorKind.network ||
          error.kind == ApiErrorKind.server ||
          error.kind == ApiErrorKind.rateLimited ||
          error.kind == ApiErrorKind.unavailable) {
        rethrow;
      }
      await expire();
      throw _expired;
    }
  }
}
