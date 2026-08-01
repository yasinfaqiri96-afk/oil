using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Accounting;

/// <summary>
/// انتخابِ کاربر برای «سال مالیِ فعالِ کانتکست» — یک انتخابِ صرفاً کاری/گزارشی که در یک کوکیِ
/// امضاشده (DataProtection) نگه داشته می‌شود.
///
/// **این سرویس هیچ منطقِ مالی‌ای را تغییر نمی‌دهد.** دورهٔ ثبتِ اسناد همچنان از AccountingDate و
/// <see cref="PeriodGuard"/> می‌آید. اینجا فقط تعیین می‌شود کاربر «کدام سال را نگاه می‌کند» و
/// همان قواعدِ موجود برای شرکتِ مالک و وضعیتِ سال بازتاب داده می‌شود.
///
/// دو گاردِ امنیتی همیشه سمتِ سرور اجرا می‌شوند:
///  ۱) هر شناسه‌ای که از کوکی می‌آید باید به شرکتِ مالکِ سیستم تعلق داشته باشد؛ سالِ شرکتِ دیگر
///     نه خوانده می‌شود نه انتخاب. (کوکی هم امضاشده است، پس دستکاری‌پذیر نیست.)
///  ۲) اگر انتخاب نامعتبر بود، بی‌سروصدا به «سالِ جاری» برمی‌گردد؛ هیچ‌گاه سالِ شرکتِ دیگری فعال نمی‌شود.
/// </summary>
public interface IFiscalYearContext
{
    /// <summary>وضعیتِ انتخاب‌گر برای درخواستِ جاری — گزینه‌ها، سالِ انتخاب‌شده و اینکه نوشتنی هست یا نه.</summary>
    Task<FiscalYearContextView> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// تلاش برای انتخابِ یک سال. فقط وقتی موفق است که سال وجود داشته باشد و به شرکتِ مالک تعلق
    /// داشته باشد؛ آنگاه کوکیِ امضاشده روی پاسخ نوشته می‌شود. در غیر این صورت هیچ کوکی‌ای نوشته
    /// نمی‌شود و <c>false</c> برمی‌گردد.
    /// </summary>
    Task<bool> TrySelectAsync(int fiscalYearId, CancellationToken cancellationToken = default);
}

public sealed class FiscalYearContextService : IFiscalYearContext
{
    // نامِ کوکی و هدفِ DataProtection. تغییرِ هدف همهٔ کوکی‌های قدیمی را باطل می‌کند (رفتارِ امن).
    public const string CookieName = "ptg-fiscal-year";
    private const string ProtectorPurpose = "PTGOilSystem.FiscalYearContext.v1";

    private readonly ApplicationDbContext _db;
    private readonly ISystemCompanyProvider _systemCompany;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDataProtector _protector;

    public FiscalYearContextService(
        ApplicationDbContext db,
        ISystemCompanyProvider systemCompany,
        IHttpContextAccessor httpContextAccessor,
        IDataProtectionProvider dataProtection)
    {
        _db = db;
        _systemCompany = systemCompany;
        _httpContextAccessor = httpContextAccessor;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
    }

    /// <summary>وضعیتِ نوشتنی: فقط سالِ باز یا بازگشایی‌شده. پیش‌نویس و بسته/در حال بستن فقط‌خواندنی‌اند.</summary>
    public static bool IsWritable(FiscalYearStatus status)
        => status is FiscalYearStatus.Open or FiscalYearStatus.Reopened;

    public async Task<FiscalYearContextView> GetAsync(CancellationToken cancellationToken = default)
    {
        var ownerCompanyId = await _systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (ownerCompanyId is not int owner)
            return new FiscalYearContextView(null, null, null, null, false, []);

        var years = await _db.FiscalYears.AsNoTracking()
            .Where(y => y.CompanyId == owner)
            .OrderByDescending(y => y.StartDate)
            .ThenByDescending(y => y.Id)
            .Select(y => new YearRow(y.Id, y.Name, y.Status, y.IsCurrent, y.StartDate, y.EndDate))
            .ToListAsync(cancellationToken);

        if (years.Count == 0)
            return new FiscalYearContextView(owner, null, null, null, false, []);

        // شناسهٔ کوکی فقط وقتی پذیرفته می‌شود که به همین شرکتِ مالک تعلق داشته باشد؛ وگرنه به
        // سالِ جاری برمی‌گردیم (گاردِ امنیتیِ ضدِ انتخابِ سالِ شرکتِ دیگر).
        var cookieId = ReadCookieFiscalYearId();
        var selected = (cookieId is int cid && years.Any(y => y.Id == cid))
            ? years.First(y => y.Id == cid)
            : PickCurrentYear(years);

        var options = years
            .Select(y => new FiscalYearContextOption(
                y.Id, y.Name, y.Status, y.IsCurrent, y.Id == selected.Id, y.StartDate, y.EndDate))
            .ToList();

        return new FiscalYearContextView(
            owner,
            selected.Id,
            selected.Name,
            selected.Status,
            IsWritable(selected.Status),
            options);
    }

    public async Task<bool> TrySelectAsync(int fiscalYearId, CancellationToken cancellationToken = default)
    {
        var ownerCompanyId = await _systemCompany.FindOwnerCompanyIdAsync(cancellationToken);
        if (ownerCompanyId is not int owner)
            return false;

        // گاردِ تعلق: سال باید وجود داشته باشد و مالِ شرکتِ مالک باشد. سالِ شرکتِ دیگر هرگز انتخاب نمی‌شود.
        var belongsToOwner = await _db.FiscalYears.AsNoTracking()
            .AnyAsync(y => y.Id == fiscalYearId && y.CompanyId == owner, cancellationToken);
        if (!belongsToOwner)
            return false;

        var response = _httpContextAccessor.HttpContext?.Response;
        if (response is null)
            return false;

        response.Cookies.Append(
            CookieName,
            _protector.Protect(fiscalYearId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(365)
            });

        return true;
    }

    private int? ReadCookieFiscalYearId()
    {
        var raw = _httpContextAccessor.HttpContext?.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw))
            return null;

        try
        {
            var value = _protector.Unprotect(raw);
            return int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // کوکیِ دستکاری‌شده یا با کلیدِ قدیمی — بی‌اثر رد می‌شود و به سالِ جاری برمی‌گردیم.
            return null;
        }
    }

    /// <summary>
    /// همان قاعدهٔ «سالِ جاری» که <c>FiscalYearOverviewService</c> دارد: اول پرچمِ صریحِ
    /// <see cref="FiscalYear.IsCurrent"/>، وگرنه سالی که امروز داخلش است، وگرنه تازه‌ترین سال.
    /// فهرست از قبل نزولی بر اساس StartDate مرتب شده، پس تازه‌ترین سال اولین عضو است.
    /// </summary>
    private static YearRow PickCurrentYear(List<YearRow> years)
    {
        var today = AfghanistanBusinessClock.SystemToday;
        return years.FirstOrDefault(y => y.IsCurrent)
            ?? years.FirstOrDefault(y => y.StartDate <= today && y.EndDate >= today)
            ?? years[0];
    }

    private sealed record YearRow(
        int Id,
        string Name,
        FiscalYearStatus Status,
        bool IsCurrent,
        DateTime StartDate,
        DateTime EndDate);
}
