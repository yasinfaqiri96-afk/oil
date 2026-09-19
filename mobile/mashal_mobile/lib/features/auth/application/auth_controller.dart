import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/auth/token_store.dart';
import '../../../core/l10n/app_strings.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/providers.dart';
import '../data/auth_api.dart';
import '../data/profile_api.dart';
import '../domain/mobile_user.dart';

sealed class AuthState {
  const AuthState();
}

/// Reading stored tokens at startup.
final class AuthRestoring extends AuthState {
  const AuthRestoring();
}

final class AuthSignedOut extends AuthState {
  const AuthSignedOut({this.message});

  /// Why the user is signed out (e.g. session expired), shown on the login screen.
  final String? message;
}

final class AuthSignedIn extends AuthState {
  const AuthSignedIn(this.user, {this.isOffline = false});

  final MobileUser user;

  /// Profile came from the secure cache because the server was unreachable.
  final bool isOffline;
}

final authControllerProvider = NotifierProvider<AuthController, AuthState>(AuthController.new);

final currentUserProvider = Provider<MobileUser?>((ref) {
  return switch (ref.watch(authControllerProvider)) {
    AuthSignedIn(:final user) => user,
    _ => null,
  };
});

class AuthController extends Notifier<AuthState> {
  Future<void>? _restoration;

  /// Completes when the startup session check has finished.
  Future<void> get restoration => _restoration ?? Future<void>.value();

  TokenStore get _store => ref.read(tokenStoreProvider);

  AuthApi get _authApi => ref.read(authApiProvider);

  @override
  AuthState build() {
    ref.read(sessionManagerProvider).onSessionExpired = _handleSessionExpired;
    _restoration = Future<void>.microtask(_restore);
    return const AuthRestoring();
  }

  Future<void> login({required String username, required String password}) async {
    final deviceId = await _store.deviceId();
    final result = await _authApi.login(
      username: username.trim(),
      password: password,
      deviceId: deviceId,
      deviceName: _deviceName(),
    );
    await _store.save(result.tokens, user: result.user);
    state = AuthSignedIn(result.user);
  }

  Future<void> logout() async {
    final stored = await _store.read();
    if (stored != null) {
      try {
        await _authApi.logout(
          accessToken: stored.tokens.accessToken,
          refreshToken: stored.tokens.refreshToken,
        );
      } on ApiException {
        // Offline logout still removes the session from this device; the server
        // session expires on its own or can be revoked later.
      }
    }
    await _store.clear();
    state = const AuthSignedOut();
  }

  Future<void> _restore() async {
    final stored = await _store.read();
    if (stored == null) {
      state = const AuthSignedOut();
      return;
    }

    if (!stored.tokens.refreshTokenExpiresAt.isAfter(DateTime.now().toUtc())) {
      await _store.clear();
      state = const AuthSignedOut(message: AppStrings.sessionExpired);
      return;
    }

    final cachedUser = stored.user;
    if (cachedUser != null) {
      state = AuthSignedIn(cachedUser);
    }

    try {
      final user = await ref.read(profileApiProvider).me();
      await _store.saveUser(user);
      if (state is! AuthSignedOut) {
        state = AuthSignedIn(user);
      }
    } on ApiException catch (error) {
      if (error.isSessionEnded) {
        await _store.clear();
        state = const AuthSignedOut(message: AppStrings.sessionExpired);
      } else if (cachedUser != null) {
        state = AuthSignedIn(cachedUser, isOffline: error.kind == ApiErrorKind.network);
      } else {
        state = AuthSignedOut(message: error.message);
      }
    }
  }

  void _handleSessionExpired() {
    if (state is! AuthSignedOut) {
      state = const AuthSignedOut(message: AppStrings.sessionExpired);
    }
  }

  static String _deviceName() => switch (defaultTargetPlatform) {
        TargetPlatform.android => 'Mashal Mobile · Android',
        TargetPlatform.iOS => 'Mashal Mobile · iOS',
        _ => 'Mashal Mobile',
      };
}
