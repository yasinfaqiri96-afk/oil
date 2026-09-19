import 'package:dio/dio.dart';

import '../config/app_config.dart';

/// No logging interceptor is ever added: requests carry passwords and tokens.
Dio createApiDio(AppConfig config, {HttpClientAdapter? adapter}) {
  final dio = Dio(BaseOptions(
    baseUrl: config.apiBaseUrl,
    connectTimeout: const Duration(seconds: 15),
    sendTimeout: const Duration(seconds: 30),
    receiveTimeout: const Duration(seconds: 30),
    contentType: Headers.jsonContentType,
    headers: {
      'Accept': 'application/json',
      'X-Client': 'mashal-mobile',
    },
  ));
  if (adapter != null) {
    dio.httpClientAdapter = adapter;
  }
  return dio;
}
