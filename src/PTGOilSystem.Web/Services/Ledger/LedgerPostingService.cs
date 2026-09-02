using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.CompanyFlow;

namespace PTGOilSystem.Web.Services.Ledger;

/// <summary>
/// PTG-P1-03 — تنها جایی که یک <see cref="LedgerEntry"/> ساخته می‌شود.
///
/// <b>این سرویس هیچ قاعدهٔ کسب‌وکاری اجرا نمی‌کند.</b> جهت، مبلغ، نرخ و تاریخ را همان کدی
/// تعیین می‌کند که قبلاً تعیین می‌کرد؛ اینجا فقط همان مقادیر در یک شکلِ واحد نوشته، و
/// پیش از نوشتن اعتبارسنجی می‌شوند. به‌همین دلیل خروجی فیلد-به-فیلد با کدِ قبلی یکسان است
/// و <c>LedgerPostingEquivalenceTests</c> همین را برای هر مسیر pin می‌کند.
/// </summary>
public interface ILedgerPostingService
{
    /// <summary>
    /// سطر تازه می‌سازد، به <c>ChangeTracker</c> اضافه می‌کند و برمی‌گرداند.
    /// <b>ذخیره نمی‌کند</b> — زمان‌بندیِ <c>SaveChanges</c> عمداً دستِ فراخوان می‌ماند تا
    /// مرزِ تراکنشِ هیچ مسیرِ موجودی عوض نشود.
    /// </summary>
    LedgerEntry Post(LedgerPostingRequest request);

    /// <summary>
    /// چند سطرِ یک عملیات، به همان ترتیب. جانشینِ مستقیمِ
    /// <c>LedgerEntries.AddRange(...)</c> در مسیرهایی که یک رویداد بیش از یک سطر می‌سازد.
    /// </summary>
    IReadOnlyList<LedgerEntry> PostRange(params LedgerPostingRequest[] requests);

    /// <summary>
    /// همان قواعد، ولی روی سطرِ موجود. مسیرهای «ویرایش سند» عمداً سطر دفتر را جای
    /// حذف‌و‌ساخت، به‌روز می‌کنند تا شناسهٔ سطر و پیوندهایش نشکند.
    /// </summary>
    LedgerEntry Apply(LedgerEntry target, LedgerPostingRequest request);

    /// <summary>
    /// برگشتِ audit-preserving: سطر اصلی دست‌نخورده می‌ماند و یک سطرِ جبرانیِ هم‌مبلغ با
    /// جهت معکوس کنارش ثبت می‌شود. اگر برگشت از قبل وجود داشته باشد <c>null</c> برمی‌گرداند
    /// — همان محافظِ ضدِ «دو بار برگشت‌زدن».
    /// </summary>
    Task<LedgerEntry?> ReverseAsync(
        LedgerEntry original,
        DateTime reversalDate,
        string description,
        string fallbackReference,
        CancellationToken cancellationToken = default);
}

/// <summary>خطای «این سطر دفتر کل اصلاً نباید نوشته می‌شد».</summary>
public sealed class LedgerPostingValidationException(string message) : InvalidOperationException(message);

public sealed class LedgerPostingService(ApplicationDbContext db) : ILedgerPostingService
{
    public LedgerEntry Post(LedgerPostingRequest request)
    {
        var entry = new LedgerEntry();
        Apply(entry, request);
        db.LedgerEntries.Add(entry);
        return entry;
    }

    public IReadOnlyList<LedgerEntry> PostRange(params LedgerPostingRequest[] requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var posted = new LedgerEntry[requests.Length];
        for (var i = 0; i < requests.Length; i++)
        {
            posted[i] = Post(requests[i]);
        }

        return posted;
    }

    public LedgerEntry Apply(LedgerEntry target, LedgerPostingRequest request)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);

        target.EntryDate = request.EntryDate;
        target.Side = request.Side;
        target.AmountUsd = request.AmountUsd;
        target.Currency = request.Currency;
        target.SourceAmount = request.SourceAmount;
        target.SourceCurrencyCode = request.SourceCurrencyCode;
        target.AppliedFxRateToUsd = request.AppliedFxRateToUsd;
        target.AppliedCurrencyPerUsdRate = request.AppliedCurrencyPerUsdRate;
        target.AppliedFxRateDate = request.AppliedFxRateDate;
        target.AppliedFxRateSource = request.AppliedFxRateSource;
        target.Description = request.Description;
        target.SourceType = request.SourceType;
        target.SourceId = request.SourceId;
        target.Reference = request.Reference;
        target.ViaSarrafGroupId = request.ViaSarrafGroupId;
        target.ContractId = request.ContractId;
        target.CustomerId = request.CustomerId;
        target.SupplierId = request.SupplierId;
        target.ServiceProviderId = request.ServiceProviderId;
        target.DriverId = request.DriverId;
        target.EmployeeId = request.EmployeeId;
        target.PartnerId = request.PartnerId;
        target.ShipmentId = request.ShipmentId;

        return target;
    }

    public async Task<LedgerEntry?> ReverseAsync(
        LedgerEntry original,
        DateTime reversalDate,
        string description,
        string fallbackReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);

        var reversalReference =
            (original.Reference ?? fallbackReference) + CompanyFlowSourceTypes.ReversalReferenceSuffix;

        // محافظِ «دو بار برگشت». همان قاعدهٔ LedgerReversalWriter، بدون تغییر.
        var alreadyReversed = await db.LedgerEntries
            .AsNoTracking()
            .AnyAsync(
                l => l.SourceType == original.SourceType
                    && l.SourceId == original.SourceId
                    && l.Reference == reversalReference,
                cancellationToken);
        if (alreadyReversed)
        {
            return null;
        }

        var reversal = Post(new LedgerPostingRequest
        {
            SourceType = original.SourceType,
            SourceId = original.SourceId,
            EntryDate = reversalDate.Date,
            Side = original.Side == LedgerSide.Debit ? LedgerSide.Credit : LedgerSide.Debit,
            AmountUsd = original.AmountUsd,
            Currency = original.Currency,
            SourceAmount = original.SourceAmount,
            SourceCurrencyCode = original.SourceCurrencyCode,
            AppliedFxRateToUsd = original.AppliedFxRateToUsd,
            AppliedCurrencyPerUsdRate = original.AppliedCurrencyPerUsdRate,
            AppliedFxRateDate = original.AppliedFxRateDate,
            AppliedFxRateSource = original.AppliedFxRateSource,
            Description = description,
            Reference = reversalReference,
            ViaSarrafGroupId = original.ViaSarrafGroupId,
            ContractId = original.ContractId,
            CustomerId = original.CustomerId,
            SupplierId = original.SupplierId,
            ServiceProviderId = original.ServiceProviderId,
            DriverId = original.DriverId,
            EmployeeId = original.EmployeeId,
            PartnerId = original.PartnerId,
            ShipmentId = original.ShipmentId,
        });

        await db.SaveChangesAsync(cancellationToken);
        return reversal;
    }

    /// <summary>
    /// اعتبارسنجیِ «سطر دفتر کل بدونِ ردیابی» — دقیقاً همان چیزی که P1-02 اسکنرش را ساخت.
    /// بهتر است نوشتن رد شود تا اینکه هفتهٔ بعد اسکنر یک سطرِ یتیم گزارش کند.
    /// </summary>
    private static void Validate(LedgerPostingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceType))
        {
            throw new LedgerPostingValidationException(
                "سطر دفتر کل بدون SourceType نوشته نمی‌شود.");
        }

        if (request.SourceId <= 0 && !request.AllowDeferredSourceId)
        {
            throw new LedgerPostingValidationException(
                $"سطر دفتر کل '{request.SourceType}' بدون SourceId نوشته نمی‌شود.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            throw new LedgerPostingValidationException(
                $"سطر دفتر کل '{request.SourceType}' بدون ارز نوشته نمی‌شود.");
        }

        if (request.Side is not (LedgerSide.Debit or LedgerSide.Credit))
        {
            throw new LedgerPostingValidationException(
                $"جهت سطر دفتر کل '{request.SourceType}' معتبر نیست.");
        }
    }
}
