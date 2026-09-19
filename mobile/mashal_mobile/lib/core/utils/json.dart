/// Tolerant readers for server DTOs. Values are displayed as sent, never recalculated.
library;

double asDouble(Object? value) => switch (value) {
      final num number => number.toDouble(),
      final String text => double.tryParse(text) ?? 0.0,
      _ => 0.0,
    };

int asInt(Object? value) => switch (value) {
      final num number => number.toInt(),
      final String text => int.tryParse(text) ?? 0,
      _ => 0,
    };

String asString(Object? value) => value is String ? value : '';

String? asNullableString(Object? value) => value is String ? value : null;

bool asBool(Object? value) => value == true;

Map<String, dynamic>? asMap(Object? value) => value is Map<String, dynamic> ? value : null;

List<Map<String, dynamic>> asMapList(Object? value) =>
    value is List<Object?> ? value.whereType<Map<String, dynamic>>().toList() : const [];

List<String> asStringList(Object? value) =>
    value is List<Object?> ? value.whereType<String>().toList() : const [];

final RegExp _offsetSuffix = RegExp(r'(Z|[+-]\d{2}:?\d{2})$');

/// Server timestamps are UTC. A value without an offset is read as UTC, never as local time.
DateTime? asUtcDateTime(Object? value) {
  if (value is! String || value.isEmpty) return null;
  final normalized = _offsetSuffix.hasMatch(value) ? value : '${value}Z';
  return DateTime.tryParse(normalized)?.toUtc();
}
