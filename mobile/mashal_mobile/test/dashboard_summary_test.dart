import 'package:flutter_test/flutter_test.dart';
import 'package:mashal_mobile/core/format/formatters.dart';
import 'package:mashal_mobile/core/utils/json.dart';
import 'package:mashal_mobile/core/utils/uuid.dart';
import 'package:mashal_mobile/features/home/domain/dashboard_summary.dart';

import 'support/fakes.dart';

void main() {
  group('DashboardSummary.fromJson', () {
    test('parses the server contract', () {
      final summary = DashboardSummary.fromJson(decoded(dashboardJson()));

      expect(summary.businessDate, '2026-09-15');
      expect(summary.inventory!.totalMt, 18420.5);
      expect(summary.goodsInTransit!.delayedCount, 3);
      expect(summary.receivables!.customerAdvancesUsd, 15000.0);
      expect(summary.todayProfit!.confidence, PnlConfidence.needsReview);
      expect(summary.activeShipments.single.progressPercent, 77.1);
      expect(summary.alerts.single.severity, AlertSeverity.warning);
      expect(summary.openPendingActions, hasLength(1));
      expect(summary.attentionCount, 2);
    });

    test('sections the user may not see arrive as null', () {
      final summary = DashboardSummary.fromJson(decoded(dashboardJson(withFinance: false)));

      expect(summary.inventory, isNotNull);
      expect(summary.todaySales, isNotNull);
      expect(summary.receivables, isNull);
      expect(summary.cashPosition, isNull);
      expect(summary.todayProfit, isNull);
      expect(summary.goodsInTransit, isNull);
    });

    test('unknown profit confidence is never treated as verified', () {
      final json = decoded(dashboardJson());
      (json['todayProfit'] as Map<String, dynamic>)['confidence'] = 'somethingNew';

      expect(DashboardSummary.fromJson(json).todayProfit!.confidence, PnlConfidence.needsReview);
    });
  });

  group('helpers', () {
    test('timestamps without an offset are read as UTC', () {
      final parsed = asUtcDateTime('2026-09-15T06:30:00');
      expect(parsed, DateTime.utc(2026, 9, 15, 6, 30));
    });

    test('formats money and quantity', () {
      expect(Formatters.money(2184300), '2,184,300');
      expect(Formatters.quantityMt(18420.5), '18,420.5');
      expect(Formatters.toPersianDigits('12,450'), '۱۲,۴۵۰');
    });

    test('generates RFC 4122 v4 UUIDs', () {
      final value = newUuidV4();
      expect(RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$').hasMatch(value), isTrue);
      expect(newUuidV4(), isNot(value));
    });
  });
}
