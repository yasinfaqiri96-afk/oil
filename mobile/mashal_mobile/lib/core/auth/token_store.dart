import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../../features/auth/domain/auth_tokens.dart';
import '../../features/auth/domain/mobile_user.dart';
import '../utils/uuid.dart';

class StoredSession {
  const StoredSession({required this.tokens, this.user});

  final AuthTokens tokens;

  /// Last profile from `/me`, so a signed-in user can open the app while offline.
  final MobileUser? user;
}

abstract interface class TokenStore {
  Future<StoredSession?> read();

  Future<void> save(AuthTokens tokens, {MobileUser? user});

  Future<void> saveUser(MobileUser user);

  Future<void> clear();

  /// Stable, random, non-personal id for this installation (session naming/revocation only).
  Future<String> deviceId();
}

/// Keychain on iOS, Keystore-backed encrypted preferences on Android.
/// Passwords are never stored; tokens never go to plain shared preferences or logs.
class SecureTokenStore implements TokenStore {
  SecureTokenStore([FlutterSecureStorage? storage])
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
              iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
            );

  static const _tokensKey = 'mashal.session.tokens';
  static const _userKey = 'mashal.session.user';
  static const _deviceKey = 'mashal.device.id';

  final FlutterSecureStorage _storage;

  @override
  Future<StoredSession?> read() async {
    final rawTokens = await _storage.read(key: _tokensKey);
    if (rawTokens == null) return null;

    try {
      final tokens = AuthTokens.fromJson(jsonDecode(rawTokens) as Map<String, dynamic>);
      if (!tokens.isComplete) {
        await clear();
        return null;
      }
      final rawUser = await _storage.read(key: _userKey);
      final user = rawUser == null
          ? null
          : MobileUser.fromJson(jsonDecode(rawUser) as Map<String, dynamic>);
      return StoredSession(tokens: tokens, user: user);
    } catch (_) {
      // Corrupt or outdated entry: fail closed and ask the user to sign in again.
      await clear();
      return null;
    }
  }

  @override
  Future<void> save(AuthTokens tokens, {MobileUser? user}) async {
    await _storage.write(key: _tokensKey, value: jsonEncode(tokens.toJson()));
    if (user != null) {
      await saveUser(user);
    }
  }

  @override
  Future<void> saveUser(MobileUser user) =>
      _storage.write(key: _userKey, value: jsonEncode(user.toJson()));

  @override
  Future<void> clear() async {
    await _storage.delete(key: _tokensKey);
    await _storage.delete(key: _userKey);
  }

  @override
  Future<String> deviceId() async {
    final existing = await _storage.read(key: _deviceKey);
    if (existing != null && existing.isNotEmpty) return existing;
    final created = newUuidV4();
    await _storage.write(key: _deviceKey, value: created);
    return created;
  }
}
