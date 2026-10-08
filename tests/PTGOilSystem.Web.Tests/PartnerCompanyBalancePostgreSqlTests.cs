using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "AccountingIntegration")]
public sealed class PartnerCompanyBalancePostgreSqlTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Historical_Profit_And_Partial_Customer_Custody_Execute_On_PostgreSql()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var code = Guid.NewGuid().ToString("N")[..8];
        var date = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var company = new Company { Code = $"MC-{code}", Name = "MASHAL test" };
        var product = new Product { Code = $"MP-{code}", Name = "Test oil" };
        var supplier = new Supplier { Name = $"Supplier {code}" };
        var customer = new Customer { Name = $"Customer {code}" };
        var owner = new Partner { Code = $"MO-{code}", Name = "Book owner" };
        var external = new Partner { Code = $"ME-{code}", Name = "External partner" };
        db.AddRange(company, product, supplier, customer, owner, external);
        await db.SaveChangesAsync();
        company.OwnerPartnerId = owner.Id;
        var contract = new Contract { ContractNumber = $"MASHAL-{code}", CompanyId = company.Id,
            ProductId = product.Id, SupplierId = supplier.Id, ContractDate = date,
            ContractType = ContractType.Purchase, OwnershipType = ContractOwnershipType.Partnership,
            QuantityMt = 100m, PricingMethod = PricingMethod.Fixed, UnitPriceUsd = 900m,
            SaleProceedsHolderPartnerId = external.Id };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = contract.Id, PartnerId = owner.Id, SharePercent = 50m, EffectiveFrom = date },
            new ContractPartner { ContractId = contract.Id, PartnerId = external.Id, SharePercent = 50m, EffectiveFrom = date });
        db.LoadingRegisters.Add(new LoadingRegister { ContractId = contract.Id, ProductId = product.Id,
            LoadingDate = date, LoadedQuantityMt = 100m, LoadingPriceUsd = 900m });
        var sale = new SalesTransaction { CompanyId = company.Id, CustomerId = customer.Id, ProductId = product.Id,
            InvoiceNumber = $"MS-{code}", SaleDate = date, QuantityMt = 50m, UnitPriceUsd = 1000m,
            TotalUsd = 50_000m, Currency = "USD", TotalInCurrency = 50_000m, AppliedFxRateToUsd = 1m,
            SourcePurchaseContractId = contract.Id };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();
        db.PaymentTransactions.Add(new PaymentTransaction { PaymentDate = date, ContractId = contract.Id,
            SalesTransactionId = sale.Id, CustomerId = customer.Id, PaymentKind = PaymentKind.CustomerReceipt,
            Direction = PaymentDirection.In, FundingSource = PaymentFundingSource.Partner, PaidByPartnerId = external.Id,
            Amount = 10_000m, AmountUsd = 10_000m, Currency = "USD", AppliedFxRateToUsd = 1m });
        await db.SaveChangesAsync();
        var reader = new PartnerCompanyBalanceReader(db);
        Assert.Empty(await reader.ReadEventsAsync(date.AddDays(-1), contract.Id));
        var events = await reader.ReadEventsAsync(date, contract.Id);
        Assert.Equal(-7_500m, events.Where(e => e.PartnerId == external.Id).Sum(e => e.EffectUsd));
        Assert.Contains(owner.Id, await reader.BookOwnerIdsAsync());
        Assert.Single(events, e => e.PartnerId == external.Id && e.Kind == PartnershipStatementLineKind.SaleProceedsHeld);
        // All rows live only in the disposable fixture DB and this transaction is rolled back.
    }
}
