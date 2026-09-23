using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// فیلترِ لیست بارگیری — تنها تعریفِ «کدام بارگیری‌ها در این نما دیده می‌شوند».
///
/// صفحهٔ لیست و «تبدیل گروهیِ همهٔ نتایج فیلتر» هر دو از همین یک تابع استفاده می‌کنند؛
/// بدون این، انتخابِ گروهی می‌توانست مجموعه‌ای متفاوت از چیزی که کاربر روی صفحه می‌بیند
/// تبدیل کند.
/// </summary>
public sealed record LoadingListFilterCriteria
{
    public string? Query { get; init; }
    public int[]? ContractIds { get; init; }
    public int[]? ProductIds { get; init; }

    /// <summary>پارامتر قدیمیِ صفحه؛ معادل <c>ReceiptStatus = "without"</c>.</summary>
    public bool WithoutReceipt { get; init; }

    public int[]? TransportTypes { get; init; }
    public string? ReceiptStatus { get; init; }
    public string? PriceStatus { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }

    /// <summary>true یعنی هیچ شرطی فعال نیست (کل جدول).</summary>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(Query)
           && (ContractIds is null || ContractIds.Length == 0)
           && (ProductIds is null || ProductIds.Length == 0)
           && !WithoutReceipt
           && (TransportTypes is null || TransportTypes.Length == 0)
           && string.IsNullOrWhiteSpace(ReceiptStatus)
           && string.IsNullOrWhiteSpace(PriceStatus)
           && !FromDate.HasValue
           && !ToDate.HasValue;
}

public static class LoadingListFilter
{
    /// <summary>
    /// شرط‌ها را روی کوئری اعمال می‌کند. چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین
    /// فیلترهای مختلف — دقیقاً همان رفتاری که صفحهٔ لیست از ابتدا داشته است.
    /// </summary>
    public static IQueryable<LoadingRegister> Apply(
        IQueryable<LoadingRegister> query,
        LoadingListFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var contractIds = criteria.ContractIds;
        if (contractIds is { Length: > 0 })
        {
            query = query.Where(l => contractIds.Contains(l.ContractId));
        }

        var productIds = criteria.ProductIds;
        if (productIds is { Length: > 0 })
        {
            query = query.Where(l => productIds.Contains(l.ProductId));
        }

        var normalizedReceiptStatus = NormalizeReceiptStatus(criteria);
        if (normalizedReceiptStatus == "without")
        {
            query = query.Where(l => !l.Receipts.Any(r => !r.IsCancelled));
        }
        else if (normalizedReceiptStatus == "with")
        {
            query = query.Where(l => l.Receipts.Any(r => !r.IsCancelled));
        }

        var transportTypeValues = (criteria.TransportTypes ?? [])
            .Where(value => Enum.IsDefined(typeof(LoadingTransportType), value))
            .Select(value => (LoadingTransportType)value)
            .ToArray();
        if (transportTypeValues.Length > 0)
        {
            query = query.Where(l => transportTypeValues.Contains(l.TransportType));
        }

        var normalizedPriceStatus = criteria.PriceStatus?.Trim().ToLowerInvariant();
        if (normalizedPriceStatus == "pending")
        {
            query = query.Where(l => l.LoadingPriceUsd == null || l.LoadingPriceUsd <= 0m);
        }
        else if (normalizedPriceStatus == "priced")
        {
            query = query.Where(l => l.LoadingPriceUsd != null && l.LoadingPriceUsd > 0m);
        }

        if (criteria.FromDate.HasValue)
        {
            var fromDate = criteria.FromDate.Value.Date;
            query = query.Where(l => l.LoadingDate >= fromDate);
        }

        if (criteria.ToDate.HasValue)
        {
            var exclusiveToDate = criteria.ToDate.Value.Date.AddDays(1);
            query = query.Where(l => l.LoadingDate < exclusiveToDate);
        }

        var normalizedQuery = NormalizeQuery(criteria.Query);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            // PTG canonical search — کلیدِ canonical به شرطِ قبلی اضافه می‌شود، جایگزینِ آن نمی‌شود:
            // هیچ نتیجه‌ای از دست نمی‌رود و «یوسف» سطرِ «يوسف» را هم پیدا می‌کند.
            // SearchKey خالی یعنی سطرِ پیش از Backfill؛ همان شرطِ قبلی هنوز آن را می‌یابد.
            var canonicalTerm = AfghanTextNormalizer.NormalizeForSearch(normalizedQuery);
            query = query.Where(l =>
                (l.SearchKey != null && l.SearchKey.Contains(canonicalTerm)) ||
                (l.Contract != null && l.Contract.SearchKey != null && l.Contract.SearchKey.Contains(canonicalTerm)) ||
                (l.Contract != null && (l.Contract.ContractName.Contains(normalizedQuery) || l.Contract.ContractNumber.Contains(normalizedQuery))) ||
                (l.Product != null && l.Product.Name.Contains(normalizedQuery)) ||
                (l.WagonNumber != null && l.WagonNumber.Contains(normalizedQuery)) ||
                (l.BillOfLadingNumber != null && l.BillOfLadingNumber.Contains(normalizedQuery)) ||
                (l.DestinationName != null && l.DestinationName.Contains(normalizedQuery))
            );
        }

        return query;
    }

    public static string? NormalizeQuery(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? NormalizeReceiptStatus(LoadingListFilterCriteria criteria)
        => criteria.WithoutReceipt ? "without" : criteria.ReceiptStatus?.Trim().ToLowerInvariant();
}
