import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mashal_mobile/core/auth/token_store.dart';
import 'package:mashal_mobile/core/config/app_config.dart';
import 'package:mashal_mobile/core/providers.dart';
import 'package:mashal_mobile/features/auth/domain/auth_tokens.dart';
import 'package:mashal_mobile/features/auth/domain/mobile_user.dart';

class FakeResponse {
  const FakeResponse(this.statusCode, [this.body]);

  final int statusCode;
  final Object? body;
}

typedef FakeHandler = FutureOr<FakeResponse> Function(RequestOptions options);

/// In-memory HTTP transport: records requests and answers from [handler].
class FakeHttpAdapter implements HttpClientAdapter {
  FakeHttpAdapter(this.handler);

  FakeHandler handler;
  final List<RequestOptions> requests = [];

  int count(String path) => requests.where((r) => r.path == path).length;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    final response = await handler(options);
    final body = response.body;
    if (body == null) {
      return ResponseBody.fromString('', response.statusCode);
    }
    return ResponseBody.fromString(
      jsonEncode(body),
      response.statusCode,
      headers: {
        Headers.contentTypeHeader: [
          response.statusCode >= 400 ? 'application/problem+json' : Headers.jsonContentType,
        ],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class InMemoryTokenStore implements TokenStore {
  StoredSession? session;

  @override
  Future<StoredSession?> read() async => session;

  @override
  Future<void> save(AuthTokens tokens, {MobileUser? user}) async {
    session = StoredSession(tokens: tokens, user: user ?? session?.user);
  }

  @override
  Future<void> saveUser(MobileUser user) async {
    final current = session;
    if (current != null) {
      session = StoredSession(tokens: current.tokens, user: user);
    }
  }

  @override
  Future<void> clear() async => session = null;

  @override
  Future<String> deviceId() async => 'test-device';
}

const testConfig = AppConfig(apiBaseUrl: 'https://mashal.test', requireHttps: true);

Map<String, dynamic> decoded(Map<String, Object?> value) =>
    jsonDecode(jsonEncode(value)) as Map<String, dynamic>;

Map<String, Object?> userJson({bool operations = true, bool finance = true}) => {
      'userId': 7,
      'username': 'operator1',
      'displayName': 'Operator One',
      'role': 'Operator',
      'permissions': ['ManageData'],
      'navigation': ['Dashboard', 'Operations'],
      'capabilities': {
        'dashboard': true,
        'operations': operations,
        'inventory': true,
        'sales': true,
        'finance': finance,
        'reports': finance,
        'manageData': true,
      },
      'company': {'id': 1, 'name': 'Saddiqi Group', 'namePersian': 'گروه صدیقی'},
      'language': 'fa-AF',
    };

MobileUser testUser({bool operations = true, bool finance = true}) =>
    MobileUser.fromJson(decoded(userJson(operations: operations, finance: finance)));

AuthTokens testTokens({
  String access = 'access-1',
  String refresh = 'refresh-1',
  Duration accessExpiresIn = const Duration(minutes: 15),
}) =>
    AuthTokens(
      accessToken: access,
      accessTokenExpiresAt: DateTime.now().toUtc().add(accessExpiresIn),
      refreshToken: refresh,
      refreshTokenExpiresAt: DateTime.now().toUtc().add(const Duration(days: 30)),
    );

Map<String, Object?> authJson({String access = 'access-1', String refresh = 'refresh-1'}) => {
      'tokenType': 'Bearer',
      'accessToken': access,
      'accessTokenExpiresAtUtc': DateTime.now().toUtc().add(const Duration(minutes: 15)).toIso8601String(),
      'refreshToken': refresh,
      'refreshTokenExpiresAtUtc': DateTime.now().toUtc().add(const Duration(days: 30)).toIso8601String(),
      'user': userJson(),
    };

Map<String, Object?> problem(String code, {int status = 401, String detail = 'server message'}) =>
    {'status': status, 'code': code, 'detail': detail, 'traceId': 'trace-1'};

Map<String, Object?> dashboardJson({bool withFinance = true}) => {
      'asOfUtc': '2026-09-15T06:30:00Z',
      'businessDate': '2026-09-15',
      'currency': 'USD',
      'inventory': {'totalMt': 18420.5, 'lowStockTankCount': 2},
      'goodsInTransit': withFinance
          ? {
              'totalMt': 6310.0,
              'loadCount': 50,
              'fromOriginCount': 9,
              'internalTransferCount': 4,
              'customerDeliveryCount': 37,
              'delayedCount': 3,
            }
          : null,
      'todaySales': {'amountUsd': 412750.0, 'count': 11},
      'receivables': withFinance
          ? {'totalUsd': 2184300.0, 'customerCount': 23, 'customerAdvancesUsd': 15000.0}
          : null,
      'cashPosition': withFinance ? {'totalUsd': 956120.0, 'accountCount': 8} : null,
      'todayProfit': withFinance
          ? {
              'revenueUsd': 412750.0,
              'costOfGoodsSoldUsd': 373810.0,
              'grossProfitUsd': 38940.0,
              'saleCount': 11,
              'costedSaleCount': 9,
              'uncostedSaleCount': 2,
              'confidence': 'needsReview',
            }
          : null,
      'activeShipments': [
        {
          'id': 101,
          'code': 'SH-1405-021',
          'subtitle': 'Diesel',
          'loadedMt': 1850.0,
          'totalMt': 2400.0,
          'progressPercent': 77.1,
          'statusText': 'در مسیر',
        },
      ],
      'alerts': [
        {
          'code': 'low_stock',
          'severity': 'warning',
          'title': 'موجودی کم',
          'message': 'T-07',
          'reference': 'T-07',
        },
      ],
      'pendingActions': [
        {'code': 'loadings_without_receipt', 'title': 'بارگیری بدون رسید', 'count': 6},
        {'code': 'shortage', 'title': 'کسری', 'count': 0},
      ],
    };

ProviderContainer makeContainer({required FakeHttpAdapter adapter, required InMemoryTokenStore store}) {
  return ProviderContainer(overrides: [
    appConfigProvider.overrideWithValue(testConfig),
    httpClientAdapterProvider.overrideWithValue(adapter),
    tokenStoreProvider.overrideWithValue(store),
  ]);
}
