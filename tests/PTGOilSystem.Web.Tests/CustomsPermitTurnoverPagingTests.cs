using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Customs;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// گزارش «گردش جواز گمرکی» حالا صفحه‌بندی واقعی دارد. جمع‌های پایین صفحه و شمارندهٔ موترها
/// باید همچنان روی کل مجموعهٔ فیلترشده باشند، نه روی صفحهٔ جاری — این تست همان را قفل می‌کند.
/// </summary>
public sealed class CustomsPermitTurnoverPagingTests
{
    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static void Seed(ApplicationDbContext db, int declarationCount)
    {
        for (var i = 1; i <= declarationCount; i++)
        {
            db.CustomsDeclarations.Add(new CustomsDeclaration
            {
                Id = i,
                DeclarationDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                DeclarationReference = $"ACCD-{i:D3}",
                WagonOrTruckNumber = $"TRK-{i:D3}",
                PermitNumber = "P-1",
                PermitHolderName = "Holder",
                ConsignmentWeightMt = 10m,
                Items =
                [
                    // هر دو ارز پر است تا عدد به نرخ روز وابسته نباشد.
                    new CustomsDeclarationItem
                    {
                        ComponentType = CustomsComponentType.Mahsooli,
                        AmountAfn = 700m,
                        AmountUsd = 10m
                    },
                    new CustomsDeclarationItem
                    {
                        ComponentType = CustomsComponentType.FawaidAama,
                        AmountAfn = 140m,
                        AmountUsd = 2m
                    }
                ]
            });
        }

        db.SaveChanges();
    }

    private static CustomsPermitTurnoverController NewController(ApplicationDbContext db)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static async Task<CustomsPermitTurnoverViewModel> IndexAsync(
        ApplicationDbContext db, int page, int pageSize)
    {
        var result = await NewController(db).Index(page: page, perPage: pageSize);
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<CustomsPermitTurnoverViewModel>(view.Model);
    }

    [Fact]
    public async Task Only_One_Page_Of_Rows_Is_Returned()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db, declarationCount: 27);

        var page1 = await IndexAsync(db, page: 1, pageSize: 10);
        var page3 = await IndexAsync(db, page: 3, pageSize: 10);

        Assert.Equal(10, page1.Rows.Count);
        Assert.Equal(7, page3.Rows.Count);
        Assert.Equal(3, page1.PageCount);
        Assert.Empty(page1.Rows.Select(r => r.Id).Intersect(page3.Rows.Select(r => r.Id)));
    }

    [Fact]
    public async Task Totals_And_Vehicle_Count_Stay_On_The_Whole_Filtered_Set()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db, declarationCount: 27);

        var page1 = await IndexAsync(db, page: 1, pageSize: 10);
        var page3 = await IndexAsync(db, page: 3, pageSize: 10);

        // ۲۷ اظهارنامه × (۷۰۰ + ۱۴۰) افغانی و (۱۰ + ۲) دالر
        Assert.Equal(27, page1.VehicleCount);
        Assert.Equal(270m, page1.TotalQuantityMt);
        Assert.Equal(27 * 840m, page1.TotalCustomsAfn);
        Assert.Equal(27 * 12m, page1.TotalCustomsUsd);
        Assert.Equal(27 * 700m, page1.TotalMahsooliAfn);
        Assert.Equal(27 * 10m, page1.TotalMahsooliUsd);

        // صفحهٔ آخر همان جمع‌ها را نشان می‌دهد.
        Assert.Equal(page1.VehicleCount, page3.VehicleCount);
        Assert.Equal(page1.TotalQuantityMt, page3.TotalQuantityMt);
        Assert.Equal(page1.TotalCustomsAfn, page3.TotalCustomsAfn);
        Assert.Equal(page1.TotalCustomsUsd, page3.TotalCustomsUsd);
        Assert.Equal(page1.TotalMahsooliAfn, page3.TotalMahsooliAfn);
        Assert.Equal(page1.TotalMahsooliUsd, page3.TotalMahsooliUsd);
    }

    [Fact]
    public async Task Filters_Still_Narrow_Both_Rows_And_Totals()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db, declarationCount: 27);

        var result = await NewController(db).Index(vehicle: "TRK-005", page: 1, perPage: 10);
        var model = Assert.IsType<CustomsPermitTurnoverViewModel>(Assert.IsType<ViewResult>(result).Model);

        Assert.Single(model.Rows);
        Assert.Equal(1, model.VehicleCount);
        Assert.Equal(840m, model.TotalCustomsAfn);
    }
}
