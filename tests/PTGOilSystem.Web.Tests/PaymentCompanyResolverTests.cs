using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// The resolver must agree with the Stage 3 backfill: prove the company or return nothing.
/// A wrong company here would post a journal against the wrong books, so every unprovable
/// shape must stay unresolved.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class PaymentCompanyResolverTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Prefers_The_Payments_Own_Company_Over_Every_Relation()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var otherCompanyId = await AddCompanyAsync(db);

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.CompanyId = otherCompanyId;
            p.ContractId = scope.Contract.Id;
        });

        Assert.Equal(otherCompanyId, await Resolve(db, payment));
    }

    [Fact]
    public async Task Falls_Back_To_The_Payments_Contract()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p => p.ContractId = scope.Contract.Id);

        Assert.Equal(scope.Company.Id, await Resolve(db, payment));
    }

    [Fact]
    public async Task Falls_Back_To_The_Linked_Sales_Explicit_Company()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var sale = new SalesTransaction
        {
            CompanyId = scope.Company.Id,
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            InvoiceNumber = PaymentAccountingAdapterTests.Unique("INV"),
            SaleDate = new DateTime(2026, 7, 10),
            QuantityMt = 10m,
            Currency = "USD",
            UnitPriceInCurrency = 100m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 100m,
            TotalInCurrency = 1_000m,
            TotalUsd = 1_000m
        };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.ContractId = null;
            p.SalesTransactionId = sale.Id;
        });

        Assert.Equal(scope.Company.Id, await Resolve(db, payment));
    }

    [Fact]
    public async Task Returns_Nothing_When_The_Payment_Has_No_Provable_Relation()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p => p.ContractId = null);

        Assert.Null(await Resolve(db, payment));
    }

    [Fact]
    public async Task Returns_Nothing_For_A_Multi_Company_Shipment()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var otherCompanyId = await AddCompanyAsync(db);

        var foreignContract = new Contract
        {
            ContractNumber = PaymentAccountingAdapterTests.Unique("CN"),
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = otherCompanyId,
            ProductId = scope.Product.Id,
            SupplierId = scope.Supplier.Id,
            ContractDate = new DateTime(2026, 7, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 100m,
            Currency = "USD",
            SettlementCurrencyCode = "USD"
        };
        db.Contracts.Add(foreignContract);
        await db.SaveChangesAsync();

        var shipment = new Shipment { ShipmentCode = PaymentAccountingAdapterTests.Unique("SH") };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();

        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = shipment.Id, ContractId = scope.Contract.Id },
            new ShipmentContract { ShipmentId = shipment.Id, ContractId = foreignContract.Id });
        await db.SaveChangesAsync();

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.ContractId = null;
            p.ShipmentId = shipment.Id;
        });

        Assert.Null(await Resolve(db, payment));
    }

    [Fact]
    public async Task Resolves_A_Single_Company_Shipment()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        var shipment = new Shipment { ShipmentCode = PaymentAccountingAdapterTests.Unique("SH") };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();

        db.ShipmentContracts.Add(
            new ShipmentContract { ShipmentId = shipment.Id, ContractId = scope.Contract.Id });
        await db.SaveChangesAsync();

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.ContractId = null;
            p.ShipmentId = shipment.Id;
        });

        Assert.Equal(scope.Company.Id, await Resolve(db, payment));
    }

    private static async Task<int?> Resolve(ApplicationDbContext db, PaymentTransaction payment)
        => await new PaymentCompanyResolver(db).ResolveAsync(payment);

    private static async Task<int> AddCompanyAsync(ApplicationDbContext db)
    {
        var company = new Company
        {
            Code = PaymentAccountingAdapterTests.Unique("C"),
            Name = PaymentAccountingAdapterTests.Unique("Company"),
            Country = "AF",
            IsActive = true
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static async Task<PaymentTransaction> AddPaymentAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        Action<PaymentTransaction> configure)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 7, 15),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = scope.CashAccount.Id,
            CustomerId = scope.Customer.Id,
            Amount = 100m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100m
        };
        configure(payment);

        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }
}
