namespace PTGOilSystem.Web.Models.ShipmentPnl;

public sealed class ShipmentPnlListItemViewModel
{
    public int Id { get; init; }
    public string ShipmentCode { get; init; } = string.Empty;
    public string? VesselName { get; init; }
    public DateTime? DepartureDate { get; init; }
    public DateTime? ArrivalDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractUnitText { get; init; } = "—";
    public string? ProductName { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? OriginName { get; init; }
    public string? DestinationName { get; init; }
    public decimal TotalSalesUsd { get; init; }
    public decimal TotalPurchaseCostUsd { get; init; }
    public decimal TotalOperationalExpensesUsd { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
    public int RelatedTransportLegCount { get; init; }
    public int RelatedSalesCount { get; init; }
    public int RelatedExpensesCount { get; init; }
    public int RelatedLedgerCount { get; init; }
}

public sealed class ShipmentPnlIndexViewModel
{
    public IReadOnlyList<ShipmentPnlListItemViewModel> Items { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalCount { get; init; }
}

public sealed class ShipmentPnlSalesItemViewModel
{
    public int Id { get; init; }
    public DateTime SaleDate { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public string? ContractNumber { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal TotalUsd { get; init; }
    // true = فروش مستقیم از محموله (ShipmentId روی خود فروش)، false = فروش پس از ورود به مخزن که
    // Lineage آن به همین محموله می‌رسد. برای تفکیک نمایشی؛ هر فروش فقط یک بار در جمع می‌آید.
    public bool IsDirectShipmentSale { get; init; }
    public IReadOnlyList<ShipmentContractBreakdownLine> ContractBreakdownLines { get; init; } = [];
}

public sealed class ShipmentPnlExpenseItemViewModel
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
    public string? ContractNumber { get; init; }
    public string? TruckDispatchLabel { get; init; }
    public decimal AllocationQuantityMt { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public bool IsCustoms { get; init; }
    /// <summary>ExpenseType.Category دیتابیس؛ ورودی ساخت‌یافتهٔ دسته‌بندی نمایشی.</summary>
    public string? ExpenseTypeCategory { get; init; }
}

public sealed class ShipmentPnlTransportLegItemViewModel
{
    public int Id { get; init; }
    public DateTime LoadedDate { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractUnitText { get; init; } = "-";
    public string? ProductName { get; init; }
    public string? SupplierName { get; init; }
    public string? TransportReference { get; init; }
    public string? DocumentReference { get; init; }
    public string? TransportGroupKey { get; init; }
    public string TransportTypeName { get; init; } = "-";
    public string TransportStatusName { get; init; } = "-";
    public bool IsOriginalVesselMovement { get; init; }
    // مبدأ این حمل یک مخزن است؛ یعنی سفر بعد از تخلیهٔ کشتی (نه مرحلهٔ اولِ کشتی→مخزن).
    public bool SourceIsStorageTank { get; init; }
    public bool IsDraft { get; init; }
    public string SourceName { get; init; } = "-";
    public string DestinationName { get; init; } = "-";
    public string? Notes { get; init; }
    public decimal QuantityMt { get; init; }
    public bool HasOutboundInventoryMovement { get; init; }
    public decimal? PurchaseUnitCostUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public string CostSource { get; init; } = string.Empty;
    public decimal ReceivedQuantityMt { get; init; }
    public decimal ShortageQuantityMt { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal SalesUsd { get; init; }
    public decimal OperationalExpensesUsd { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
    public decimal UnsoldQuantityMt { get; init; }
    public string SalesTraceNote { get; init; } = string.Empty;
}

public sealed class ShipmentPnlLedgerItemViewModel
{
    public int Id { get; init; }
    public DateTime EntryDate { get; init; }
    public string SideName { get; init; } = string.Empty;
    public decimal AmountUsd { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public string? Reference { get; init; }
    public string Description { get; init; } = string.Empty;
}

// یک قرارداد خرید داخل محموله، با مقدار تخصیص/استفاده‌شده/باقی‌مانده (فقط نمایش).
public sealed class ShipmentContractLineViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = string.Empty;
    public string? SupplierName { get; init; }
    public string? ProductName { get; init; }
    public string ContractUnitText { get; init; } = "-";
    public decimal AllocatedQuantityMt { get; init; }
    // آیا این قرارداد نرخ نهاییِ خرید دارد؟ اگر نه، بهای خرید این مقدار در «سود کل محموله»
    // شمرده نمی‌شود (چون TotalPurchaseCostUsd فقط قراردادهای دارای نرخ نهایی را جمع می‌زند).
    public bool HasFinalPrice { get; init; }
    // Only the original vessel stage consumes the shipment allocation.
    public decimal UsedQuantityMt { get; init; }
    public decimal TransportedFromInventoryQuantityMt { get; init; }
    public decimal TransportShortageQuantityMt { get; init; }
    public decimal DirectLossQuantityMt { get; init; }
    public decimal RemainingBeforeLossQuantityMt
        => decimal.Round(AllocatedQuantityMt - UsedQuantityMt, 4, MidpointRounding.AwayFromZero);
    public decimal RemainingQuantityMt
        => decimal.Round(Math.Max(RemainingBeforeLossQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    public decimal LoadedAvailableBeforeDirectLossQuantityMt
        => decimal.Round(Math.Max(UsedQuantityMt - TransportShortageQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    public decimal ShortageRegisterableQuantityMt
        => decimal.Round(Math.Max(LoadedAvailableBeforeDirectLossQuantityMt - DirectLossQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    // نرخ نهایی و ارزش تخمینی قرارداد (USD) — فقط نمایش؛ از ContractPricingAdapter موجود گرفته می‌شود.
    public decimal? UnitPriceUsd { get; init; }
    public decimal? TotalValueUsd { get; init; }
    public decimal? DirectLossValueUsd
        => UnitPriceUsd.HasValue && UnitPriceUsd.Value > 0m && DirectLossQuantityMt > 0m
            ? decimal.Round(DirectLossQuantityMt * UnitPriceUsd.Value, 2, MidpointRounding.AwayFromZero)
            : null;
}

public sealed class ShipmentPnlDispatchTraceItemViewModel
{
    public int DeliveryReceiptId { get; init; }
    public DateTime ReceiptDate { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public string? DocumentReference { get; init; }
    public int? TruckDispatchId { get; init; }
    public DateTime? DispatchDate { get; init; }
    public string? TruckPlateNumber { get; init; }
    public decimal? LoadedQuantityMt { get; init; }
}

public sealed class ShipmentPnlRegisteredVesselReceiptItemViewModel
{
    public int Id { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string ContractNumber { get; init; } = "-";
    public string DestinationTerminalName { get; init; } = "-";
    public string? DestinationTankName { get; init; }
    public decimal ReceivedQuantityMt { get; init; }

    // برای deep-link «حمل بعدی از این مخزن» (پیش‌پرکردن مبدأ در فرم انتقال از موجودی).
    public int? DestinationTerminalId { get; init; }
    public int? DestinationStorageTankId { get; init; }
    public int ProductId { get; init; }

    public bool CanChainOnward => DestinationTerminalId.HasValue
        && DestinationStorageTankId.HasValue
        && ProductId > 0;
}

// یک دریافت/پرداخت نقدیِ وصل‌شده به همین کشتی (از PaymentTransaction.ShipmentId).
// فقط نمایش؛ هیچ محاسبهٔ حاشیهٔ سود به آن وابسته نیست.
public sealed class ShipmentPnlCustomerReceiptItemViewModel
{
    public int Id { get; init; }
    public DateTime PaymentDate { get; init; }
    public string? CustomerName { get; init; }
    public string? Reference { get; init; }
    public string? CashAccountName { get; init; }
    public decimal AmountUsd { get; init; }
    public bool IsInflow { get; init; }
    public decimal SignedAmountUsd => IsInflow ? AmountUsd : -AmountUsd;
}

public sealed class ShipmentPnlDetailsViewModel
{
    public int Id { get; init; }
    public string ShipmentCode { get; init; } = string.Empty;
    public int? VesselId { get; init; }
    public string? VesselName { get; init; }
    public DateTime? DepartureDate { get; init; }
    public DateTime? ArrivalDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? Notes { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractUnitText { get; init; } = "—";
    public string? CompanyName { get; init; }
    public string? ProductName { get; init; }
    public string? CustomerName { get; init; }
    public string? SupplierName { get; init; }
    public string? OriginName { get; init; }
    public string? DestinationName { get; init; }
    public decimal TotalSalesUsd { get; init; }
    public decimal TotalPurchaseCostUsd { get; init; }
    public decimal TotalOperationalExpensesUsd { get; init; }
    public decimal TotalExpensesUsd { get; init; }
    public decimal GrossMarginUsd { get; init; }
    public int LedgerEntriesCount { get; init; }
    public decimal LedgerDebitTotalUsd { get; init; }
    public decimal LedgerCreditTotalUsd { get; init; }

    // دریافت‌های نقدیِ وصل‌شده به همین کشتی (PaymentTransaction با ShipmentId و طرف مشتری).
    // خالص = دریافت (In) منهای بازپرداخت به مشتری (Out). فقط نمایش.
    public IReadOnlyList<ShipmentPnlCustomerReceiptItemViewModel> CustomerReceipts { get; init; } = [];
    public decimal CustomerReceiptsUsd { get; init; }
    public decimal OutstandingReceivableUsd
        => decimal.Round(TotalSalesUsd - CustomerReceiptsUsd, 2, MidpointRounding.AwayFromZero);
    public decimal CollectionCoveragePercent
        => TotalSalesUsd > 0m
            ? decimal.Round(Math.Min(Math.Max(CustomerReceiptsUsd / TotalSalesUsd, 0m), 1m) * 100m, 1, MidpointRounding.AwayFromZero)
            : 0m;
    public IReadOnlyList<ShipmentPnlSalesItemViewModel> Sales { get; init; } = [];
    public IReadOnlyList<ShipmentPnlExpenseItemViewModel> Expenses { get; init; } = [];
    public IReadOnlyList<ShipmentPnlTransportLegItemViewModel> TransportLegs { get; init; } = [];
    public IReadOnlyList<ShipmentPnlLedgerItemViewModel> LedgerEntries { get; init; } = [];
    public IReadOnlyList<ShipmentPnlDispatchTraceItemViewModel> DispatchTraces { get; init; } = [];
    public IReadOnlyList<ShipmentPnlRegisteredVesselReceiptItemViewModel> RegisteredVesselReceipts { get; init; } = [];
    public decimal RegisteredVesselReceiptQuantityMt
        => decimal.Round(RegisteredVesselReceipts.Sum(r => r.ReceivedQuantityMt), 4, MidpointRounding.AwayFromZero);
    // قراردادهای خرید داخل این محموله (از ShipmentContracts) با مقدار باقی‌مانده.
    public IReadOnlyList<ShipmentContractLineViewModel> ContractLines { get; init; } = [];
    public decimal TotalCargoValueUsd
    {
        get
        {
            var contractValueUsd = ContractLines.Sum(c => c.TotalValueUsd ?? 0m);
            if (contractValueUsd > 0m)
            {
                return contractValueUsd;
            }

            // اگر ارزش قراردادی هنوز نرخ نداشته باشد، از مجموع مصرف خرید حمل‌ها استفاده می‌کنیم
            // تا کارت بالایی با جدول سود و زیان به تفکیک حمل هم‌خوان بماند.
            return TransportLegs.Sum(l => l.PurchaseCostUsd);
        }
    }
    // ضایعات مستقیم محموله + اخطارهای نمایشی (از منطق نمای کشتی منتقل شده؛ بدون محاسبهٔ مالی جدید).
    public IReadOnlyList<ShipmentJourneyLossItem> Losses { get; init; } = [];
    public IReadOnlyList<ShipmentSaleDisplayRow> SaleDisplayRows
        => ShipmentPnlDisplayGrouping.GroupSales(Sales);
    // تفکیک فروش منتسب به محموله (هر فروش فقط یک بار؛ جمع دو گروه = کل فروش).
    public decimal DirectSaleQuantityMt
        => decimal.Round(Sales.Where(s => s.IsDirectShipmentSale).Sum(s => s.QuantityMt), 4, MidpointRounding.AwayFromZero);
    public decimal DirectSaleUsd
        => decimal.Round(Sales.Where(s => s.IsDirectShipmentSale).Sum(s => s.TotalUsd), 4, MidpointRounding.AwayFromZero);
    public decimal StorageSaleQuantityMt
        => decimal.Round(Sales.Where(s => !s.IsDirectShipmentSale).Sum(s => s.QuantityMt), 4, MidpointRounding.AwayFromZero);
    public decimal StorageSaleUsd
        => decimal.Round(Sales.Where(s => !s.IsDirectShipmentSale).Sum(s => s.TotalUsd), 4, MidpointRounding.AwayFromZero);
    public bool HasStorageOriginSales => Sales.Any(s => !s.IsDirectShipmentSale);
    public bool HasDirectShipmentSales => Sales.Any(s => s.IsDirectShipmentSale);
    public IReadOnlyList<ShipmentExpenseDisplayRow> ExpenseDisplayRows
        => ShipmentPnlDisplayGrouping.GroupExpenses(Expenses);
    /// <summary>
    /// گروه‌های دسته‌بندی مصارف — منبع واحد کارت‌های KPI، گروه‌های تب «مصارف و گمرک»
    /// و جدول «جزئیات هزینه‌ها بر اساس دسته». View نباید خودش دسته‌بندی کند.
    /// </summary>
    public IReadOnlyList<ShipmentExpenseCategoryGroup> ExpenseCategoryGroups
        => ShipmentExpenseCategorizer.Group(ExpenseDisplayRows);
    public IReadOnlyList<ShipmentLossDisplayRow> LossDisplayRows
        => ShipmentPnlDisplayGrouping.GroupLosses(Losses, ContractLines);
    public IReadOnlyList<ShipmentTransportDisplayRow> TransportDisplayRows
        => ShipmentPnlDisplayGrouping.GroupTransports(TransportLegs);
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasLineageData { get; init; }
    public bool IsLineageCalculationActive { get; init; }
    public string? LineageBadgeText { get; init; }
    public string? LineageBadgeTone { get; init; }
    public string? OverallLineageConfidence { get; init; }
    public bool HasVerifiedLineage { get; init; }
    public bool HasEstimatedLineage { get; init; }
    public bool HasLegacyLineage { get; init; }
    public int NeedsReviewCount { get; init; }
    public IReadOnlyList<string> LineageWarnings { get; init; } = [];
    public decimal? LineageCargoInVesselMt { get; init; }
    public decimal? LineageArrivedReceivedMt { get; init; }
    public decimal? LineageInTransitMt { get; init; }
    public decimal? LineageInStockMt { get; init; }
    public decimal? LineageSoldMt { get; init; }
    public decimal? LineageSoldUsd { get; init; }
    public decimal? LineageLossMt { get; init; }
    public decimal? LineageExpenseUsd { get; init; }
    public decimal? LineageCustomsUsd { get; init; }
    public decimal LossQuantityMt { get; init; }
    public decimal DirectLossQuantityMt { get; init; }
    // مجموع مقدار ضایعاتی که مسئولیتش «ضرر شرکت» ثبت شده — از سود تحقق‌یافته کسر می‌شود.
    public decimal CompanyLossQuantityMt { get; init; }

    // ===== مرحله‌های مستقل جریان مقدار (MT) =====
    // Explicit stages prevent later inventory transfers from inflating vessel cargo.
    public decimal OriginalShipmentQuantityMt { get; init; }
    public decimal VesselUnloadedQuantityMt { get; init; }
    public decimal InventoryTransportedOutQuantityMt { get; init; }
    public decimal InTransitQuantityMt { get; init; }
    public decimal DeliveredAtDestinationQuantityMt { get; init; }
    public decimal RemainingInSourceTankQuantityMt { get; init; }
    public decimal InventoryTransportShortageQuantityMt { get; init; }
    public decimal CancelledTransportQuantityMt { get; init; }
    public decimal DraftTransportQuantityMt { get; init; }
    public bool HasExactSourceTankLineage { get; init; }

    // Compatibility aliases. New KPI/UI code must use the explicit properties.
    public decimal TransportQuantityMt => InventoryTransportedOutQuantityMt;
    public decimal ReceivedQuantityMt => VesselUnloadedQuantityMt;
    public decimal ShortageQuantityMt => InventoryTransportShortageQuantityMt;
    public decimal ShipmentSalesQuantityMt { get; init; }
    public decimal TraceSoldQuantityMt => TransportLegs.Sum(l => l.SoldQuantityMt);
    public decimal SoldQuantityMt
        => decimal.Round(Math.Max(ShipmentSalesQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    // ماندهٔ منبع از محاسبهٔ منبع‌محور Controller می‌آید.
    public decimal InStockQuantityMt => RemainingInSourceTankQuantityMt;
    public decimal DispatchedQuantityMt => DispatchTraces.Sum(d => d.ReceivedQuantityMt);
    // مجموع کسری/ضایعه برای کارت خلاصه.
    public decimal VesselUnloadingShortageQuantityMt
        => ContractLines.Sum(c => c.TransportShortageQuantityMt);
    public decimal TotalLossAndShortageMt
        => VesselUnloadingShortageQuantityMt + InventoryTransportShortageQuantityMt + DirectLossQuantityMt;
    // ضایعات واقعی و سنددار = مجموع LossEventهای ثبت‌شدهٔ محموله (همان ردیف‌های تب کسری‌ها/ضایعات).
    // بدون هیچ اختلاف مقدارِ بی‌سند؛ اگر کاربر LossEvent ثبت نکرده باشد صفر است.
    public decimal RecordedLossQuantityMt => LossQuantityMt;
    // کسری مشتق (اختلاف بارگیری/رسید در لِج‌های ریشه و انتقال) — سند «ضایعات» نیست و جدا نمایش داده می‌شود.
    public decimal TotalTransportShortageMt
        => VesselUnloadingShortageQuantityMt + InventoryTransportShortageQuantityMt;
    public decimal RemainingUnsoldQuantityMt
        => decimal.Round(
            Math.Max(OriginalShipmentQuantityMt - SoldQuantityMt - TotalLossAndShortageMt, 0m),
            4,
            MidpointRounding.AwayFromZero);
    // «باقی روی کشتی» = بار محموله منهای هر مقدار که از بار کشتی خارج شده:
    //   حمل از موجودی (موتر/واگون) + فروخته‌شده + کسری تخلیهٔ کشتی→مخزن + ضایعهٔ مستقیم.
    // به‌خواستهٔ کاربر، فروش به‌صورت کامل کسر می‌شود (حتی اگر از روی واگنِ حمل‌شده فروخته شده باشد)؛
    // یعنی حمل و فروش هر دو به‌عنوان «خروج از کشتی» شمرده می‌شوند. کسری حمل جدا کسر نمی‌شود چون
    // بخشی از «حمل‌شده» است. این رقم فقط نمایشی است و به P&L/ارزش موجودی کاری ندارد (آن‌ها RemainingUnsold را می‌بینند).
    public decimal RemainingInVesselTankQuantityMt
        => decimal.Round(
            Math.Max(
                OriginalShipmentQuantityMt
                - InventoryTransportedOutQuantityMt
                - SoldQuantityMt
                - VesselUnloadingShortageQuantityMt
                - DirectLossQuantityMt,
                0m),
            4,
            MidpointRounding.AwayFromZero);
    // مجموع مقدار باقی‌ماندهٔ همهٔ قراردادها — برای فعال/غیرفعال‌کردن دکمهٔ «انتقال از موجودی».
    public decimal TotalRemainingQuantityMt => ContractLines.Sum(c => c.RemainingQuantityMt);
    public decimal TotalRemainingBeforeLossQuantityMt => ContractLines.Sum(c => c.RemainingBeforeLossQuantityMt);
    public decimal TotalAllocatedQuantityMt => ContractLines.Sum(c => c.AllocatedQuantityMt);
    public decimal TotalUsedQuantityMt => ContractLines.Sum(c => c.UsedQuantityMt);
    public decimal TotalShortageRegisterableQuantityMt => ContractLines.Sum(c => c.ShortageRegisterableQuantityMt);
    public decimal AvailableForSaleOrReceiptQuantityMt => RemainingInSourceTankQuantityMt;
    public decimal ShipShortageLoadedQuantityMt
    {
        get
        {
            if (TotalAllocatedQuantityMt > 0m)
            {
                return TotalAllocatedQuantityMt;
            }

            if (QuantityMt > 0m)
            {
                return QuantityMt;
            }

            return OriginalShipmentQuantityMt;
        }
    }
    public decimal ShipShortageQuantityMt
    {
        get
        {
            var loss = Math.Max(TotalLossAndShortageMt, 0m);
            return ShipShortageLoadedQuantityMt > 0m
                ? Math.Min(loss, ShipShortageLoadedQuantityMt)
                : loss;
        }
    }
    public decimal ShipShortageActualQuantityMt
        => decimal.Round(Math.Max(ShipShortageLoadedQuantityMt - ShipShortageQuantityMt, 0m), 4, MidpointRounding.AwayFromZero);
    public decimal ShipShortagePercent
        => ShipShortageLoadedQuantityMt > 0m
            ? decimal.Round(ShipShortageQuantityMt / ShipShortageLoadedQuantityMt * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
    public decimal AverageCostPerMtUsd
    {
        get
        {
            var contractQuantity = ContractLines.Sum(c => c.AllocatedQuantityMt);
            var contractValue = ContractLines.Sum(c => c.TotalValueUsd ?? 0m);
            if (contractQuantity > 0m && contractValue > 0m)
            {
                return decimal.Round(contractValue / contractQuantity, 6, MidpointRounding.AwayFromZero);
            }

            if (OriginalShipmentQuantityMt > 0m && TotalPurchaseCostUsd > 0m)
            {
                return decimal.Round(TotalPurchaseCostUsd / OriginalShipmentQuantityMt, 6, MidpointRounding.AwayFromZero);
            }

            return 0m;
        }
    }
    public decimal RealizedPurchaseCostUsd
        => decimal.Round(SoldQuantityMt * AverageCostPerMtUsd, 4, MidpointRounding.AwayFromZero);
    public decimal RealizedOperationalExpensesUsd
    {
        get
        {
            if (TotalOperationalExpensesUsd <= 0m || SoldQuantityMt <= 0m)
            {
                return 0m;
            }

            if (OriginalShipmentQuantityMt <= 0m)
            {
                return TotalOperationalExpensesUsd;
            }

            var soldShare = Math.Min(SoldQuantityMt / OriginalShipmentQuantityMt, 1m);
            return decimal.Round(
                TotalOperationalExpensesUsd * soldShare,
                4,
                MidpointRounding.AwayFromZero);
        }
    }
    // بهای خرید مقدار ضایعاتِ «ضرر شرکت» (ارزش خودِ جنس از دست رفته).
    public decimal CompanyLossPurchaseCostUsd
        => decimal.Round(CompanyLossQuantityMt * AverageCostPerMtUsd, 4, MidpointRounding.AwayFromZero);
    // سهم مصارف عملیاتی نسبت به همان مقدار ضایعاتِ «ضرر شرکت».
    public decimal CompanyLossExpenseShareUsd
    {
        get
        {
            if (TotalOperationalExpensesUsd <= 0m || CompanyLossQuantityMt <= 0m || OriginalShipmentQuantityMt <= 0m)
            {
                return 0m;
            }

            var lossShare = Math.Min(CompanyLossQuantityMt / OriginalShipmentQuantityMt, 1m);
            return decimal.Round(TotalOperationalExpensesUsd * lossShare, 4, MidpointRounding.AwayFromZero);
        }
    }
    // مجموع کسرِ «ضرر شرکت» از سود: هم بهای جنس ضایع‌شده، هم سهم مصارف آن.
    public decimal CompanyLossDeductionUsd
        => decimal.Round(CompanyLossPurchaseCostUsd + CompanyLossExpenseShareUsd, 4, MidpointRounding.AwayFromZero);
    // ===== منبع قطعی و یگانهٔ نتیجهٔ سود/زیان =====
    // این تنها عددِ نتیجه است؛ کارت KPI، خلاصهٔ حساب و تب سود و زیان همگی باید همین را نشان دهند.
    // = درآمد واقعی فروش − بهای خرید مقدار فروخته‌شده − هزینه‌های قطعی همان مقدار − زیان قطعی غیرقابل‌وصول (ضرر شرکت).
    public decimal RealizedGrossMarginUsd
        => decimal.Round(
            TotalSalesUsd - RealizedPurchaseCostUsd - RealizedOperationalExpensesUsd - CompanyLossDeductionUsd,
            4,
            MidpointRounding.AwayFromZero);
    public bool NetResultIsProfit => RealizedGrossMarginUsd >= 0m;
    public decimal NetResultAbsUsd => Math.Abs(RealizedGrossMarginUsd);
    public bool IsFullySold => RemainingUnsoldQuantityMt <= 0.0001m;
    // ارزش موجودی باقی‌مانده به بهای خرید — جدا از سود؛ هرگز با درآمد/سود تحقق‌یافته مخلوط نمی‌شود.
    public decimal RemainingInventoryValueUsd
        => AverageCostPerMtUsd > 0m && RemainingUnsoldQuantityMt > 0m
            ? decimal.Round(RemainingUnsoldQuantityMt * AverageCostPerMtUsd, 2, MidpointRounding.AwayFromZero)
            : 0m;
    // نرخ نهایی خرید بعضی قراردادها ثبت نشده → بهای خرید ناقص است و اعداد سود قابل‌اعتماد نیستند.
    public bool PurchasePricingIncomplete
        => ContractLines.Any(c => c.AllocatedQuantityMt > 0.0001m && !c.HasFinalPrice);
    public decimal EstimatedShortageValueUsd => EstimateLossValueUsd(ShipShortageQuantityMt);
    public decimal EstimatedDirectLossValueUsd => ContractLines.Sum(c => c.DirectLossValueUsd ?? 0m);
    public decimal EstimateLossValueUsd(decimal quantityMt)
        => AverageCostPerMtUsd > 0m && quantityMt > 0m
            ? decimal.Round(quantityMt * AverageCostPerMtUsd, 2, MidpointRounding.AwayFromZero)
            : 0m;
    // وضعیت سادهٔ محموله برای نمایش (بدون منطق مالی).
    public string SimpleStatusFa
    {
        get
        {
            if (ContractLines.Count == 0)
            {
                return "نیاز به قرارداد";
            }
            if (TotalUsedQuantityMt <= 0m)
            {
                return "آماده عملیات";
            }
            return TotalRemainingQuantityMt > 0.0001m ? "در جریان" : "تکمیل‌شده";
        }
    }
}

public static class ShipmentShortageResponsibilityTypes
{
    public const string CompanyLoss = "CompanyLoss";
    public const string SupplierDeduction = "SupplierDeduction";
    public const string ServiceProviderClaim = "ServiceProviderClaim";
    public const string PartnerShareDeduction = "PartnerShareDeduction";
    public const string Split = "Split";
}

public sealed class ShipmentDirectLossCreateViewModel
{
    public int ShipmentId { get; set; }
    public DateTime EventDate { get; set; } = DateTime.UtcNow.Date;
    public decimal LossQuantityMt { get; set; }
    public decimal? LossAmountUsd { get; set; }
    public string ResponsibilityType { get; set; } = ShipmentShortageResponsibilityTypes.CompanyLoss;
    public string? ResponsiblePartyName { get; set; }
    public int? ContractId { get; set; }
    public string? ClaimStatus { get; set; }
    public string? Notes { get; set; }
    public string? ReturnUrl { get; set; }
    public List<ShipmentDirectLossSplitLineInput> SplitLines { get; set; } = [];
}

public sealed class ShipmentDirectLossSplitLineInput
{
    public string? ResponsibilityType { get; set; }
    public string? ResponsiblePartyName { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal? AmountUsd { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
}
