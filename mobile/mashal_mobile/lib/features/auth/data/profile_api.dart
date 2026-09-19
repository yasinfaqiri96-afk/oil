import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/network/api_client.dart';
import '../../../core/providers.dart';
import '../domain/mobile_user.dart';

class ProfileApi {
  ProfileApi(this._client);

  static const mePath = '/api/mobile/v1/me';

  final ApiClient _client;

  Future<MobileUser> me() async => MobileUser.fromJson(await _client.getJson(mePath));
}

final profileApiProvider = Provider<ProfileApi>((ref) => ProfileApi(ref.watch(apiClientProvider)));
