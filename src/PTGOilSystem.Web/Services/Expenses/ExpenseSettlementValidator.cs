using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Expenses;

/// <summary>خطای «این رویداد مالی ناقص است و اصلاً نباید ذخیره می‌شد».</summary>
public sealed class ExpenseSettlementValidationException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// تنها مالکِ قاعدهٔ «هر مصرف دقیقاً یک هویت تسویه دارد».
///
/// پیش از فاز ۱، یک مصرف می‌توانست بدون پرداخت و بدون طرف‌حساب ذخیره شود. آن سطر در دفتر
/// می‌نشست، در هیچ حساب طرف‌حسابی دیده نمی‌شد و از «طلبات و بدهی‌ها» بیرون می‌افتاد —
/// یعنی بدهیِ نامرئی. این اعتبارسنج همان حالت را در لحظهٔ ذخیره رد می‌کند.
///
/// کنترلرها این قواعد را تکرار نمی‌کنند؛ فقط دادهٔ کسب‌وکاری می‌فرستند و
/// <see cref="IExpenseLedgerPoster"/> پیش از نوشتن، از همین‌جا عبور می‌کند.
/// </summary>
public interface IExpenseSettlementValidator
{
    /// <summary>در صورت ناقص‌بودن استثنا پرتاب می‌کند.</summary>
    void Validate(ExpenseTransaction expense);

    /// <summary>همان قاعده، بدون استثنا — برای اعتبارسنجی فرم.</summary>
    ExpenseSettlementValidationResult Check(ExpenseTransaction expense);
}

public sealed record ExpenseSettlementValidationResult(
    bool IsValid,
    string? Code = null,
    string? Message = null,
    string? MemberName = null)
{
    public static readonly ExpenseSettlementValidationResult Valid = new(true);
}

public sealed class ExpenseSettlementValidator : IExpenseSettlementValidator
{
    public const string ModeMissing = "EXPENSE_SETTLEMENT_MODE_REQUIRED";
    public const string CounterpartyMissing = "EXPENSE_COUNTERPARTY_REQUIRED";
    public const string CounterpartyNotAllowed = "EXPENSE_COUNTERPARTY_NOT_ALLOWED";
    public const string CashAccountMissing = "EXPENSE_CASH_ACCOUNT_REQUIRED";
    public const string CashAccountNotAllowed = "EXPENSE_CASH_ACCOUNT_NOT_ALLOWED";

    public void Validate(ExpenseTransaction expense)
    {
        var result = Check(expense);
        if (!result.IsValid)
        {
            throw new ExpenseSettlementValidationException(result.Code!, result.Message!);
        }
    }

    public ExpenseSettlementValidationResult Check(ExpenseTransaction expense)
    {
        ArgumentNullException.ThrowIfNull(expense);

        // مصرفِ لغوشده دیگر تعهدی ندارد؛ رکوردهای تاریخی هم لغو می‌شوند و نباید
        // در لحظهٔ لغو به‌خاطر قاعدهٔ تازه رد شوند.
        if (expense.IsCancelled)
        {
            return ExpenseSettlementValidationResult.Valid;
        }

        return expense.SettlementMode switch
        {
            // رکوردهای پیش از فاز ۱. ذخیرهٔ تازه با این حالت مجاز نیست، ولی ردیف موجود
            // خوانده و ویرایش‌های غیرمالی‌اش انجام می‌شود — حدس زدن ممنوع است.
            ExpenseSettlementMode.Unknown => expense.Id == 0
                ? new ExpenseSettlementValidationResult(
                    false,
                    ModeMissing,
                    "وضعیت تسویهٔ مصرف مشخص نیست: یا پرداخت‌شده از یک حساب نقدی، یا بدهی به یک طرف‌حساب.",
                    nameof(ExpenseTransaction.SettlementMode))
                : ExpenseSettlementValidationResult.Valid,

            ExpenseSettlementMode.Payable => RequireCounterparty(expense) ?? RejectCashAccount(expense),

            ExpenseSettlementMode.PaidImmediately => RequireCashAccount(expense) ?? RejectCounterparty(expense),

            // بدون حرکت پول و بدون طرف‌حساب بیرونی. حسابِ مقابل از نوع مصرف می‌آید و
            // نبودش را ExpenseAccountingAdapter به‌عنوان Skip گزارش می‌کند، نه به‌عنوان بدهیِ گم‌شده.
            ExpenseSettlementMode.NonCash => RejectCounterparty(expense) ?? RejectCashAccount(expense),

            _ => new ExpenseSettlementValidationResult(
                false,
                ModeMissing,
                "وضعیت تسویهٔ مصرف معتبر نیست.",
                nameof(ExpenseTransaction.SettlementMode))
        };
    }

    private static ExpenseSettlementValidationResult? RequireCounterparty(ExpenseTransaction expense)
        => expense.CounterpartyType.HasValue && expense.CounterpartyId is > 0
            ? null
            : new ExpenseSettlementValidationResult(
                false,
                CounterpartyMissing,
                "مصرفِ پرداخت‌نشده باید طرف‌حساب مشخص داشته باشد تا به‌عنوان بدهی دیده شود.",
                nameof(ExpenseTransaction.CounterpartyId));

    private static ExpenseSettlementValidationResult? RequireCashAccount(ExpenseTransaction expense)
        => expense.CashAccountId is > 0
            ? null
            : new ExpenseSettlementValidationResult(
                false,
                CashAccountMissing,
                "مصرفِ پرداخت‌شده باید حساب نقدی (صندوق یا بانک) داشته باشد.",
                nameof(ExpenseTransaction.CashAccountId));

    private static ExpenseSettlementValidationResult RejectCounterparty(ExpenseTransaction expense)
        => expense.CounterpartyType.HasValue || expense.CounterpartyId is > 0
            ? new ExpenseSettlementValidationResult(
                false,
                CounterpartyNotAllowed,
                "مصرفی که پرداخت شده یا حرکت پول ندارد، طرف‌حسابِ بدهکار نمی‌گیرد.",
                nameof(ExpenseTransaction.CounterpartyId))
            : ExpenseSettlementValidationResult.Valid;

    private static ExpenseSettlementValidationResult RejectCashAccount(ExpenseTransaction expense)
        => expense.CashAccountId is > 0
            ? new ExpenseSettlementValidationResult(
                false,
                CashAccountNotAllowed,
                "مصرفی که هنوز پرداخت نشده، حساب نقدی نمی‌گیرد.",
                nameof(ExpenseTransaction.CashAccountId))
            : ExpenseSettlementValidationResult.Valid;
}
