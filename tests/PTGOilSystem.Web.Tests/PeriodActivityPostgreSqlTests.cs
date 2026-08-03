using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// اثباتِ روی PostgreSQL واقعی که پارامترهای تاریخِ بازهٔ دوره با ستون‌های timestamptz سازگارند.
/// این خطا (Kind=Unspecified) در Provider درون‌حافظه دیده نمی‌شود؛ فقط اینجا. حتی با جدول‌های خالی
/// هم پارامتر تاریخ به دیتابیس فرستاده می‌شود، پس همین seedِ کمینه برای پوششِ همهٔ کوئری‌ها کافی است.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class PeriodActivityPostgreSqlTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Build_Executes_Every_Query_With_Timestamptz_Date_Parameters()
    {
        await using var db = fixture.CreateDbContext();

        var company = new Company
        {
            Code = $"PA{Guid.NewGuid():N}"[..12],
            Name = "Period Activity",
            Country = "AF",
            IsActive = true
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var year = new FiscalYear
        {
            CompanyId = company.Id,
            Name = "FY-PA",
            StartDate = Utc(2026, 1, 1),
            EndDate = Utc(2026, 12, 31),
            Status = FiscalYearStatus.Open
        };
        db.FiscalYears.Add(year);
        await db.SaveChangesAsync();

        var period = new FiscalPeriod
        {
            CompanyId = company.Id,
            FiscalYearId = year.Id,
            PeriodNumber = 3,
            Name = "P3",
            StartDate = Utc(2026, 3, 1),
            EndDate = Utc(2026, 3, 31),
            Status = FiscalPeriodStatus.Open
        };
        db.FiscalPeriods.Add(period);
        await db.SaveChangesAsync();

        // پیش از اصلاحِ Kind، اینجا Npgsql خطای «Cannot write DateTime with Kind=Unspecified» می‌داد.
        var model = await new PeriodActivityService(db).BuildAsync(
            period.Id, company.Id, accountingEnabled: true);

        Assert.NotNull(model);
        Assert.Equal(0, model!.Kpis.SalesCount);
        Assert.Equal(0m, model.Kpis.ExpensesUsd);
        Assert.Equal(0, model.Kpis.JournalCount);
        Assert.Empty(model.Journals);
    }

    private static DateTime Utc(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
