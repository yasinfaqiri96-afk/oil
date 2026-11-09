using System;
using System.Collections.Generic;
using System.Linq;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Ledger;

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

        // قرارداد چندتأمین‌کننده و بارگیری بدون تأمین‌کننده: فقط وقتی سهم هر تأمین‌کننده قطعی است.
        if (contract is null
            || contract.ContractType != ContractType.Purchase
            || ContractSupplierScope.PayableShares(loading, contract) is null)
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

    /// <summary>
    /// PTG-P1-03 — همان سطرِ قبلی، این‌بار به‌شکلِ «درخواستِ ثبت» تا از مسیر متمرکز
    /// (<c>ILedgerPostingService</c>) نوشته شود. مقادیر دقیقاً از همان snapshotی می‌آیند که
    /// <see cref="ApplySnapshot"/> روی سطرهای موجود اعمال می‌کند، پس خروجی فیلد-به-فیلد
    /// همان چیزی است که پیش از تمرکز نوشته می‌شد.
    /// </summary>
    public static LedgerPostingRequest Create(LoadingRegister loading, Contract contract)
    {
        var requests = CreateAll(loading, contract);
        if (requests.Count != 1)
        {
            throw new InvalidOperationException(
                $"Loading #{loading.Id} is split between {requests.Count} suppliers; use CreateAll.");
        }

        return requests[0];
    }

    /// <summary>
    /// سطرهای طلب این بارگیری: یک سطر برای بارگیری/قرارداد تک‌تأمین‌کننده (دقیقاً مثل قبل)، یا یک
    /// سطر برای هر تأمین‌کنندهٔ قرارداد چندتأمین‌کننده وقتی بارگیری تأمین‌کننده ندارد. جمع مبلغ سطرها
    /// همیشه برابر ارزش بارگیری است؛ بدهی دوباره ساخته نمی‌شود.
    /// </summary>
    public static IReadOnlyList<LedgerPostingRequest> CreateAll(LoadingRegister loading, Contract contract)
    {
        ArgumentNullException.ThrowIfNull(loading);
        ArgumentNullException.ThrowIfNull(contract);

        var shares = ContractSupplierScope.PayableShares(loading, contract)
            ?? throw new InvalidOperationException($"Loading #{loading.Id} has no payable supplier.");
        var snapshot = BuildSnapshot(loading);
        var amountParts = ContractSupplierScope.SplitAmount(snapshot?.AmountUsd ?? 0m, shares);
        var sourceParts = snapshot is null ? null : ContractSupplierScope.SplitAmount(snapshot.SourceAmount, shares);

        return amountParts
            .Select((part, index) => Build(
                loading,
                contract,
                snapshot,
                part.SupplierId,
                part.Amount,
                sourceParts?[index].Amount,
                shares.Count > 1))
            .ToList();
    }

    private static LedgerPostingRequest Build(
        LoadingRegister loading,
        Contract contract,
        LoadingLedgerSnapshot? snapshot,
        int supplierId,
        decimal amountUsd,
        decimal? sourceAmount,
        bool isShare)
    {
        return new LedgerPostingRequest
        {
            Side = LedgerSide.Credit,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceCurrencyCode = snapshot?.SourceCurrencyCode
                ?? (HasRubLockSnapshot(loading) ? "RUB" : SystemCurrency.BaseCurrencyCode),
            Description = isShare
                ? $"سهم تأمین‌کننده از بارگیری #{loading.Id}"
                : $"بدهی تأمین‌کننده بابت بارگیری #{loading.Id}",
            SourceType = SourceType,
            SourceId = loading.Id,
            Reference = BuildReference(loading),
            ContractId = contract.Id,
            // تأمین‌کنندهٔ بارگیری، تأمین‌کنندهٔ قرارداد، یا سهم‌دار قرارداد چندتأمین‌کننده.
            SupplierId = supplierId,

            // وقتی snapshot وجود ندارد (بارگیری دالریِ بی‌قیمت) مقادیر دقیقاً همان
            // پیش‌فرض‌هایی می‌مانند که ApplySnapshot هم دست نمی‌زد.
            EntryDate = snapshot?.EntryDate ?? default,
            AmountUsd = amountUsd,
            SourceAmount = sourceAmount,
            AppliedFxRateToUsd = snapshot?.AppliedFxRateToUsd,
            AppliedFxRateDate = snapshot?.AppliedFxRateDate,
            AppliedFxRateSource = snapshot?.AppliedFxRateSource,
        };
    }

    /// <summary>
    /// مقادیرِ وابسته به snapshot بارگیری. <c>null</c> یعنی «چیزی برای نوشتن نیست» —
    /// همان حالتی که <see cref="ApplyUsdSnapshot"/> با <c>false</c> اعلام می‌کرد.
    ///
    /// نکتهٔ مهم: مسیر روبل عمداً <c>SourceCurrencyCode</c> را برنمی‌گرداند، چون
    /// هماهنگ‌سازیِ سطرهای روبلیِ موجود هرگز آن ستون را نمی‌نوشت.
    /// </summary>
    private sealed record LoadingLedgerSnapshot(
        DateTime EntryDate,
        decimal AmountUsd,
        decimal SourceAmount,
        string? SourceCurrencyCode,
        decimal AppliedFxRateToUsd,
        DateTime AppliedFxRateDate,
        string AppliedFxRateSource);

    private static LoadingLedgerSnapshot? BuildSnapshot(LoadingRegister loading)
    {
        // انتخابِ مسیر با وجودِ snapshot قفلِ روبل تعیین می‌شود، نه با کدِ ارز: سطرهای روبلیِ
        // قفل‌شده دقیقاً مثل قبل هماهنگ می‌شوند و بارگیری دالری از حسابِ مقدار × قیمت می‌آید.
        if (HasRubLockSnapshot(loading))
        {
            return new LoadingLedgerSnapshot(
                loading.LoadingDate.Date,
                loading.AmountUsdAtRubLock!.Value,
                loading.AmountRubAtRubLock!.Value,
                SourceCurrencyCode: null,
                decimal.Round(1m / loading.RubPerUsdRate!.Value, 6, MidpointRounding.AwayFromZero),
                loading.RubRateDate?.Date ?? loading.LoadingDate.Date,
                loading.RubRateSource ?? "Loading RUB settlement");
        }

        var amountUsd = CalculateUsdAmount(loading);
        if (amountUsd is not > 0m)
        {
            return null;
        }

        return new LoadingLedgerSnapshot(
            loading.LoadingDate.Date,
            amountUsd.Value,
            amountUsd.Value,
            SystemCurrency.BaseCurrencyCode,
            1m,
            loading.LoadingDate.Date,
            UsdRateSource);
    }

    /// <summary>
    /// مبلغ و نرخِ سطر موجود را با snapshot فعلی بارگیری هماهنگ می‌کند.
    /// فقط فیلدهایی که با اصلاح قیمت کهنه می‌شوند نوشته می‌شوند؛ هویت سطر (SourceType/SourceId/طرف حساب)
    /// دست‌نخورده می‌ماند. اگر چیزی عوض نشده باشد false برمی‌گرداند تا فراخوان لاگ بی‌مورد ثبت نکند.
    /// </summary>
    public static bool ApplySnapshot(LedgerEntry entry, LoadingRegister loading)
        => ApplySnapshot(entry, loading, shares: null);

    /// <summary>
    /// همان <see cref="ApplySnapshot(LedgerEntry, LoadingRegister)"/> برای سطر سهمیِ قرارداد
    /// چندتأمین‌کننده: مبلغ سطر سهم همان تأمین‌کننده (<see cref="LedgerEntry.SupplierId"/>) است.
    /// </summary>
    public static bool ApplySnapshot(LedgerEntry entry, LoadingRegister loading, Contract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var shares = ContractSupplierScope.PayableShares(loading, contract);
        return shares is not null && ApplySnapshot(entry, loading, shares.Count > 1 ? shares : null);
    }

    private static bool ApplySnapshot(
        LedgerEntry entry,
        LoadingRegister loading,
        IReadOnlyList<(int SupplierId, decimal Fraction)>? shares)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(loading);

        // سطر دالریِ بی‌قیمت: مبلغ کهنه دست‌نخورده می‌ماند (پاک‌سازی کارِ فراخوان است).
        var snapshot = BuildSnapshot(loading);
        if (snapshot is null)
        {
            return false;
        }

        if (shares is not null)
        {
            if (entry.SupplierId is not { } supplierId
                || ContractSupplierScope.ShareOf(snapshot.AmountUsd, shares, supplierId) is not { } shareUsd
                || ContractSupplierScope.ShareOf(snapshot.SourceAmount, shares, supplierId) is not { } shareSource)
            {
                return false;
            }

            snapshot = snapshot with { AmountUsd = shareUsd, SourceAmount = shareSource };
        }

        var changed = entry.EntryDate != snapshot.EntryDate
            || entry.AmountUsd != snapshot.AmountUsd
            || entry.SourceAmount != snapshot.SourceAmount
            || entry.AppliedFxRateToUsd != snapshot.AppliedFxRateToUsd
            || entry.AppliedFxRateDate != snapshot.AppliedFxRateDate
            || entry.AppliedFxRateSource != snapshot.AppliedFxRateSource
            || (snapshot.SourceCurrencyCode is not null
                && entry.SourceCurrencyCode != snapshot.SourceCurrencyCode);

        entry.EntryDate = snapshot.EntryDate;
        entry.AmountUsd = snapshot.AmountUsd;
        entry.SourceAmount = snapshot.SourceAmount;
        entry.AppliedFxRateToUsd = snapshot.AppliedFxRateToUsd;
        entry.AppliedFxRateDate = snapshot.AppliedFxRateDate;
        entry.AppliedFxRateSource = snapshot.AppliedFxRateSource;

        // مسیر روبل این ستون را هرگز نمی‌نوشت؛ همان رفتار حفظ می‌شود.
        if (snapshot.SourceCurrencyCode is not null)
        {
            entry.SourceCurrencyCode = snapshot.SourceCurrencyCode;
        }

        return changed;
    }
}
