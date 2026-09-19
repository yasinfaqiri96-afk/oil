/// Contract of `GET /api/mobile/v1/dashboard` (server: MobileDashboardResponse).
/// A section the user may not see on the web is sent as null and is not shown.
/// Every number is authoritative from the server; the app only formats it.
library;

import '../../../core/utils/json.dart';

enum PnlConfidence { verified, estimated, legacy, needsReview }

enum AlertSeverity { info, warning, critical }

T? _section<T>(Object? value, T Function(Map<String, dynamic>) parse) =>
    switch (asMap(value)) { final Map<String, dynamic> map => parse(map), _ => null };

class DashboardSummary {
  const DashboardSummary({
    required this.asOfUtc,
    required this.businessDate,
    required this.currency,
    required this.activeShipments,
    required this.alerts,
    required this.pendingActions,
    this.inventory,
    this.goodsInTransit,
    this.todaySales,
    this.receivables,
    this.cashPosition,
    this.todayProfit,
  });

  factory DashboardSummary.fromJson(Map<String, dynamic> json) => DashboardSummary(
        asOfUtc: asUtcDateTime(json['asOfUtc']) ?? DateTime.now().toUtc(),
        businessDate: asString(json['businessDate']),
        currency: asNullableString(json['currency']) ?? 'USD',
        inventory: _section(json['inventory'], InventoryKpi.fromJson),
        goodsInTransit: _section(json['goodsInTransit'], GoodsInTransitKpi.fromJson),
        todaySales: _section(json['todaySales'], TodaySalesKpi.fromJson),
        receivables: _section(json['receivables'], ReceivablesKpi.fromJson),
        cashPosition: _section(json['cashPosition'], CashPositionKpi.fromJson),
        todayProfit: _section(json['todayProfit'], TodayProfitKpi.fromJson),
        activeShipments: asMapList(json['activeShipments']).map(ActiveShipment.fromJson).toList(),
        alerts: asMapList(json['alerts']).map(DashboardAlert.fromJson).toList(),
        pendingActions: asMapList(json['pendingActions']).map(PendingAction.fromJson).toList(),
      );

  final DateTime asOfUtc;

  /// Kabul business date from the server clock (yyyy-MM-dd).
  final String businessDate;
  final String currency;
  final InventoryKpi? inventory;
  final GoodsInTransitKpi? goodsInTransit;
  final TodaySalesKpi? todaySales;
  final ReceivablesKpi? receivables;
  final CashPositionKpi? cashPosition;
  final TodayProfitKpi? todayProfit;
  final List<ActiveShipment> activeShipments;
  final List<DashboardAlert> alerts;
  final List<PendingAction> pendingActions;

  List<PendingAction> get openPendingActions => pendingActions.where((a) => a.count > 0).toList();

  int get attentionCount => alerts.length + openPendingActions.length;
}

class InventoryKpi {
  const InventoryKpi({required this.totalMt, required this.lowStockTankCount});

  factory InventoryKpi.fromJson(Map<String, dynamic> json) => InventoryKpi(
        totalMt: asDouble(json['totalMt']),
        lowStockTankCount: asInt(json['lowStockTankCount']),
      );

  final double totalMt;
  final int lowStockTankCount;
}

class GoodsInTransitKpi {
  const GoodsInTransitKpi({
    required this.totalMt,
    required this.loadCount,
    required this.fromOriginCount,
    required this.internalTransferCount,
    required this.customerDeliveryCount,
    required this.delayedCount,
  });

  factory GoodsInTransitKpi.fromJson(Map<String, dynamic> json) => GoodsInTransitKpi(
        totalMt: asDouble(json['totalMt']),
        loadCount: asInt(json['loadCount']),
        fromOriginCount: asInt(json['fromOriginCount']),
        internalTransferCount: asInt(json['internalTransferCount']),
        customerDeliveryCount: asInt(json['customerDeliveryCount']),
        delayedCount: asInt(json['delayedCount']),
      );

  final double totalMt;
  final int loadCount;
  final int fromOriginCount;
  final int internalTransferCount;
  final int customerDeliveryCount;
  final int delayedCount;
}

class TodaySalesKpi {
  const TodaySalesKpi({required this.amountUsd, required this.count});

  factory TodaySalesKpi.fromJson(Map<String, dynamic> json) =>
      TodaySalesKpi(amountUsd: asDouble(json['amountUsd']), count: asInt(json['count']));

  final double amountUsd;
  final int count;
}

class ReceivablesKpi {
  const ReceivablesKpi({required this.totalUsd, required this.customerCount, required this.customerAdvancesUsd});

  factory ReceivablesKpi.fromJson(Map<String, dynamic> json) => ReceivablesKpi(
        totalUsd: asDouble(json['totalUsd']),
        customerCount: asInt(json['customerCount']),
        customerAdvancesUsd: asDouble(json['customerAdvancesUsd']),
      );

  final double totalUsd;
  final int customerCount;
  final double customerAdvancesUsd;
}

class CashPositionKpi {
  const CashPositionKpi({required this.totalUsd, required this.accountCount});

  factory CashPositionKpi.fromJson(Map<String, dynamic> json) =>
      CashPositionKpi(totalUsd: asDouble(json['totalUsd']), accountCount: asInt(json['accountCount']));

  final double totalUsd;
  final int accountCount;
}

class TodayProfitKpi {
  const TodayProfitKpi({
    required this.revenueUsd,
    required this.costOfGoodsSoldUsd,
    required this.grossProfitUsd,
    required this.saleCount,
    required this.costedSaleCount,
    required this.uncostedSaleCount,
    required this.confidence,
  });

  factory TodayProfitKpi.fromJson(Map<String, dynamic> json) => TodayProfitKpi(
        revenueUsd: asDouble(json['revenueUsd']),
        costOfGoodsSoldUsd: asDouble(json['costOfGoodsSoldUsd']),
        grossProfitUsd: asDouble(json['grossProfitUsd']),
        saleCount: asInt(json['saleCount']),
        costedSaleCount: asInt(json['costedSaleCount']),
        uncostedSaleCount: asInt(json['uncostedSaleCount']),
        confidence: switch (asString(json['confidence'])) {
          'verified' => PnlConfidence.verified,
          'estimated' => PnlConfidence.estimated,
          'legacy' => PnlConfidence.legacy,
          // Unknown values are never shown as verified profit.
          _ => PnlConfidence.needsReview,
        },
      );

  final double revenueUsd;
  final double costOfGoodsSoldUsd;
  final double grossProfitUsd;
  final int saleCount;
  final int costedSaleCount;
  final int uncostedSaleCount;
  final PnlConfidence confidence;
}

class ActiveShipment {
  const ActiveShipment({
    required this.id,
    required this.code,
    required this.subtitle,
    required this.loadedMt,
    required this.totalMt,
    required this.progressPercent,
    required this.statusText,
  });

  factory ActiveShipment.fromJson(Map<String, dynamic> json) => ActiveShipment(
        id: asInt(json['id']),
        code: asString(json['code']),
        subtitle: asString(json['subtitle']),
        loadedMt: asDouble(json['loadedMt']),
        totalMt: asDouble(json['totalMt']),
        progressPercent: asDouble(json['progressPercent']).clamp(0.0, 100.0),
        statusText: asString(json['statusText']),
      );

  final int id;
  final String code;
  final String subtitle;
  final double loadedMt;
  final double totalMt;
  final double progressPercent;
  final String statusText;
}

class DashboardAlert {
  const DashboardAlert({
    required this.code,
    required this.severity,
    required this.title,
    required this.message,
    this.reference,
  });

  factory DashboardAlert.fromJson(Map<String, dynamic> json) => DashboardAlert(
        code: asString(json['code']),
        severity: switch (asString(json['severity'])) {
          'critical' => AlertSeverity.critical,
          'warning' => AlertSeverity.warning,
          _ => AlertSeverity.info,
        },
        title: asString(json['title']),
        message: asString(json['message']),
        reference: asNullableString(json['reference']),
      );

  final String code;
  final AlertSeverity severity;
  final String title;
  final String message;
  final String? reference;
}

class PendingAction {
  const PendingAction({required this.code, required this.title, required this.count});

  factory PendingAction.fromJson(Map<String, dynamic> json) => PendingAction(
        code: asString(json['code']),
        title: asString(json['title']),
        count: asInt(json['count']),
      );

  final String code;
  final String title;
  final int count;
}
