using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

public sealed class ExpenseRuleEngine : IExpenseRuleEngine
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    // مرحله ۵ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Accounting.IExpenseAccountingAdapter? _expenseAccounting;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);

    public ExpenseRuleEngine(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _audit = audit;
        _expenseAccounting = expenseAccounting;
    }

    public ExpenseRuleEngine(ApplicationDbContext db, IAuditService audit)
        : this(
            db,
            new CurrencyConversionService(new PricingService(db)),
            audit)
    {
    }

    public decimal CalculateAmount(
        ExpenseRule rule,
        decimal? quantityMt = null,
        decimal? baseAmountUsd = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var kind = NormalizeCalculationKind(rule.CalculationKind);
        return kind switch
        {
            ExpenseRuleCalculationKinds.Flat => rule.Amount,
            ExpenseRuleCalculationKinds.PerMt => CalculatePerMt(rule.Amount, quantityMt),
            ExpenseRuleCalculationKinds.Percent => CalculatePercent(rule.Amount, baseAmountUsd),
            _ => throw new BusinessRuleException("EXP_RULE_KIND_UNSUPPORTED", "نوع محاسبه Rule پشتیبانی نمی‌شود.")
        };
    }

    public async Task<int> GenerateExpenseAsync(
        ExpenseRule rule,
        ExpenseRuleGenerationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(request);

        if (!rule.IsActive)
        {
            throw new BusinessRuleException("EXP_RULE_INACTIVE", "Rule غیرفعال است و قابل استفاده نیست.");
        }

        await ValidateOperationalReferencesAsync(request, ct);

        var conversion = await _currencyConversion.ResolveToBaseAsync(
            rule.Currency,
            request.ExpenseDate.Date,
            request.AppliedFxRateToUsd,
            ct);

        var kind = NormalizeCalculationKind(rule.CalculationKind);
        var sourceAmount = CalculateSourceAmount(rule, request, conversion, kind);
        var amountUsd = conversion.ConvertToBase(sourceAmount);
        var description = BuildExpenseDescription(rule, request);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            var expense = new ExpenseTransaction
            {
                ExpenseTypeId = rule.ExpenseTypeId,
                ExpenseRuleId = rule.Id,
                ContractId = request.ContractId,
                ShipmentId = request.ShipmentId,
                TruckDispatchId = request.TruckDispatchId,
                ExpenseDate = request.ExpenseDate,
                Amount = sourceAmount,
                Currency = conversion.SourceCurrencyCode,
                AppliedFxRateToUsd = conversion.AppliedRateToBase,
                AmountUsd = amountUsd,
                Description = description
            };

            _db.ExpenseTransactions.Add(expense);
            await _db.SaveChangesAsync(ct);

            // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
            if (_expenseAccounting is not null)
            {
                await _expenseAccounting.TryPostExpenseAsync(expense, ct);
            }

            var ledgerEntry = Ledger.Post(new LedgerPostingRequest
            {
                EntryDate = expense.ExpenseDate,
                Side = LedgerSide.Debit,
                AmountUsd = expense.AmountUsd,
                Currency = SystemCurrency.BaseCurrencyCode,
                SourceAmount = expense.Amount,
                SourceCurrencyCode = expense.Currency,
                AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
                AppliedFxRateDate = conversion.EffectiveDate.Date,
                AppliedFxRateSource = conversion.SourceDescription,
                Description = $"ثبت هزینه Rule-Based {rule.Name}",
                SourceType = "Expense",
                SourceId = expense.Id,
                Reference = BuildLedgerReference(rule, expense),
                ContractId = expense.ContractId,
                ShipmentId = expense.ShipmentId
            });
            await _db.SaveChangesAsync(ct);

            await _audit.LogAndSaveAsync(
                nameof(ExpenseTransaction),
                expense.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ExpenseTypeId", expense.ExpenseTypeId),
                    ("ExpenseRuleId", expense.ExpenseRuleId),
                    ("ContractId", expense.ContractId),
                    ("ShipmentId", expense.ShipmentId),
                    ("TruckDispatchId", expense.TruckDispatchId),
                    ("ExpenseDate", expense.ExpenseDate),
                    ("Amount", expense.Amount),
                    ("Currency", expense.Currency),
                    ("AppliedFxRateToUsd", expense.AppliedFxRateToUsd),
                    ("AmountUsd", expense.AmountUsd),
                    ("CalculationKind", rule.CalculationKind),
                    ("QuantityMt", request.QuantityMt),
                    ("BaseAmountUsd", request.BaseAmountUsd),
                    ("LedgerReference", ledgerEntry.Reference)));

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return expense.Id;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }

            throw;
        }
    }

    private async Task ValidateOperationalReferencesAsync(ExpenseRuleGenerationRequest request, CancellationToken ct)
    {
        if (!request.ContractId.HasValue && !request.ShipmentId.HasValue && !request.TruckDispatchId.HasValue)
        {
            throw new BusinessRuleException("EXP_RULE_TRACE_REQUIRED", "برای تولید هزینه از روی Rule، حداقل یک مرجع عملیاتی انتخاب کنید.");
        }

        Contract? contract = null;
        Shipment? shipment = null;
        TruckDispatch? dispatch = null;

        if (request.ContractId.HasValue)
        {
            contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == request.ContractId.Value, ct);

            if (contract is null)
            {
                throw new BusinessRuleException("EXP_RULE_CONTRACT_INVALID", "قرارداد انتخاب‌شده معتبر نیست.");
            }
        }

        if (request.ShipmentId.HasValue)
        {
            shipment = await _db.Shipments
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == request.ShipmentId.Value, ct);

            if (shipment is null)
            {
                throw new BusinessRuleException("EXP_RULE_SHIPMENT_INVALID", "Shipment انتخاب‌شده معتبر نیست.");
            }
        }

        if (request.TruckDispatchId.HasValue)
        {
            dispatch = await _db.TruckDispatches
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == request.TruckDispatchId.Value, ct);

            if (dispatch is null)
            {
                throw new BusinessRuleException("EXP_RULE_DISPATCH_INVALID", "دیسپچ انتخاب‌شده معتبر نیست.");
            }
        }

        if (contract is not null && shipment is not null && shipment.ContractId.HasValue && shipment.ContractId.Value != contract.Id)
        {
            throw new BusinessRuleException("EXP_RULE_SHIPMENT_CONTRACT_MISMATCH", "Shipment انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
        }

        if (contract is not null && dispatch is not null && dispatch.ContractId != contract.Id)
        {
            throw new BusinessRuleException("EXP_RULE_DISPATCH_CONTRACT_MISMATCH", "دیسپچ انتخاب‌شده با قرارداد انتخابی هم‌خوان نیست.");
        }

        if (shipment is not null && dispatch is not null && shipment.ContractId.HasValue && shipment.ContractId.Value != dispatch.ContractId)
        {
            throw new BusinessRuleException("EXP_RULE_SHIPMENT_DISPATCH_MISMATCH", "Shipment و دیسپچ انتخاب‌شده به یک قرارداد اشاره نمی‌کنند.");
        }
    }

    private static decimal CalculateSourceAmount(
        ExpenseRule rule,
        ExpenseRuleGenerationRequest request,
        CurrencyConversionResult conversion,
        string kind)
    {
        return kind switch
        {
            ExpenseRuleCalculationKinds.Flat => rule.Amount,
            ExpenseRuleCalculationKinds.PerMt => CalculatePerMt(rule.Amount, request.QuantityMt),
            ExpenseRuleCalculationKinds.Percent => CalculatePercentInSourceCurrency(rule, request.BaseAmountUsd, conversion),
            _ => throw new BusinessRuleException("EXP_RULE_KIND_UNSUPPORTED", "نوع محاسبه Rule پشتیبانی نمی‌شود.")
        };
    }

    private static decimal CalculatePercentInSourceCurrency(
        ExpenseRule rule,
        decimal? baseAmountUsd,
        CurrencyConversionResult conversion)
    {
        var percentAmountUsd = CalculatePercent(rule.Amount, baseAmountUsd);

        if (SystemCurrency.IsBaseCurrency(rule.Currency))
        {
            return percentAmountUsd;
        }

        return decimal.Round(percentAmountUsd / conversion.AppliedRateToBase, 4, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculatePerMt(decimal rate, decimal? quantityMt)
    {
        if (!quantityMt.HasValue || quantityMt.Value <= 0)
        {
            throw new BusinessRuleException("EXP_RULE_QTY_REQUIRED", "برای Rule نوع Rate × Quantity باید Quantity معتبر وارد شود.");
        }

        return rate * quantityMt.Value;
    }

    private static decimal CalculatePercent(decimal percent, decimal? baseAmountUsd)
    {
        if (!baseAmountUsd.HasValue || baseAmountUsd.Value <= 0)
        {
            throw new BusinessRuleException("EXP_RULE_BASE_REQUIRED", "برای Rule نوع Percentage باید Base Amount معتبر وارد شود.");
        }

        return (baseAmountUsd.Value * percent) / 100m;
    }

    private static string NormalizeCalculationKind(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return ExpenseRuleCalculationKinds.All
            .FirstOrDefault(kind => string.Equals(kind, normalized, StringComparison.OrdinalIgnoreCase))
            ?? normalized;
    }

    private static string BuildExpenseDescription(ExpenseRule rule, ExpenseRuleGenerationRequest request)
    {
        var basis = NormalizeCalculationKind(rule.CalculationKind) switch
        {
            ExpenseRuleCalculationKinds.Flat => $"Flat={rule.Amount:N4} {rule.Currency}",
            ExpenseRuleCalculationKinds.PerMt => $"Rate={rule.Amount:N4} {rule.Currency}/MT | Qty={request.QuantityMt:N4} MT",
            ExpenseRuleCalculationKinds.Percent => $"Percent={rule.Amount:N4}% | Base={request.BaseAmountUsd:N4} USD",
            _ => $"RuleAmount={rule.Amount:N4} {rule.Currency}"
        };

        var description = $"Generated from Rule '{rule.Name}' | {basis}";
        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            description += $" | {request.Description.Trim()}";
        }

        return description.Length <= 1000 ? description : description[..1000];
    }

    private static string BuildLedgerReference(ExpenseRule rule, ExpenseTransaction expense)
    {
        var prefix = $"RULE-{rule.Id}";
        var combined = $"{prefix} | {rule.Name} | EXP-{expense.Id}";
        return combined.Length <= 200 ? combined : combined[..200];
    }
}
