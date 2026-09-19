import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mashal_mobile/app/app.dart';
import 'package:mashal_mobile/core/l10n/app_strings.dart';
import 'package:mashal_mobile/core/network/api_exception.dart';
import 'package:mashal_mobile/core/theme/app_theme.dart';
import 'package:mashal_mobile/features/home/application/dashboard_providers.dart';
import 'package:mashal_mobile/features/home/data/dashboard_repository.dart';
import 'package:mashal_mobile/features/home/domain/dashboard_summary.dart';
import 'package:mashal_mobile/features/home/presentation/home_screen.dart';

import '../support/fakes.dart';

class _ScriptedRepository implements DashboardRepository {
  _ScriptedRepository(this._responses);

  final List<Future<DashboardSummary> Function()> _responses;
  int calls = 0;

  @override
  Future<DashboardSummary> fetchSummary() {
    final index = calls < _responses.length ? calls : _responses.length - 1;
    calls++;
    return _responses[index]();
  }
}

Widget _app(DashboardRepository repository) => ProviderScope(
      overrides: [dashboardRepositoryProvider.overrideWithValue(repository)],
      child: MaterialApp(
        locale: MashalApp.locale,
        supportedLocales: const [MashalApp.locale, Locale('fa')],
        localizationsDelegates: GlobalMaterialLocalizations.delegates,
        theme: AppTheme.light(),
        home: const HomeScreen(),
      ),
    );

DashboardSummary _summary({bool withFinance = true}) =>
    DashboardSummary.fromJson(decoded(dashboardJson(withFinance: withFinance)));

void main() {
  testWidgets('shows a loading indicator, then the live dashboard', (tester) async {
    final pending = Completer<DashboardSummary>();
    await tester.pumpWidget(_app(_ScriptedRepository([() => pending.future])));
    await tester.pump();

    expect(find.byType(CircularProgressIndicator), findsOneWidget);

    pending.complete(_summary());
    await tester.pumpAndSettle();

    expect(find.text(AppStrings.kpiInventory), findsOneWidget);
    expect(find.text(AppStrings.kpiReceivables), findsOneWidget);
    expect(find.text(AppStrings.confidenceNeedsReview), findsOneWidget);
    expect(find.text('SH-1405-021'), findsOneWidget);
  });

  testWidgets('shows an offline error with retry and recovers', (tester) async {
    final repository = _ScriptedRepository([
      () => Future<DashboardSummary>.error(
            const ApiException(kind: ApiErrorKind.network, message: AppStrings.networkError),
          ),
      () async => _summary(),
    ]);
    await tester.pumpWidget(_app(repository));
    await tester.pumpAndSettle();

    expect(find.text(AppStrings.networkError), findsOneWidget);
    expect(find.text(AppStrings.kpiInventory), findsNothing);

    await tester.tap(find.text(AppStrings.retry));
    await tester.pumpAndSettle();

    expect(repository.calls, 2);
    expect(find.text(AppStrings.kpiInventory), findsOneWidget);
  });

  testWidgets('forbidden dashboard shows no retry button', (tester) async {
    final repository = _ScriptedRepository([
      () => Future<DashboardSummary>.error(
            const ApiException(kind: ApiErrorKind.forbidden, message: AppStrings.accessDenied),
          ),
    ]);
    await tester.pumpWidget(_app(repository));
    await tester.pumpAndSettle();

    expect(find.text(AppStrings.accessDenied), findsOneWidget);
    expect(find.text(AppStrings.retry), findsNothing);
  });

  testWidgets('financial cards are hidden when the server omits them', (tester) async {
    await tester.pumpWidget(_app(_ScriptedRepository([() async => _summary(withFinance: false)])));
    await tester.pumpAndSettle();

    expect(find.text(AppStrings.kpiInventory), findsOneWidget);
    expect(find.text(AppStrings.kpiTodaySales), findsOneWidget);
    expect(find.text(AppStrings.kpiReceivables), findsNothing);
    expect(find.text(AppStrings.kpiCashPosition), findsNothing);
    expect(find.text(AppStrings.kpiTodayProfit), findsNothing);
    expect(find.text(AppStrings.kpiGoodsInTransit), findsNothing);
  });
}
