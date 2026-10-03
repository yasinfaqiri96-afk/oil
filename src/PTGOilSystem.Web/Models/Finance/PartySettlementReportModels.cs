using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Models.Finance;

/// <summary>جهت انتقال بین طرف‌حساب «الف» و «ب» در گزارش تسویه‌ها.</summary>
public enum PartySettlementReportDirection
{
    Both = 0,
    AToB = 1,
    BToA = 2
}

public enum PartySettlementReportStatus
{
    Active = 0,
    Cancelled = 1,
    All = 2
}

/// <summary>
/// فیلتر گزارش «تسویه بین طرف‌حساب‌ها». طرف‌حساب‌ها همان کلید «نوع:شناسه» فورم تسویه‌اند.
/// اگر فقط «الف» انتخاب شود: الف ← ب یعنی پرداختی‌های الف و ب ← الف یعنی دریافتی‌های الف.
/// </summary>
public sealed class PartySettlementReportFilter
{
    public string? PartyA { get; set; }
    public string? PartyB { get; set; }
    public PartySettlementReportDirection Direction { get; set; } = PartySettlementReportDirection.Both;
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? Currency { get; set; }
    public PartySettlementReportStatus Status { get; set; } = PartySettlementReportStatus.Active;
    public string? Search { get; set; }
}

/// <summary>جمع به تفکیک ارز؛ ارزهای مختلف هرگز با هم جمع نمی‌شوند.</summary>
public sealed record PartySettlementCurrencyTotal(string Currency, decimal Amount, int Count);

public sealed record PartySettlementReportRow(
    int Id,
    string Number,
    DateTime SettlementDate,
    string FromName,
    string FromTypeLabel,
    string ToName,
    string ToTypeLabel,
    decimal Amount,
    string Currency,
    decimal? CurrencyPerUsdRate,
    string? Description,
    string? CreatedByUserName,
    PartySettlementStatus Status,
    DateTime? CancelledAtUtc,
    string? CancelledByUserName,
    string? CancellationReason);

public sealed class PartySettlementReportViewModel
{
    public required PartySettlementReportFilter Filter { get; init; }
    public string? PartyAName { get; init; }
    public string? PartyBName { get; init; }
    public bool HasPartyA => !string.IsNullOrWhiteSpace(PartyAName);
    public bool HasPartyB => !string.IsNullOrWhiteSpace(PartyBName);

    /// <summary>تعداد همهٔ سطرهای مطابق فیلتر (شامل لغوشده‌ها اگر وضعیت اجازه دهد).</summary>
    public int TotalCount { get; init; }
    public int ActiveCount { get; init; }
    public int CancelledCount { get; init; }

    // جمع‌ها فقط از تسویه‌های فعال (لغوشده‌ها اثر حسابداری ندارند).
    public IReadOnlyList<PartySettlementCurrencyTotal> AToBTotals { get; init; } = [];
    public IReadOnlyList<PartySettlementCurrencyTotal> BToATotals { get; init; } = [];
    public IReadOnlyList<PartySettlementCurrencyTotal> Totals { get; init; } = [];

    public IReadOnlyList<PartySettlementReportRow> Rows { get; init; } = [];
}
