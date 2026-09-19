import 'package:flutter/material.dart';

import '../../../core/l10n/app_strings.dart';
import '../../../core/theme/app_colors.dart';

class SplashScreen extends StatelessWidget {
  const SplashScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(AppStrings.appName, style: Theme.of(context).textTheme.headlineSmall),
            const SizedBox(height: AppSpacing.lg),
            const SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 2)),
          ],
        ),
      ),
    );
  }
}
