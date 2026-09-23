using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Reporting;

/// <summary>
/// جمعِ حرکت پولِ یک صندوق/بانک. دو منبعِ واقعی دارد و هر دو اینجا یک‌جا شمرده می‌شوند:
/// سندِ روزنامچه و «مصرفِ نقدیِ مستقل» (مصرفی که نقد پرداخت شده و سندِ روزنامچه ندارد).
/// </summary>
/// <remarks>
/// مبلغِ ارزی فقط وقتی جمع زده می‌شود که همهٔ اسنادِ حساب به ارزِ خودِ حساب باشند؛ در غیر این
/// صورت معادلِ دالریِ ثبت‌شدهٔ همان اسناد مبنای جمع است — همان قاعدهٔ صفحهٔ جزئیات صندوق.
/// </remarks>
public sealed record CashAccountActivityTotals(
    int CashAccountId,
    string AccountCurrency,
    decimal NativeIn,
    decimal NativeOut,
    decimal UsdIn,
    decimal UsdOut,
    int ForeignCurrencyDocumentCount,
    int MissingUsdEquivalentCount)
{
    /// <summary>سندی به ارزِ دیگر دارد، پس جمعِ ارزی بی‌معناست و جمعِ دالری نمایش داده می‌شود.</summary>
    public bool UsesUsdTotals => ForeignCurrencyDocumentCount > 0;

    public string TotalsCurrency => UsesUsdTotals ? SystemCurrency.BaseCurrencyCode : AccountCurrency;

    public decimal TotalIn => UsesUsdTotals ? UsdIn : NativeIn;

    public decimal TotalOut => UsesUsdTotals ? UsdOut : NativeOut;

    /// <summary>ماندهٔ حساب به <see cref="TotalsCurrency"/>.</summary>
    public decimal Balance => TotalIn - TotalOut;

    /// <summary>ماندهٔ حساب به معادلِ دالریِ ثبت‌شده در هر سند (نه تبدیل با نرخ امروز).</summary>
    public decimal BalanceUsd => UsdIn - UsdOut;
}

public interface ICashPositionReader
{
    /// <summary>جمعِ همهٔ حساب‌هایی که حرکت دارند.</summary>
    Task<IReadOnlyList<CashAccountActivityTotals>> ReadAccountTotalsAsync(CancellationToken ct = default);

    /// <param name="cashAccountIds">null یعنی همهٔ حساب‌ها.</param>
    /// <param name="beforeDate">اگر داده شود فقط اسنادِ پیش از این روز (ماندهٔ اول دوره).</param>
    Task<IReadOnlyList<CashAccountActivityTotals>> ReadAccountTotalsAsync(
        IReadOnlyCollection<int>? cashAccountIds,
        DateTime? beforeDate,
        CancellationToken ct = default);
}

/// <summary>
/// تنها مرجعِ «ماندهٔ صندوق و بانک». کارت‌های مالی، روزنامچه، جزئیات صندوق، وضعیت مالی شرکت،
/// گزارش گردش پول و API موبایل همه از همین‌جا می‌خوانند و فرمول موازی نمی‌سازند.
/// </summary>
public sealed class CashPositionReader(ApplicationDbContext db) : ICashPositionReader
{
    /// <summary>
    /// سندِ روزنامچه‌ای که واقعاً صندوق/بانکِ شرکت را حرکت داده است. پرداختی که شریک از جیب
    /// خودش داده صندوق شرکت را تکان نداده و سندِ بی‌حساب هم حرکتِ هیچ صندوقی نیست.
    /// </summary>
    public static IQueryable<PaymentTransaction> CashPayments(IQueryable<PaymentTransaction> payments)
        => payments.Where(p => p.CashAccountId != null && p.FundingSource != PaymentFundingSource.Partner);

    /// <summary>
    /// مصرفی که «نقد پرداخت شد» ثبت شده و حرکتِ پولش سندِ روزنامچه ندارد (گمرکِ نقدی و مانند آن).
    /// مصرفی که خودش سندِ روزنامچهٔ پرداخت دارد (کمیسیونِ نقدی) بیرون می‌ماند، چون خروجِ پولش
    /// همان سندِ روزنامچه است و دو بار شمرده نمی‌شود. تشخیص فقط با پیوندِ
    /// <see cref="PaymentTransaction.ExpenseTransactionId"/> است، نه با تطبیقِ مبلغ یا تاریخ.
    /// </summary>
    public static IQueryable<ExpenseTransaction> StandaloneCashExpenses(
        ApplicationDbContext db,
        IQueryable<ExpenseTransaction> expenses)
        => expenses.Where(e => e.CashAccountId != null
            && !e.IsCancelled
            && e.SettlementMode == ExpenseSettlementMode.PaidImmediately
            && !db.PaymentTransactions.Any(p => p.ExpenseTransactionId == e.Id));

    public Task<IReadOnlyList<CashAccountActivityTotals>> ReadAccountTotalsAsync(CancellationToken ct = default)
        => ReadAccountTotalsAsync(cashAccountIds: null, beforeDate: null, ct);

    public async Task<IReadOnlyList<CashAccountActivityTotals>> ReadAccountTotalsAsync(
        IReadOnlyCollection<int>? cashAccountIds,
        DateTime? beforeDate,
        CancellationToken ct = default)
    {
        var payments = CashPayments(db.PaymentTransactions.AsNoTracking());
        var expenses = StandaloneCashExpenses(db, db.ExpenseTransactions.AsNoTracking());
        if (cashAccountIds is not null)
        {
            var ids = cashAccountIds.Distinct().ToArray();
            payments = payments.Where(p => ids.Contains(p.CashAccountId!.Value));
            expenses = expenses.Where(e => ids.Contains(e.CashAccountId!.Value));
        }

        if (beforeDate.HasValue)
        {
            var before = beforeDate.Value.Date;
            payments = payments.Where(p => p.PaymentDate < before);
            expenses = expenses.Where(e => e.ExpenseDate < before);
        }

        // جمع در دیتابیس به تفکیکِ حساب/ارز/جهت؛ فقط ترکیب‌های متمایز خوانده می‌شوند.
        var paymentRows = await payments
            .GroupBy(p => new { CashAccountId = p.CashAccountId!.Value, p.Currency, p.Direction })
            .Select(g => new
            {
                g.Key.CashAccountId,
                g.Key.Currency,
                g.Key.Direction,
                Amount = g.Sum(p => p.Amount),
                AmountUsd = g.Sum(p => p.AmountUsd),
                Count = g.Count(),
                ZeroUsdCount = g.Count(p => p.Amount != 0m && p.AmountUsd == 0m)
            })
            .ToListAsync(ct);

        var expenseRows = await expenses
            .GroupBy(e => new { CashAccountId = e.CashAccountId!.Value, e.Currency })
            .Select(g => new
            {
                g.Key.CashAccountId,
                g.Key.Currency,
                Amount = g.Sum(e => e.Amount),
                AmountUsd = g.Sum(e => e.AmountUsd),
                Count = g.Count(),
                ZeroUsdCount = g.Count(e => e.Amount != 0m && e.AmountUsd == 0m)
            })
            .ToListAsync(ct);

        var accountIds = paymentRows.Select(r => r.CashAccountId)
            .Concat(expenseRows.Select(r => r.CashAccountId))
            .Distinct()
            .ToList();
        if (accountIds.Count == 0)
        {
            return [];
        }

        var accountCurrencies = await db.CashAccounts
            .AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Currency })
            .ToDictionaryAsync(a => a.Id, a => SystemCurrency.Normalize(a.Currency), ct);

        var result = new List<CashAccountActivityTotals>(accountIds.Count);
        foreach (var accountId in accountIds.Order())
        {
            var accountCurrency = accountCurrencies.GetValueOrDefault(accountId, SystemCurrency.BaseCurrencyCode);
            bool SameCurrency(string? currency) =>
                string.Equals(SystemCurrency.Normalize(currency), accountCurrency, StringComparison.OrdinalIgnoreCase);

            var accountPayments = paymentRows.Where(r => r.CashAccountId == accountId).ToList();
            var accountExpenses = expenseRows.Where(r => r.CashAccountId == accountId).ToList();

            var nativeIn = accountPayments
                .Where(r => r.Direction == PaymentDirection.In && SameCurrency(r.Currency))
                .Sum(r => r.Amount);
            var nativeOut = accountPayments
                    .Where(r => r.Direction == PaymentDirection.Out && SameCurrency(r.Currency))
                    .Sum(r => r.Amount)
                + accountExpenses.Where(r => SameCurrency(r.Currency)).Sum(r => r.Amount);
            var usdIn = accountPayments.Where(r => r.Direction == PaymentDirection.In).Sum(r => r.AmountUsd);
            var usdOut = accountPayments.Where(r => r.Direction == PaymentDirection.Out).Sum(r => r.AmountUsd)
                + accountExpenses.Sum(r => r.AmountUsd);
            var foreignCount = accountPayments.Where(r => !SameCurrency(r.Currency)).Sum(r => r.Count)
                + accountExpenses.Where(r => !SameCurrency(r.Currency)).Sum(r => r.Count);
            var missingUsd = accountPayments.Where(r => !SystemCurrency.IsBaseCurrency(r.Currency)).Sum(r => r.ZeroUsdCount)
                + accountExpenses.Where(r => !SystemCurrency.IsBaseCurrency(r.Currency)).Sum(r => r.ZeroUsdCount);

            result.Add(new CashAccountActivityTotals(
                accountId,
                accountCurrency,
                nativeIn,
                nativeOut,
                usdIn,
                usdOut,
                foreignCount,
                missingUsd));
        }

        return result;
    }

    /// <summary>ماندهٔ کلِ صندوق‌ها و بانک‌ها به معادلِ دالریِ ثبت‌شده در هر سند.</summary>
    public static decimal TotalBalanceUsd(IEnumerable<CashAccountActivityTotals> totals)
        => totals.Sum(t => t.BalanceUsd);

    /// <summary>تعداد اسنادِ ارزیِ بی‌معادلِ دالری که در <see cref="TotalBalanceUsd"/> صفر افتاده‌اند.</summary>
    public static int TotalMissingUsdEquivalentCount(IEnumerable<CashAccountActivityTotals> totals)
        => totals.Sum(t => t.MissingUsdEquivalentCount);
}
