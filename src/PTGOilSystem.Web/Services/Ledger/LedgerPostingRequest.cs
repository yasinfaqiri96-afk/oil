using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.Ledger;

/// <summary>
/// PTG-P1-03 — معنای تجاریِ **از پیش تعیین‌شدهٔ** یک سطر دفتر کل.
///
/// این یک درخواستِ ثبت است، نه یک محاسبه. هیچ عددی اینجا ساخته یا گِرد نمی‌شود: مبلغ، نرخ،
/// جهت و تاریخ را همان کدی تعیین می‌کند که قبلاً تعیین می‌کرد. تنها چیزی که متمرکز می‌شود
/// «چطور یک <see cref="LedgerEntry"/> ساخته و اعتبارسنجی می‌شود» است.
///
/// دلیلِ وجودش: پیش از این ~۱۸ نقطهٔ مستقل مستقیماً <c>new LedgerEntry</c> می‌ساختند. هیچ‌کدام
/// امروز عدد غلط نمی‌دهند (شبیه‌سازیِ ۱۲ ماهه: یتیم/گم‌شده/تکراری = ۰)، ولی هر فیلدِ تازه یا
/// قاعدهٔ تازه باید ۱۸ بار درست تکرار می‌شد. یک‌بار فراموش‌کردن = یک سطرِ ناقص در دفتری که
/// ماندهٔ واقعی طرف‌حساب‌ها را می‌سازد.
/// </summary>
public sealed record LedgerPostingRequest
{
    /// <summary>نوع سند مبدأ — عیناً همان رشته‌ای که پیش از این نوشته می‌شد.</summary>
    public required string SourceType { get; init; }

    /// <summary>
    /// شناسهٔ سند مبدأ. صفر فقط در یک حالت مجاز است: سندی که هنوز <c>Id</c> ندارد و
    /// بلافاصله پس از نخستین <c>SaveChanges</c> مقدار می‌گیرد (<see cref="AllowDeferredSourceId"/>).
    /// </summary>
    public required int SourceId { get; init; }

    public required DateTime EntryDate { get; init; }
    public required LedgerSide Side { get; init; }
    public required decimal AmountUsd { get; init; }
    public required string Description { get; init; }

    /// <summary>ارزِ خودِ سطرِ دفتر کل. عملاً همیشه ارز پایه است.</summary>
    public string Currency { get; init; } = SystemCurrency.BaseCurrencyCode;

    public decimal? SourceAmount { get; init; }
    public string? SourceCurrencyCode { get; init; }
    public decimal? AppliedFxRateToUsd { get; init; }
    public decimal? AppliedCurrencyPerUsdRate { get; init; }
    public DateTime? AppliedFxRateDate { get; init; }
    public string? AppliedFxRateSource { get; init; }

    public string? Reference { get; init; }
    public Guid? ViaSarrafGroupId { get; init; }

    // ---- ارجاعات طرف/قرارداد/محموله ----
    public int? ContractId { get; init; }
    public int? CustomerId { get; init; }
    public int? SupplierId { get; init; }
    public int? ServiceProviderId { get; init; }
    public int? DriverId { get; init; }
    public int? EmployeeId { get; init; }
    public int? PartnerId { get; init; }
    public int? ShipmentId { get; init; }

    /// <summary>
    /// سند مبدأ هنوز ذخیره نشده و <c>SourceId</c> بعداً پر می‌شود. چند مسیرِ موجود عمداً
    /// این‌طور کار می‌کنند (سطر دفتر و سند در یک <c>SaveChanges</c> نوشته می‌شوند و بعد
    /// شناسه‌ها به هم وصل می‌شوند)، پس این حالت باید صریح باشد، نه سکوت.
    /// </summary>
    public bool AllowDeferredSourceId { get; init; }
}
