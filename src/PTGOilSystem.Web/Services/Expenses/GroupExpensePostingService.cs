using Microsoft.EntityFrameworkCore;
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
    public async Task<IReadOnlyList<ExpenseAccountingResult?>> PostOperationAsync(
        ExpenseTransaction expense, ExpenseType type, CurrencyConversionResult conversion,
        ExpenseSettlementMode? settlementMode = null, int? cashAccountId = null)
    {
        var sources = new TransportSourceAllocationService(db);
        int? legId = expense.TransportLegId;
        if (!legId.HasValue && expense.TruckDispatchId.HasValue)
        {
            var dispatch = await db.TruckDispatches.FindAsync(expense.TruckDispatchId.Value);
            if (dispatch is not null) legId = await sources.ResolveCurrentLegIdAsync(dispatch);
        }
        if (!legId.HasValue)
            return [await PostShareAsync(expense, type, conversion, settlementMode, cashAccountId)];

        var quantity = await db.InventoryTransportLegs.Where(l => l.Id == legId.Value)
            .Select(l => l.QuantityMt).SingleAsync();
        var plan = await sources.BuildFromLegAsync(legId.Value, quantity);
        var weights = plan.Shares.GroupBy(s => s.SourcePurchaseContractId)
            .OrderBy(g => g.Key).Select(g => (ContractId: g.Key, Quantity: g.Sum(s => s.QuantityMt))).ToArray();
        if (weights.Length == 0)
            throw new InvalidOperationException("Expense transport source allocations are missing.");
        var shares = SplitMoney(expense.Amount, weights.Select(w => w.Quantity).ToArray());
        var results = new List<ExpenseAccountingResult?>();
        for (var i = 0; i < weights.Length; i++)
        {
            if (shares[i] == 0m) continue;
            // Each financial row can own one contract. The canonical leg still owns all
            // source loading/receipt allocations, so no new transport or inventory is made.
            var row = new ExpenseTransaction
            {
                ExpenseTypeId = expense.ExpenseTypeId, ExpenseBatchId = expense.ExpenseBatchId,
                ContractId = weights[i].ContractId, ShipmentId = expense.ShipmentId,
                TransportLegId = expense.TransportLegId, TruckDispatchId = expense.TruckDispatchId,
                LoadingRegisterId = expense.LoadingRegisterId, ServiceProviderId = expense.ServiceProviderId,
                ExpenseDate = expense.ExpenseDate, Amount = shares[i], Currency = expense.Currency,
                AppliedFxRateToUsd = expense.AppliedFxRateToUsd, AmountUsd = conversion.ConvertToBase(shares[i]),
                Description = expense.Description, CostResponsibility = expense.CostResponsibility
            };
            results.Add(await PostShareAsync(row, type, conversion, settlementMode, cashAccountId));
        }
        return results;
    }

    // Cent-accurate largest remainder allocation. Tie order comes from ascending contract Id.
    internal static decimal[] SplitMoney(decimal amount, IReadOnlyList<decimal> weights)
    {
        var totalWeight = weights.Sum();
        if (totalWeight <= 0m || weights.Any(w => w <= 0m))
            throw new InvalidOperationException("Expense source quantities must be positive.");
        var units = decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
        var quotas = weights.Select(w => units * w / totalWeight).ToArray();
        var allocated = quotas.Select(decimal.Floor).ToArray();
        var remaining = (int)(units - allocated.Sum());
        foreach (var index in Enumerable.Range(0, weights.Count)
            .OrderByDescending(i => quotas[i] - allocated[i]).ThenBy(i => i).Take(remaining))
            allocated[index] += 1m;
        return allocated.Select(u => u / 100m).ToArray();
    }

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
