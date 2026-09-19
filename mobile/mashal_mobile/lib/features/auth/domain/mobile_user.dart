import '../../../core/utils/json.dart';

/// Contract of `GET /api/mobile/v1/me`. Used only to show or hide UI —
/// the server enforces every permission independently.
class MobileUser {
  const MobileUser({
    required this.userId,
    required this.username,
    required this.displayName,
    required this.role,
    required this.permissions,
    required this.navigation,
    required this.capabilities,
    required this.language,
    this.company,
  });

  factory MobileUser.fromJson(Map<String, dynamic> json) => MobileUser(
        userId: asInt(json['userId']),
        username: asString(json['username']),
        displayName: asString(json['displayName']),
        role: asString(json['role']),
        permissions: asStringList(json['permissions']),
        navigation: asStringList(json['navigation']),
        capabilities: MobileCapabilities.fromJson(asMap(json['capabilities']) ?? const {}),
        language: asNullableString(json['language']) ?? 'fa-AF',
        company: switch (asMap(json['company'])) {
          final Map<String, dynamic> company => MobileCompany.fromJson(company),
          _ => null,
        },
      );

  final int userId;
  final String username;
  final String displayName;
  final String role;
  final List<String> permissions;
  final List<String> navigation;
  final MobileCapabilities capabilities;
  final String language;
  final MobileCompany? company;

  Map<String, dynamic> toJson() => {
        'userId': userId,
        'username': username,
        'displayName': displayName,
        'role': role,
        'permissions': permissions,
        'navigation': navigation,
        'capabilities': capabilities.toJson(),
        'language': language,
        'company': company?.toJson(),
      };
}

class MobileCapabilities {
  const MobileCapabilities({
    required this.dashboard,
    required this.operations,
    required this.inventory,
    required this.sales,
    required this.finance,
    required this.reports,
    required this.manageData,
  });

  factory MobileCapabilities.fromJson(Map<String, dynamic> json) => MobileCapabilities(
        dashboard: asBool(json['dashboard']),
        operations: asBool(json['operations']),
        inventory: asBool(json['inventory']),
        sales: asBool(json['sales']),
        finance: asBool(json['finance']),
        reports: asBool(json['reports']),
        manageData: asBool(json['manageData']),
      );

  static const none = MobileCapabilities(
    dashboard: false,
    operations: false,
    inventory: false,
    sales: false,
    finance: false,
    reports: false,
    manageData: false,
  );

  final bool dashboard;
  final bool operations;
  final bool inventory;
  final bool sales;
  final bool finance;
  final bool reports;
  final bool manageData;

  Map<String, dynamic> toJson() => {
        'dashboard': dashboard,
        'operations': operations,
        'inventory': inventory,
        'sales': sales,
        'finance': finance,
        'reports': reports,
        'manageData': manageData,
      };
}

class MobileCompany {
  const MobileCompany({required this.id, required this.name, this.namePersian});

  factory MobileCompany.fromJson(Map<String, dynamic> json) => MobileCompany(
        id: asInt(json['id']),
        name: asString(json['name']),
        namePersian: asNullableString(json['namePersian']),
      );

  final int id;
  final String name;
  final String? namePersian;

  String get displayName {
    final persian = namePersian;
    return persian != null && persian.isNotEmpty ? persian : name;
  }

  Map<String, dynamic> toJson() => {'id': id, 'name': name, 'namePersian': namePersian};
}
