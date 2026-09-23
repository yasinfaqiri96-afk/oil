using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ProductsControllerTests
{
    [Fact]
    public async Task Index_Populates_Active_Units_For_Filters()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Units.AddRange(
            new Unit { Id = 1, Code = "MT", Name = "Metric Ton", IsActive = true },
            new Unit { Id = 2, Code = "LTR", Name = "Liter", IsActive = false });
        db.Products.Add(new Product
        {
            Id = 7,
            Code = "GO",
            Name = "Gas Oil",
            UnitId = 1,
            UnitOfMeasure = "MT",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var controller = new ProductsController(
            db,
            new AuditService(db),
            new MasterDataDeleteSafetyService(db));

        var result = await controller.Index(null, null, null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<Product>>(view.Model);
        Assert.Single(model);

        var units = Assert.IsType<SelectList>((object)controller.ViewBag.Units);
        var items = units.Cast<SelectListItem>().ToList();
        Assert.Single(items);
        Assert.Equal("1", items[0].Value);
    }

    [Fact]
    public void Products_View_Uses_Approved_Ak_List_Structure_Without_Losing_Behavior_Hooks()
    {
        var viewPath = GetProjectFilePath("src", "PTGOilSystem.Web", "Views", "Products", "Index.cshtml");
        var content = File.ReadAllText(viewPath);

        Assert.Contains("class=\"ak-list-page\"", content);
        Assert.Contains("Views/Shared/Components/Ak/_AkPageHeader.cshtml", content);
        Assert.Contains("Views/Shared/_AkSearchFilter.cshtml", content);
        Assert.Contains("new AkSearchFilterModel(\n            \"q\"", content.Replace("\r\n", "\n"));
        // فیلتر واحد چندانتخابی شد؛ همان پارامتر unitId، فقط با امکان تکرار مقدار.
        Assert.Contains("MultiSelect(\"unitId\"", content);
        Assert.Contains("new(\"isActive\"", content);
        Assert.Contains("Url.Action(\"Create\", new { returnUrl })", content);
        Assert.DoesNotContain("_CreateModalShell", content);
        Assert.DoesNotContain("productsCreateModal", content);
        Assert.Contains("class=\"ak-table\"", content);
        Assert.Contains("class=\"dropdown ak-row-menu\"", content);
        Assert.Contains("class=\"ak-status", content);
        Assert.Contains("asp-action=\"Details\" asp-route-id=\"@item.Id\"", content);
        Assert.Contains("asp-action=\"Edit\" asp-route-id=\"@item.Id\"", content);
        Assert.Contains("asp-action=\"Delete\" asp-route-id=\"@item.Id\" method=\"post\"", content);
        Assert.Contains("data-ptg-confirm=\"true\"", content);
        Assert.Contains("Html.AntiForgeryToken()", content);
        Assert.Contains("Html.PartialAsync(\"_PagedListFooter\")", content);
        Assert.DoesNotContain("mdc-", content);
        Assert.DoesNotContain("ds-card-grid", content);
    }

    private static string GetProjectFilePath(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
    }
}
