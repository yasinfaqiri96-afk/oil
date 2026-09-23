using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// کارت «حرکت نقدی» در «وضعیت مالی شرکت» قبلاً با دو SumAsync جدا ساخته می‌شد (یکی با
/// <c>WHERE Direction = In</c> و یکی با <c>Out</c>). حالا هر دو جمع در یک رفت‌وبرگشت گرفته
/// می‌شوند و شرطِ جهت داخل خودِ SUM رفته است. این تست قفل می‌کند که عدد همان بماند:
/// ورودی منهای خروجی، و سندی که نه ورودی است نه خروجی در هیچ‌کدام شمرده نشود.
/// حرکت نقدی فقط حرکتِ واقعیِ صندوق/بانک است (مرجع: CashPositionReader)، پس اسنادِ این تست
/// روی یک صندوق ثبت می‌شوند.
/// </summary>
public sealed class CompanyOverviewCashMovementTests
{
    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.CashAccounts.Add(new CashAccount { Id = 1, Code = "CASH", Name = "Main Cash", Currency = "USD" });
        db.SaveChanges();
        return db;
    }

    private static async Task<CompanyFinancialOverviewViewModel> OverviewAsync(ApplicationDbContext db)
    {
        var controller = new ReportsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var view = Assert.IsType<ViewResult>(await controller.CompanyOverview());
        return Assert.IsType<CompanyFinancialOverviewViewModel>(view.Model);
    }

    [Fact]
    public async Task Net_Cash_Movement_Is_Inflow_Minus_Outflow()
    {
        await using var db = NewDb();
        var day = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        db.PaymentTransactions.AddRange(
            new PaymentTransaction { Id = 1, PaymentDate = day, Direction = PaymentDirection.In, CashAccountId = 1, AmountUsd = 1_250.75m },
            new PaymentTransaction { Id = 2, PaymentDate = day, Direction = PaymentDirection.In, CashAccountId = 1, AmountUsd = 400.25m },
            new PaymentTransaction { Id = 3, PaymentDate = day, Direction = PaymentDirection.Out, CashAccountId = 1, AmountUsd = 900m });
        await db.SaveChangesAsync();

        var model = await OverviewAsync(db);

        Assert.Equal(751.00m, model.NetCashMovementUsd);
    }

    [Fact]
    public async Task No_Payments_Means_Zero_Not_A_Crash()
    {
        await using var db = NewDb();

        var model = await OverviewAsync(db);

        Assert.Equal(0m, model.NetCashMovementUsd);
    }

    [Fact]
    public async Task Only_Outflow_Gives_A_Negative_Movement()
    {
        await using var db = NewDb();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 1,
            PaymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            Direction = PaymentDirection.Out,
            CashAccountId = 1,
            AmountUsd = 320.50m
        });
        await db.SaveChangesAsync();

        var model = await OverviewAsync(db);

        Assert.Equal(-320.50m, model.NetCashMovementUsd);
    }
}
