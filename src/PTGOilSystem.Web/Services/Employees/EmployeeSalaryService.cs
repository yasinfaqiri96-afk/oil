using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.Employees;

public sealed record EmployeeSalaryTransactionCommand(
    int EmployeeId,
    DateTime TransactionDate,
    EmployeeSalaryTransactionType TransactionType,
    decimal Amount,
    string Currency,
    decimal? AppliedFxRateToUsd,
    int? CashAccountId,
    string? Reference,
    string? Description,
    int? SalaryPeriodYear,
    int? SalaryPeriodMonth);

public interface IEmployeeSalaryService
{
    Task<EmployeeSalaryTransaction> CreateAsync(EmployeeSalaryTransactionCommand command, CancellationToken ct = default);
    Task CancelAsync(int transactionId, string cancellationReason, CancellationToken ct = default);
}

public static class EmployeeSalarySummaryCalculator
{
    public static EmployeeFinancialSummaryViewModel FromTransactions(IEnumerable<EmployeeSalaryTransaction> transactions)
    {
        var active = transactions.Where(t => !t.IsCancelled).ToList();

        return new EmployeeFinancialSummaryViewModel
        {
            AccruedSalaryUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual)
                .Sum(t => t.AmountUsd),
            PaidSalaryUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment)
                .Sum(t => t.AmountUsd),
            AdvancesUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance)
                .Sum(t => t.AmountUsd),
            DeductionsUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryDeduction)
                .Sum(t => t.AmountUsd),
            BonusesUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.Bonus)
                .Sum(t => t.AmountUsd),
            AdjustmentsUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.Adjustment)
                .Sum(t => t.AmountUsd)
        };
    }
}

public sealed class EmployeeSalaryService : IEmployeeSalaryService
{
    private readonly ApplicationDbContext _db;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ILogger<EmployeeSalaryService> _logger;

    public EmployeeSalaryService(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ILogger<EmployeeSalaryService> logger)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _audit = audit;
        _logger = logger;
    }

    public async Task<EmployeeSalaryTransaction> CreateAsync(EmployeeSalaryTransactionCommand command, CancellationToken ct = default)
    {
        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == command.EmployeeId, ct);
        if (employee is null)
        {
            throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");
        }

        if (!employee.IsActive)
        {
            throw new BusinessRuleException("EMPLOYEE_INACTIVE", "برای کارمند غیرفعال نمی‌توان تراکنش معاش ثبت کرد.");
        }

        ValidateAmount(command.TransactionType, command.Amount);
        ValidateSalaryPeriod(command.TransactionType, command.SalaryPeriodYear, command.SalaryPeriodMonth);

        var normalizedCurrency = SystemCurrency.Normalize(command.Currency);
        var hasCurrenciesConfigured = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive, ct);
        if (hasCurrenciesConfigured
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == normalizedCurrency && c.IsActive, ct))
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_CURRENCY_INVALID", "ارز انتخاب‌شده معتبر نیست.");
        }

        CashAccount? cashAccount = null;
        if (EmployeeSalaryTransactionTypeLabels.RequiresCashAccount(command.TransactionType))
        {
            if (!command.CashAccountId.HasValue)
            {
                throw new BusinessRuleException("EMPLOYEE_SALARY_CASH_REQUIRED", "برای پرداخت معاش یا برداشت، حساب نقد / بانک الزامی است.");
            }

            cashAccount = await _db.CashAccounts
                .FirstOrDefaultAsync(a => a.Id == command.CashAccountId.Value && a.IsActive, ct);
            if (cashAccount is null)
            {
                throw new BusinessRuleException("EMPLOYEE_SALARY_CASH_INVALID", "حساب نقد / بانک انتخاب‌شده معتبر و فعال نیست.");
            }

            // حساب «مختلط» همه ارزها را می‌پذیرد؛ تطابق ارز فقط برای حساب‌های تک‌ارزی الزامی است.
            if (cashAccount.AccountType != CashAccountType.Mixed
                && !string.Equals(cashAccount.Currency, normalizedCurrency, StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessRuleException("EMPLOYEE_SALARY_CASH_CURRENCY_MISMATCH", "ارز تراکنش باید با ارز حساب نقد / بانک یکسان باشد.");
            }
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                normalizedCurrency,
                command.TransactionDate.Date,
                command.AppliedFxRateToUsd,
                ct);
        }
        catch (BusinessRuleException)
        {
            throw;
        }

        var amountUsd = conversion.ConvertToBase(command.Amount);
        var salaryTransaction = new EmployeeSalaryTransaction
        {
            EmployeeId = employee.Id,
            TransactionDate = command.TransactionDate.Date,
            TransactionType = command.TransactionType,
            Amount = command.Amount,
            Currency = conversion.SourceCurrencyCode,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            AmountUsd = amountUsd,
            CashAccountId = cashAccount?.Id,
            Reference = NormalizeText(command.Reference, 200),
            Description = NormalizeText(command.Description, 1000),
            SalaryPeriodYear = command.SalaryPeriodYear,
            SalaryPeriodMonth = command.SalaryPeriodMonth
        };

        IDbContextTransaction? dbTransaction = null;
        if (_db.Database.IsRelational())
        {
            dbTransaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            _db.EmployeeSalaryTransactions.Add(salaryTransaction);
            await _db.SaveChangesAsync(ct);

            if (cashAccount is not null)
            {
                await CreateCashTraceAsync(employee, salaryTransaction, cashAccount, conversion, ct);
            }

            await _audit.LogAsync(
                nameof(EmployeeSalaryTransaction),
                salaryTransaction.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("EmployeeId", salaryTransaction.EmployeeId),
                    ("TransactionDate", salaryTransaction.TransactionDate),
                    ("TransactionType", salaryTransaction.TransactionType),
                    ("Amount", salaryTransaction.Amount),
                    ("Currency", salaryTransaction.Currency),
                    ("AmountUsd", salaryTransaction.AmountUsd),
                    ("CashAccountId", salaryTransaction.CashAccountId),
                    ("PaymentTransactionId", salaryTransaction.PaymentTransactionId),
                    ("LedgerEntryId", salaryTransaction.LedgerEntryId),
                    ("Reference", salaryTransaction.Reference),
                    ("SalaryPeriodYear", salaryTransaction.SalaryPeriodYear),
                    ("SalaryPeriodMonth", salaryTransaction.SalaryPeriodMonth)));

            await _db.SaveChangesAsync(ct);

            if (dbTransaction is not null)
            {
                await dbTransaction.CommitAsync(ct);
            }

            return salaryTransaction;
        }
        catch (Exception ex)
        {
            if (dbTransaction is not null)
            {
                await dbTransaction.RollbackAsync(ct);
            }

            _logger.LogError(ex, "Failed to create employee salary transaction for employee {EmployeeId}.", command.EmployeeId);
            throw;
        }
    }

    public async Task CancelAsync(int transactionId, string cancellationReason, CancellationToken ct = default)
    {
        var reason = NormalizeText(cancellationReason, 1000);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_CANCEL_REASON_REQUIRED", "دلیل لغو تراکنش الزامی است.");
        }

        var transaction = await _db.EmployeeSalaryTransactions
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction is null)
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_TRANSACTION_NOT_FOUND", "تراکنش معاش پیدا نشد.");
        }

        if (transaction.IsCancelled)
        {
            return;
        }

        var previous = new
        {
            transaction.IsCancelled,
            transaction.CancelledAtUtc,
            transaction.CancellationReason
        };

        transaction.IsCancelled = true;
        transaction.CancelledAtUtc = DateTime.UtcNow;
        transaction.CancellationReason = reason;

        await _audit.LogAsync(
            nameof(EmployeeSalaryTransaction),
            transaction.Id,
            AuditAction.Reverse,
            diff: AuditDiffFormatter.ForUpdate(
                ("IsCancelled", previous.IsCancelled, transaction.IsCancelled),
                ("CancelledAtUtc", previous.CancelledAtUtc, transaction.CancelledAtUtc),
                ("CancellationReason", previous.CancellationReason, transaction.CancellationReason)));

        await _db.SaveChangesAsync(ct);
    }

    private async Task CreateCashTraceAsync(
        Employee employee,
        EmployeeSalaryTransaction salaryTransaction,
        CashAccount cashAccount,
        CurrencyConversionResult conversion,
        CancellationToken ct)
    {
        var paymentKind = salaryTransaction.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
            ? PaymentKind.EmployeeSalaryAdvance
            : PaymentKind.EmployeeSalaryPayment;

        var payment = new PaymentTransaction
        {
            PaymentDate = salaryTransaction.TransactionDate,
            Direction = PaymentDirection.Out,
            PaymentKind = paymentKind,
            CashAccountId = cashAccount.Id,
            EmployeeId = employee.Id,
            Amount = salaryTransaction.Amount,
            Currency = salaryTransaction.Currency,
            AppliedFxRateToUsd = salaryTransaction.AppliedFxRateToUsd,
            AmountUsd = salaryTransaction.AmountUsd,
            Reference = salaryTransaction.Reference,
            Description = salaryTransaction.Description
        };

        _db.PaymentTransactions.Add(payment);
        await _db.SaveChangesAsync(ct);

        var ledgerEntry = Ledger.Post(new LedgerPostingRequest
        {
            EntryDate = salaryTransaction.TransactionDate,
            Side = LedgerSide.Debit,
            AmountUsd = salaryTransaction.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = salaryTransaction.Amount,
            SourceCurrencyCode = salaryTransaction.Currency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            AppliedFxRateDate = conversion.EffectiveDate.Date,
            AppliedFxRateSource = conversion.SourceDescription,
            Description = BuildLedgerDescription(employee, salaryTransaction, cashAccount),
            SourceType = payment.PaymentKind.ToString(),
            SourceId = payment.Id,
            Reference = salaryTransaction.Reference,
            EmployeeId = employee.Id
        });
        await _db.SaveChangesAsync(ct);

        payment.LedgerEntryId = ledgerEntry.Id;
        salaryTransaction.PaymentTransactionId = payment.Id;
        salaryTransaction.LedgerEntryId = ledgerEntry.Id;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            nameof(PaymentTransaction),
            payment.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("PaymentDate", payment.PaymentDate),
                ("Direction", payment.Direction),
                ("PaymentKind", payment.PaymentKind),
                ("CashAccountId", payment.CashAccountId),
                ("EmployeeId", payment.EmployeeId),
                ("Amount", payment.Amount),
                ("Currency", payment.Currency),
                ("AmountUsd", payment.AmountUsd),
                ("Reference", payment.Reference),
                ("LedgerEntryId", payment.LedgerEntryId)));

        await _audit.LogAsync(
            nameof(LedgerEntry),
            ledgerEntry.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("EntryDate", ledgerEntry.EntryDate),
                ("Side", ledgerEntry.Side),
                ("AmountUsd", ledgerEntry.AmountUsd),
                ("SourceAmount", ledgerEntry.SourceAmount),
                ("SourceCurrencyCode", ledgerEntry.SourceCurrencyCode),
                ("AppliedFxRateToUsd", ledgerEntry.AppliedFxRateToUsd),
                ("SourceType", ledgerEntry.SourceType),
                ("SourceId", ledgerEntry.SourceId),
                ("Reference", ledgerEntry.Reference),
                ("EmployeeId", ledgerEntry.EmployeeId)));
    }

    private static void ValidateAmount(EmployeeSalaryTransactionType transactionType, decimal amount)
    {
        if (transactionType == EmployeeSalaryTransactionType.Adjustment)
        {
            if (amount == 0m)
            {
                throw new BusinessRuleException("EMPLOYEE_SALARY_ADJUSTMENT_ZERO", "مبلغ اصلاحیه نمی‌تواند صفر باشد.");
            }

            return;
        }

        if (amount <= 0m)
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_AMOUNT_INVALID", "مبلغ باید بزرگ‌تر از صفر باشد.");
        }
    }

    private static void ValidateSalaryPeriod(EmployeeSalaryTransactionType transactionType, int? year, int? month)
    {
        if (!EmployeeSalaryTransactionTypeLabels.RequiresSalaryPeriod(transactionType))
        {
            return;
        }

        if (!year.HasValue || !month.HasValue)
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_PERIOD_REQUIRED", "برای ثبت معاش دوره، سال و ماه معاش الزامی است.");
        }

        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_PERIOD_INVALID", "دوره معاش معتبر نیست.");
        }
    }

    private static string BuildLedgerDescription(
        Employee employee,
        EmployeeSalaryTransaction salaryTransaction,
        CashAccount cashAccount)
    {
        var type = EmployeeSalaryTransactionTypeLabels.ToPersian(salaryTransaction.TransactionType);
        var reference = string.IsNullOrWhiteSpace(salaryTransaction.Reference) ? string.Empty : $" / {salaryTransaction.Reference}";
        var description = string.IsNullOrWhiteSpace(salaryTransaction.Description) ? string.Empty : $" / {salaryTransaction.Description}";
        return $"{type} / {employee.EmployeeCode} - {employee.FullName} / {cashAccount.Name}{reference}{description}";
    }

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
