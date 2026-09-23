using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سطرِ لجرِ «فروش» که CustomerId ندارد ولی ContractId قراردادِ خرید (منبع) را دارد — همان شکلی که
/// SaleLedgerFactory برای فروش‌های قدیمی/گروهی می‌سازد. مالکِ قطعیِ آن مشتریِ سند فروش است
/// (SalesTransaction.CustomerId الزامی است). نباید از راهِ قرارداد به تأمین‌کننده هم برسد، وگرنه یک
/// فروش در دو حساب شمرده می‌شود.
/// </summary>
public sealed class LegacySaleLedgerOwnershipTests
{
    [Fact]
    public async Task Sale_Row_Without_CustomerId_Belongs_Only_To_The_Sale_Customer_Never_To_The_Supplier()
    {
        await using var db = NewDb();
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GAS", Name = "Gasoline" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Contracts.Add(new Contract
        {
            Id = 5,
            ContractNumber = "P-005",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 100,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            InvoiceNumber = "GSALE-1",
            SaleDate = new DateTime(2026, 4, 22),
            QuantityMt = 10m,
            UnitPriceUsd = 600m,
            TotalUsd = 6_000m,
            TotalInCurrency = 6_000m,
            Currency = "USD"
        });
        db.LedgerEntries.AddRange(
            // بارگیری: بدهی 5000 به تأمین‌کننده.
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 4, 2),
                Side = LedgerSide.Credit,
                AmountUsd = 5_000m,
                SourceType = CompanyFlowSourceTypes.Loading,
                SourceId = 1,
                ContractId = 5,
                SupplierId = 1,
                Description = "Loading"
            },
            // سطرِ فروشِ قدیمی: بدون CustomerId، با ContractId قراردادِ خرید.
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 4, 22),
                Side = LedgerSide.Credit,
                AmountUsd = 6_000m,
                SourceType = LedgerEntryOwnership.SaleSourceType,
                SourceId = 100,
                ContractId = 5,
                Reference = "GSALE-1",
                Description = "Sale"
            });
        await db.SaveChangesAsync();

        var balances = await PartyBalanceReadService.CreateDefault(db).GetBalancesAsync(new ManagementReportFilterViewModel());
        var supplier = balances.Single(b => b.PartyType == PartyStatementPartyType.Supplier && b.PartyId == 1);
        var customer = balances.Single(b => b.PartyType == PartyStatementPartyType.Customer && b.PartyId == 1);
        var statements = PartyStatementReadService.CreateDefault(db);
        var supplierStatement = await statements.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, 1),
            new PartyStatementFilter { IncludeOperationalColumns = false });
        var customerStatement = await statements.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, 1),
            new PartyStatementFilter { IncludeOperationalColumns = false });

        // تأمین‌کننده فقط بارگیری را می‌بیند؛ فروش هرگز در حساب او نیست.
        Assert.Equal(-5_000m, supplier.ClosingBalanceUsd);
        Assert.Equal(-5_000m, supplierStatement.Summary.ClosingBalance);
        Assert.DoesNotContain(supplierStatement.Rows, r => r.SourceType == LedgerEntryOwnership.SaleSourceType);

        // مشتری، از راهِ سند فروش، همان 6000 را طلب دارد — یک بار.
        Assert.Equal(6_000m, customer.ClosingBalanceUsd);
        Assert.Equal(6_000m, customerStatement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task Supplier_Ownership_Rule_Never_Infers_A_Sale_Row_Through_The_Contract()
    {
        await using var db = NewDb();
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Contracts.Add(new Contract
        {
            Id = 5,
            ContractNumber = "P-005",
            ContractType = ContractType.Purchase,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 4, 1)
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 4, 22),
            Side = LedgerSide.Credit,
            AmountUsd = 6_000m,
            SourceType = LedgerEntryOwnership.SaleSourceType,
            SourceId = 100,
            ContractId = 5,
            Description = "Sale"
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.LedgerEntries.CountAsync(LedgerEntryOwnership.SupplierOwned(1)));
        Assert.Equal(0, await db.LedgerEntries.CountAsync(LedgerEntryOwnership.SupplierOwnedAny([1])));
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
