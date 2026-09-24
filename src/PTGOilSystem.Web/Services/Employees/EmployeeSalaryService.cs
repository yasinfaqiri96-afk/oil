using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

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
    int? SalaryPeriodMonth,
    int? EmployeeLoanId = null,
    int? PayrollRunLineId = null,
    int? RecoveryYear = null,
    int? RecoveryMonth = null);

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
            RecoveredAdvancesUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery)
                .Sum(t => t.AmountUsd),
            DeductionsUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryDeduction)
                .Sum(t => t.AmountUsd),
            BonusesUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.Bonus)
                .Sum(t => t.AmountUsd),
            AdjustmentsUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.Adjustment)
                .Sum(t => t.AmountUsd),
            LoansUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement)
                .Sum(t => t.AmountUsd),
            LoanRecoveredUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.LoanRecovery)
                .Sum(t => t.AmountUsd),
            LoanRepaidUsd = active
                .Where(t => t.TransactionType == EmployeeSalaryTransactionType.LoanRepayment)
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
    private readonly IPaymentAccountingAdapter? _paymentAccounting;
    private readonly IEmployeeSalaryAccountingAdapter? _salaryAccounting;

    public EmployeeSalaryService(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ILogger<EmployeeSalaryService> logger,
        IPaymentAccountingAdapter? paymentAccounting = null,
        IEmployeeSalaryAccountingAdapter? salaryAccounting = null)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _audit = audit;
        _logger = logger;
        _paymentAccounting = paymentAccounting;
        _salaryAccounting = salaryAccounting;
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
        await EnsureNoActiveAccrualAsync(command, ct);

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

        var conversion = await _currencyConversion.ResolveToBaseAsync(
            normalizedCurrency,
            command.TransactionDate.Date,
            command.AppliedFxRateToUsd,
            ct);

        var amountUsd = conversion.ConvertToBase(command.Amount);
        if (command.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery)
        {
            await EnsureRecoveryWithinOutstandingAdvanceAsync(employee.Id, conversion.SourceCurrencyCode, command.Amount, ct);
        }

        await ValidateLoanLinkAsync(command, employee.Id, conversion.SourceCurrencyCode, ct);
        ValidateRecoveryMonth(command);

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
            SalaryPeriodMonth = command.SalaryPeriodMonth,
            EmployeeLoanId = command.EmployeeLoanId,
            PayrollRunLineId = command.PayrollRunLineId,
            RecoveryYear = command.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance ? command.RecoveryYear : null,
            RecoveryMonth = command.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance ? command.RecoveryMonth : null
        };

        IDbContextTransaction? dbTransaction = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        {
            dbTransaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            _db.EmployeeSalaryTransactions.Add(salaryTransaction);
            await SaveChangesTranslatingDuplicateAsync(ct);

            if (cashAccount is not null)
            {
                var payment = await CreateCashTraceAsync(employee, salaryTransaction, cashAccount, conversion, ct);

                // Stage 4 — همان Dual-write روزنامچه، داخل همین Transaction. Pilot خاموش = Skip.
                if (_paymentAccounting is not null)
                {
                    await _paymentAccounting.TryPostPaymentAsync(payment, ct);
                }
            }
            else if (_salaryAccounting is not null)
            {
                await _salaryAccounting.TryPostAsync(salaryTransaction, ct);
            }

            if (salaryTransaction.EmployeeLoanId.HasValue)
            {
                await RefreshLoanStatusAsync(salaryTransaction.EmployeeLoanId.Value, ct);
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
        finally
        {
            if (dbTransaction is not null)
            {
                await dbTransaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// لغوِ تراکنش معاش هیچ سندی را پاک نمی‌کند:
    /// - پرداخت/مساعدهٔ نقدی: یک سند روزنامچهٔ معکوس (بازگشت همان مبلغ به همان صندوق، با مرجعِ
    ///   <c>-CANCEL</c>) و سطر دفتر خودش ثبت می‌شود، و ژورنالِ دفتر کلِ پرداخت برگشت می‌خورد.
    /// - ثبت معاش/بونس/وصول مساعده: ژورنالِ دفتر کلش با ژورنالِ قرینه برمی‌گردد.
    /// در همهٔ حالت‌ها تراکنش با دلیل «لغوشده» علامت می‌خورد و Audit ثبت می‌شود.
    /// </summary>
    public async Task CancelAsync(int transactionId, string cancellationReason, CancellationToken ct = default)
    {
        var reason = NormalizeText(cancellationReason, 1000);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_CANCEL_REASON_REQUIRED", "دلیل لغو تراکنش الزامی است.");
        }

        var transaction = await _db.EmployeeSalaryTransactions
            .Include(t => t.Employee)
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction is null)
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_TRANSACTION_NOT_FOUND", "تراکنش معاش پیدا نشد.");
        }

        if (transaction.IsCancelled)
        {
            return;
        }

        // مساعده‌ای که از معاش وصول شده، تا وقتی وصولش پابرجاست لغو نمی‌شود؛ وگرنه طلبِ مساعده منفی می‌شود.
        if (transaction.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance)
        {
            await EnsureAdvanceCancellationKeepsRecoveriesCoveredAsync(transaction, ct);
        }

        if (transaction.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement
            && transaction.EmployeeLoanId.HasValue
            && await _db.EmployeeSalaryTransactions.AnyAsync(t =>
                t.EmployeeLoanId == transaction.EmployeeLoanId
                && t.Id != transaction.Id
                && !t.IsCancelled, ct))
        {
            throw new BusinessRuleException("EMPLOYEE_LOAN_HAS_REPAYMENTS", "این قرضه قسط یا بازپرداخت دارد؛ اول آن‌ها را لغو کنید.");
        }

        var previous = new
        {
            transaction.IsCancelled,
            transaction.CancelledAtUtc,
            transaction.CancellationReason
        };

        IDbContextTransaction? dbTransaction = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        {
            dbTransaction = await _db.Database.BeginTransactionAsync(ct);
        }

        try
        {
            var reversalDate = AfghanistanBusinessClock.SystemToday;
            if (transaction.PaymentTransactionId.HasValue)
            {
                await ReverseCashTraceAsync(transaction, reversalDate, reason, ct);
            }

            transaction.IsCancelled = true;
            transaction.CancelledAtUtc = DateTime.UtcNow;
            transaction.CancellationReason = reason;
            await _db.SaveChangesAsync(ct);

            if (!transaction.PaymentTransactionId.HasValue && _salaryAccounting is not null)
            {
                await _salaryAccounting.TryReverseAsync(transaction, reversalDate, ct);
            }

            if (transaction.EmployeeLoanId.HasValue)
            {
                await RefreshLoanStatusAsync(transaction.EmployeeLoanId.Value, ct);
            }

            await _audit.LogAsync(
                nameof(EmployeeSalaryTransaction),
                transaction.Id,
                AuditAction.Reverse,
                diff: AuditDiffFormatter.ForUpdate(
                    ("IsCancelled", previous.IsCancelled, transaction.IsCancelled),
                    ("CancelledAtUtc", previous.CancelledAtUtc, transaction.CancelledAtUtc),
                    ("CancellationReason", previous.CancellationReason, transaction.CancellationReason),
                    ("ReversalPaymentTransactionId", (int?)null, transaction.ReversalPaymentTransactionId),
                    ("ReversalLedgerEntryId", (int?)null, transaction.ReversalLedgerEntryId)));

            await _db.SaveChangesAsync(ct);

            if (dbTransaction is not null)
            {
                await dbTransaction.CommitAsync(ct);
            }
        }
        catch (Exception ex)
        {
            if (dbTransaction is not null)
            {
                await dbTransaction.RollbackAsync(ct);
            }

            _logger.LogError(ex, "Failed to cancel employee salary transaction {TransactionId}.", transactionId);
            throw;
        }
        finally
        {
            if (dbTransaction is not null)
            {
                await dbTransaction.DisposeAsync();
            }
        }
    }

    private async Task ReverseCashTraceAsync(
        EmployeeSalaryTransaction transaction,
        DateTime reversalDate,
        string reason,
        CancellationToken ct)
    {
        var original = await _db.PaymentTransactions
            .FirstOrDefaultAsync(p => p.Id == transaction.PaymentTransactionId!.Value, ct);
        if (original is null)
        {
            throw new BusinessRuleException("EMPLOYEE_SALARY_PAYMENT_MISSING", "سند روزنامچهٔ این تراکنش پیدا نشد؛ لغو بدون برگشتِ پول انجام نمی‌شود.");
        }

        var employee = transaction.Employee!;
        var reference = (original.Reference ?? $"EMP-SAL-{transaction.Id}") + CompanyFlowSourceTypes.ReversalReferenceSuffix;
        var description = $"لغو {EmployeeSalaryTransactionTypeLabels.ToPersian(transaction.TransactionType)} #{transaction.Id} / {employee.EmployeeCode} - {employee.FullName} / {reason}";
        if (description.Length > 1000)
        {
            description = description[..1000];
        }

        // برگشتِ خروج = ورودِ پول (EmployeeReturn)؛ برگشتِ بازپرداخت = خروجِ دوبارهٔ همان پول.
        var originalIsInflow = original.Direction == PaymentDirection.In;
        var reversal = new PaymentTransaction
        {
            PaymentDate = reversalDate,
            Direction = originalIsInflow ? PaymentDirection.Out : PaymentDirection.In,
            PaymentKind = originalIsInflow ? PaymentKind.EmployeeLoan : PaymentKind.EmployeeReturn,
            CashAccountId = original.CashAccountId,
            EmployeeId = original.EmployeeId,
            Amount = original.Amount,
            Currency = original.Currency,
            AppliedFxRateToUsd = original.AppliedFxRateToUsd,
            AmountUsd = original.AmountUsd,
            Reference = reference.Length > 200 ? reference[..200] : reference,
            Description = description
        };

        _db.PaymentTransactions.Add(reversal);
        await _db.SaveChangesAsync(ct);

        var ledgerEntry = Ledger.Post(new LedgerPostingRequest
        {
            EntryDate = reversalDate,
            Side = originalIsInflow ? LedgerSide.Debit : LedgerSide.Credit,
            AmountUsd = reversal.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = reversal.Amount,
            SourceCurrencyCode = reversal.Currency,
            AppliedFxRateToUsd = reversal.AppliedFxRateToUsd,
            Description = description,
            SourceType = reversal.PaymentKind.ToString(),
            SourceId = reversal.Id,
            Reference = reversal.Reference,
            EmployeeId = reversal.EmployeeId
        });
        await _db.SaveChangesAsync(ct);

        reversal.LedgerEntryId = ledgerEntry.Id;
        transaction.ReversalPaymentTransactionId = reversal.Id;
        transaction.ReversalLedgerEntryId = ledgerEntry.Id;
        await _db.SaveChangesAsync(ct);

        // دفتر کل: ژورنالِ پرداختِ اصلی برمی‌گردد. سندِ معکوس (EmployeeReturn) خودش ژورنال
        // نمی‌گیرد، پس اثر دو بار خنثی نمی‌شود.
        if (_paymentAccounting is not null)
        {
            await _paymentAccounting.TryPostPaymentReversalAsync(original, ct);
        }

        await _audit.LogAsync(
            nameof(PaymentTransaction),
            reversal.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("PaymentDate", reversal.PaymentDate),
                ("Direction", reversal.Direction),
                ("PaymentKind", reversal.PaymentKind),
                ("CashAccountId", reversal.CashAccountId),
                ("EmployeeId", reversal.EmployeeId),
                ("Amount", reversal.Amount),
                ("Currency", reversal.Currency),
                ("AmountUsd", reversal.AmountUsd),
                ("Reference", reversal.Reference),
                ("ReversesPaymentTransactionId", original.Id),
                ("LedgerEntryId", reversal.LedgerEntryId)));
    }

    private async Task<PaymentTransaction> CreateCashTraceAsync(
        Employee employee,
        EmployeeSalaryTransaction salaryTransaction,
        CashAccount cashAccount,
        CurrencyConversionResult conversion,
        CancellationToken ct)
    {
        var paymentKind = salaryTransaction.TransactionType switch
        {
            EmployeeSalaryTransactionType.SalaryAdvance => PaymentKind.EmployeeSalaryAdvance,
            EmployeeSalaryTransactionType.LoanDisbursement => PaymentKind.EmployeeLoan,
            EmployeeSalaryTransactionType.LoanRepayment => PaymentKind.EmployeeLoanRepayment,
            _ => PaymentKind.EmployeeSalaryPayment
        };
        var isInflow = EmployeeSalaryTransactionTypeLabels.IsCashInflow(salaryTransaction.TransactionType);

        var payment = new PaymentTransaction
        {
            PaymentDate = salaryTransaction.TransactionDate,
            Direction = isInflow ? PaymentDirection.In : PaymentDirection.Out,
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
            Side = isInflow ? LedgerSide.Credit : LedgerSide.Debit,
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

        return payment;
    }

    /// <summary>
    /// یک «ثبت معاش» فعال برای هر کارمند در هر ماه. همین قاعده در دیتابیس هم با ایندکسِ یکتای
    /// فیلترشده (UX_EmployeeSalaryTransactions_ActiveAccrualPerPeriod) نگه داشته می‌شود.
    /// </summary>
    private async Task EnsureNoActiveAccrualAsync(EmployeeSalaryTransactionCommand command, CancellationToken ct)
    {
        if (command.TransactionType != EmployeeSalaryTransactionType.SalaryAccrual)
        {
            return;
        }

        var exists = await _db.EmployeeSalaryTransactions.AsNoTracking().AnyAsync(t =>
            t.EmployeeId == command.EmployeeId
            && t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual
            && !t.IsCancelled
            && t.SalaryPeriodYear == command.SalaryPeriodYear
            && t.SalaryPeriodMonth == command.SalaryPeriodMonth, ct);
        if (exists)
        {
            throw DuplicateAccrual();
        }
    }

    private async Task SaveChangesTranslatingDuplicateAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsActiveAccrualIndexViolation(ex))
        {
            throw DuplicateAccrual();
        }
    }

    private static bool IsActiveAccrualIndexViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("UX_EmployeeSalaryTransactions_ActiveAccrualPerPeriod", StringComparison.Ordinal) == true;

    private static BusinessRuleException DuplicateAccrual()
        => new("EMPLOYEE_SALARY_ACCRUAL_DUPLICATE",
            "معاشِ این ماه برای این کارمند قبلاً ثبت شده است. برای تغییر، ثبتِ قبلی را با دلیل لغو کنید.");

    /// <summary>طلبِ مساعدهٔ باز = مساعده‌های فعال − وصول‌های فعال (به دالر).</summary>
    public static async Task<decimal> GetOutstandingAdvanceUsdAsync(
        ApplicationDbContext db,
        int employeeId,
        int? excludeTransactionId,
        CancellationToken ct)
    {
        var rows = await db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.EmployeeId == employeeId
                && !t.IsCancelled
                && (excludeTransactionId == null || t.Id != excludeTransactionId)
                && (t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                    || t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery))
            .Select(t => new { t.TransactionType, t.AmountUsd })
            .ToListAsync(ct);

        return rows.Where(r => r.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance).Sum(r => r.AmountUsd)
            - rows.Where(r => r.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery).Sum(r => r.AmountUsd);
    }

    /// <summary>طلبِ باز مساعده به ارزِ خودش (مساعده منهای وصول، همان ارز).</summary>
    public static async Task<decimal> GetOutstandingAdvanceAsync(
        ApplicationDbContext db,
        int employeeId,
        string currency,
        int? excludeTransactionId,
        CancellationToken ct)
    {
        var code = SystemCurrency.Normalize(currency);
        var rows = await db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.EmployeeId == employeeId
                && !t.IsCancelled
                && t.Currency == code
                && (excludeTransactionId == null || t.Id != excludeTransactionId)
                && (t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                    || t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery))
            .Select(t => new { t.TransactionType, t.Amount })
            .ToListAsync(ct);

        return rows.Where(r => r.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance).Sum(r => r.Amount)
            - rows.Where(r => r.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery).Sum(r => r.Amount);
    }

    /// <summary>ماندهٔ قرضه به ارزِ خودش: پرداخت منهای قسط‌های کسرشده و بازپرداخت‌ها.</summary>
    public static async Task<decimal> GetLoanOutstandingAsync(
        ApplicationDbContext db,
        int loanId,
        int? excludeTransactionId,
        CancellationToken ct)
    {
        var rows = await db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.EmployeeLoanId == loanId
                && !t.IsCancelled
                && (excludeTransactionId == null || t.Id != excludeTransactionId))
            .Select(t => new { t.TransactionType, t.Amount })
            .ToListAsync(ct);

        return rows.Where(r => r.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement).Sum(r => r.Amount)
            - rows.Where(r => r.TransactionType is EmployeeSalaryTransactionType.LoanRecovery or EmployeeSalaryTransactionType.LoanRepayment).Sum(r => r.Amount);
    }

    private async Task EnsureRecoveryWithinOutstandingAdvanceAsync(
        int employeeId,
        string currency,
        decimal amount,
        CancellationToken ct)
    {
        var outstanding = await GetOutstandingAdvanceAsync(_db, employeeId, currency, null, ct);
        if (amount > outstanding)
        {
            throw new BusinessRuleException(
                "EMPLOYEE_ADVANCE_RECOVERY_EXCEEDS_OUTSTANDING",
                $"مبلغ وصول ({amount:N2} {currency}) از طلبِ باز مساعده ({outstanding:N2} {currency}) بیشتر است.");
        }
    }

    private async Task ValidateLoanLinkAsync(EmployeeSalaryTransactionCommand command, int employeeId, string currency, CancellationToken ct)
    {
        var isLoanType = command.TransactionType is EmployeeSalaryTransactionType.LoanDisbursement
            or EmployeeSalaryTransactionType.LoanRecovery
            or EmployeeSalaryTransactionType.LoanRepayment;
        if (!isLoanType)
        {
            if (command.EmployeeLoanId.HasValue)
                throw new BusinessRuleException("EMPLOYEE_LOAN_LINK_INVALID", "این نوع تراکنش به قرضه پیوند نمی‌خورد.");
            return;
        }

        if (!command.EmployeeLoanId.HasValue)
            throw new BusinessRuleException("EMPLOYEE_LOAN_REQUIRED", "قرضهٔ مربوط مشخص نیست.");

        var loan = await _db.EmployeeLoans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == command.EmployeeLoanId.Value, ct);
        if (loan is null || loan.EmployeeId != employeeId)
            throw new BusinessRuleException("EMPLOYEE_LOAN_NOT_FOUND", "قرضه پیدا نشد.");
        if (loan.Status == EmployeeLoanStatus.Cancelled)
            throw new BusinessRuleException("EMPLOYEE_LOAN_CANCELLED", "قرضهٔ لغوشده تراکنش نمی‌گیرد.");
        if (!string.Equals(SystemCurrency.Normalize(loan.Currency), currency, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("EMPLOYEE_LOAN_CURRENCY", $"ارزِ قسط/بازپرداخت باید همان ارزِ قرضه ({loan.Currency}) باشد.");

        if (command.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement)
        {
            if (await _db.EmployeeSalaryTransactions.AnyAsync(t => t.EmployeeLoanId == loan.Id
                    && t.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement && !t.IsCancelled, ct))
                throw new BusinessRuleException("EMPLOYEE_LOAN_ALREADY_DISBURSED", "این قرضه قبلاً پرداخت شده است.");
            return;
        }

        var outstanding = await GetLoanOutstandingAsync(_db, loan.Id, null, ct);
        if (command.Amount > outstanding)
            throw new BusinessRuleException("EMPLOYEE_LOAN_EXCEEDS_OUTSTANDING",
                $"مبلغ ({command.Amount:N2} {loan.Currency}) از ماندهٔ قرضه ({outstanding:N2} {loan.Currency}) بیشتر است.");
    }

    private static void ValidateRecoveryMonth(EmployeeSalaryTransactionCommand command)
    {
        if (command.TransactionType != EmployeeSalaryTransactionType.SalaryAdvance
            || (!command.RecoveryYear.HasValue && !command.RecoveryMonth.HasValue))
            return;

        if (!command.RecoveryYear.HasValue || !command.RecoveryMonth.HasValue
            || command.RecoveryYear is < 2000 or > 2100 || command.RecoveryMonth is < 1 or > 12)
            throw new BusinessRuleException("EMPLOYEE_ADVANCE_RECOVERY_MONTH", "ماهِ وصولِ مساعده معتبر نیست.");

        var recovery = new DateTime(command.RecoveryYear.Value, command.RecoveryMonth.Value, 1);
        var advanceMonth = new DateTime(command.TransactionDate.Year, command.TransactionDate.Month, 1);
        if (recovery < advanceMonth)
            throw new BusinessRuleException("EMPLOYEE_ADVANCE_RECOVERY_MONTH", "ماهِ وصول نمی‌تواند پیش از ماهِ مساعده باشد.");
    }

    /// <summary>ماندهٔ صفر = «تسویه»؛ برگشتِ قسط یا بازپرداخت قرضه را دوباره فعال می‌کند.</summary>
    private async Task RefreshLoanStatusAsync(int loanId, CancellationToken ct)
    {
        var loan = await _db.EmployeeLoans.FirstOrDefaultAsync(l => l.Id == loanId, ct);
        if (loan is null || loan.Status == EmployeeLoanStatus.Cancelled)
            return;

        var disbursed = await _db.EmployeeSalaryTransactions.AnyAsync(t => t.EmployeeLoanId == loanId
            && t.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement && !t.IsCancelled, ct);
        if (!disbursed)
            return;

        var outstanding = await GetLoanOutstandingAsync(_db, loanId, null, ct);
        var next = outstanding <= 0m ? EmployeeLoanStatus.Paid : EmployeeLoanStatus.Active;
        if (next == loan.Status)
            return;

        var before = loan.Status;
        loan.Status = next;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(nameof(EmployeeLoan), loan.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("Status", before, loan.Status)));
    }

    private async Task EnsureAdvanceCancellationKeepsRecoveriesCoveredAsync(
        EmployeeSalaryTransaction advance,
        CancellationToken ct)
    {
        var outstandingWithout = await GetOutstandingAdvanceAsync(_db, advance.EmployeeId, advance.Currency, advance.Id, ct);
        if (outstandingWithout < 0m)
        {
            throw new BusinessRuleException(
                "EMPLOYEE_ADVANCE_ALREADY_RECOVERED",
                "این مساعده از معاش وصول شده است. اول وصولِ آن را لغو کنید.");
        }
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
