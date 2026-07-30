namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// Declares the authoritative read source for each reporting concern.
/// A report must not union legacy LedgerEntry and the accounting journal:
/// they represent overlapping business events during the controlled cutover.
/// </summary>
public static class ReportingSourcePolicy
{
    public const string OperationalPartyBalance =
        "PartyStatementReadService over LedgerEntry/PaymentTransaction/SarrafSettlement";

    public const string StatutoryAccounting =
        "Posted JournalEntry and JournalLine only";

    public const string PhysicalInventory =
        "StockService over InventoryMovement";

    public const string RealisedSalesProfit =
        "SalesTransaction plus active SalesCostConsumption plus non-cancelled ExpenseTransaction";

    public const string ContractLifecycle =
        "Contract, Shipment, LoadingRegister, transport and expense operational entities";
}
