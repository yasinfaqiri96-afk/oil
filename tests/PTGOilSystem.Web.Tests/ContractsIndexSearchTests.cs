using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// فهرست قراردادها دیگر شرکت و شرکای قرارداد را Include نمی‌کند (در صفحه رندر نمی‌شوند).
/// جست‌وجو اما هنوز باید روی نام شریک، تأمین‌کننده، مشتری و جنس کار کند؛ این تست همان را
/// قفل می‌کند تا حذف Include به‌اشتباه دامنهٔ جست‌وجو را کوتاه نکند.
/// </summary>
public sealed class ContractsIndexSearchTests
{
    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static void Seed(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, UnitOfMeasure = "MT", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier Alpha", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer Beta", IsActive = true });
        db.Partners.Add(new Partner { Id = 1, Name = "Partner Gamma", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractName = "قرارداد خرید آزمایشی",
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            Currency = "USD",
            UnitPriceInCurrency = 450m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 450m
        });
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractName = "قرارداد فروش آزمایشی",
            ContractNumber = "SAL-001",
            ContractType = ContractType.Sale,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            CustomerId = 1,
            ContractDate = new DateTime(2026, 4, 24),
            QuantityMt = 50m,
            PricingMethod = PricingMethod.Fixed,
            Currency = "USD",
            UnitPriceInCurrency = 500m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 500m
        });
        db.ContractPartners.Add(new ContractPartner
        {
            Id = 1,
            ContractId = 1,
            PartnerId = 1,
            SharePercent = 50m
        });
        db.SaveChanges();
    }

    private static PTGOilSystem.Web.Controllers.ContractsController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static async Task<ContractIndexViewModel> SearchAsync(ApplicationDbContext db, string term)
    {
        var result = await BuildController(db).Index(term, null, null);
        var view = Assert.IsType<ViewResult>(result);
        return Assert.IsType<ContractIndexViewModel>(view.Model);
    }

    [Fact]
    public async Task Search_Still_Finds_A_Contract_By_Its_Partner_Name()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db);

        var model = await SearchAsync(db, "Gamma");

        Assert.Single(model.Items);
        Assert.Equal("PUR-001", model.Items[0].ContractNumber);
    }

    [Fact]
    public async Task Search_Still_Finds_A_Contract_By_Supplier_Customer_And_Product()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db);

        var bySupplier = await SearchAsync(db, "Alpha");
        Assert.Single(bySupplier.Items);
        Assert.Equal("PUR-001", bySupplier.Items[0].ContractNumber);

        var byCustomer = await SearchAsync(db, "Beta");
        Assert.Single(byCustomer.Items);
        Assert.Equal("SAL-001", byCustomer.Items[0].ContractNumber);

        // جنس روی هر دو قرارداد یکی است، پس هر دو باید بیایند.
        var byProduct = await SearchAsync(db, "Gas Oil");
        Assert.Equal(2, byProduct.Items.Count);
    }

    [Fact]
    public async Task The_List_Still_Carries_Everything_The_Page_Renders()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        Seed(db);

        var model = await SearchAsync(db, "PUR-001");

        var contract = Assert.Single(model.Items);
        Assert.Equal("قرارداد خرید آزمایشی — PUR-001", contract.DisplayLabel);
        Assert.Equal("Gas Oil", contract.Product?.Name);
        Assert.Equal("MT", contract.Unit?.Symbol);
        Assert.Equal("Supplier Alpha", contract.Supplier?.Name);
        Assert.Equal(100m, contract.QuantityMt);
    }
}
