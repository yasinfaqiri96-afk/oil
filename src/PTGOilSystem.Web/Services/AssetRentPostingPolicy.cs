using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// تنها مرجعِ «آیا این کرایه باید اثر مالی بگیرد یا نه».
///
/// Controller (ثبت)، Adapter (ژورنال) و Reconciliation (کنترل) هر سه از همین کلاس می‌پرسند، پس
/// نمی‌توانند با هم اختلاف پیدا کنند: چیزی که ثبت نمی‌شود هرگز به‌عنوان «ثبت‌نشده» گزارش نمی‌شود.
///
/// تصمیمِ فاز یک:
///   • فقط کرایهٔ دستیِ OperationalAssetsController.CreateRent اثر مالی می‌گیرد.
///   • کرایه‌هایی که LoadingController خودکار می‌سازد بیرون از این فاز می‌مانند، چون معادلِ
///     Expense/Freight آن‌ها از سمتِ بارگیری/رسید قبلاً ثبت شده و ثبت دوباره یعنی دوباره‌شماری.
///     این کرایه‌ها با لینکِ عملیاتیِ خودشان شناخته می‌شوند و به ستون جدید نیازی نیست.
///   • استفادهٔ داخلی شرکت درآمد خارجی نیست و هیچ حسابی را بدهکار نمی‌کند.
///   • کرایهٔ شریک فعلاً پشتیبانی نمی‌شود، چون LedgerEntry ستون PartnerId ندارد و ساختنش
///     Migration می‌خواهد.
/// </summary>
public static class AssetRentPostingPolicy
{
    public const string SkipCancelled = "RENT_CANCELLED";
    public const string SkipSystemGenerated = "SYSTEM_GENERATED_RENT_NOT_POSTED";
    public const string SkipInternalUse = "INTERNAL_USE_NO_EXTERNAL_REVENUE";
    public const string SkipPartnerUnsupported = "PARTNER_LEDGER_UNSUPPORTED";
    public const string SkipCounterpartyUnresolved = "RENT_COUNTERPARTY_UNRESOLVED";
    public const string SkipInvalidAmount = "INVALID_RENT_AMOUNT";

    /// <summary>
    /// کرایه‌ای که کاربر ثبت نکرده و یکی از جریان‌های عملیاتی ساخته است. هر چهار لینک از قبل روی
    /// موجودیت وجود دارند، پس تشخیص بدون تغییر ساختار دیتابیس انجام می‌شود.
    /// </summary>
    public static bool IsSystemGenerated(AssetRentTransaction rent)
        => rent.LoadingRegisterId.HasValue
            || rent.TransportLegId.HasValue
            || rent.InventoryTransportReceiptId.HasValue
            || rent.TruckDispatchId.HasValue;

    /// <summary>کرایهٔ دستی: هیچ لینک عملیاتی ندارد.</summary>
    public static bool IsManual(AssetRentTransaction rent) => !IsSystemGenerated(rent);

    /// <summary>
    /// دلیلِ عدم ثبت، یا <c>null</c> اگر کرایه باید اثر مالی بگیرد. دلیل‌ها رشته‌های ثابت‌اند تا هم
    /// در log و هم در تست قابل ادعا باشند.
    /// </summary>
    public static string? ResolveSkipReason(AssetRentTransaction rent)
    {
        ArgumentNullException.ThrowIfNull(rent);

        if (rent.IsCancelled)
            return SkipCancelled;
        if (IsSystemGenerated(rent))
            return SkipSystemGenerated;
        if (rent.AmountUsd <= 0m || rent.AmountOriginal <= 0m)
            return SkipInvalidAmount;
        if (rent.UsageType == AssetRentUsageType.InternalCompanyUse
            || rent.ChargedToType == AssetRentChargedToType.CompanyInternal)
            return SkipInternalUse;
        if (rent.UsageType == AssetRentUsageType.PartnerUse
            || rent.ChargedToType == AssetRentChargedToType.Partner)
            return SkipPartnerUnsupported;

        return rent.ChargedToType switch
        {
            AssetRentChargedToType.Customer =>
                rent.ChargedToCustomerId.HasValue ? null : SkipCounterpartyUnresolved,
            AssetRentChargedToType.SalesContract or AssetRentChargedToType.PurchaseContract =>
                rent.ChargedToContractId.HasValue ? null : SkipCounterpartyUnresolved,
            AssetRentChargedToType.Other =>
                rent.ChargedToServiceProviderId.HasValue || rent.ChargedToCustomerId.HasValue
                    ? null
                    : SkipCounterpartyUnresolved,
            _ => SkipCounterpartyUnresolved
        };
    }

    /// <summary>کرایه‌ای که طبق سیاست این فاز باید ردیف لجر داشته باشد.</summary>
    public static bool ShouldPostToLedger(AssetRentTransaction rent)
        => ResolveSkipReason(rent) is null;
}
