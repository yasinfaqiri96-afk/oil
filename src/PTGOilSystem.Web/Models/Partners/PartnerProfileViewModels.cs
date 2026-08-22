using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Partners;

public sealed class PartnerProfileViewModel
{
    public int PartnerId { get; init; }
    public int Id => PartnerId;
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? NamePersian { get; init; }
    public string? Country { get; init; }
    public string? ContactPerson { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Email { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    public string ActiveTab { get; init; } = "overview";
    public int? SelectedContractId { get; init; }
    public PartnerContractSummaryViewModel? SelectedContract { get; init; }

    public int ContractsCount { get; init; }
    public int ActiveContractsCount { get; init; }
    public int PurchaseContractsCount { get; init; }
    public int SaleContractsCount { get; init; }
    public decimal AverageSharePercent { get; init; }
    public decimal TotalContractQuantityMt { get; init; }
    public decimal PartnerContractQuantityMt { get; init; }
    public decimal EstimatedContractValueUsd { get; init; }
    public decimal PartnerEstimatedContractValueUsd { get; init; }
    public decimal SalesRevenueUsd { get; init; }
    public decimal PartnerSalesRevenueUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public decimal OperationalExpensesUsd { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal PartnerTotalCostUsd { get; init; }
    public decimal GrossProfitUsd { get; init; }
    public decimal PartnerGrossProfitUsd { get; init; }
    public decimal CashInUsd { get; init; }
    public decimal CashOutUsd { get; init; }
    public decimal PartnerCashInUsd { get; init; }
    public decimal PartnerCashOutUsd { get; init; }

    // پرداخت واقعیِ خودِ شریک (FundingSource = Partner). مبلغ کامل است، نه سهم درصدی.
    public decimal ActualPartnerPaidUsd { get; init; }

    // مانده شریک با همان علامت صورت‌حساب رسمی: مثبت = شریک طلبکار، منفی = شریک بدهکار.
    public decimal PartnerPositionUsd { get; init; }
    public DateTime? LastContractDate { get; init; }
    public DateTime? LastFinancialDate { get; init; }

    public IReadOnlyList<PartnerStatementContractFilterOptionViewModel> StatementContractOptions { get; init; } = [];
    public IReadOnlyList<PartnerContractSummaryViewModel> Contracts { get; init; } = [];
    public IReadOnlyList<PartnerPaymentSummaryViewModel> Payments { get; init; } = [];
    public IReadOnlyList<PartnerStatementRowViewModel> StatementRows { get; init; } = [];
}

public sealed class PartnerStatementContractFilterOptionViewModel
{
    public int ContractId { get; init; }
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
}

public sealed class PartnerContractSummaryViewModel
{
    public int ContractId { get; init; }
    public string ContractName { get; init; } = string.Empty;
    public string ContractNumber { get; init; } = string.Empty;
    public string DisplayLabel => Contract.BuildDisplayLabel(ContractName, ContractNumber);
    public ContractType ContractType { get; init; }
    public string ContractTypeName { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string CounterpartyName { get; init; } = string.Empty;
    public DateTime ContractDate { get; init; }
    public ContractStatus Status { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public string StatusBadgeClass { get; init; } = "status-badge-neutral";
    public decimal SharePercent { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal PartnerQuantityMt { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal? FinalPriceUsd { get; init; }
    public decimal? EstimatedTotalUsd { get; init; }
    public decimal PartnerEstimatedTotalUsd { get; init; }
    public decimal ExecutedQuantityMt { get; init; }
    public decimal SalesRevenueUsd { get; init; }
    public decimal PurchaseCostUsd { get; init; }
    public decimal OperationalExpensesUsd { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal GrossProfitUsd { get; init; }
    public decimal PartnerGrossProfitUsd { get; init; }
    public decimal CashInUsd { get; init; }
    public decimal CashOutUsd { get; init; }
    public decimal PartnerCashInUsd { get; init; }
    public decimal PartnerCashOutUsd { get; init; }

    // پرداخت واقعیِ خودِ شریک روی همین قرارداد (FundingSource = Partner).
    public decimal ActualPartnerPaidUsd { get; init; }
    public decimal StatementBalanceUsd { get; init; }
}

public sealed class PartnerPaymentSummaryViewModel
{
    public int PaymentId { get; init; }
    public DateTime PaymentDate { get; init; }
    public PaymentDirection Direction { get; init; }
    public string DirectionName { get; init; } = string.Empty;
    public PaymentKind PaymentKind { get; init; }
    public string PaymentKindName { get; init; } = string.Empty;
    public string CashAccount { get; init; } = string.Empty;
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public decimal SharePercent { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal AmountUsd { get; init; }
    public decimal PartnerAmountUsd { get; init; }
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public int? LedgerEntryId { get; init; }
}

public sealed class PartnerStatementRowViewModel
{
    public int LedgerEntryId { get; init; }
    public DateTime Date { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? Reference { get; init; }
    public int? ContractId { get; init; }
    public string? ContractNumber { get; init; }
    public string ContractTypeName { get; init; } = string.Empty;
    public decimal SharePercent { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Currency { get; init; } = "USD";
    public decimal? Debit { get; init; }
    public decimal? Credit { get; init; }
    public decimal? PartnerDebitUsd { get; init; }
    public decimal? PartnerCreditUsd { get; init; }
    public decimal RunningBalanceUsd { get; init; }
}
