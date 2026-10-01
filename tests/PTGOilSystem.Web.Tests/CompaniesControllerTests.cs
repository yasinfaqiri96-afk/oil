using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «مالک دفتر» در ثبتِ شرکت — همان فیلد و همان اعتبارسنجیِ ویرایش، از همان لحظهٔ ساخت.
/// </summary>
public class CompaniesControllerTests
{
    [Fact]
    public async Task Create_Form_Offers_The_Active_Partners_As_Book_Owner()
    {
        await using var db = NewDb();
        db.Partners.AddRange(
            new Partner { Id = 1, Code = "PA1", Name = "Fawad", IsActive = true },
            new Partner { Id = 2, Code = "PA2", Name = "Archived", IsActive = false });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        Assert.IsType<ViewResult>(await controller.Create());

        var options = Assert.IsType<List<SelectListItem>>(controller.ViewBag.OwnerPartners);
        var option = Assert.Single(options);
        Assert.Equal("1", option.Value);
    }

    [Fact]
    public async Task Create_With_A_Book_Owner_Saves_It_And_Audits_It()
    {
        await using var db = NewDb();
        db.Partners.Add(new Partner { Id = 1, Code = "PA1", Name = "Fawad", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.Create(new Company
        {
            Code = "FS",
            Name = "Fawad Saddiqi",
            Country = "AF",
            IsActive = true,
            OwnerPartnerId = 1
        });

        Assert.IsType<RedirectToActionResult>(result);
        var company = await db.Companies.SingleAsync(c => c.Code == "FS");
        Assert.Equal(1, company.OwnerPartnerId);
        var audit = await db.AuditLogs.SingleAsync(a => a.EntityName == nameof(Company) && a.EntityId == company.Id);
        Assert.Contains("OwnerPartnerId", audit.Diff);
    }

    [Fact]
    public async Task Create_Without_A_Book_Owner_Is_Still_Allowed()
    {
        await using var db = NewDb();

        var controller = BuildController(db);
        var result = await controller.Create(new Company { Code = "CO1", Name = "Independent", Country = "AF", IsActive = true });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null((await db.Companies.SingleAsync(c => c.Code == "CO1")).OwnerPartnerId);
    }

    [Fact]
    public async Task Create_With_An_Unknown_Book_Owner_Is_Rejected_Like_Edit()
    {
        await using var db = NewDb();

        var controller = BuildController(db);
        var result = await controller.Create(new Company
        {
            Code = "CO2",
            Name = "Ghost owner",
            Country = "AF",
            IsActive = true,
            OwnerPartnerId = 999
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(Company.OwnerPartnerId)));
        Assert.NotNull(controller.ViewBag.OwnerPartners);
        Assert.Empty(db.Companies);
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CompaniesController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db), new MasterDataDeleteSafetyService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
