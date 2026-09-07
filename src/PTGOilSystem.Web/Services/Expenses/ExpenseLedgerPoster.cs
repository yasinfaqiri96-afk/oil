using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;

namespace PTGOilSystem.Web.Services.Expenses;

/// <summary>
/// آنچه یک مسیرِ کسب‌وکاری دربارهٔ سطر دفترِ یک مصرف تعیین می‌کند — و نه بیشتر.
///
/// جهت، فیلدهای طرف‌حساب، ارجاعِ قرارداد/محموله و شکلِ ارز از خودِ
/// <see cref="ExpenseTransaction"/> خوانده می‌شوند و مسیرها دربارهٔ آن‌ها تصمیم نمی‌گیرند.
/// تنها متن (شرح و مرجع) و ردِ نرخِ ارز است که به‌درستی از مسیر می‌آید.
/// </summary>
public sealed record ExpenseLedgerRequest
{
    public required ExpenseTransaction Expense { get; init; }

    /// <summary>وقتی <see cref="Description"/> یا <see cref="Reference"/> داده نشود، متن از این ساخته می‌شود.</summary>
    public ExpenseType? ExpenseType { get; init; }

    public string? Description { get; init; }
    public string? Reference { get; init; }

    public DateTime? FxRateDate { get; init; }
    public string? FxRateSource { get; init; }

    /// <summary>
    /// فیلدهایی که این مسیر هرگز خودش تعیین نمی‌کند و باید عیناً از سطرِ موجود بمانند
    /// (هماهنگ‌سازیِ سندِ ویرایش‌شده). برای ثبتِ تازه null است.
    /// </summary>
    public LedgerEntry? CarryFrom { get; init; }

    /// <summary>سند هنوز Id ندارد و بلافاصله بعد از نخستین SaveChanges پر می‌شود.</summary>
    public bool AllowDeferredSourceId { get; init; }
}

/// <summary>
/// تنها مالکِ «یک مصرف چطور در دفتر کل می‌نشیند».
///
/// پیش از این نُه مسیرِ مستقل، هرکدام <see cref="LedgerPostingRequest"/> خودشان را دستی
/// می‌ساختند. سه قاعدهٔ متفاوت برای <c>Side</c> کنار هم زندگی می‌کردند و فیلدهای طرف‌حساب
/// در هر مسیر متفاوت پر می‌شد: فقط دو مسیر از نُه مسیر <c>DriverId</c> را می‌فرستادند، پس
/// کرایهٔ راننده در همه‌جای دیگر سطری بی‌طرف‌حساب می‌ساخت و از «طلبات و بدهی‌ها» بیرون
/// می‌افتاد. رجوع: <see cref="ResolveSide"/>.
///
/// این کلاس هیچ مبلغی محاسبه یا گِرد نمی‌کند؛ همان اعداد را در یک شکلِ واحد می‌نویسد.
/// </summary>
public interface IExpenseLedgerPoster
{
    /// <summary>
    /// جهتِ سطر مصرف — یک قاعده برای همهٔ مسیرها.
    ///
    /// طرف‌حسابِ بیرونی داریم ⇒ تعهد ایجاد شده و سطر <c>Credit</c> روی حساب همان طرف است.
    /// طرف‌حسابی نیست ⇒ هزینهٔ داخلی و سطر <c>Debit</c>.
    ///
    /// این همان چیزی است که هر نُه مسیر امروز می‌نویسند: مسیرهایی که <c>Debit</c> ثابت
    /// داشتند مصرفی می‌سازند که نه شرکت خدماتی دارد نه راننده، و مسیری که <c>Credit</c>
    /// ثابت داشت همیشه یکی از این دو را دارد. تفاوت فقط این است که راننده حالا در همهٔ
    /// مسیرها شمرده می‌شود، نه دو تا.
    /// </summary>
    LedgerSide ResolveSide(ExpenseTransaction expense);

    LedgerPostingRequest BuildRequest(ExpenseLedgerRequest request);

    LedgerEntry Post(ExpenseLedgerRequest request);

    LedgerEntry Apply(LedgerEntry target, ExpenseLedgerRequest request);
}

public sealed class ExpenseLedgerPoster(ILedgerPostingService ledger) : IExpenseLedgerPoster
{
    public const string ExpenseSourceType = "Expense";

    /// <summary>سقفِ ستون <c>LedgerEntry.Reference</c>.</summary>
    private const int ReferenceMaxLength = 200;

    public LedgerSide ResolveSide(ExpenseTransaction expense)
    {
        ArgumentNullException.ThrowIfNull(expense);
        return HasCounterparty(expense) ? LedgerSide.Credit : LedgerSide.Debit;
    }

    public LedgerPostingRequest BuildRequest(ExpenseLedgerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expense = request.Expense
            ?? throw new ArgumentException("Expense is required.", nameof(request));

        return new LedgerPostingRequest
        {
            SourceType = ExpenseSourceType,
            SourceId = expense.Id,
            AllowDeferredSourceId = request.AllowDeferredSourceId,
            EntryDate = expense.ExpenseDate,
            Side = ResolveSide(expense),
            AmountUsd = expense.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = expense.Amount,
            SourceCurrencyCode = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            AppliedFxRateDate = request.FxRateDate ?? expense.ExpenseDate,
            AppliedFxRateSource = request.FxRateSource,
            Description = ResolveDescription(request),
            Reference = ResolveReference(request),

            // ارجاعات همیشه از خودِ سند خوانده می‌شوند تا هیچ مسیری نتواند یکی را جا بیندازد.
            ContractId = expense.ContractId,
            ShipmentId = expense.ShipmentId,
            ServiceProviderId = expense.ServiceProviderId,
            DriverId = expense.DriverId,

            // فیلدهایی که مسیرِ مصرف هرگز تعیین نمی‌کند.
            CustomerId = request.CarryFrom?.CustomerId,
            SupplierId = request.CarryFrom?.SupplierId,
            EmployeeId = request.CarryFrom?.EmployeeId,
            PartnerId = request.CarryFrom?.PartnerId,
            ViaSarrafGroupId = request.CarryFrom?.ViaSarrafGroupId,
            AppliedCurrencyPerUsdRate = request.CarryFrom?.AppliedCurrencyPerUsdRate
        };
    }

    public LedgerEntry Post(ExpenseLedgerRequest request)
        => ledger.Post(BuildRequest(request));

    public LedgerEntry Apply(LedgerEntry target, ExpenseLedgerRequest request)
        => ledger.Apply(target, BuildRequest(request));

    /// <summary>
    /// «طرف‌حسابِ بیرونیِ» یک مصرف: شرکت خدماتی، وگرنه راننده.
    /// همان ترتیبی که <c>ExpenseAccountingAdapter</c> برای سطرِ طرف در دفتر کل جدید دارد.
    /// </summary>
    public static bool HasCounterparty(ExpenseTransaction expense)
        => expense.ServiceProviderId.HasValue || expense.DriverId.HasValue;

    /// <summary>
    /// PTG-P1-04 — هویت تسویه برای مصرفی که طرف‌حسابش از فیلدهای خودِ سند خوانده می‌شود.
    ///
    /// قاعده تازه نیست: ترتیبِ «شرکت خدماتی، وگرنه راننده» همان
    /// <see cref="Accounting.ExpenseAccountingAdapter.ResolveParty"/> است و از همان‌جا خوانده
    /// می‌شود تا نسخهٔ سومی از این قاعده ساخته نشود. نبودِ طرف‌حسابِ بیرونی هم حالتی مستند
    /// است، نه حدس: دفتر کلِ جدید برای همین مصرف‌ها «سطرِ طرف» نمی‌نویسد و حسابِ مقابل را از
    /// <see cref="ExpenseType.PayableAccountKind"/> می‌گیرد — یعنی دقیقاً تعریفِ
    /// <see cref="ExpenseSettlementMode.NonCash"/>.
    ///
    /// <see cref="ExpenseTransaction.CashAccountId"/> عمداً دست‌نخورده می‌ماند: مصرفی که از
    /// صندوق پرداخت شده، حالتش را خودِ آن مسیر صریح ست می‌کند.
    /// </summary>
    public static void ApplyCounterpartySettlement(ExpenseTransaction expense)
    {
        ArgumentNullException.ThrowIfNull(expense);

        var (partyType, partyId) = Accounting.ExpenseAccountingAdapter.ResolveParty(expense);
        if (partyType is null)
        {
            expense.SettlementMode = ExpenseSettlementMode.NonCash;
            expense.CounterpartyType = null;
            expense.CounterpartyId = null;
            return;
        }

        expense.SettlementMode = ExpenseSettlementMode.Payable;
        expense.CounterpartyType = partyType;
        expense.CounterpartyId = partyId;
    }

    public static string BuildDescription(ExpenseType expenseType, ExpenseTransaction expense)
    {
        ArgumentNullException.ThrowIfNull(expenseType);
        ArgumentNullException.ThrowIfNull(expense);

        var baseText = $"ثبت هزینه {expenseType.NamePersian ?? expenseType.Name}";
        return string.IsNullOrWhiteSpace(expense.Description)
            ? baseText
            : $"{baseText} - {expense.Description}";
    }

    public static string BuildReference(ExpenseType expenseType, ExpenseTransaction expense)
    {
        ArgumentNullException.ThrowIfNull(expenseType);
        ArgumentNullException.ThrowIfNull(expense);

        var prefix = string.IsNullOrWhiteSpace(expenseType.Code)
            ? $"EXP-{expense.Id}"
            : $"{expenseType.Code}-{expense.Id}";
        if (string.IsNullOrWhiteSpace(expense.Description))
        {
            return prefix;
        }

        var combined = $"{prefix} | {expense.Description.Trim()}";
        return combined.Length <= ReferenceMaxLength ? combined : combined[..ReferenceMaxLength];
    }

    private static string ResolveDescription(ExpenseLedgerRequest request)
        => request.Description
            ?? (request.ExpenseType is not null
                ? BuildDescription(request.ExpenseType, request.Expense)
                : throw new ArgumentException(
                    "Either Description or ExpenseType must be supplied.",
                    nameof(request)));

    private static string ResolveReference(ExpenseLedgerRequest request)
        => request.Reference
            ?? (request.ExpenseType is not null
                ? BuildReference(request.ExpenseType, request.Expense)
                : throw new ArgumentException(
                    "Either Reference or ExpenseType must be supplied.",
                    nameof(request)));
}
