using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Accounting;

public sealed record EmployeeSalaryAccountingResult(
    PaymentPostingStatus Status,
    JournalEntry? Journal,
    string? Reason);

public interface IEmployeeSalaryAccountingAdapter
{
    /// <summary>
    /// ژورنالِ بخشِ غیرنقدیِ معاش (ثبت معاش، بونس، وصول مساعده) را ثبت می‌کند. اگر Accounting یا
    /// Pilot خاموش باشد هیچ ژورنالی ساخته نمی‌شود و تراکنشِ معاش دست‌نخورده می‌ماند.
    /// </summary>
    Task<EmployeeSalaryAccountingResult> TryPostAsync(
        EmployeeSalaryTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>ژورنالِ تراکنشِ لغوشده را با ژورنالِ قرینه برمی‌گرداند. چیزی حذف نمی‌شود؛ تکرار بی‌اثر است.</summary>
    Task<EmployeeSalaryAccountingResult> TryReverseAsync(
        EmployeeSalaryTransaction transaction,
        DateTime reversalDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// مدیریت بشری — دفتر کلِ دوطرفه برای بخشِ غیرنقدیِ معاش (Pilot: EmployeeSalary).
///
///   SalaryAccrual / Bonus   Dr Salary Expense      Cr Employee Payable   (party = employee)
///   AdvanceRecovery / LoanRecovery   Dr Employee Payable    Cr Employee Advance   (party = employee)
///
/// پرداخت و مساعدهٔ نقدی اینجا نیستند؛ آن‌ها PaymentTransaction دارند و از
/// <see cref="PaymentAccountingAdapter"/> می‌گذرند. پس مصرفِ معاش فقط یک بار — هنگام ثبت معاش —
/// شناسایی می‌شود و پرداخت هرگز دوباره آن را مصرف نمی‌کند.
///
/// «کسر معاش» و «اصلاح حساب» عمداً نگاشت نمی‌شوند: معنای حسابداری‌شان از خودِ تراکنش معلوم نیست
/// (کاهشِ مصرف؟ جریمه؟ اصلاحِ پرداخت؟) و حدس زدنِ آن ممنوع است. Legacy-only می‌مانند.
///
/// کارمند شرکت ندارد؛ در این سیستمِ تک‌شرکتی معاش همیشه از آنِ شرکتِ مالک است — همان تنها شرکتی
/// که سرویسِ ثبت می‌پذیرد.
/// </summary>
public sealed class EmployeeSalaryAccountingAdapter(
    ApplicationDbContext db,
    IAccountingPostingService postingService,
    IAccountingJournalNumberGenerator journalNumberGenerator,
    ISystemCompanyProvider systemCompany,
    IOptions<AccountingOptions> options,
    ILogger<EmployeeSalaryAccountingAdapter> logger)
    : IEmployeeSalaryAccountingAdapter
{
    public const string SourceModule = "EmployeeSalary";
    public const string SourceEntityType = nameof(EmployeeSalaryTransaction);

    private readonly AccountingOptions _options = options.Value;

    public static bool IsMapped(EmployeeSalaryTransactionType type)
        => type is EmployeeSalaryTransactionType.SalaryAccrual
            or EmployeeSalaryTransactionType.Bonus
            or EmployeeSalaryTransactionType.AdvanceRecovery
            or EmployeeSalaryTransactionType.LoanRecovery;

    public async Task<EmployeeSalaryAccountingResult> TryPostAsync(
        EmployeeSalaryTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var (companyId, settings, skipReason) = await ResolveAsync(transaction, cancellationToken);
        if (skipReason is not null)
            return Skipped(transaction, "Post", companyId, skipReason);

        var sourceEventId = BuildCreatedSourceEventId(transaction.Id);
        var existing = await FindJournalAsync(companyId, sourceEventId, cancellationToken);
        if (existing is not null)
        {
            LogOutcome(transaction, "Post", companyId, existing.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new EmployeeSalaryAccountingResult(PaymentPostingStatus.Duplicate, existing, "DUPLICATE_SOURCE_EVENT");
        }

        var (debitAccountId, debitIsParty, creditAccountId, creditIsParty) = transaction.TransactionType switch
        {
            EmployeeSalaryTransactionType.AdvanceRecovery or EmployeeSalaryTransactionType.LoanRecovery =>
                (settings!.EmployeePayableAccountId, true, settings.EmployeeAdvanceAccountId, true),
            _ => (settings!.SalaryExpenseAccountId!.Value, false, settings.EmployeePayableAccountId, true)
        };

        var rate = transaction.AppliedFxRateToUsd!.Value;
        var request = new AccountingPostRequest(
            companyId,
            journalNumberGenerator.ForEmployeeSalary(companyId, transaction.Id),
            transaction.TransactionDate.Date,
            transaction.TransactionDate.Date,
            transaction.TransactionDate.Date,
            SourceModule,
            [
                new AccountingPostLine(
                    debitAccountId,
                    Debit: transaction.AmountUsd,
                    Credit: 0m,
                    transaction.Currency,
                    transaction.Amount,
                    rate,
                    debitIsParty ? AccountingPartyType.Employee : null,
                    debitIsParty ? transaction.EmployeeId : null,
                    Description: DebitDescription(transaction.TransactionType)),
                new AccountingPostLine(
                    creditAccountId,
                    Debit: 0m,
                    Credit: transaction.AmountUsd,
                    transaction.Currency,
                    transaction.Amount,
                    rate,
                    creditIsParty ? AccountingPartyType.Employee : null,
                    creditIsParty ? transaction.EmployeeId : null,
                    Description: CreditDescription(transaction.TransactionType))
            ],
            SourceEventId: sourceEventId,
            SourceEntityType: SourceEntityType,
            SourceEntityId: transaction.Id,
            Description: $"{transaction.TransactionType} #{transaction.Id} employee {transaction.EmployeeId} on {transaction.TransactionDate:yyyy-MM-dd}");

        try
        {
            var journal = await postingService.PostAsync(request, cancellationToken);
            LogOutcome(transaction, "Post", companyId, journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new EmployeeSalaryAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(transaction, "Post", exception);
            throw;
        }
    }

    public async Task<EmployeeSalaryAccountingResult> TryReverseAsync(
        EmployeeSalaryTransaction transaction,
        DateTime reversalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // برگشت فقط به روشن‌بودنِ هستهٔ حسابداری بسته است، نه Pilot: ژورنالی که هست باید برگردد.
        if (!_options.Enabled)
            return Skipped(transaction, "Reverse", 0, "ACCOUNTING_DISABLED");

        var companyId = await systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (companyId is null)
            return Skipped(transaction, "Reverse", 0, "OWNER_COMPANY_MISSING");

        var reversedEventId = BuildReversedSourceEventId(transaction.Id);
        var existingReversal = await FindJournalAsync(companyId.Value, reversedEventId, cancellationToken);
        if (existingReversal is not null)
        {
            LogOutcome(transaction, "Reverse", companyId.Value, existingReversal.Lines.Sum(x => x.Debit),
                PaymentPostingStatus.Duplicate, "DUPLICATE_SOURCE_EVENT");
            return new EmployeeSalaryAccountingResult(PaymentPostingStatus.Duplicate, existingReversal, "DUPLICATE_SOURCE_EVENT");
        }

        var original = await FindJournalAsync(companyId.Value, BuildCreatedSourceEventId(transaction.Id), cancellationToken);
        if (original is null)
            return Skipped(transaction, "Reverse", companyId.Value, "ORIGINAL_JOURNAL_NOT_POSTED");

        var request = new AccountingReversalRequest(
            original.Id,
            journalNumberGenerator.ForEmployeeSalaryReversal(companyId.Value, transaction.Id),
            reversalDate.Date,
            SourceModule,
            reversedEventId,
            Description: $"Reversal of employee salary transaction #{transaction.Id}");

        try
        {
            var journal = await postingService.ReverseAsync(request, cancellationToken);
            LogOutcome(transaction, "Reverse", companyId.Value, journal.Lines.Sum(x => x.Debit), PaymentPostingStatus.Posted, null);
            return new EmployeeSalaryAccountingResult(PaymentPostingStatus.Posted, journal, null);
        }
        catch (Exception exception)
        {
            LogFailure(transaction, "Reverse", exception);
            throw;
        }
    }

    public static string BuildCreatedSourceEventId(int salaryTransactionId)
        => $"EmployeeSalary:{salaryTransactionId}:Created";

    public static string BuildReversedSourceEventId(int salaryTransactionId)
        => $"EmployeeSalary:{salaryTransactionId}:Reversed";

    private async Task<(int CompanyId, AccountingSettings? Settings, string? SkipReason)> ResolveAsync(
        EmployeeSalaryTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return (0, null, "ACCOUNTING_DISABLED");
        if (!_options.Pilots.EmployeeSalary)
            return (0, null, "PILOT_DISABLED");
        if (!IsMapped(transaction.TransactionType))
            return (0, null, "UNSUPPORTED_SALARY_TRANSACTION_TYPE");
        if (transaction.IsCancelled)
            return (0, null, "EMPLOYEE_SALARY_CANCELLED");
        if (transaction.Amount <= 0m || transaction.AmountUsd <= 0m)
            return (0, null, "INVALID_SALARY_AMOUNT");

        var rate = transaction.AppliedFxRateToUsd;
        if (!rate.HasValue || rate.Value <= 0m)
            return (0, null, "INVALID_SALARY_FX");
        if (SystemCurrency.IsBaseCurrency(transaction.Currency) && rate.Value != 1m)
            return (0, null, "INVALID_SALARY_FX");
        var expectedUsd = decimal.Round(transaction.Amount * rate.Value, 4, MidpointRounding.AwayFromZero);
        if (transaction.AmountUsd != expectedUsd)
            return (0, null, "INVALID_SALARY_CONVERSION");

        var companyId = await systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (companyId is null)
            return (0, null, "OWNER_COMPANY_MISSING");

        var settings = await db.AccountingSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == companyId.Value, cancellationToken);
        if (settings is null)
            return (companyId.Value, null, "ACCOUNTING_SETTINGS_MISSING");
        if (!string.Equals(settings.FunctionalCurrencyCode?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return (companyId.Value, null, "UNSUPPORTED_FUNCTIONAL_CURRENCY");

        var needsExpense = transaction.TransactionType is not (EmployeeSalaryTransactionType.AdvanceRecovery
            or EmployeeSalaryTransactionType.LoanRecovery);
        if (needsExpense && settings.SalaryExpenseAccountId is null or <= 0)
            return (companyId.Value, null, "SALARY_EXPENSE_ACCOUNT_NOT_CONFIGURED");

        var accountIds = needsExpense
            ? new[] { settings.SalaryExpenseAccountId!.Value, settings.EmployeePayableAccountId }
            : new[] { settings.EmployeePayableAccountId, settings.EmployeeAdvanceAccountId };
        if (accountIds.Any(x => x <= 0) || accountIds.Distinct().Count() != accountIds.Length)
            return (companyId.Value, null, "ACCOUNTING_SETTINGS_INCOMPLETE");

        var validAccountCount = await db.Accounts.AsNoTracking().CountAsync(
            x => accountIds.Contains(x.Id) && x.CompanyId == companyId.Value && x.IsActive,
            cancellationToken);
        if (validAccountCount != accountIds.Length)
            return (companyId.Value, null, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");

        return (companyId.Value, settings, null);
    }

    private static string DebitDescription(EmployeeSalaryTransactionType type)
        => type switch
        {
            EmployeeSalaryTransactionType.Bonus => "Employee bonus expense",
            EmployeeSalaryTransactionType.AdvanceRecovery => "Employee payable reduced by advance recovery",
            EmployeeSalaryTransactionType.LoanRecovery => "Employee payable reduced by loan installment",
            _ => "Salary expense"
        };

    private static string CreditDescription(EmployeeSalaryTransactionType type)
        => type switch
        {
            EmployeeSalaryTransactionType.AdvanceRecovery => "Employee advance recovered",
            EmployeeSalaryTransactionType.LoanRecovery => "Employee loan installment recovered",
            _ => "Employee salary payable"
        };

    private async Task<JournalEntry?> FindJournalAsync(
        int companyId,
        string sourceEventId,
        CancellationToken cancellationToken)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyId == companyId
                    && x.SourceModule == SourceModule
                    && x.SourceEventId == sourceEventId,
                cancellationToken);

    private EmployeeSalaryAccountingResult Skipped(
        EmployeeSalaryTransaction transaction,
        string eventKind,
        int companyId,
        string reason)
    {
        LogOutcome(transaction, eventKind, companyId, 0m, PaymentPostingStatus.Skipped, reason);
        return new EmployeeSalaryAccountingResult(PaymentPostingStatus.Skipped, null, reason);
    }

    private void LogOutcome(
        EmployeeSalaryTransaction transaction,
        string eventKind,
        int companyId,
        decimal journalDebitTotal,
        PaymentPostingStatus status,
        string? reason)
        => logger.LogInformation(
            "Employee salary accounting pilot: SalaryTransactionId {SalaryTransactionId}, Type {TransactionType}, EventKind {EventKind}, CompanyId {CompanyId}, EmployeeId {EmployeeId}, AmountUsd {AmountUsd}, JournalDebitTotal {JournalDebitTotal}, PostingStatus {PostingStatus}, SkipOrFailureReason {SkipOrFailureReason}",
            transaction.Id,
            transaction.TransactionType,
            eventKind,
            companyId,
            transaction.EmployeeId,
            transaction.AmountUsd,
            journalDebitTotal,
            status,
            reason);

    private void LogFailure(EmployeeSalaryTransaction transaction, string eventKind, Exception exception)
    {
        var failureReason = exception is AccountingValidationException validation
            ? validation.Code
            : exception.GetType().Name;
        logger.LogError(
            exception,
            "Employee salary accounting posting failed for SalaryTransactionId {SalaryTransactionId} ({EventKind}) with FailureReason {FailureReason}",
            transaction.Id,
            eventKind,
            failureReason);
    }
}
