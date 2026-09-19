import 'package:intl/intl.dart';

/// Display formatting only. Values are already rounded by the server.
abstract final class Formatters {
  static final NumberFormat _whole = NumberFormat('#,##0', 'en_US');
  static final NumberFormat _oneDecimal = NumberFormat('#,##0.0', 'en_US');
  static final DateFormat _time = DateFormat('HH:mm');

  static String money(double value) => _whole.format(value);

  static String quantityMt(double value) => _oneDecimal.format(value);

  static String count(int value) => _whole.format(value);

  static String percent(double value) => '${value.toStringAsFixed(0)}٪';

  static String localTime(DateTime utc) => _time.format(utc.toLocal());

  static String toPersianDigits(String input) {
    const latin = '0123456789';
    const persian = '۰۱۲۳۴۵۶۷۸۹';
    final buffer = StringBuffer();
    for (final rune in input.runes) {
      final char = String.fromCharCode(rune);
      final index = latin.indexOf(char);
      buffer.write(index >= 0 ? persian[index] : char);
    }
    return buffer.toString();
  }
}
