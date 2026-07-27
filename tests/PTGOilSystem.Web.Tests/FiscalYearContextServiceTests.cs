using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// گاردهای «سال مالیِ فعالِ کانتکست»: تعلق به شرکتِ مالک، بازگشتِ امن به سالِ جاری، و
/// فقط‌خواندنی‌بودنِ سالِ بسته/پیش‌نویس. هیچ منطقِ posting اینجا آزموده نمی‌شود چون این سرویس آن را
/// تغییر نمی‌دهد.
/// </summary>
public class FiscalYearContextServiceTests
{
    private const string ProtectorPurpose = "PTGOilSystem.FiscalYearContext.v1";

    [Fact]
    public async Task Get_Defaults_To_The_Current_Year_When_No_Cookie_Is_Present()
    {
        using var db = NewDb();
        SeedOwner(db);
        SeedYear(db, id: 10, companyId: 1, name: "FY-2025", start: new DateTime(2025, 1, 1), isCurrent: false, status: FiscalYearStatus.Closed);
        SeedYear(db, id: 11, companyId: 1, name: "FY-2026", start: new DateTime(2026, 1, 1), isCurrent: true, status: FiscalYearStatus.Open);
        await db.SaveChangesAsync();

        var (service, _) = NewService(db, new DefaultHttpContext());
        var view = await service.GetAsync();

        Assert.Equal(2, view.Options.Count);
        Assert.Equal(11, view.SelectedFiscalYearId);
        Assert.True(view.SelectedIsWritable);
    }

    [Fact]
    public async Task Get_Uses_The_Cookie_When_It_Points_To_An_Owner_Year()
    {
        using var db = NewDb();
        SeedOwner(db);
        SeedYear(db, id: 10, companyId: 1, name: "FY-2025", start: new DateTime(2025, 1, 1), isCurrent: false, status: FiscalYearStatus.Closed);
        SeedYear(db, id: 11, companyId: 1, name: "FY-2026", start: new DateTime(2026, 1, 1), isCurrent: true, status: FiscalYearStatus.Open);
        await db.SaveChangesAsync();

        var provider = new EphemeralDataProtectionProvider();
        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = CookieHeaderFor(provider, fiscalYearId: 10);

        var (service, _) = NewService(db, request, provider);
        var view = await service.GetAsync();

        Assert.Equal(10, view.SelectedFiscalYearId);
        // سالِ بسته انتخاب می‌شود ولی فقط‌خواندنی است.
        Assert.False(view.SelectedIsWritable);
    }

    [Fact]
    public async Task Get_Ignores_A_Cookie_That_Points_To_Another_Companys_Year()
    {
        using var db = NewDb();
        SeedOwner(db);
        SeedCompany(db, id: 2, code: "B", isOwner: false);
        SeedYear(db, id: 11, companyId: 1, name: "FY-2026", start: new DateTime(2026, 1, 1), isCurrent: true, status: FiscalYearStatus.Open);
        SeedYear(db, id: 99, companyId: 2, name: "OTHER-2026", start: new DateTime(2026, 1, 1), isCurrent: true, status: FiscalYearStatus.Open);
        await db.SaveChangesAsync();

        var provider = new EphemeralDataProtectionProvider();
        var request = new DefaultHttpContext();
        // کوکیِ معتبرِ امضاشده اما برای سالِ شرکتِ دیگر — باید نادیده گرفته و به سالِ جاریِ مالک برگردد.
        request.Request.Headers.Cookie = CookieHeaderFor(provider, fiscalYearId: 99);

        var (service, _) = NewService(db, request, provider);
        var view = await service.GetAsync();

        Assert.Equal(11, view.SelectedFiscalYearId);
        Assert.DoesNotContain(view.Options, o => o.FiscalYearId == 99);
    }

    [Fact]
    public async Task TrySelect_Rejects_A_Year_From_Another_Company_And_Writes_No_Cookie()
    {
        using var db = NewDb();
        SeedOwner(db);
        SeedCompany(db, id: 2, code: "B", isOwner: false);
        SeedYear(db, id: 99, companyId: 2, name: "OTHER-2026", start: new DateTime(2026, 1, 1), isCurrent: true, status: FiscalYearStatus.Open);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        var (service, _) = NewService(db, http);

        var ok = await service.TrySelectAsync(99);

        Assert.False(ok);
        Assert.True(http.Response.Headers.SetCookie.Count == 0);
    }

    [Fact]
    public async Task TrySelect_Writes_A_Signed_HttpOnly_Cookie_For_An_Owner_Year()
    {
        using var db = NewDb();
        SeedOwner(db);
        SeedYear(db, id: 11, companyId: 1, name: "FY-2026", start: new DateTime(2026, 1, 1), isCurrent: true, status: FiscalYearStatus.Open);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        var (service, _) = NewService(db, http);

        var ok = await service.TrySelectAsync(11);

        Assert.True(ok);
        var setCookie = Assert.Single(http.Response.Headers.SetCookie);
        Assert.Contains(FiscalYearContextService.CookieName + "=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        // شناسه به‌صورت خام در کوکی نیست — امضاشده/رمزشده است.
        Assert.DoesNotContain("=11;", setCookie);
    }

    [Fact]
    public async Task Get_Returns_No_Options_When_There_Is_No_Owner_Company()
    {
        using var db = NewDb();
        // شرکتی هست ولی هیچ‌کدام مالکِ سیستم نیستند.
        SeedCompany(db, id: 5, code: "X", isOwner: false);
        await db.SaveChangesAsync();

        var (service, _) = NewService(db, new DefaultHttpContext());
        var view = await service.GetAsync();

        Assert.False(view.HasOptions);
        Assert.Null(view.SelectedFiscalYearId);
    }

    [Theory]
    [InlineData(FiscalYearStatus.Open, true)]
    [InlineData(FiscalYearStatus.Reopened, true)]
    [InlineData(FiscalYearStatus.Draft, false)]
    [InlineData(FiscalYearStatus.Closing, false)]
    [InlineData(FiscalYearStatus.Closed, false)]
    public void Writable_Is_True_Only_For_Open_Or_Reopened(FiscalYearStatus status, bool expected)
        => Assert.Equal(expected, FiscalYearContextService.IsWritable(status));

    // ── داربست ────────────────────────────────────────────────────────────────────

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (FiscalYearContextService Service, IHttpContextAccessor Accessor) NewService(
        ApplicationDbContext db,
        HttpContext httpContext,
        IDataProtectionProvider? provider = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var service = new FiscalYearContextService(
            db,
            new SystemCompanyProvider(db),
            accessor,
            provider ?? new EphemeralDataProtectionProvider());
        return (service, accessor);
    }

    private static string CookieHeaderFor(IDataProtectionProvider provider, int fiscalYearId)
    {
        var value = provider.CreateProtector(ProtectorPurpose)
            .Protect(fiscalYearId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"{FiscalYearContextService.CookieName}={value}";
    }

    private static void SeedOwner(ApplicationDbContext db) => SeedCompany(db, id: 1, code: "A", isOwner: true);

    private static void SeedCompany(ApplicationDbContext db, int id, string code, bool isOwner)
        => db.Companies.Add(new Company
        {
            Id = id,
            Code = code,
            Name = $"Company {code}",
            Country = "AF",
            IsActive = true,
            IsSystemOwner = isOwner
        });

    private static void SeedYear(
        ApplicationDbContext db,
        int id,
        int companyId,
        string name,
        DateTime start,
        bool isCurrent,
        FiscalYearStatus status)
        => db.FiscalYears.Add(new FiscalYear
        {
            Id = id,
            CompanyId = companyId,
            Name = name,
            StartDate = start,
            EndDate = start.AddYears(1).AddDays(-1),
            Status = status,
            IsCurrent = isCurrent
        });
}
