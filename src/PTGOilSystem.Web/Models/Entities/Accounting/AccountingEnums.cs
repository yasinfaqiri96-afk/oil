namespace PTGOilSystem.Web.Models.Entities;

public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Revenue = 4,
    Expense = 5
}

public enum NormalBalance
{
    Debit = 1,
    Credit = 2
}

public enum JournalEntryStatus
{
    Draft = 0,
    Posted = 1
}

public enum FiscalYearStatus
{
    Draft = 0,
    Open = 1,
    Closing = 2,
    Closed = 3,

    /// <summary>
    /// سالِ بسته‌ای که عمداً دوباره باز شده (مرحله ۱۵). هیچ مسیری امروز این مقدار را نمی‌نویسد؛
    /// فقط برای نمایش و برای اینکه قفلِ دوره از همین امروز تکلیفش را بداند اضافه شده است.
    /// </summary>
    Reopened = 4
}

public enum FiscalPeriodStatus
{
    Open = 1,

    /// <summary>وضعیت قدیمی. از نظر ثبت دقیقاً مثل <see cref="HardLocked"/> رفتار می‌کند.</summary>
    Closed = 2,

    /// <summary>ثبت عادی ممنوع؛ فقط عملیات استثناییِ دارای Permission و Audit.</summary>
    SoftLocked = 3,

    /// <summary>ثبت، برگشت، Repost و Backdate بدون استثنا ممنوع.</summary>
    HardLocked = 4
}

public enum FiscalYearCloseRunStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>
/// مرحله ۱۳ — طبقه‌بندیِ صریحِ پولی/غیرپولیِ حساب. **هرگز از شماره یا نامِ حساب حدس زده نمی‌شود.**
/// فقط حساب‌های <see cref="MonetaryTreatment.Monetary"/> در تسعیرِ پایان دوره شرکت می‌کنند.
/// <see cref="MonetaryTreatment.Unspecified"/> یعنی هنوز تعیین‌تکلیف نشده — چک‌لیست آن را Blocked نشان می‌دهد.
/// </summary>
public enum MonetaryTreatment
{
    Unspecified = 0,
    Monetary = 1,
    NonMonetary = 2
}

/// <summary>
/// مرحله ۱۳/۱۴ — نوعِ اجرای بستنِ سال. Trial قابل‌تکرار و بی‌اثر بر وضعیتِ سال است؛ Final اتمیک و
/// غیرقابل‌تکرارِ ناخواسته است.
/// </summary>
public enum FiscalYearCloseRunType
{
    Trial = 0,
    Final = 1
}

public enum AccountingPartyType
{
    Customer = 1,
    Supplier = 2,
    ServiceProvider = 3,
    Sarraf = 4,
    Driver = 5,
    Employee = 6,
    Partner = 7,
    Company = 8
}
