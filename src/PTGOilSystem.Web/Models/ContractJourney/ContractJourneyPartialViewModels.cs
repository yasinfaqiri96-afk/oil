using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace PTGOilSystem.Web.Models.ContractJourney;

public sealed class ContractJourneyDetailTabLinkViewModel
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
}

public sealed class ContractJourneyDetailTabsRailViewModel
{
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string ActiveTab { get; init; } = ContractJourneyTabs.Details.Summary;
    public string AriaLabel { get; init; } = string.Empty;
    public string BackToIndexTab { get; init; } = ContractJourneyTabs.Index.Picker;
    public IReadOnlyList<ContractJourneyDetailTabLinkViewModel> Tabs { get; init; } = [];
}

public sealed class ContractJourneySummaryTitlebarViewModel
{
    public int ContractId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string BackLabel { get; init; } = string.Empty;
    public string BackToIndexTab { get; init; } = ContractJourneyTabs.Index.Picker;
    public bool HasStatusPill { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
}

public sealed class ContractJourneyRowDetailModalViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string CloseLabel { get; init; } = string.Empty;
    public string FieldLabel { get; init; } = string.Empty;
    public string ValueLabel { get; init; } = string.Empty;
}

public sealed class ContractJourneyHeroFactViewModel
{
    public string Icon { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class ContractJourneyHeroStageRowViewModel
{
    public string Number { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Tone { get; init; } = string.Empty;
    public IHtmlContent GaugeHtml { get; init; } = HtmlString.Empty;
}

public sealed class ContractJourneyHeroGraphBarViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Tone { get; init; } = string.Empty;
    public decimal Percent { get; init; }
}

public sealed class ContractJourneyOperationalHeroViewModel
{
    public int ContractId { get; init; }
    public string ActiveTab { get; init; } = string.Empty;
    public string AriaLabel { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ContractActionsLabel { get; init; } = string.Empty;
    public string BackLabel { get; init; } = string.Empty;
    public string PrintableDocumentLabel { get; init; } = string.Empty;
    public string TodayText { get; init; } = string.Empty;
    public bool ShowTodayPill { get; init; }
    public bool ShowFacts { get; init; }
    public bool ShowStatusCard { get; init; }
    public bool IsProfit { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusValue { get; init; } = string.Empty;
    public string NetProfitLabel { get; init; } = string.Empty;
    public string NetProfitValue { get; init; } = string.Empty;
    public string GraphTitle { get; init; } = string.Empty;
    public string GraphAriaLabel { get; init; } = string.Empty;
    public IHtmlContent GraphGaugeHtml { get; init; } = HtmlString.Empty;
    public IReadOnlyList<ContractJourneyHeroFactViewModel> Facts { get; init; } = [];
    public IReadOnlyList<ContractJourneyHeroStageRowViewModel> StageRows { get; init; } = [];
    public IReadOnlyList<ContractJourneyHeroGraphBarViewModel> GraphBars { get; init; } = [];
}

public sealed class ContractJourneyPagerViewModel
{
    public string PageKey { get; init; } = string.Empty;
    public int CurrentPage { get; init; }
    public int PageCount { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed class ContractJourneyInventoryTabRowViewModel
{
    public string MovementDate { get; init; } = string.Empty;
    public string DirectionLabel { get; init; } = string.Empty;
    public string DirectionBadgeClass { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string TerminalName { get; init; } = string.Empty;
    public string SourceContractName { get; init; } = string.Empty;
    public string StorageTankCode { get; init; } = string.Empty;
    public string QuantityText { get; init; } = string.Empty;
    public string QuantityToneClass { get; init; } = string.Empty;
    public string RunningBalanceText { get; init; } = string.Empty;
    public string ReferenceText { get; init; } = string.Empty;
    public string DetailTitle { get; init; } = string.Empty;
}

public sealed class ContractJourneyInventoryTabViewModel
{
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string StockCardUrl { get; init; } = string.Empty;
    public string NoRecordsText { get; init; } = string.Empty;
    public IHtmlContent PagerHtml { get; init; } = HtmlString.Empty;
    public ContractJourneyPagerViewModel Pager { get; init; } = new();
    public IReadOnlyList<ContractJourneyInventoryMovementItemViewModel> Items { get; init; } = [];
}

public sealed class ContractJourneyDispatchTabRowViewModel
{
    public int Id { get; init; }
    public string DetailUrl { get; init; } = string.Empty;
    public string DispatchDate { get; init; } = string.Empty;
    public string TruckPlateNumber { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string LoadedQuantityText { get; init; } = string.Empty;
    public string DetailTitle { get; init; } = string.Empty;
}

public sealed class ContractJourneyDispatchTabViewModel
{
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string CreateUrl { get; init; } = string.Empty;
    public string NoRecordsText { get; init; } = string.Empty;
    public IHtmlContent PagerHtml { get; init; } = HtmlString.Empty;
    public ContractJourneyPagerViewModel Pager { get; init; } = new();
    public IReadOnlyList<ContractJourneyDispatchItemViewModel> Items { get; init; } = [];
}

public sealed class ContractJourneySalesTabViewModel
{
    public bool IsPurchaseContract { get; init; }
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string ReturnUrl { get; init; } = string.Empty;
    public string CreateUrl { get; init; } = string.Empty;
    public string NoSalesText { get; init; } = string.Empty;
    public string NoPreSalesText { get; init; } = string.Empty;
    public IHtmlContent SalesPagerHtml { get; init; } = HtmlString.Empty;
    public IHtmlContent PreSalesPagerHtml { get; init; } = HtmlString.Empty;
    public ContractJourneyPagerViewModel SalesPager { get; init; } = new();
    public ContractJourneyPagerViewModel PreSalesPager { get; init; } = new();
    public IReadOnlyList<ContractJourneySaleItemViewModel> SalesItems { get; init; } = [];
    public IReadOnlyList<ContractJourneyPreSaleItemViewModel> PreSaleItems { get; init; } = [];
}

public sealed class ContractJourneyLoadingsTabViewModel
{
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string PageDirection { get; init; } = "rtl";
    public string NoRecordsText { get; init; } = string.Empty;
    public IHtmlContent PagerHtml { get; init; } = HtmlString.Empty;
    public ContractJourneyPagerViewModel Pager { get; init; } = new();
    public IReadOnlyList<ContractJourneyLoadingItemViewModel> Items { get; init; } = [];
}

public sealed class ContractJourneyReceiptsTabViewModel
{
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string PageDirection { get; init; } = "rtl";
    public int? FirstLoadingId { get; init; }
    public decimal BulkReceiptDefaultQuantityMt { get; init; }
    public string NoRecordsText { get; init; } = string.Empty;
    public IHtmlContent PagerHtml { get; init; } = HtmlString.Empty;
    public IHtmlContent PendingPagerHtml { get; init; } = HtmlString.Empty;
    public ContractJourneyPagerViewModel Pager { get; init; } = new();
    public IReadOnlyList<SelectListItem> BulkReceiptTerminals { get; init; } = [];
    public IReadOnlyList<ContractJourneyBulkReceiptStorageTankOptionViewModel> BulkReceiptStorageTanks { get; init; } = [];
    public IReadOnlyList<ContractJourneyBulkReceiptCandidateViewModel> BulkReceiptCandidates { get; init; } = [];
    public IReadOnlyList<ContractJourneyReceiptItemViewModel> Items { get; init; } = [];
}

public sealed class ContractJourneyBulkReceiptStorageTankOptionViewModel
{
    public int Id { get; init; }
    public int TerminalId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class ContractJourneyFinanceTabRowViewModel
{
    public int PaymentTransactionId { get; init; }
    public string DetailsUrl { get; init; } = string.Empty;
    public string EditUrl { get; init; } = string.Empty;
    public string PaymentDate { get; init; } = string.Empty;
    public string DirectionAndKindLabel { get; init; } = string.Empty;
    public string CashAccountName { get; init; } = string.Empty;
    public string AmountText { get; init; } = string.Empty;
    public string LedgerEntryText { get; init; } = string.Empty;
}

public sealed class ContractJourneyFinanceSummaryViewModel
{
    public string DebitTotalText { get; init; } = string.Empty;
    public string CreditTotalText { get; init; } = string.Empty;
    public string BalanceText { get; init; } = string.Empty;
    public string SupplierPayableTotalText { get; init; } = string.Empty;
    public string SupplierPaidText { get; init; } = string.Empty;
    public string SupplierRemainingText { get; init; } = string.Empty;
    public string SupplierRemainingToneClass { get; init; } = string.Empty;
    public string GrossMarginText { get; init; } = string.Empty;
    public string GrossMarginToneClass { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    // سود محقق‌شدهٔ حسابداری (ProfitAndLossService) در کنار برآورد عملیاتی بالا.
    public string RealisedRevenueText { get; init; } = string.Empty;
    public string RealisedCogsText { get; init; } = string.Empty;
    public string RealisedGrossProfitText { get; init; } = string.Empty;
    public string RealisedToneClass { get; init; } = string.Empty;
    public string RealisedConfidenceLabel { get; init; } = string.Empty;
    public string RealisedConfidenceTone { get; init; } = string.Empty;
    public string? RealisedUncostedWarning { get; init; }
    public string? RealisedVarianceText { get; init; }
    public string? RealisedVarianceReason { get; init; }
}

public sealed class ContractJourneyFinanceTabViewModel
{
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string CreateUrl { get; init; } = string.Empty;
    public string SarrafSettlementCreateUrl { get; init; } = string.Empty;
    public string SarrafSettlementIndexUrl { get; init; } = string.Empty;
    public string BalanceTransferCreateUrl { get; init; } = string.Empty;
    public string BalanceTransferIndexUrl { get; init; } = string.Empty;
    public string AccountStatementUrl { get; init; } = string.Empty;
    public string NoRecordsText { get; init; } = string.Empty;
    public IHtmlContent PagerHtml { get; init; } = HtmlString.Empty;
    public ContractJourneyPagerViewModel Pager { get; init; } = new();
    public ContractJourneyFinanceSummaryViewModel Summary { get; init; } = new();
    public IReadOnlyList<ContractJourneyPaymentItemViewModel> Items { get; init; } = [];
    public IReadOnlyList<ContractJourneySarrafSettlementItemViewModel> SarrafSettlements { get; init; } = [];
}

public sealed class ContractJourneyLedgerTabRowViewModel
{
    public int LedgerEntryId { get; init; }
    public string DetailsUrl { get; init; } = string.Empty;
    public string EntryDate { get; init; } = string.Empty;
    public string SideLabel { get; init; } = string.Empty;
    public string AmountText { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public string DetailTitle { get; init; } = string.Empty;
}

public sealed class ContractJourneyLedgerTabViewModel
{
    public int ContractId { get; init; }
    public bool LockContract { get; init; }
    public string LedgerIndexUrl { get; init; } = string.Empty;
    public string NoRecordsText { get; init; } = string.Empty;
    public IHtmlContent PagerHtml { get; init; } = HtmlString.Empty;
    public ContractJourneyPagerViewModel Pager { get; init; } = new();
    public IReadOnlyList<ContractJourneyLedgerItemViewModel> Items { get; init; } = [];
}
