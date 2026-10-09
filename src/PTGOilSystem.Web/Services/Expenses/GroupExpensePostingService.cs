using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Services.Expenses;

/// <summary>Posts each group share through the same settlement, ledger and accounting owners as a single expense.
/// The caller owns the transaction for the entire group; this service never commits independently.</summary>
public sealed class GroupExpensePostingService(
    ApplicationDbContext db,
    IExpenseSettlementValidator settlementValidator,
    IExpenseLedgerPoster ledger,
    IAuditService audit,
    IExpenseAccountingAdapter? accounting = null)
{
    public async Task<ExpenseAccountingResult?> PostShareAsync(
        ExpenseTransaction expense, ExpenseType type, CurrencyConversionResult conversion,
        ExpenseSettlementMode? settlementMode = null, int? cashAccountId = null)
    {
        ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);
        if (settlementMode is ExpenseSettlementMode.PaidImmediately or ExpenseSettlementMode.NonCash)
        {
            expense.SettlementMode = settlementMode.Value;
            expense.CounterpartyType = null;
            expense.CounterpartyId = null;
            expense.CashAccountId = settlementMode == ExpenseSettlementMode.PaidImmediately ? cashAccountId : null;
        }
        else if (settlementMode == ExpenseSettlementMode.Payable)
        {
            expense.SettlementMode = ExpenseSettlementMode.Payable;
        }
        settlementValidator.Validate(expense);
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        var entry = ledger.Post(new ExpenseLedgerRequest
        {
            Expense = expense,
            ExpenseType = type,
            FxRateDate = conversion.EffectiveDate.Date,
            FxRateSource = conversion.SourceDescription
        });
        await db.SaveChangesAsync();

        var result = accounting is null ? null : await accounting.TryPostExpenseAsync(expense);
        await audit.LogAndSaveAsync(nameof(ExpenseTransaction), expense.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("ExpenseBatchId", expense.ExpenseBatchId),
                ("ContractId", expense.ContractId),
                ("LoadingRegisterId", expense.LoadingRegisterId),
                ("TransportLegId", expense.TransportLegId),
                ("TruckDispatchId", expense.TruckDispatchId),
                ("ServiceProviderId", expense.ServiceProviderId),
                ("SettlementMode", expense.SettlementMode),
                ("CostResponsibility", expense.CostResponsibility),
                ("Currency", expense.Currency),
                ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                ("Amount", expense.Amount),
                ("AmountUsd", expense.AmountUsd),
                ("LedgerReference", entry.Reference),
                ("AccountingStatus", result?.Status.ToString() ?? "NotConfigured"),
                ("AccountingReason", result?.Reason)));
        return result;
    }
}
