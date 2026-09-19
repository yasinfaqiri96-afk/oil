import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mashal_mobile/core/auth/token_store.dart';
import 'package:mashal_mobile/core/l10n/app_strings.dart';
import 'package:mashal_mobile/core/network/api_exception.dart';
import 'package:mashal_mobile/features/auth/application/auth_controller.dart';
import 'package:mashal_mobile/features/auth/data/auth_api.dart';
import 'package:mashal_mobile/features/auth/data/profile_api.dart';

import '../support/fakes.dart';

void main() {
  group('AuthController', () {
    test('login stores tokens securely and signs the user in', () async {
      final store = InMemoryTokenStore();
      final adapter = FakeHttpAdapter((options) =>
          options.path == AuthApi.loginPath ? FakeResponse(200, authJson()) : const FakeResponse(404));
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);
      final controller = container.read(authControllerProvider.notifier);
      await controller.restoration;
      expect(container.read(authControllerProvider), isA<AuthSignedOut>());

      await controller.login(username: ' operator1 ', password: 'secret-pass');

      final state = container.read(authControllerProvider);
      expect(state, isA<AuthSignedIn>());
      expect((state as AuthSignedIn).user.username, 'operator1');
      expect(store.session!.tokens.refreshToken, 'refresh-1');
      expect(jsonEncode(store.session!.tokens.toJson()), isNot(contains('secret-pass')));

      final body = adapter.requests.single.data as Map<String, Object?>;
      expect(body['username'], 'operator1');
      expect(body['deviceId'], 'test-device');
    });

    test('failed login keeps the user signed out and exposes the server message', () async {
      final store = InMemoryTokenStore();
      final adapter = FakeHttpAdapter(
        (options) => FakeResponse(401, problem('invalid_credentials', detail: AppStrings.invalidCredentials)),
      );
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);
      final controller = container.read(authControllerProvider.notifier);
      await controller.restoration;

      await expectLater(
        controller.login(username: 'operator1', password: 'wrong'),
        throwsA(isA<ApiException>()
            .having((e) => e.kind, 'kind', ApiErrorKind.unauthorized)
            .having((e) => e.message, 'message', AppStrings.invalidCredentials)),
      );

      expect(container.read(authControllerProvider), isA<AuthSignedOut>());
      expect(store.session, isNull);
    });

    test('logout revokes the session on the server and clears local tokens', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens(), user: testUser());
      final adapter = FakeHttpAdapter((options) => switch (options.path) {
            ProfileApi.mePath => FakeResponse(200, userJson()),
            AuthApi.logoutPath => const FakeResponse(204),
            _ => const FakeResponse(404),
          });
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);
      final controller = container.read(authControllerProvider.notifier);
      await controller.restoration;
      expect(container.read(authControllerProvider), isA<AuthSignedIn>());

      await controller.logout();

      final logoutRequest = adapter.requests.singleWhere((r) => r.path == AuthApi.logoutPath);
      expect(logoutRequest.headers['Authorization'], 'Bearer access-1');
      expect((logoutRequest.data as Map<String, Object?>)['refreshToken'], 'refresh-1');
      expect(store.session, isNull);
      expect(container.read(authControllerProvider), isA<AuthSignedOut>());
    });

    test('logout still clears the device when the server is unreachable', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens(), user: testUser());
      final adapter = FakeHttpAdapter(
        (options) => throw DioException.connectionError(requestOptions: options, reason: 'offline'),
      );
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);
      final controller = container.read(authControllerProvider.notifier);
      await controller.restoration;

      await controller.logout();

      expect(store.session, isNull);
      expect(container.read(authControllerProvider), isA<AuthSignedOut>());
    });

    test('startup with a revoked session signs out with an explanation', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens(), user: testUser());
      final adapter = FakeHttpAdapter((options) => FakeResponse(401, problem('session_revoked')));
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);

      container.read(authControllerProvider);
      await container.read(authControllerProvider.notifier).restoration;

      final state = container.read(authControllerProvider);
      expect(state, isA<AuthSignedOut>());
      expect((state as AuthSignedOut).message, AppStrings.sessionExpired);
      expect(store.session, isNull);
    });

    test('startup while offline keeps the cached profile and marks the session offline', () async {
      final store = InMemoryTokenStore()..session = StoredSession(tokens: testTokens(), user: testUser());
      final adapter = FakeHttpAdapter(
        (options) => throw DioException.connectionError(requestOptions: options, reason: 'offline'),
      );
      final container = makeContainer(adapter: adapter, store: store);
      addTearDown(container.dispose);

      container.read(authControllerProvider);
      await container.read(authControllerProvider.notifier).restoration;

      final state = container.read(authControllerProvider);
      expect(state, isA<AuthSignedIn>());
      expect((state as AuthSignedIn).isOffline, isTrue);
      expect(store.session, isNotNull);
    });
  });
}
