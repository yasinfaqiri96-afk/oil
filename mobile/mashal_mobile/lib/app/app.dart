import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/l10n/app_strings.dart';
import '../core/theme/app_theme.dart';
import 'router.dart';

class MashalApp extends ConsumerWidget {
  const MashalApp({super.key});

  /// Dari (Afghanistan). Material falls back to `fa`, which is RTL.
  static const Locale locale = Locale('fa', 'AF');

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final router = ref.watch(appRouterProvider);

    return MaterialApp.router(
      title: AppStrings.appName,
      debugShowCheckedModeBanner: false,
      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      themeMode: ThemeMode.system,
      locale: locale,
      supportedLocales: const [locale, Locale('fa')],
      localizationsDelegates: GlobalMaterialLocalizations.delegates,
      routerConfig: router,
    );
  }
}
