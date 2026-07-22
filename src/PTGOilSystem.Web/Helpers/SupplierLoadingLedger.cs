using System;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// سطر دفتر قدیمی (Legacy) بابت بدهی تأمین‌کننده از یک بارگیری قیمت‌دارِ قرارداد خرید.
/// دو حالت پشتیبانی می‌شود:
///  • تسویهٔ روبلیِ قفل‌شده — مبلغ از snapshot قفلِ روبل (AmountUsdAtRubLock/AmountRubAtRubLock).
///  • تسویهٔ دالری — مبلغ از خودِ بارگیری: LoadedQuantityMt × LoadingPriceUsd، نرخ تبادله ۱.
/// هر بارگیری دقیقاً یک سطر دارد: کلید یکتا (SourceType، SourceId).
/// بعد از اصلاح قیمت یا بازقفل نرخ، همان سطر با مبلغ جدید هماهنگ می‌شود و سطر دوم ساخته نمی‌شود.
/// دفتر کل جدید از این مسیر عبور نمی‌کند و همچنان با Reversal + Revision کار می‌کند؛
/// این کلاس فقط سطر Legacy را از snapshot خود بارگیری بازتولید می‌کند.
/// </summary>
public static class SupplierLoadingLedger
{
    public const string SourceType = "Loading";
    private const string UsdRateSource = "Loading USD settlement";

    /// <summary>
    /// بارگیریِ یک قرارداد خرید وقتی سطر دارد که مبلغش قطعی باشد: یا روبلیِ قفل‌شده، یا دالریِ قیمت‌دار.
    /// </summary>
    public static bool IsPostable(LoadingRegister loading, Contract? contract)
    {
        ArgumentNullException.ThrowIfNull(loading);

        if (contract is null
            || contract.ContractType != ContractType.Purchase
            || !contract.SupplierId.HasValue)
        {
            return false;
        }

        return IsRubPostable(loading) || IsUsdPostable(loading);
    }

    private static bool IsRubPostable(LoadingRegister loading)
        => LoadingRubSettlement.IsRubSettlement(loading.SettlementCurrencyCode)
            && HasRubLockSnapshot(loading);

    private static bool IsUsdPostable(LoadingRegister loading)
        => !LoadingRubSettlement.IsRubSettlement(loading.SettlementCurrencyCode)
            && CalculateUsdAmount(loading) is > 0m;

    // مبلغِ سطر دالری همان حسابِ ارزشِ بارگیری در بقیهٔ سیستم است (مقدار × قیمت واحد، گرد شده به ۴ رقم).
    private static decimal? CalculateUsdAmount(LoadingRegister loading)
        => LoadingRubSettlement.CalculateLoadingValueUsd(loading.LoadedQuantityMt, loading.LoadingPriceUsd);

    // وجودِ snapshot قفلِ روبل — مستقل از کدِ ارزِ تسویه، تا هماهنگ‌سازیِ سطرهای روبلیِ قدیمی دست‌نخورده بماند.
    private static bool HasRubLockSnapshot(LoadingRegister loading)
        => loading.RubRateStatus == RubSettlementRateStatus.Locked
            && loading.AmountUsdAtRubLock is > 0m
            && loading.AmountRubAtRubLock is > 0m
            && loading.RubPerUsdRate is > 0m;

    public static string BuildReference(LoadingRegister loading)
    {
        ArgumentNullException.ThrowIfNull(loading);

        var reference = string.IsNullOrWhiteSpace(loading.BillOfLadingNumber)
            ? $"LOAD-{loading.Id}"
            : loading.BillOfLadingNumber.Trim();
        return reference.Length > 200 ? reference[..200] : reference;
    }

    public static LedgerEntry Create(LoadingRegister loading, Contract contract)
    {
        ArgumentNullException.ThrowIfNull(loading);
        ArgumentNullException.ThrowIfNull(contract);

        var entry = new LedgerEntry
        {
            Side = LedgerSide.Credit,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceCurrencyCode = HasRubLockSnapshot(loading) ? "RUB" : SystemCurrency.BaseCurrencyCode,
            Description = $"بدهی تأمین‌کننده بابت بارگیری #{loading.Id}",
            SourceType = SourceType,
            SourceId = loading.Id,
            Reference = BuildReference(loading),
            ContractId = contract.Id,
            SupplierId = contract.SupplierId!.Value
        };

        ApplySnapshot(entry, loading);
        return entry;
    }

    /// <summary>
    /// مبلغ و نرخِ سطر موجود را با snapshot فعلی بارگیری هماهنگ می‌کند.
    /// فقط فیلدهایی که با اصلاح قیمت کهنه می‌شوند نوشته می‌شوند؛ هویت سطر (SourceType/SourceId/طرف حساب)
    /// دست‌نخورده می‌ماند. اگر چیزی عوض نشده باشد false برمی‌گرداند تا فراخوان لاگ بی‌مورد ثبت نکند.
    /// </summary>
    public static bool ApplySnapshot(LedgerEntry entry, LoadingRegister loading)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(loading);

        // انتخابِ مسیر با وجودِ snapshot قفلِ روبل تعیین می‌شود، نه با کدِ ارز: سطرهای روبلیِ
        // قفل‌شده دقیقاً مثل قبل هماهنگ می‌شوند و بارگیری دالری از حسابِ مقدار × قیمت می‌آید.
        return HasRubLockSnapshot(loading)
            ? ApplyRubSnapshot(entry, loading)
            : ApplyUsdSnapshot(entry, loading);
    }

    private static bool ApplyRubSnapshot(LedgerEntry entry, LoadingRegister loading)
    {
        var entryDate = loading.LoadingDate.Date;
        var amountUsd = loading.AmountUsdAtRubLock!.Value;
        var amountRub = loading.AmountRubAtRubLock!.Value;
        var fxRateToUsd = decimal.Round(1m / loading.RubPerUsdRate!.Value, 6, MidpointRounding.AwayFromZero);
        var fxRateDate = loading.RubRateDate?.Date ?? loading.LoadingDate.Date;
        var fxRateSource = loading.RubRateSource ?? "Loading RUB settlement";

        var changed = entry.EntryDate != entryDate
            || entry.AmountUsd != amountUsd
            || entry.SourceAmount != amountRub
            || entry.AppliedFxRateToUsd != fxRateToUsd
            || entry.AppliedFxRateDate != fxRateDate
            || entry.AppliedFxRateSource != fxRateSource;

        entry.EntryDate = entryDate;
        entry.AmountUsd = amountUsd;
        entry.SourceAmount = amountRub;
        entry.AppliedFxRateToUsd = fxRateToUsd;
        entry.AppliedFxRateDate = fxRateDate;
        entry.AppliedFxRateSource = fxRateSource;

        return changed;
    }

    /// <summary>
    /// سطر دالری: مبلغ دفتر و مبلغ منبع هر دو USD و نرخ تبادله ۱ است؛
    /// با ویرایش مقدار یا قیمتِ بارگیری همین سطر به‌روزرسانی می‌شود.
    /// اگر قیمت برداشته شود مبلغ کهنه دست‌نخورده می‌ماند (پاک‌سازی کارِ فراخوان است).
    /// </summary>
    private static bool ApplyUsdSnapshot(LedgerEntry entry, LoadingRegister loading)
    {
        var amountUsd = CalculateUsdAmount(loading);
        if (amountUsd is not > 0m)
        {
            return false;
        }

        var entryDate = loading.LoadingDate.Date;
        var fxRateDate = loading.LoadingDate.Date;

        var changed = entry.EntryDate != entryDate
            || entry.AmountUsd != amountUsd.Value
            || entry.SourceAmount != amountUsd.Value
            || entry.SourceCurrencyCode != SystemCurrency.BaseCurrencyCode
            || entry.AppliedFxRateToUsd != 1m
            || entry.AppliedFxRateDate != fxRateDate
            || entry.AppliedFxRateSource != UsdRateSource;

        entry.EntryDate = entryDate;
        entry.AmountUsd = amountUsd.Value;
        entry.SourceAmount = amountUsd.Value;
        entry.SourceCurrencyCode = SystemCurrency.BaseCurrencyCode;
        entry.AppliedFxRateToUsd = 1m;
        entry.AppliedFxRateDate = fxRateDate;
        entry.AppliedFxRateSource = UsdRateSource;

        return changed;
    }
}
