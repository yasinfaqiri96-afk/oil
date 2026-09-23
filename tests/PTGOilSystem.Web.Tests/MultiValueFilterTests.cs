using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// قرارداد فیلتر چندانتخابی: بین مقادیرِ یک فیلتر OR و بین فیلترهای مختلف AND.
/// صفحهٔ قراردادها نمایندهٔ همهٔ صفحه‌های لیستی است؛ همان الگو در بقیه تکرار شده است.
/// </summary>
public class MultiValueFilterTests
{
    [Fact]
    public async Task Index_Status_Filter_Is_Or_Between_Selected_Values()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var controller = BuildController(db);

        var result = await controller.Index(null, null, [ContractStatus.Draft, ContractStatus.Closed]);

        var model = Assert.IsType<ContractIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(
            new List<string> { "PUR-DRAFT", "SAL-CLOSED" },
            model.Items.Select(c => c.ContractNumber).OrderBy(n => n).ToList());
    }

    [Fact]
    public async Task Index_Different_Filters_Stay_And()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var controller = BuildController(db);

        // نوع = خرید  AND  وضعیت ∈ {جدید، بسته‌شده}
        var result = await controller.Index(null, [ContractType.Purchase], [ContractStatus.Draft, ContractStatus.Closed]);

        var model = Assert.IsType<ContractIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("PUR-DRAFT", Assert.Single(model.Items).ContractNumber);
    }

    [Fact]
    public async Task Index_Without_Values_Returns_Everything()
    {
        await using var db = NewDb();
        SeedContracts(db);
        var controller = BuildController(db);

        var result = await controller.Index(null, [], []);

        var model = Assert.IsType<ContractIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(3, model.Items.Count);
    }

    [Fact]
    public void MultiSelect_Definition_Keeps_Every_Applied_Value()
    {
        var definition = AkFilterDefinition.MultiSelect(
            "status",
            "وضعیت",
            [new AkFilterOption("Draft", "جدید"), new AkFilterOption("Closed", "بسته‌شده")],
            new[] { ContractStatus.Draft, ContractStatus.Closed });

        Assert.True(definition.Multiple);
        Assert.Equal(new List<string> { "Draft", "Closed" }, definition.AppliedValues.ToList());
    }

    [Fact]
    public void Single_Value_Definition_Still_Reports_Its_Value()
    {
        var definition = new AkFilterDefinition("status", "وضعیت", "select", null, "Active");

        Assert.False(definition.Multiple);
        Assert.Equal(new List<string> { "Active" }, definition.AppliedValues.ToList());
    }

    [Fact]
    public void Query_Values_Reads_Every_Repeat_Of_The_Same_Param()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?contractId=1&contractId=2&contractId=&q=x");

        Assert.Equal(new List<string> { "1", "2" }, AkFilterQuery.Values(context, "contractId").ToList());
        Assert.Empty(AkFilterQuery.Values(context, "productId"));
    }

    [Fact]
    public void Only_Returns_The_Value_For_A_Single_Selection()
    {
        Assert.Equal(7, new[] { 7 }.Only());
        Assert.Null(new[] { 7, 8 }.Only());
        Assert.Null(Array.Empty<int>().Only());
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ContractsController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static void SeedContracts(ApplicationDbContext db)
    {
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, UnitOfMeasure = "MT", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", IsActive = true });

        db.Contracts.AddRange(
            NewContract(1, "PUR-DRAFT", ContractType.Purchase, ContractStatus.Draft),
            NewContract(2, "PUR-ACTIVE", ContractType.Purchase, ContractStatus.Active),
            NewContract(3, "SAL-CLOSED", ContractType.Sale, ContractStatus.Closed));
        db.SaveChanges();
    }

    private static Contract NewContract(int id, string number, ContractType type, ContractStatus status)
        => new()
        {
            Id = id,
            ContractName = number,
            ContractNumber = number,
            ContractType = type,
            Status = status,
            CompanyId = 1,
            ProductId = 1,
            UnitId = 1,
            SupplierId = type == ContractType.Purchase ? 1 : null,
            CustomerId = type == ContractType.Sale ? 1 : null,
            ContractDate = new DateTime(2026, 4, 23),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            Currency = "USD",
            UnitPriceInCurrency = 450m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 450m
        };

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
