using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Customs;
using PTGOilSystem.Web.Models.Entities;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class CustomsDeclarationsControllerTests
{
    [Fact]
    public async Task Create_Get_Preselects_TransportLeg_Source()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db);
        SeedTransportLeg(db);
        db.CashAccounts.Add(new CashAccount
        {
            Id = 20,
            Code = "CASH-USD",
            Name = "Main Cash",
            Currency = "USD",
            IsActive = true
        });
        db.ServiceProviders.AddRange(
            new ServiceProvider
            {
                Id = 20,
                Name = "General Logistics Company",
                ProviderType = ServiceProviderType.TransportCompany,
                IsActive = true
            },
            new ServiceProvider
            {
                Id = 21,
                Name = "Customs Broker",
                ProviderType = ServiceProviderType.CustomsBroker,
                IsActive = true
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(transportLegId: 10, returnUrl: "/InventoryTransportLegs/Details/10");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CustomsDeclarationCreateViewModel>(view.Model);
        Assert.Null(model.LoadingRegisterId);
        Assert.Equal(10, model.TransportLegId);
        Assert.Equal("PUR-001", model.ContractNumber);
        Assert.Equal("Gas Oil", model.ProductName);
        Assert.Equal("WGN-10", model.WagonOrTruckNumber);
        Assert.Equal(19.5m, model.ConsignmentWeightMt);
        Assert.Equal("/InventoryTransportLegs/Details/10", model.ReturnUrl);

        var cashAccounts = Assert.IsType<SelectList>(controller.ViewData["CashAccounts"]);
        Assert.Contains(cashAccounts, item => item.Value == "20" && item.Text.Contains("Main Cash"));

        var serviceProviders = Assert.IsType<SelectList>(controller.ViewData["CustomsBrokers"]);
        Assert.Contains(serviceProviders, item => item.Value == "20" && item.Text == "General Logistics Company");
        Assert.Contains(serviceProviders, item => item.Value == "21" && item.Text == "Customs Broker");
    }

    [Fact]
    public async Task Create_Post_With_TransportLeg_Persists_CustomsDeclaration_Source()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db);
        SeedTransportLeg(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new CustomsDeclarationCreateViewModel
        {
            TransportLegId = 10,
            DeclarationDate = new DateTime(2026, 5, 3),
            WagonOrTruckNumber = "WGN-10",
            ConsignmentWeightMt = 19.5m,
            Items =
            [
                new CustomsDeclarationItemRowViewModel
                {
                    ComponentType = CustomsComponentType.Mahsooli,
                    ComponentLabel = "Mahsooli",
                    Currency = "AFN",
                    Amount = 70_000m,
                    Rate = 70m
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var declaration = await db.CustomsDeclarations.Include(cd => cd.Items).SingleAsync();
        Assert.Null(declaration.LoadingRegisterId);
        Assert.Equal(10, declaration.TransportLegId);
        Assert.Equal(70_000m, declaration.TotalAfn);
        // معادل USD از روی نرخ ۷۰ حساب می‌شود: 70,000 / 70 = 1,000.
        Assert.Equal(1_000m, declaration.TotalUsd);
        var item = Assert.Single(declaration.Items);
        Assert.Equal(70_000m, item.AmountAfn);
        Assert.Equal(1_000m, item.AmountUsd);
    }

    [Fact]
    public async Task Create_Post_Stores_Consistent_Equivalents_For_Mixed_Currency_Rows()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db);
        SeedTransportLeg(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // مثالِ سند: محصولی ۷۰۰۰۰ AFN با نرخ ۷۰ ⇒ ۱۰۰۰ USD؛ فواید ۲۰۰ USD با نرخ ۷۰ ⇒ ۱۴۰۰۰ AFN.
        var result = await controller.Create(new CustomsDeclarationCreateViewModel
        {
            TransportLegId = 10,
            DeclarationDate = new DateTime(2026, 5, 3),
            WagonOrTruckNumber = "WGN-10",
            Items =
            [
                new CustomsDeclarationItemRowViewModel
                {
                    ComponentType = CustomsComponentType.Mahsooli,
                    Currency = "AFN",
                    Amount = 70_000m,
                    Rate = 70m
                },
                new CustomsDeclarationItemRowViewModel
                {
                    ComponentType = CustomsComponentType.FawaidAama,
                    Currency = "USD",
                    Amount = 200m,
                    Rate = 70m
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(result);

        var declaration = await db.CustomsDeclarations.Include(cd => cd.Items).SingleAsync();
        // کل گمرکی = ۸۴۰۰۰ AFN یا ۱۲۰۰ USD (نه ۱۵۴۰۰۰).
        Assert.Equal(84_000m, declaration.TotalAfn);
        Assert.Equal(1_200m, declaration.TotalUsd);

        var mahsooli = declaration.Items.Single(i => i.ComponentType == CustomsComponentType.Mahsooli);
        Assert.Equal(70_000m, mahsooli.AmountAfn);
        Assert.Equal(1_000m, mahsooli.AmountUsd);

        var fawaid = declaration.Items.Single(i => i.ComponentType == CustomsComponentType.FawaidAama);
        Assert.Equal(14_000m, fawaid.AmountAfn);
        Assert.Equal(200m, fawaid.AmountUsd);
    }

    [Fact]
    public async Task Edit_Get_Reconstructs_Rows_With_Currency_And_Rate()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db);
        SeedTransportLeg(db);
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 5,
            TransportLegId = 10,
            DeclarationDate = new DateTime(2026, 5, 3),
            WagonOrTruckNumber = "WGN-10",
            TotalAfn = 84_000m,
            TotalUsd = 1_200m,
            Items =
            [
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.Mahsooli, AmountAfn = 70_000m, AmountUsd = 1_000m },
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.FawaidAama, AmountAfn = 14_000m, AmountUsd = 200m }
            ]
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Edit(5, returnUrl: "/CustomsDeclarations/Details/5");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Create", view.ViewName);
        var model = Assert.IsType<CustomsDeclarationCreateViewModel>(view.Model);
        Assert.Equal(5, model.Id);
        Assert.Equal(2, model.Items.Count);

        var mahsooli = model.Items.Single(i => i.ComponentType == CustomsComponentType.Mahsooli);
        Assert.Equal("AFN", mahsooli.Currency);
        Assert.Equal(70_000m, mahsooli.Amount);
        Assert.Equal(70m, mahsooli.Rate); // 70,000 / 1,000
    }

    [Fact]
    public async Task Edit_Post_Updates_Declaration_And_Replaces_Items()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db);
        SeedTransportLeg(db);
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 5,
            TransportLegId = 10,
            DeclarationDate = new DateTime(2026, 5, 3),
            WagonOrTruckNumber = "WGN-10",
            TotalAfn = 70_000m,
            TotalUsd = 1_000m,
            Items =
            [
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.Mahsooli, AmountAfn = 70_000m, AmountUsd = 1_000m }
            ]
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Edit(new CustomsDeclarationCreateViewModel
        {
            Id = 5,
            DeclarationDate = new DateTime(2026, 5, 4),
            WagonOrTruckNumber = "WGN-10-FIXED",
            Items =
            [
                new CustomsDeclarationItemRowViewModel
                {
                    ComponentType = CustomsComponentType.Mahsooli,
                    Currency = "AFN",
                    Amount = 35_000m,
                    Rate = 70m
                }
            ]
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var declaration = await db.CustomsDeclarations.Include(cd => cd.Items).SingleAsync();
        Assert.Equal("WGN-10-FIXED", declaration.WagonOrTruckNumber);
        Assert.Equal(35_000m, declaration.TotalAfn);
        Assert.Equal(500m, declaration.TotalUsd); // 35,000 / 70
        var item = Assert.Single(declaration.Items);
        Assert.Equal(35_000m, item.AmountAfn);
        Assert.Equal(500m, item.AmountUsd);
        // منبع نباید تغییر کند.
        Assert.Equal(10, declaration.TransportLegId);
    }

    [Fact]
    public async Task Create_Post_Rejects_Both_LoadingRegister_And_TransportLeg()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db);
        SeedLoadingRegister(db);
        SeedTransportLeg(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new CustomsDeclarationCreateViewModel
        {
            LoadingRegisterId = 20,
            TransportLegId = 10,
            DeclarationDate = new DateTime(2026, 5, 3)
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.CustomsDeclarations.ToListAsync());
    }

    [Fact]
    public async Task Create_Post_Rejects_Missing_Source()
    {
        await using var db = CreateDb();
        SeedReferenceData(db);
        SeedPurchaseContract(db);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Create(new CustomsDeclarationCreateViewModel
        {
            DeclarationDate = new DateTime(2026, 5, 3)
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.CustomsDeclarations.ToListAsync());
    }

    [Fact]
    public async Task PermitTurnover_Uses_Mahsooli_Not_TotalAfn_For_Tax()
    {
        await using var db = CreateDb();
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            DeclarationDate = new DateTime(2026, 3, 1),
            WagonOrTruckNumber = "V-1",
            DeclarationReference = "ACCD-1",
            ConsignmentWeightMt = 20m,
            TotalAfn = 100_000m, // total customs (all components)
            Items =
            [
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.Mahsooli, AmountAfn = 70_000m },
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.FawaidAama, AmountAfn = 30_000m }
            ]
        });
        await db.SaveChangesAsync();

        var controller = new CustomsPermitTurnoverController(db);
        var result = await controller.Index(taxPercent: 2m);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CustomsPermitTurnoverViewModel>(view.Model);

        var row = Assert.Single(model.Rows);
        Assert.Equal(70_000m, row.MahsooliAfn);
        Assert.Equal(100_000m, row.TotalCustomsAfn);

        Assert.Equal(70_000m, model.TotalMahsooliAfn);
        Assert.Equal(100_000m, model.TotalCustomsAfn);
        // Tax is computed from Mahsooli only: 70,000 × 2% = 1,400
        Assert.Equal(1_400m, model.EstimatedTaxAfn);
    }

    [Fact]
    public async Task PermitTurnover_Declaration_Without_Mahsooli_Shows_Zero()
    {
        await using var db = CreateDb();
        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            DeclarationDate = new DateTime(2026, 3, 2),
            WagonOrTruckNumber = "V-2",
            TotalAfn = 40_000m,
            Items =
            [
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.FawaidAama, AmountAfn = 25_000m },
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.KhatAhan, AmountAfn = 15_000m }
            ]
        });
        await db.SaveChangesAsync();

        var controller = new CustomsPermitTurnoverController(db);
        var result = await controller.Index(taxPercent: 5m);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CustomsPermitTurnoverViewModel>(view.Model);

        var row = Assert.Single(model.Rows);
        Assert.Equal(0m, row.MahsooliAfn);
        Assert.Equal(40_000m, row.TotalCustomsAfn);
        Assert.Equal(0m, model.TotalMahsooliAfn);
        Assert.Equal(0m, model.EstimatedTaxAfn);
    }

    [Fact]
    public async Task PermitTurnover_ItemLevelAfnEquivalent_DoesNotDoubleCount()
    {
        await using var db = CreateDb();

        // USD/AFN rate for the declaration date
        db.DailyFxRates.Add(new DailyFxRate
        {
            Id = 1,
            BaseCurrency = "USD",
            QuoteCurrency = "AFN",
            RateDate = new DateTime(2026, 6, 1),
            Rate = 70m
        });

        db.CustomsDeclarations.Add(new CustomsDeclaration
        {
            Id = 1,
            DeclarationDate = new DateTime(2026, 6, 1),
            WagonOrTruckNumber = "V-1",
            DeclarationReference = "ACCD-1",
            ConsignmentWeightMt = 20m,
            // Items: Mahsooli has both AFN and USD (AFN-native), NormStandard is USD-only
            Items =
            [
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.Mahsooli, AmountAfn = 70_000m, AmountUsd = 1_000m },
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.NormStandard, AmountAfn = 0m, AmountUsd = 200m }
            ]
        });

        await db.SaveChangesAsync();

        var controller = new CustomsPermitTurnoverController(db);
        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CustomsPermitTurnoverViewModel>(view.Model);

        var row = Assert.Single(model.Rows);
        Assert.Equal(70_000m, row.MahsooliAfn);
        Assert.Equal(1_000m, row.MahsooliUsd);

        // کل گمرکی به‌صورت معادلِ یک‌ارزی: AFN = 70k + (200×70=14k) = 84k؛ USD = 1000 + 200 = 1200.
        // هیچ ردیفی دوبار (AFN و USD) شمرده نمی‌شود؛ پس نه ۱۵۴۰۰۰.
        Assert.Equal(84_000m, row.TotalCustomsAfn);
        Assert.Equal(1_200m, row.TotalCustomsUsd);
        Assert.Equal(84_000m, row.TotalCustomsAfnEquivalent);
        Assert.Equal(84_000m, model.TotalCustomsAfn);
        Assert.Equal(1_200m, model.TotalCustomsUsd);
        Assert.Equal(84_000m, model.TotalCustomsAfnEquivalent);
    }

    [Fact]
    public async Task UploadDocument_Saves_Valid_File()
    {
        await using var db = CreateDb();
        db.CustomsDeclarations.Add(new CustomsDeclaration { Id = 1, DeclarationDate = new DateTime(2026, 3, 1), TotalAfn = 0m });
        await db.SaveChangesAsync();

        var webRoot = Path.Combine(Path.GetTempPath(), "ptg-tests", Guid.NewGuid().ToString("N"));
        var controller = BuildController(db, new FakeWebHostEnvironment { WebRootPath = webRoot });
        try
        {
            var file = BuildFile("receipt.pdf", "application/pdf", 2048);
            var result = await controller.UploadDocument(1, file, "سند پرداخت", "رسید بانک");

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Details", redirect.ActionName);

            var doc = await db.CustomsDeclarationDocuments.SingleAsync();
            Assert.Equal(1, doc.CustomsDeclarationId);
            Assert.Equal("receipt.pdf", doc.OriginalFileName);
            Assert.Equal("application/pdf", doc.ContentType);
            Assert.Equal(2048, doc.FileSizeBytes);
            Assert.Equal("سند پرداخت", doc.DocumentType);
            Assert.StartsWith("/uploads/customs-declarations/1/", doc.FilePath);

            var absolute = Path.Combine(webRoot, doc.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(absolute));
        }
        finally
        {
            if (Directory.Exists(webRoot))
            {
                Directory.Delete(webRoot, true);
            }
        }
    }

    [Fact]
    public async Task UploadDocument_Rejects_Disallowed_Extension()
    {
        await using var db = CreateDb();
        db.CustomsDeclarations.Add(new CustomsDeclaration { Id = 1, DeclarationDate = new DateTime(2026, 3, 1), TotalAfn = 0m });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var file = BuildFile("malware.exe", "application/octet-stream", 1024);

        var result = await controller.UploadDocument(1, file, null, null);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(await db.CustomsDeclarationDocuments.ToListAsync());
    }

    [Fact]
    public async Task UploadDocument_Rejects_Oversized_File()
    {
        await using var db = CreateDb();
        db.CustomsDeclarations.Add(new CustomsDeclaration { Id = 1, DeclarationDate = new DateTime(2026, 3, 1), TotalAfn = 0m });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var file = BuildFile("big.pdf", "application/pdf", (10 * 1024 * 1024) + 1);

        var result = await controller.UploadDocument(1, file, null, null);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(await db.CustomsDeclarationDocuments.ToListAsync());
    }

    [Fact]
    public void Details_View_Has_Documents_Section()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PTGOilSystem.Web", "Views", "CustomsDeclarations", "Details.cshtml"));

        var content = File.ReadAllText(viewPath);

        Assert.Contains("اسناد گمرکی این موتر", content);
        Assert.Contains("asp-action=\"UploadDocument\"", content);
        Assert.Contains("asp-action=\"DownloadDocument\"", content);
        Assert.Contains("asp-action=\"DeleteDocument\"", content);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static CustomsDeclarationsController BuildController(ApplicationDbContext db, IWebHostEnvironment? environment = null)
        => new(
            db,
            NullLogger<CustomsDeclarationsController>.Instance,
            environment ?? new FakeWebHostEnvironment
            {
                WebRootPath = Path.Combine(Path.GetTempPath(), "ptg-tests", Guid.NewGuid().ToString("N"))
            })
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider()),
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static IUrlHelper BuildUrlHelper()
        => new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));

    private static IFormFile BuildFile(string fileName, string contentType, int sizeBytes)
    {
        var stream = new MemoryStream(new byte[sizeBytes]);
        return new FormFile(stream, 0, sizeBytes, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
    }

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
    }

    private static void SeedPurchaseContract(ApplicationDbContext db)
    {
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
    }

    private static void SeedLoadingRegister(ApplicationDbContext db)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = 20,
            ContractId = 1,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 5, 1),
            LoadedQuantityMt = 20m,
            WagonNumber = "LR-WGN"
        });
    }

    private static void SeedTransportLeg(ApplicationDbContext db)
    {
        db.InventoryTransportLegs.Add(new InventoryTransportLeg
        {
            Id = 10,
            SourcePurchaseContractId = 1,
            ProductId = 1,
            SourceTerminalId = 1,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = "WGN-10",
            RwbNo = "RWB-10",
            LoadedDate = new DateTime(2026, 5, 2),
            QuantityMt = 20m,
            ChargeableQuantityMt = 19.5m,
            Status = InventoryTransportLegStatus.Loaded
        });
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
