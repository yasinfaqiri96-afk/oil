using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// How the amount of a single loading expense line is determined.
/// </summary>
public enum LoadingExpenseCalculationMode
{
    /// <summary>User types the total amount directly.</summary>
    FixedAmount = 1,

    /// <summary>Amount = QuantityMt × UnitRateUsd.</summary>
    PerMetricTon = 2
}

/// <summary>
/// Who the loading expense line is settled with. Drives the downstream
/// financial document that is created (none / service-provider expense+ledger /
/// internal asset rent).
/// </summary>
public enum LoadingExpensePartyType
{
    /// <summary>Direct expense recorded only on this loading. No Ledger, no ExpenseTransaction.</summary>
    None = 0,

    /// <summary>Settled with an external service company → ExpenseTransaction + LedgerEntry.</summary>
    ServiceProvider = 1,

    /// <summary>Internal use of an owned operational asset → AssetRentTransaction (no Ledger).</summary>
    OperationalAsset = 2
}

/// <summary>
/// One row of a loading's expenses. Replaces the old fixed Transport/Warehouse/
/// Railway/Other columns in the UI. The old <see cref="LoadingRegister"/> money
/// columns are kept and mirrored from the "None" lines for backward compatibility.
/// </summary>
public class LoadingExpenseLine : BaseEntity
{
    public int LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }

    public int ExpenseTypeId { get; set; }
    public ExpenseType? ExpenseType { get; set; }

    public LoadingExpenseCalculationMode CalculationMode { get; set; } = LoadingExpenseCalculationMode.FixedAmount;

    public decimal? QuantityMt { get; set; }
    public decimal? UnitRateUsd { get; set; }
    public decimal AmountUsd { get; set; }

    public LoadingExpensePartyType PartyType { get; set; } = LoadingExpensePartyType.None;

    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }

    public int? OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }

    public AccountingPartyType? CarrierPartyType { get; set; }
    public int? CarrierPartyId { get; set; }

    [MaxLength(1000)] public string? Notes { get; set; }

    // Links to the financial documents created for this line (when applicable).
    public int? ExpenseTransactionId { get; set; }
    public ExpenseTransaction? ExpenseTransaction { get; set; }
    public int? LedgerEntryId { get; set; }
    public int? AssetRentTransactionId { get; set; }
    public AssetRentTransaction? AssetRentTransaction { get; set; }
}
