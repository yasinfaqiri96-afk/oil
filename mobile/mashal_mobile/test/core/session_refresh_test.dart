import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mashal_mobile/core/auth/token_store.dart';
import 'package:mashal_mobile/core/network/api_exception.dart';
import 'package:mashal_mobile/core/providers.dart';
import 'package:mashal_mobile/features/auth/data/auth_api.dart';
import 'package:mashal_mobile/features/auth/data/profile_api.dart';

import '../support/fakes.dart';

void main() {
  group('token refresh', () {
    test('an expired access token is refreshed once for concurrent requests, then retried', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens(access: 'old'));
      final adapter = FakeHttpAdapter((options) {
        switch (options.path) {
          case ProfileApi.mePath:
            return options.headers['Authorization'] == 'Bearer new'
                ? FakeResponse(200, userJson())
                : FakeResponse(401, problem('token_expired'));
          case AuthApi.refreshPath:
            return FakeResponse(200, authJson(access: 'new', refresh: 'refresh-2'));
        }
        return const FakeResponse(404);
      });
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);
      final client = container.read(apiClientProvider);

      final results = await Future.wait([
        client.getJson(ProfileApi.mePath),
        client.getJson(ProfileApi.mePath),
      ]);

      expect(results, hasLength(2));
      expect(adapter.count(AuthApi.refreshPath), 1);
      expect(store.session!.tokens.accessToken, 'new');
      expect(store.session!.tokens.refreshToken, 'refresh-2');
    });

    test('a token that is about to expire is refreshed before the request is sent', () async {
      final store = InMemoryTokenStore()
        ..session = StoredSession(tokens: testTokens(access: 'old', accessExpiresIn: const Duration(seconds: 5)));
      final adapter = FakeHttpAdapter((options) => switch (options.path) {
            AuthApi.refreshPath => FakeResponse(200, authJson(access: 'new')),
            ProfileApi.mePath => options.headers['Authorization'] == 'Bearer new'
                ? FakeResponse(200, userJson())
                : FakeResponse(401, problem('token_expired')),
            _ => const FakeResponse(404),
          });
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);

      await container.read(apiClientProvider).getJson(ProfileApi.mePath);

      expect(adapter.count(AuthApi.refreshPath), 1);
      expect(adapter.count(ProfileApi.mePath), 1);
    });

    test('a rejected refresh token ends the session and clears stored tokens', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens(access: 'old'));
      final adapter = FakeHttpAdapter((options) => switch (options.path) {
            ProfileApi.mePath => FakeResponse(401, problem('token_expired')),
            AuthApi.refreshPath => FakeResponse(401, problem('refresh_token_reused')),
            _ => const FakeResponse(404),
          });
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);
      var expired = false;
      container.read(sessionManagerProvider).onSessionExpired = () => expired = true;

      await expectLater(
        container.read(apiClientProvider).getJson(ProfileApi.mePath),
        throwsA(isA<ApiException>().having((e) => e.kind, 'kind', ApiErrorKind.sessionExpired)),
      );

      expect(expired, isTrue);
      expect(store.session, isNull);
    });

    test('a revoked session is not refreshed', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens());
      final adapter = FakeHttpAdapter((options) => switch (options.path) {
            ProfileApi.mePath => FakeResponse(401, problem('session_revoked')),
            _ => FakeResponse(200, authJson()),
          });
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);

      await expectLater(
        container.read(apiClientProvider).getJson(ProfileApi.mePath),
        throwsA(isA<ApiException>().having((e) => e.kind, 'kind', ApiErrorKind.sessionExpired)),
      );

      expect(adapter.count(AuthApi.refreshPath), 0);
      expect(store.session, isNull);
    });

    test('403 is reported as forbidden and keeps the session', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens());
      final adapter = FakeHttpAdapter((options) => FakeResponse(403, problem('forbidden', status: 403)));
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);

      await expectLater(
        container.read(apiClientProvider).getJson('/api/mobile/v1/dashboard'),
        throwsA(isA<ApiException>().having((e) => e.kind, 'kind', ApiErrorKind.forbidden)),
      );

      expect(adapter.count(AuthApi.refreshPath), 0);
      expect(store.session, isNotNull);
    });

    test('a network failure is reported as offline and keeps the session', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens());
      final adapter = FakeHttpAdapter(
        (options) => throw DioException.connectionError(requestOptions: options, reason: 'offline'),
      );
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);

      await expectLater(
        container.read(apiClientProvider).getJson('/api/mobile/v1/dashboard'),
        throwsA(isA<ApiException>().having((e) => e.kind, 'kind', ApiErrorKind.network)),
      );

      expect(store.session, isNotNull);
    });
  });
}
