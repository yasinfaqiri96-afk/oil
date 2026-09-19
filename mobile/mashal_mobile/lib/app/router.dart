import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../core/l10n/app_strings.dart';
import '../features/auth/application/auth_controller.dart';
import '../features/auth/presentation/login_screen.dart';
import '../features/auth/presentation/splash_screen.dart';
import '../features/finance/presentation/finance_screen.dart';
import '../features/home/presentation/home_screen.dart';
import '../features/more/presentation/more_screen.dart';
import '../features/operations/presentation/operations_screen.dart';
import '../features/sales/presentation/sales_screen.dart';
import '../shared/widgets/placeholder_screen.dart';
import 'shell/main_shell.dart';

abstract final class AppRoutes {
  static const splash = '/splash';
  static const login = '/login';
  static const home = '/home';
  static const operations = '/operations';
  static const sales = '/sales';
  static const finance = '/finance';
  static const more = '/more';
  static const approvals = '/more/approvals';
  static const notifications = '/more/notifications';
}

class _AuthRefreshListenable extends ChangeNotifier {
  void notify() => notifyListeners();
}

final appRouterProvider = Provider<GoRouter>((ref) {
  final refresh = _AuthRefreshListenable();
  ref.listen<AuthState>(authControllerProvider, (_, __) => refresh.notify());

  final router = GoRouter(
    initialLocation: AppRoutes.home,
    refreshListenable: refresh,
    redirect: (context, state) {
      final location = state.matchedLocation;
      return switch (ref.read(authControllerProvider)) {
        AuthRestoring() => location == AppRoutes.splash ? null : AppRoutes.splash,
        AuthSignedOut() => location == AppRoutes.login ? null : AppRoutes.login,
        AuthSignedIn() =>
          location == AppRoutes.login || location == AppRoutes.splash ? AppRoutes.home : null,
      };
    },
    routes: [
      GoRoute(path: AppRoutes.splash, builder: (context, state) => const SplashScreen()),
      GoRoute(path: AppRoutes.login, builder: (context, state) => const LoginScreen()),
      StatefulShellRoute.indexedStack(
        builder: (context, state, navigationShell) => MainShell(navigationShell: navigationShell),
        branches: [
          StatefulShellBranch(routes: [
            GoRoute(path: AppRoutes.home, builder: (context, state) => const HomeScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(path: AppRoutes.operations, builder: (context, state) => const OperationsScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(path: AppRoutes.sales, builder: (context, state) => const SalesScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(path: AppRoutes.finance, builder: (context, state) => const FinanceScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(
              path: AppRoutes.more,
              builder: (context, state) => const MoreScreen(),
              routes: [
                GoRoute(
                  path: 'approvals',
                  builder: (context, state) => const PlaceholderScreen(
                    title: AppStrings.approvals,
                    message: AppStrings.approvalsBackendMissing,
                  ),
                ),
                GoRoute(
                  path: 'notifications',
                  builder: (context, state) => const PlaceholderScreen(
                    title: AppStrings.notifications,
                    message: AppStrings.notificationsBackendMissing,
                  ),
                ),
              ],
            ),
          ]),
        ],
      ),
    ],
  );

  ref.onDispose(() {
    router.dispose();
    refresh.dispose();
  });
  return router;
});
