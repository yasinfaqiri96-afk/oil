using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Models.Reports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class InventorySalesReportTests
{
    [Theory]
    [InlineData(false, true, false, 0)]
    [InlineData(true, false, true, 0)]
    [InlineData(true, true, true, 1)]
    public async Task Details_Are_Optional_Permission_Guarded_And_Include_The_Full_Selected_Day(
        bool showDetails, bool inventoryAccess, bool expectOperations, int expectedMovements)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal", IsActive = true });
        var day = new DateTime(2026, 5, 1);
        db.InventoryMovements.AddRange(
            new InventoryMovement { Id = 1, ProductId = 1, TerminalId = 1,
                Direction = MovementDirection.In, QuantityMt = 10m, MovementDate = day.AddHours(17) },
            new InventoryMovement { Id = 2, ProductId = 1, TerminalId = 1,
                Direction = MovementDirection.In, QuantityMt = 20m, MovementDate = day.AddDays(1) });
        await db.SaveChangesAsync();
        var claims = new List<Claim> { new(AppClaimTypes.AllowedNavigation, RoleNavigationKeys.Reports) };
        if (inventoryAccess) claims.Add(new(AppClaimTypes.AllowedNavigation, RoleNavigationKeys.Inventory));
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddControllersWithViews();
        using var services = registrations.BuildServiceProvider();
        var controller = new ReportsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            } }
        };
        var view = Assert.IsType<ViewResult>(await controller.InventorySales(
            new ManagementReportFilterViewModel { ProductId = 1, ToDate = day }, showDetails, page: -1));
        var model = Assert.IsType<InventorySalesReportViewModel>(view.Model);
        Assert.Equal(10m, model.Stock.TotalPhysicalMt);
        Assert.Equal(expectOperations, model.Operations is not null);
        Assert.Equal(expectedMovements, model.TotalMovements);
        Assert.Equal(expectedMovements, model.Movements.Count);
        Assert.Equal(inventoryAccess, model.CanViewMovements);
        Assert.Equal(1, model.Page);
        Assert.Equal(2, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public void Hub_Uses_One_Inventory_Report_While_Other_Reports_Remain()
    {
        var result = Assert.IsType<ViewResult>(new ReportsController(null!).Index());
        var model = Assert.IsType<ReportHubViewModel>(result.Model);
        var group = Assert.Single(model.Groups.Where(g => g.TitleEn == "Stock, Sales & Transport"));
        Assert.Single(group.Cards.Where(c => c.Action == "InventorySales"));
        Assert.DoesNotContain(group.Cards, c => c.Action is "InventoryOperations" or "SellableStock" or "StockCard");
        Assert.Contains(group.Cards, c => c.Action == "NegativeStock");
        Assert.Contains(group.Cards, c => c.Action == "PreSales");
    }
}
