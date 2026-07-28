namespace PTGOilSystem.Web.Services.CompanyFlow;

/// <summary>
/// جهت تجاری یک رویداد از دید شرکت — تنها قرارداد نمایشیِ سیستم.
///
/// این enum جایگزینِ کاربردیِ Debit/Credit در لایهٔ کاربر است. ساختار داخلی حسابداری
/// (<see cref="Models.Entities.LedgerSide"/>، JournalEntryLine، ستون‌های دیتابیس) دست‌نخورده
/// می‌ماند؛ فقط خروجی‌های کاربری از این جهت عبور می‌کنند.
/// </summary>
public enum CompanyFlowDirection
{
    /// <summary>رسید — هر پول، کالا، خدمت یا ارزشی که شرکت از طرف مقابل دریافت کرده است.</summary>
    Receipt = 1,

    /// <summary>برد — هر پول، کالا، خدمت یا ارزشی که شرکت به طرف مقابل داده است.</summary>
    Outflow = 2
}

/// <summary>
/// نقش طرف‌حساب در تعیین جهت. برای رویدادهایی لازم است که معنی تجاری‌شان بدون دانستن
/// نقشِ طرف مقابل مشخص نمی‌شود (مثلاً یک سطر نقدیِ عمومی).
/// </summary>
public enum CompanyFlowPartyRole
{
    Unknown = 0,
    Supplier = 1,
    Customer = 2,
    ServiceProvider = 3,
    Sarraf = 4,
    Employee = 5,
    Partner = 6,
    Driver = 7,
    Company = 8
}

/// <summary>
/// نوع حسابی که جهت روی آن نشان داده می‌شود. فرمول بیلانس بین این دو فرق دارد و
/// این تفاوت فقط در همین لایه مدیریت می‌شود.
/// </summary>
public enum CompanyFlowAccountKind
{
    /// <summary>صورت‌حساب طرف‌حساب یا دفتر قرارداد: بیلانس = اول دوره + Σبرد − Σرسید.</summary>
    PartyAccount = 1,

    /// <summary>حساب نقدی (صندوق/بانک): بیلانس = اول دوره + Σرسید − Σبرد.</summary>
    CashAccount = 2
}

/// <summary>
/// وضعیت چرخهٔ عمر سند — برای اینکه لغو/برگشت/ثبت مجدد گردش را دوبرابر نکند و
/// کارت‌های مدیریتی مقدار خالص را نشان دهند.
/// </summary>
public enum CompanyFlowLifecycle
{
    /// <summary>سند اصلی و جاری.</summary>
    Original = 0,

    /// <summary>سطر معکوس‌کننده (reversal/cancel) — جهتش وارونهٔ سند اصلی است.</summary>
    Reversal = 1,

    /// <summary>سند لغوشده که فقط برای Audit نمایش داده می‌شود و در جمع‌ها نمی‌آید.</summary>
    Cancelled = 2
}
