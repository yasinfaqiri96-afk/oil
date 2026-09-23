namespace PTGOilSystem.Web.Models.MasterData;

public sealed class TransportResourceProfileViewModel
{
    public const int PageSize = 10;

    public int? SelectedId { get; init; }
    public string ActiveTab { get; init; } = "info";

    // سوابق و اسناد فقط برای صفحهٔ جاری از DB خوانده می‌شوند؛ شمار کل جدا با CountAsync.
    public IReadOnlyList<TransportResourceTripItem> Trips { get; init; } = [];
    public int TripTotalCount { get; init; }
    public int TripPage { get; init; } = 1;
    public int TripPageCount { get; init; } = 1;

    public IReadOnlyList<TransportResourceDocumentItem> Documents { get; init; } = [];
    public int DocumentTotalCount { get; init; }
    public int DocumentPage { get; init; } = 1;
    public int DocumentPageCount { get; init; } = 1;
    public int ExpiredDocumentCount { get; init; }

    /// <summary>دارایی‌های عملیاتیِ وصل‌شده (موتر: LinkedTruckId، راننده: تخصیص فعال).</summary>
    public IReadOnlyList<TransportResourceLinkItem> LinkedAssets { get; init; } = [];

    // شاخص‌ها بر پایهٔ «عملیات فیزیکی» (ثبت‌های مرتبط با FK یکی شمرده می‌شوند) و بدون ثبت‌های لغوشده.
    public int OperationCount { get; init; }
    public int InProgressOperationCount { get; init; }
    public decimal OperationQuantityMt { get; init; }
    public DateTime? LastOperationDate { get; init; }
    public int CancelledRecordCount { get; init; }

    public static TransportResourceProfileViewModel Empty(string? activeTab = null)
        => new() { ActiveTab = NormalizeTab(activeTab) };

    public static string NormalizeTab(string? tab)
        => tab?.Trim().ToLowerInvariant() switch
        {
            "trips" => "trips",
            "docs" => "docs",
            _ => "info"
        };
}

public sealed class TransportResourceTripItem
{
    public DateTime? Date { get; init; }
    /// <summary>منبع ثبت: حوالهٔ موتر، بارگیری، انتقال یا محموله.</summary>
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Product { get; init; }
    public string Status { get; init; } = "";
    public decimal QuantityMt { get; init; }
    public decimal? DeliveredQuantityMt { get; init; }
    public decimal? ShortageMt { get; init; }
    public string Quantity => $"{QuantityMt:N3} MT";
    public string Route { get; init; } = "";
    public string Reference { get; init; } = "";
    public bool IsCancelled { get; init; }
    public bool IsInProgress { get; init; }
    public string? Controller { get; init; }
    public string? Action { get; init; }
    public int? RouteId { get; init; }
}

public sealed class TransportResourceLinkItem
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
}

public sealed class TransportResourceDocumentItem
{
    public DateTime? Date { get; init; }
    public string Type { get; init; } = "";
    public string Number { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime? ExpiryDate { get; init; }
    public bool IsExpired { get; init; }
    public string? Controller { get; init; }
    public string? Action { get; init; }
    public int? RouteId { get; init; }
}
