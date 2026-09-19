import 'package:flutter/foundation.dart';

/// Build-time configuration, set with `--dart-define=MASHAL_API_BASE_URL=https://…`.
class AppConfig {
  const AppConfig({required this.apiBaseUrl, this.requireHttps = kReleaseMode});

  static const AppConfig fromEnvironment = AppConfig(
    apiBaseUrl: String.fromEnvironment('MASHAL_API_BASE_URL'),
  );

  final String apiBaseUrl;

  /// Release builds refuse plain HTTP so tokens never travel unencrypted.
  final bool requireHttps;

  bool get isConfigured =>
      apiBaseUrl.isNotEmpty && (!requireHttps || apiBaseUrl.startsWith('https://'));
}
