using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Reports;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class InventoryOperationsReservationTests
{
    [Fact]
    public async Task Inventory_Report_Subtracts_Only_Active_Undelivered_PreSales()
    {
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal" });
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = 1, ProductId = 1, TerminalId = 1, Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 1), QuantityMt = 100m
        });
        db.PreSaleOrders.AddRange(
            new PreSaleOrder
            {
                Id = 1, OrderNumber = "ACTIVE", CustomerId = 1, ProductId = 1,
                OrderDate = new DateTime(2026, 5, 1), QuantityMt = 30m,
                Status = PreSaleOrderStatus.PartiallyDelivered
            },
            new PreSaleOrder
            {
                Id = 2, OrderNumber = "CANCELLED", CustomerId = 1, ProductId = 1,
                OrderDate = new DateTime(2026, 5, 1), QuantityMt = 50m,
                Status = PreSaleOrderStatus.Cancelled
            });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1, PreSaleOrderId = 1, CustomerId = 1, ProductId = 1,
            InvoiceNumber = "DEL-1", SaleDate = new DateTime(2026, 5, 2),
            QuantityMt = 10m
        });
        await db.SaveChangesAsync();

        var result = Assert.IsType<ViewResult>(
            await new ReportsController(db).InventoryOperations(new ManagementReportFilterViewModel()));
        var model = Assert.IsType<InventoryOperationsReportViewModel>(result.Model);

        Assert.Equal("20.0000 MT", model.Metrics.Single(m => m.Label == "تعهد پیش‌فروش").Value);
        Assert.Equal("80.0000 MT", model.Metrics.Single(m => m.Label == "موجودی قابل فروش").Value);
    }
}
