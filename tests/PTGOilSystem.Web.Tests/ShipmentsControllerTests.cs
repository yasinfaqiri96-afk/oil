using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Shipments;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ShipmentsControllerTests
{
    [Fact]
    public async Task Create_Post_Creates_Shipment_With_Purchase_Contract_Allocations()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var controller = BuildController(db);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "KALUGA",
            VesselId = 1,
            DepartureDate = new DateTime(2026, 5, 10),
            ArrivalDate = new DateTime(2026, 5, 15),
            OriginLocationId = 1,
            DestinationLocationId = 2,
            QuantityMt = 4144.504m,
            Notes = "Third-country vessel shipment",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = 1,
                    QuantityMt = 2740m,
                    Notes = "Petrogas share"
                },
                new ShipmentContractAllocationInput
                {
                    ContractId = 2,
                    QuantityMt = 1404.504m,
                    Notes = "BNK share"
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("ShipmentPnl", redirect.ControllerName);
        Assert.Equal("Details", redirect.ActionName);

        var shipment = await db.Shipments.SingleAsync();
        Assert.Equal("KALUGA", shipment.ShipmentCode);
        Assert.Equal(4144.504m, shipment.QuantityMt);
        Assert.Equal(1, shipment.ContractId);

        var links = await db.ShipmentContracts
            .OrderBy(sc => sc.ContractId)
            .ToListAsync();
        Assert.Collection(
            links,
            first =>
            {
                Assert.Equal(1, first.ContractId);
                Assert.Equal(2740m, first.QuantityMt);
            },
            second =>
            {
                Assert.Equal(2, second.ContractId);
                Assert.Equal(1404.504m, second.QuantityMt);
            });
    }

    [Fact]
    public async Task Create_Post_Recalculates_Shipment_Quantity_From_Contract_Allocations()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var controller = BuildController(db);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "KALUGA",
            QuantityMt = 4144.504m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 2740m },
                new ShipmentContractAllocationInput { ContractId = 2, QuantityMt = 1000m }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.ModelState.IsValid);

        var shipment = await db.Shipments.SingleAsync();
        Assert.Equal(3740m, shipment.QuantityMt);
        Assert.Equal(3740m, await db.ShipmentContracts.SumAsync(sc => sc.QuantityMt ?? 0m));
    }

    [Theory]
    [InlineData("fa", "تخصیص محموله فقط باید از قراردادهای خرید باشد.")]
    [InlineData("en", "Shipment allocations must use purchase contracts.")]
    public async Task Create_Post_Rejects_Sale_Contract_Allocation(string language, string expectedError)
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var controller = BuildController(db, language);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "KALUGA",
            QuantityMt = 100m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 3, QuantityMt = 100m }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState["ContractAllocations[0].ContractId"]!.Errors,
            error => error.ErrorMessage == expectedError);
        Assert.Empty(await db.ShipmentContracts.ToListAsync());
        Assert.Empty(await db.InventoryTransportLegs.ToListAsync());
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Theory]
    [InlineData("fa", "محموله‌ای با این کد قبلاً ثبت شده است.")]
    [InlineData("en", "A shipment with this code already exists.")]
    public async Task Create_Post_Rejects_Duplicate_Shipment_Code(string language, string expectedError)
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        db.Shipments.Add(new Shipment { ShipmentCode = "KALUGA", QuantityMt = 1m });
        await db.SaveChangesAsync();
        var controller = BuildController(db, language);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = " kaluga ",
            QuantityMt = 2740m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 2740m }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState["ShipmentCode"]!.Errors,
            error => error.ErrorMessage == expectedError);
        Assert.Empty(await db.ShipmentContracts.ToListAsync());
        Assert.Empty(await db.InventoryTransportLegs.ToListAsync());
        Assert.Single(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Single_Contract_Defaults_Primary_To_First_Allocation()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var controller = BuildController(db);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "SINGLE",
            QuantityMt = 2740m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 2740m }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);
        var shipment = await db.Shipments.SingleAsync();
        // PrimaryContractId خالی → اولین تخصیص.
        Assert.Equal(1, shipment.ContractId);
        Assert.Single(await db.ShipmentContracts.ToListAsync());
    }

    [Theory]
    [InlineData("fa", "این قرارداد خرید قبلاً در ردیف دیگری اضافه شده است.")]
    [InlineData("en", "This purchase contract is already linked in another row.")]
    public async Task Create_Post_Rejects_Duplicate_Contract_Allocation(string language, string expectedError)
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var controller = BuildController(db, language);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "DUP",
            QuantityMt = 200m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 100m },
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 100m }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState["ContractAllocations[1].ContractId"]!.Errors,
            error => error.ErrorMessage == expectedError);
        Assert.Empty(await db.ShipmentContracts.ToListAsync());
        Assert.Empty(await db.InventoryTransportLegs.ToListAsync());
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Does_Not_Follow_NonLocal_ReturnUrl()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        var controller = BuildController(db);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "RU",
            QuantityMt = 2740m,
            ReturnUrl = "https://evil.example.com/steal",
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 2740m }
            ]
        });

        // مقصد خارجی دنبال نمی‌شود؛ به صفحهٔ پروندهٔ محموله می‌رود.
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("ShipmentPnl", redirect.ControllerName);
        Assert.Equal("Details", redirect.ActionName);
    }

    [Fact]
    public async Task Create_Post_With_Tank_Pick_Creates_Loaded_Leg_And_Deducts_Stock()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedTankStockAsync(db, contractId: 1, productId: 1, terminalId: 1, tankId: 1, quantityMt: 2740m);
        var controller = BuildController(db);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "TANKLOAD",
            QuantityMt = 2740m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput
                {
                    ContractId = 1,
                    StorageTankId = 1,
                    QuantityMt = 1000m
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);

        var shipment = await db.Shipments.SingleAsync();
        var leg = await db.InventoryTransportLegs.SingleAsync();
        Assert.Equal(shipment.Id, leg.ShipmentId);
        Assert.Equal(1, leg.SourcePurchaseContractId);
        Assert.Equal(1, leg.SourceStorageTankId);
        Assert.Equal(1000m, shipment.QuantityMt);
        Assert.Equal(1000m, leg.QuantityMt);
        Assert.Equal(InventoryTransportLegStatus.Loaded, leg.Status);
        Assert.NotNull(leg.OutboundInventoryMovementId);

        var outMovement = await db.InventoryMovements.SingleAsync(m => m.Direction == MovementDirection.Out);
        Assert.Equal(1000m, outMovement.QuantityMt);
        Assert.Equal(1, outMovement.StorageTankId);

        // موجودی باقی‌مانده دقیقاً به اندازهٔ کسر کم شده.
        var stock = new StockService(db);
        var remaining = await stock.GetFreeQuantityMtAsync(1, terminalId: 1, contractId: 1, storageTankId: 1);
        Assert.Equal(1740m, remaining);
    }

    [Fact]
    public async Task Create_Post_Rejects_Tank_Pick_Above_Tank_Stock()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedTankStockAsync(db, contractId: 1, productId: 1, terminalId: 1, tankId: 1, quantityMt: 500m);
        var controller = BuildController(db);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "OVERTANK",
            QuantityMt = 2740m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 2740m }
            ],
            TankPicks =
            [
                new ShipmentTankPickInput { ContractId = 1, StorageTankId = 1, QuantityMt = 1000m }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.Shipments.ToListAsync());
        Assert.Empty(await db.InventoryTransportLegs.ToListAsync());
        Assert.Empty(await db.InventoryMovements.Where(m => m.Direction == MovementDirection.Out).ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Rejects_Tank_Pick_Above_Contract_Remaining()
    {
        await using var db = CreateDb();
        await SeedReferenceDataAsync(db);
        await SeedTankStockAsync(db, contractId: 1, productId: 1, terminalId: 1, tankId: 1, quantityMt: 5000m);
        var controller = BuildController(db);

        var result = await controller.Create(new ShipmentCreateViewModel
        {
            ShipmentCode = "OVERCONTRACT",
            QuantityMt = 2740m,
            ContractAllocations =
            [
                new ShipmentContractAllocationInput { ContractId = 1, QuantityMt = 2740m }
            ],
            TankPicks =
            [
                // موجودی مخزن کافی است ولی بیش از مقدار این قرارداد در محموله.
                new ShipmentTankPickInput { ContractId = 1, StorageTankId = 1, QuantityMt = 3000m }
            ]
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.Shipments.ToListAsync());
        Assert.Empty(await db.InventoryTransportLegs.ToListAsync());
    }

    private static async Task SeedTankStockAsync(
        ApplicationDbContext db,
        int contractId,
        int productId,
        int terminalId,
        int tankId,
        decimal quantityMt)
    {
        if (!await db.Terminals.AnyAsync(t => t.Id == terminalId))
        {
            db.Terminals.Add(new Terminal { Id = terminalId, Code = $"T{terminalId}", Name = $"Terminal {terminalId}", IsActive = true });
        }

        db.StorageTanks.Add(new StorageTank
        {
            Id = tankId,
            TerminalId = terminalId,
            TankCode = $"TK{tankId}",
            ProductId = productId,
            CapacityMt = 100000m,
            IsActive = true
        });

        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = productId,
            ContractId = contractId,
            TerminalId = terminalId,
            StorageTankId = tankId,
            Direction = MovementDirection.In,
            MovementDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            QuantityMt = quantityMt,
            ReferenceDocument = "SEED-IN"
        });

        await db.SaveChangesAsync();
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static ShipmentsController BuildController(ApplicationDbContext db, string? language = null)
    {
        var context = new DefaultHttpContext();
        if (language is not null)
            context.Request.Headers.Cookie = $"ptg-ui-lang={language}";
        return new ShipmentsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new InMemoryTempDataProvider())
        };
    }

    private static async Task SeedReferenceDataAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Suppliers.AddRange(
            new Supplier { Id = 1, Name = "Petrogas", IsActive = true },
            new Supplier { Id = 2, Name = "BNK-HOMUR", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "PMS", Name = "Petrol", IsActive = true });
        db.Vessels.Add(new Vessel { Id = 1, Name = "KALUGA", IsActive = true });
        db.Locations.AddRange(
            new Location { Id = 1, Name = "Ilinka", IsActive = true },
            new Location { Id = 2, Name = "Third Country Port", IsActive = true });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 1,
                ContractNumber = "PETROGAS-001",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 5, 1),
                QuantityMt = 2740m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 570m
            },
            new Contract
            {
                Id = 2,
                ContractNumber = "BNK-001",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 2,
                ProductId = 1,
                ContractDate = new DateTime(2026, 5, 2),
                QuantityMt = 1404.504m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 600.73m
            },
            new Contract
            {
                Id = 3,
                ContractNumber = "SALE-001",
                ContractType = ContractType.Sale,
                CompanyId = 1,
                CustomerId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 5, 3),
                QuantityMt = 100m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 763m
            });
        await db.SaveChangesAsync();
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
