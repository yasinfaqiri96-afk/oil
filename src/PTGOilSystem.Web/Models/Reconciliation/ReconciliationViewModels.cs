namespace PTGOilSystem.Web.Models.Reconciliation;

public sealed class ReconciliationIndexViewModel
{
    public int OpenContractsCount { get; init; }
    public int ShipmentsWithoutSalesCount { get; init; }
    public int ShipmentsWithoutExpensesCount { get; init; }
    public int DispatchesWithoutReceiptCount { get; init; }
    public int MissingLedgerCount { get; init; }
    public int NonZeroBalancesCount { get; init; }
    public int LossEventsCount { get; init; }
    public int IncompleteAfterReceiptCount { get; init; }
    public int EmployeeIssueCount { get; init; }
    public int RoznamchaIssueCount { get; init; }
    public int SuspenseMoneyCount { get; init; }
}

public sealed class IncompleteAfterReceiptItemViewModel
{
    /// <summary>کلید نوع مسیر: DirectDispatch | Transfer | DispatchConflict | DispatchPartial</summary>
    public string PathType { get; init; } = "";

    /// <summary>متن قابل نمایش نوع مسیر برای کاربر فارسی‌زبان.</summary>
    public string PathTypeText { get; init; } = "";

    public string ContractNumber { get; init; } = "";
    public int? LoadingRegisterId { get; init; }
    public int? LoadingReceiptId { get; init; }
    public int? AllocationId { get; init; }
    public int? TruckDispatchId { get; init; }
    public int? SalesTransactionId { get; init; }
    public DateTime? Date { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal? DispatchedQuantityMt { get; init; }
    public decimal? RemainingQuantityMt { get; init; }
    public string? TruckPlateNumber { get; init; }
    public string? DriverName { get; init; }
    public string? DestinationName { get; init; }
    public string CurrentStatus { get; init; } = "";
    public string Issue { get; init; } = "";

    /// <summary>کنترلر برای لینک Details (مثلاً Dispatch، LoadingReceipts).</summary>
    public string? DetailsControllerName { get; init; }
    public string? DetailsActionName { get; init; }
    public int? DetailsRouteId { get; init; }
}

public sealed class IncompleteAfterReceiptViewModel
{
    /// <summary>
    /// موتر بار را از رسید گرفته اما هنوز نه فروش (Sale)، نه تخلیه در مخزن (DeliveryReceipt) ثبت شده است.
    /// </summary>
    public IReadOnlyList<IncompleteAfterReceiptItemViewModel> DirectDispatchesWithoutFinish { get; init; } = [];

    /// <summary>
    /// تخصیص رسید با مقصد TransferToOtherTerminal و وضعیت InTransit؛ هنوز رسید مقصد ثبت نشده است.
    /// </summary>
    public IReadOnlyList<IncompleteAfterReceiptItemViewModel> TransfersInTransit { get; init; } = [];

    /// <summary>
    /// موتر هم فروش (Sale) خورده هم تخلیه شده (DeliveryReceipt یا InventoryMovement(In))؛ تضاد منطقی.
    /// </summary>
    public IReadOnlyList<IncompleteAfterReceiptItemViewModel> DispatchSaleAndDeliveryConflicts { get; init; } = [];

    /// <summary>
    /// تخصیص DirectDispatchToTruck که مقدار dispatch‌شده کمتر از مقدار تخصیص است؛ مقدار باقی‌مانده مشخص.
    /// </summary>
    public IReadOnlyList<IncompleteAfterReceiptItemViewModel> DirectDispatchPartialRemaining { get; init; } = [];

    /// <summary>
    /// رسیدهای «ضایعات بعداً از تسویه مخزن» که موجودی‌شان هنوز در مخزن است و تسویهٔ نهایی نشده‌اند.
    /// </summary>
    public IReadOnlyList<IncompleteAfterReceiptItemViewModel> PendingTankSettlements { get; init; } = [];

    public int TotalCount =>
        DirectDispatchesWithoutFinish.Count
        + TransfersInTransit.Count
        + DispatchSaleAndDeliveryConflicts.Count
        + DirectDispatchPartialRemaining.Count
        + PendingTankSettlements.Count;
}

public sealed class OpenContractItemViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ContractUnitText { get; init; } = "—";
    public decimal ContractQuantityMt { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal RemainingQuantityMt { get; init; }
    public string Status { get; init; } = "Open";
}

public sealed class OpenContractsViewModel
{
    public IReadOnlyList<OpenContractItemViewModel> Rows { get; init; } = [];
}

public sealed class OpenShipmentItemViewModel
{
    public int ShipmentId { get; init; }
    public string ShipmentCode { get; init; } = "";
    public string? ContractNumber { get; init; }
    public string ContractUnitText { get; init; } = "—";
    public decimal QuantityMt { get; init; }
    public string Status { get; init; } = "Open";
}

public sealed class DispatchNeedsReviewItemViewModel
{
    public int DispatchId { get; init; }
    public DateTime DispatchDate { get; init; }
    public string TruckPlateNumber { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string ContractUnitText { get; init; } = "—";
    public decimal LoadedQuantityMt { get; init; }
    public string Status { get; init; } = "Needs Review";
    public string Reason { get; init; } = "";
}

public sealed class OpenShipmentsViewModel
{
    public IReadOnlyList<OpenShipmentItemViewModel> ShipmentsWithoutSales { get; init; } = [];
    public IReadOnlyList<OpenShipmentItemViewModel> ShipmentsWithoutExpenses { get; init; } = [];
    public IReadOnlyList<DispatchNeedsReviewItemViewModel> DispatchesWithoutReceipt { get; init; } = [];
}

public sealed class MissingLedgerItemViewModel
{
    public int SourceId { get; init; }
    public string SourceType { get; init; } = "";
    public DateTime Date { get; init; }
    public string Reference { get; init; } = "";
    public decimal AmountUsd { get; init; }
    public string Status { get; init; } = "Missing Ledger";
}

public sealed class DirectSaleReconciliationItemViewModel
{
    public int AllocationId { get; init; }
    public int LoadingReceiptId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string? CustomerName { get; init; }
    public int? SalesTransactionId { get; init; }
    public string? InvoiceNumber { get; init; }
    public decimal AllocationQuantityMt { get; init; }
    public decimal? SaleQuantityMt { get; init; }
    public decimal? SaleTotalUsd { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class DirectDispatchReconciliationItemViewModel
{
    public int? AllocationId { get; init; }
    public int? TruckDispatchId { get; init; }
    public int? SalesTransactionId { get; init; }
    public int? LoadingReceiptId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string? TruckPlateNumber { get; init; }
    public string? DriverName { get; init; }
    public string? DestinationName { get; init; }
    public string? InvoiceNumber { get; init; }
    public decimal? AllocationQuantityMt { get; init; }
    public decimal? DispatchedQuantityMt { get; init; }
    public decimal? SaleQuantityMt { get; init; }
    public decimal? SaleTotalUsd { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public decimal? RemainingQuantityMt { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class InventoryMovementContractIssueViewModel
{
    public int MovementId { get; init; }
    public DateTime MovementDate { get; init; }
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ContractType { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public string DirectionText { get; init; } = "";
    public decimal QuantityMt { get; init; }
    public string? ReferenceDocument { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class ToInventoryAllocationIssueViewModel
{
    public int AllocationId { get; init; }
    public int LoadingReceiptId { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string ContractNumber { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public decimal AllocationQuantityMt { get; init; }
    public int? InventoryMovementId { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class ToInventoryReceiptIssueViewModel
{
    public int LoadingReceiptId { get; init; }
    public DateTime ReceiptDate { get; init; }
    public string ContractNumber { get; init; } = "";
    public string TerminalName { get; init; } = "";
    public decimal ReceivedQuantityMt { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class DuplicateCustomsIssueViewModel
{
    public int ContractId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ContractType { get; init; } = "";
    public decimal CustomsDeclarationTotalUsd { get; init; }
    public int CustomsDeclarationCount { get; init; }
    public decimal CustomsExpenseTotalUsd { get; init; }
    public int CustomsExpenseCount { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class CustomsSourceIssueViewModel
{
    public int CustomsDeclarationId { get; init; }
    public int? LoadingRegisterId { get; init; }
    public int? TransportLegId { get; init; }
    public DateTime DeclarationDate { get; init; }
    public string? WagonOrTruckNumber { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class InventoryTransportLegIssueViewModel
{
    public int LegId { get; init; }
    public string ContractNumber { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string SourceTerminalName { get; init; } = "";
    public string? SourceTankCode { get; init; }
    public decimal QuantityMt { get; init; }
    public string StatusText { get; init; } = "";
    public int? MovementId { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class SupplierPaymentReconciliationItemViewModel
{
    public int PaymentId { get; init; }
    public DateTime PaymentDate { get; init; }
    public int? SupplierId { get; init; }
    public string SupplierName { get; init; } = "—";
    public int? ContractId { get; init; }
    public string ContractNumber { get; init; } = "—";
    public string Reference { get; init; } = "—";
    public decimal AmountUsd { get; init; }
    public int? LedgerEntryId { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class SupplierLedgerFxIssueViewModel
{
    public int LedgerEntryId { get; init; }
    public DateTime EntryDate { get; init; }
    public string SupplierName { get; init; } = "—";
    public string ContractNumber { get; init; } = "—";
    public string SourceType { get; init; } = "";
    public string Reference { get; init; } = "—";
    public string SourceCurrencyCode { get; init; } = "";
    public decimal? SourceAmount { get; init; }
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal AmountUsd { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class ServiceProviderReconciliationItemViewModel
{
    public int? SourceId { get; init; }
    public string SourceType { get; init; } = "";
    public DateTime Date { get; init; }
    public int? ServiceProviderId { get; init; }
    public string ServiceProviderName { get; init; } = "—";
    public string Reference { get; init; } = "—";
    public decimal AmountUsd { get; init; }
    public int? LedgerEntryId { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class SarrafSettlementReconciliationItemViewModel
{
    public int SettlementId { get; init; }
    public DateTime SettlementDate { get; init; }
    public string SarrafName { get; init; } = "-";
    public string SupplierName { get; init; } = "-";
    public string ContractNumber { get; init; } = "-";
    public string Reference { get; init; } = "-";
    public decimal RequestedAmountUsd { get; init; }
    public decimal SupplierLedgerAmountUsd { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public int? LedgerEntryId { get; init; }
    public decimal DifferenceAmountUsd { get; init; }
    public decimal? DifferenceLedgerAmountUsd { get; init; }
    public int? ExchangeDifferenceLedgerEntryId { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class OperationalAssetReconciliationItemViewModel
{
    public int? SourceId { get; init; }
    public string SourceType { get; init; } = "";
    public DateTime Date { get; init; }
    public int? OperationalAssetId { get; init; }
    public string OperationalAssetName { get; init; } = "—";
    public string Reference { get; init; } = "—";
    public decimal AmountUsd { get; init; }
    public decimal? RelatedAmountUsd { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class MissingLedgerViewModel
{
    public IReadOnlyList<MissingLedgerItemViewModel> SalesWithoutLedger { get; init; } = [];
    public IReadOnlyList<MissingLedgerItemViewModel> ExpensesWithoutLedger { get; init; } = [];
    public IReadOnlyList<MissingLedgerItemViewModel> PaymentsWithoutLedger { get; init; } = [];
    public IReadOnlyList<SupplierPaymentReconciliationItemViewModel> SupplierPaymentsWithoutSupplier { get; init; } = [];
    public IReadOnlyList<SupplierPaymentReconciliationItemViewModel> SupplierPaymentContractSupplierMismatches { get; init; } = [];
    public IReadOnlyList<SupplierPaymentReconciliationItemViewModel> SupplierPaymentLedgerMissingSupplierOrContract { get; init; } = [];
    public IReadOnlyList<SupplierPaymentReconciliationItemViewModel> SupplierPaymentsWithoutLedger { get; init; } = [];
    public IReadOnlyList<SupplierLedgerFxIssueViewModel> SupplierLedgerFxIssues { get; init; } = [];
    public IReadOnlyList<ServiceProviderReconciliationItemViewModel> ServiceProviderExpensesWithoutLedger { get; init; } = [];
    public IReadOnlyList<ServiceProviderReconciliationItemViewModel> ServiceProviderPaymentsWithoutLedger { get; init; } = [];
    public IReadOnlyList<ServiceProviderReconciliationItemViewModel> ServiceProviderLedgerMissingSource { get; init; } = [];
    public IReadOnlyList<ServiceProviderReconciliationItemViewModel> ServiceProviderPaymentLedgerMismatches { get; init; } = [];
    public IReadOnlyList<ServiceProviderReconciliationItemViewModel> ServiceProviderExpenseLedgerMismatches { get; init; } = [];
    public IReadOnlyList<ServiceProviderReconciliationItemViewModel> CancelledServiceProviderExpensesWithBalanceImpact { get; init; } = [];
    public IReadOnlyList<SarrafSettlementReconciliationItemViewModel> SarrafSettlementsWithoutSupplierLedger { get; init; } = [];
    public IReadOnlyList<SarrafSettlementReconciliationItemViewModel> SarrafSettlementSupplierLedgerMismatches { get; init; } = [];
    public IReadOnlyList<SarrafSettlementReconciliationItemViewModel> SarrafSettlementsWithoutDifferenceLedger { get; init; } = [];
    public IReadOnlyList<SarrafSettlementReconciliationItemViewModel> SarrafSettlementDifferenceLedgerMismatches { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentTransactionsWithoutShares { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentShareSumMismatches { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentOwnershipCoverageIssues { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> OperationalAssetLinkIssues { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetExpenseInactiveAssetIssues { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentPostedWithoutLedger { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentPostableWithoutLedger { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentLedgerIntegrityIssues { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentContractRequirementIssues { get; init; } = [];
    public IReadOnlyList<OperationalAssetReconciliationItemViewModel> AssetRentDuplicateCandidates { get; init; } = [];
    public IReadOnlyList<DirectSaleReconciliationItemViewModel> DirectSaleAllocationsWithoutSale { get; init; } = [];
    public IReadOnlyList<DirectSaleReconciliationItemViewModel> DirectSaleSalesWithoutLedger { get; init; } = [];
    public IReadOnlyList<DirectSaleReconciliationItemViewModel> DirectSaleQuantityMismatches { get; init; } = [];
    public IReadOnlyList<DirectSaleReconciliationItemViewModel> DirectSaleLedgerAmountMismatches { get; init; } = [];
    public IReadOnlyList<DirectDispatchReconciliationItemViewModel> DirectDispatchAllocationsWithoutDispatch { get; init; } = [];
    public IReadOnlyList<DirectDispatchReconciliationItemViewModel> DirectDispatchQuantityMismatches { get; init; } = [];
    public IReadOnlyList<DirectDispatchReconciliationItemViewModel> DirectDispatchesWithoutAllocation { get; init; } = [];
    public IReadOnlyList<DirectDispatchReconciliationItemViewModel> DirectDispatchesWithInventoryMovement { get; init; } = [];
    public IReadOnlyList<DirectDispatchReconciliationItemViewModel> DirectDispatchStatusMismatches { get; init; } = [];
    public IReadOnlyList<DirectDispatchReconciliationItemViewModel> DirectDispatchSalesWithoutLedger { get; init; } = [];
    public IReadOnlyList<DirectDispatchReconciliationItemViewModel> DirectDispatchSaleQuantityMismatches { get; init; } = [];
    public IReadOnlyList<InventoryMovementContractIssueViewModel> InventoryMovementsWithNonPurchaseContract { get; init; } = [];
    public IReadOnlyList<ToInventoryAllocationIssueViewModel> ToInventoryAllocationsWithoutMovement { get; init; } = [];
    public IReadOnlyList<ToInventoryReceiptIssueViewModel> ToInventoryReceiptsWithoutAllocation { get; init; } = [];
    public IReadOnlyList<DuplicateCustomsIssueViewModel> DuplicateCustomsCandidates { get; init; } = [];
    public IReadOnlyList<CustomsSourceIssueViewModel> CustomsSourceIssues { get; init; } = [];
    public IReadOnlyList<InventoryTransportLegIssueViewModel> InventoryTransportLegIssues { get; init; } = [];

    public int DirectSaleIssueCount =>
        DirectSaleAllocationsWithoutSale.Count
        + DirectSaleSalesWithoutLedger.Count
        + DirectSaleQuantityMismatches.Count
        + DirectSaleLedgerAmountMismatches.Count;

    public int DirectDispatchIssueCount =>
        DirectDispatchAllocationsWithoutDispatch.Count
        + DirectDispatchQuantityMismatches.Count
        + DirectDispatchesWithoutAllocation.Count
        + DirectDispatchesWithInventoryMovement.Count
        + DirectDispatchStatusMismatches.Count
        + DirectDispatchSalesWithoutLedger.Count
        + DirectDispatchSaleQuantityMismatches.Count;

    public int InventoryIntegrityIssueCount =>
        InventoryMovementsWithNonPurchaseContract.Count
        + ToInventoryAllocationsWithoutMovement.Count
        + ToInventoryReceiptsWithoutAllocation.Count
        + DuplicateCustomsCandidates.Count
        + CustomsSourceIssues.Count
        + InventoryTransportLegIssues.Count;

    public int SupplierPaymentIssueCount =>
        SupplierPaymentsWithoutSupplier.Count
        + SupplierPaymentContractSupplierMismatches.Count
        + SupplierPaymentLedgerMissingSupplierOrContract.Count
        + SupplierPaymentsWithoutLedger.Count
        + SupplierLedgerFxIssues.Count;

    public int ServiceProviderIssueCount =>
        ServiceProviderExpensesWithoutLedger.Count
        + ServiceProviderPaymentsWithoutLedger.Count
        + ServiceProviderLedgerMissingSource.Count
        + ServiceProviderPaymentLedgerMismatches.Count
        + ServiceProviderExpenseLedgerMismatches.Count
        + CancelledServiceProviderExpensesWithBalanceImpact.Count;

    public int SarrafSettlementIssueCount =>
        SarrafSettlementsWithoutSupplierLedger.Count
        + SarrafSettlementSupplierLedgerMismatches.Count
        + SarrafSettlementsWithoutDifferenceLedger.Count
        + SarrafSettlementDifferenceLedgerMismatches.Count;

    public int OperationalAssetIssueCount =>
        AssetRentTransactionsWithoutShares.Count
        + AssetRentShareSumMismatches.Count
        + AssetRentOwnershipCoverageIssues.Count
        + OperationalAssetLinkIssues.Count
        + AssetExpenseInactiveAssetIssues.Count
        + AssetRentPostedWithoutLedger.Count
        + AssetRentPostableWithoutLedger.Count
        + AssetRentLedgerIntegrityIssues.Count
        + AssetRentContractRequirementIssues.Count
        + AssetRentDuplicateCandidates.Count;
}

public sealed class NonZeroBalanceItemViewModel
{
    public int EntityId { get; init; }
    public string EntityType { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal DebitUsd { get; init; }
    public decimal CreditUsd { get; init; }
    // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته). هم‌علامتِ صفحات بیلانس.
    public decimal BalanceUsd => DebitUsd - CreditUsd;
    public string Status { get; init; } = "Non-zero Balance";
}

public sealed class NonZeroBalancesViewModel
{
    public IReadOnlyList<NonZeroBalanceItemViewModel> ContractBalances { get; init; } = [];
    public IReadOnlyList<NonZeroBalanceItemViewModel> CustomerBalances { get; init; } = [];
    public IReadOnlyList<NonZeroBalanceItemViewModel> SupplierBalances { get; init; } = [];
}

public sealed class EmployeeSalaryReconciliationItemViewModel
{
    public int TransactionId { get; init; }
    public int EmployeeId { get; init; }
    public string EmployeeCode { get; init; } = "";
    public string EmployeeName { get; init; } = "";
    public DateTime TransactionDate { get; init; }
    public string TransactionTypeName { get; init; } = "";
    public decimal AmountUsd { get; init; }
    public int? CashAccountId { get; init; }
    public string? CashAccountName { get; init; }
    public int? PaymentTransactionId { get; init; }
    public int? LedgerEntryId { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public decimal? PaymentAmountUsd { get; init; }
    public string? Reference { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class EmployeeBalanceReconciliationItemViewModel
{
    public int EmployeeId { get; init; }
    public string EmployeeCode { get; init; } = "";
    public string EmployeeName { get; init; } = "";
    public decimal BalanceUsd { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Warning";
}

public sealed class EmployeeReconciliationViewModel
{
    public IReadOnlyList<EmployeeSalaryReconciliationItemViewModel> TransactionsWithoutLedger { get; init; } = [];
    public IReadOnlyList<EmployeeSalaryReconciliationItemViewModel> TransactionsWithoutCashAccount { get; init; } = [];
    public IReadOnlyList<EmployeeSalaryReconciliationItemViewModel> LedgerAmountMismatches { get; init; } = [];
    public IReadOnlyList<EmployeeSalaryReconciliationItemViewModel> CancelledTransactionsWithActiveLedger { get; init; } = [];
    public IReadOnlyList<EmployeeBalanceReconciliationItemViewModel> NegativeOrUnexpectedBalances { get; init; } = [];

    public int TotalCount =>
        TransactionsWithoutLedger.Count
        + TransactionsWithoutCashAccount.Count
        + LedgerAmountMismatches.Count
        + CancelledTransactionsWithActiveLedger.Count
        + NegativeOrUnexpectedBalances.Count;
}

public sealed class RoznamchaReconciliationItemViewModel
{
    public int PaymentTransactionId { get; init; }
    public DateTime PaymentDate { get; init; }
    public string DirectionName { get; init; } = "";
    public string PaymentKindName { get; init; } = "";
    public string? CashAccountName { get; init; }
    public string? CounterpartyName { get; init; }
    public decimal AmountUsd { get; init; }
    public int? LedgerEntryId { get; init; }
    public decimal? LedgerAmountUsd { get; init; }
    public string? Reference { get; init; }
    public string Issue { get; init; } = "";
    public string Status { get; init; } = "Needs Review";
}

public sealed class RoznamchaReconciliationViewModel
{
    public IReadOnlyList<RoznamchaReconciliationItemViewModel> PaymentsWithoutCounterparty { get; init; } = [];
    public IReadOnlyList<RoznamchaReconciliationItemViewModel> PaymentsWithoutCashAccount { get; init; } = [];
    public IReadOnlyList<RoznamchaReconciliationItemViewModel> LedgerAmountMismatches { get; init; } = [];
    public IReadOnlyList<RoznamchaReconciliationItemViewModel> ExpenseDoubleCountingRisks { get; init; } = [];
    public IReadOnlyList<RoznamchaReconciliationItemViewModel> EmployeePaymentsWithoutEmployeeLink { get; init; } = [];
    public IReadOnlyList<RoznamchaReconciliationItemViewModel> SupplierCustomerPaymentsWithoutProfileLink { get; init; } = [];

    public int TotalCount =>
        PaymentsWithoutCounterparty.Count
        + PaymentsWithoutCashAccount.Count
        + LedgerAmountMismatches.Count
        + ExpenseDoubleCountingRisks.Count
        + EmployeePaymentsWithoutEmployeeLink.Count
        + SupplierCustomerPaymentsWithoutProfileLink.Count;
}
