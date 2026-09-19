import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mashal_mobile/app/app.dart';
import 'package:mashal_mobile/core/l10n/app_strings.dart';
import 'package:mashal_mobile/core/providers.dart';
import 'package:mashal_mobile/features/auth/application/auth_controller.dart';
import 'package:mashal_mobile/features/auth/domain/mobile_user.dart';
import 'package:mashal_mobile/features/home/application/dashboard_providers.dart';
import 'package:mashal_mobile/features/home/data/dashboard_repository.dart';
import 'package:mashal_mobile/features/home/domain/dashboard_summary.dart';

import 'support/fakes.dart';

class _SignedInAuth extends AuthController {
  _SignedInAuth(this._user);

  final MobileUser _user;

  @override
  AuthState build() => AuthSignedIn(_user);
}

class _SignedOutAuth extends AuthController {
  @override
  AuthState build() => const AuthSignedOut();
}

class _StaticRepository implements DashboardRepository {
  @override
  Future<DashboardSummary> fetchSummary() async => DashboardSummary.fromJson(decoded(dashboardJson()));
}

void main() {
  testWidgets('signed-in user lands on the RTL dashboard and can open Operations', (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        appConfigProvider.overrideWithValue(testConfig),
        authControllerProvider.overrideWith(() => _SignedInAuth(testUser())),
        dashboardRepositoryProvider.overrideWithValue(_StaticRepository()),
      ],
      child: const MashalApp(),
    ));
    await tester.pumpAndSettle();

    final inventoryLabel = find.text(AppStrings.kpiInventory);
    expect(inventoryLabel, findsOneWidget);
    expect(Directionality.of(tester.element(inventoryLabel)), TextDirection.rtl);

    await tester.tap(find.text(AppStrings.navOperations));
    await tester.pumpAndSettle();
    expect(find.text(AppStrings.loading), findsOneWidget);
  });

  testWidgets('operations tab is locked when the user has no operations access', (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        appConfigProvider.overrideWithValue(testConfig),
        authControllerProvider.overrideWith(() => _SignedInAuth(
              MobileUser.fromJson(decoded({
                ...userJson(),
                'capabilities': {'dashboard': true},
              })),
            )),
        dashboardRepositoryProvider.overrideWithValue(_StaticRepository()),
      ],
      child: const MashalApp(),
    ));
    await tester.pumpAndSettle();

    await tester.tap(find.text(AppStrings.navOperations));
    await tester.pumpAndSettle();
    expect(find.text(AppStrings.noAccessSection), findsOneWidget);
    expect(find.text(AppStrings.loading), findsNothing);
  });

  testWidgets('signed-out user sees the login form', (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        appConfigProvider.overrideWithValue(testConfig),
        authControllerProvider.overrideWith(_SignedOutAuth.new),
      ],
      child: const MashalApp(),
    ));
    await tester.pumpAndSettle();

    expect(find.byType(TextFormField), findsNWidgets(2));
    expect(find.widgetWithText(FilledButton, AppStrings.signIn), findsOneWidget);
  });
}
