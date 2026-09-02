using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Accounting;

namespace PTGOilSystem.Web.Services;

/// <summary>نتیجهٔ ثبت مالی یک کرایه. <c>SkipReason</c> فقط وقتی null است که ردیف لجر ساخته شده باشد.</summary>
public sealed record AssetRentPostingResult(LedgerEntry? Ledger, string? SkipReason)
{
    public bool Posted => SkipReason is null;
}

/// <summary>نتیجهٔ برگشت مالی یک کرایه. برای کرایه‌های بدون اثر مالی، <c>SkipReason</c> پر است و هیچ ردیفی ساخته نمی‌شود.</summary>
public sealed record AssetRentReversalResult(LedgerEntry? Reversal, string? SkipReason)
{
    public bool Reversed => SkipReason is null;
}

public interface IAssetRentPostingService
{
    /// <summary>
    /// ردیف لجر کرایه را می‌سازد، به تراکنش لینک می‌کند و ژورنال حسابداری را (اگر Pilot روشن باشد)
    /// ثبت می‌کند. اگر سیاست بگوید این کرایه اثر مالی ندارد، هیچ‌چیز ساخته نمی‌شود.
    /// </summary>
    Task<AssetRentPostingResult> PostAsync(
        AssetRentTransaction rent,
        CurrencyConversionResult conversion,
        string? assetCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// اثر مالی کرایه را با یک ردیف قرینه برمی‌گرداند و ژورنال را (اگر وجود داشته باشد) reverse
    /// می‌کند. برای کرایه‌ای که اصلاً ردیف مالی ندارد بی‌اثر است، پس صدا زدنش از هر مسیر لغوی امن است.
    /// </summary>
    Task<AssetRentReversalResult> ReverseAsync(
        AssetRentTransaction rent,
        DateTime reversalDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// تنها جایی که ردیفِ لجرِ کرایهٔ دارایی ساخته یا برگردانده می‌شود.
///
/// چرا سرویس و نه کد داخل Controller: کرایه از دو جای مستقل ساخته و لغو می‌شود —
/// <c>OperationalAssetsController</c> (کرایهٔ دستی) و <c>LoadingController</c> (کرایهٔ خودکار
/// بارگیری). تا وقتی منطقِ «ثبت» در یکی و «لغو» در دیگری کپی باشد، یک روز یکی از آن دو عوض می‌شود و
/// کرایهٔ لغوشده با بدهیِ باقی‌مانده در حساب طرف‌حساب می‌ماند. با این سرویس هر دو مسیر یک
/// پیاده‌سازی دارند و مسیر لغوِ بارگیری هم به‌جای «کامنتی که می‌گوید ردیف مالی وجود ندارد» واقعاً
/// دفتر را می‌پرسد.
///
/// دامنهٔ ثبت را <see cref="AssetRentPostingPolicy"/> تعیین می‌کند — همان مرجعی که
/// <see cref="AssetRentAccountingAdapter"/> و Reconciliation هم از آن می‌پرسند.
///
/// این سرویس تراکنش دیتابیس باز نمی‌کند: فراخوان‌ها خودشان کرایه و ردیف را در یک واحد کاری
/// commit می‌کنند، دقیقاً مثل الگوی Expense/Sale.
/// </summary>
public sealed class AssetRentPostingService(
    ApplicationDbContext db,
    IAssetRentAccountingAdapter? rentAccounting = null)
    : IAssetRentPostingService
{
    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(db);

    /// <summary>ردیف اصلی از قبل وجود دارد؛ ثبت دوباره یعنی دوباره‌شماری.</summary>
    public const string SkipAlreadyPosted = "ALREADY_POSTED";

    /// <summary>کرایه هیچ ردیف مالی ندارد که برگردانده شود (کرایهٔ خودکار، استفادهٔ داخلی، شریک).</summary>
    public const string SkipNoFinancialPosting = "NO_FINANCIAL_POSTING";

    /// <summary>ردیف برگشت از قبل ساخته شده؛ لغو دوم ردیف سوم نمی‌سازد.</summary>
    public const string SkipAlreadyReversed = "ALREADY_REVERSED";

    public async Task<AssetRentPostingResult> PostAsync(
        AssetRentTransaction rent,
        CurrencyConversionResult conversion,
        string? assetCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rent);
        ArgumentNullException.ThrowIfNull(conversion);

        var skipReason = AssetRentPostingPolicy.ResolveSkipReason(rent);
        if (skipReason is not null)
        {
            if (rent.UsageType == AssetRentUsageType.InternalCompanyUse && rentAccounting is not null)
            {
                await rentAccounting.TryPostRentAsync(rent, cancellationToken);
            }
            return new AssetRentPostingResult(null, skipReason);
        }

        // گاردِ قطعیِ ضدِ ثبت دوباره: مستقل از IsPostedToLedger، خودِ دفتر پرسیده می‌شود.
        if (await FindOriginalLedgerAsync(rent.Id, cancellationToken) is not null)
        {
            return new AssetRentPostingResult(null, SkipAlreadyPosted);
        }

        var contractCustomerId = await ResolveContractCustomerIdAsync(rent, cancellationToken);
        var ledger = Ledger.Post(
            AssetRentLedgerFactory.BuildRentLedgerEntry(rent, conversion, assetCode, contractCustomerId));
        await db.SaveChangesAsync(cancellationToken);

        rent.LedgerEntryId = ledger.Id;
        rent.IsPostedToLedger = true;
        await db.SaveChangesAsync(cancellationToken);

        if (rentAccounting is not null)
        {
            await rentAccounting.TryPostRentAsync(rent, cancellationToken);
        }

        return new AssetRentPostingResult(ledger, null);
    }

    public async Task<AssetRentReversalResult> ReverseAsync(
        AssetRentTransaction rent,
        DateTime reversalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rent);

        var original = await FindOriginalLedgerAsync(rent.Id, cancellationToken);
        if (original is null)
        {
            // Internal company use intentionally has no legacy party ledger, but it can have
            // a balanced accounting journal (freight cost / internal asset recovery).
            if (rent.UsageType == AssetRentUsageType.InternalCompanyUse && rentAccounting is not null)
            {
                var accountingReversal = await rentAccounting.TryReverseRentAsync(
                    rent,
                    reversalDate,
                    cancellationToken);
                if (accountingReversal.Status == PaymentPostingStatus.Posted)
                {
                    return new AssetRentReversalResult(null, null);
                }

                if (accountingReversal.Status == PaymentPostingStatus.Duplicate)
                {
                    return new AssetRentReversalResult(null, SkipAlreadyReversed);
                }
            }

            // کرایهٔ بدون اثر مالی. اگر پرچم‌ها بگویند ثبت شده ولی دفتر خالی باشد، همان تناقض را
            // Reconciliation گزارش می‌کند؛ اینجا ردیف مصنوعی ساخته نمی‌شود.
            return new AssetRentReversalResult(null, SkipNoFinancialPosting);
        }

        if (await HasReversalLedgerAsync(rent.Id, cancellationToken))
        {
            return new AssetRentReversalResult(null, SkipAlreadyReversed);
        }

        var reversal = Ledger.Post(
            AssetRentLedgerFactory.BuildReversalLedgerEntry(rent, original, reversalDate));
        await db.SaveChangesAsync(cancellationToken);

        if (rentAccounting is not null)
        {
            await rentAccounting.TryReverseRentAsync(rent, reversalDate, cancellationToken);
        }

        return new AssetRentReversalResult(reversal, null);
    }

    /// <summary>ردیف اصلی کرایه: همان (SourceType, SourceId) با جهتِ Credit.</summary>
    private Task<LedgerEntry?> FindOriginalLedgerAsync(int rentId, CancellationToken cancellationToken)
        => db.LedgerEntries
            .Where(l => l.SourceType == AssetRentLedgerFactory.LedgerSourceType
                && l.SourceId == rentId
                && l.Side == LedgerSide.Credit)
            .OrderBy(l => l.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private Task<bool> HasReversalLedgerAsync(int rentId, CancellationToken cancellationToken)
        => db.LedgerEntries
            .AnyAsync(l => l.SourceType == AssetRentLedgerFactory.LedgerSourceType
                && l.SourceId == rentId
                && l.Side == LedgerSide.Debit,
                cancellationToken);

    /// <summary>
    /// مشتریِ قراردادِ فروشِ طرفِ کرایه — از خود قرارداد خوانده می‌شود، نه استنتاج. برای قرارداد
    /// خرید یا قراردادِ بدون مشتری نتیجه null است و ردیف لجر فقط ContractId می‌گیرد.
    /// </summary>
    private async Task<int?> ResolveContractCustomerIdAsync(
        AssetRentTransaction rent,
        CancellationToken cancellationToken)
    {
        if (!rent.ChargedToContractId.HasValue)
        {
            return null;
        }

        return await db.Contracts
            .AsNoTracking()
            .Where(c => c.Id == rent.ChargedToContractId.Value
                && c.ContractType == ContractType.Sale
                && c.CustomerId != null)
            .Select(c => c.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
