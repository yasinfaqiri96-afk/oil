using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Stage 3 — provable company-ownership backfill for payments plus the
/// read-only ownership report. The backfill statements are the exact ones the
/// migration executes (shared via PaymentCompanyBackfillSql), replayed here on
/// seeded legacy-shaped rows because the fixture database is empty when the
/// migration itself runs.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class PaymentCompanyOwnershipTests(AccountingPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Backfill_Assigns_Provable_Companies_And_Leaves_Ambiguous_Null()
    {
        await using var db = fixture.CreateDbContext();
        var seed = await SeedAsync(db);

        foreach (var statement in PaymentCompanyBackfillSql.Statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }

        var payments = await db.PaymentTransactions
            .AsNoTracking()
            .Where(x => seed.AllPaymentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        // Provable paths.
        Assert.Equal(seed.CompanyA.Id, payments[seed.ContractBoundPaymentId].CompanyId);
        Assert.Equal(seed.CompanyA.Id, payments[seed.SalesCompanyPaymentId].CompanyId);
        Assert.Equal(seed.CompanyA.Id, payments[seed.SalesContractPaymentId].CompanyId);
        Assert.Equal(seed.CompanyA.Id, payments[seed.ExpenseContractPaymentId].CompanyId);
        Assert.Equal(seed.CompanyA.Id, payments[seed.SingleCompanyShipmentPaymentId].CompanyId);
        Assert.Equal(seed.CompanyA.Id, payments[seed.ExpenseShipmentPaymentId].CompanyId);

        // Ambiguous paths stay NULL — never guessed.
        Assert.Null(payments[seed.FreePaymentId].CompanyId);
        Assert.Null(payments[seed.MixedShipmentPaymentId].CompanyId);
        Assert.Null(payments[seed.SarrafOnlyPaymentId].CompanyId);
    }

    [Fact]
    public async Task Backfill_Is_Idempotent_And_Never_Overwrites()
    {
        await using var db = fixture.CreateDbContext();
        var seed = await SeedAsync(db);

        foreach (var statement in PaymentCompanyBackfillSql.Statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }

        // Manually flip one payment to Company B, then re-run: it must stay B.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"PaymentTransactions\" SET \"CompanyId\" = {0} WHERE \"Id\" = {1}",
            seed.CompanyB.Id,
            seed.ContractBoundPaymentId);

        foreach (var statement in PaymentCompanyBackfillSql.Statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }

        var payment = await db.PaymentTransactions
            .AsNoTracking()
            .SingleAsync(x => x.Id == seed.ContractBoundPaymentId);
        Assert.Equal(seed.CompanyB.Id, payment.CompanyId);
    }

    [Fact]
    public async Task Ownership_Report_Counts_Resolved_And_Ambiguous_Records()
    {
        await using var db = fixture.CreateDbContext();
        var seed = await SeedAsync(db);
        foreach (var statement in PaymentCompanyBackfillSql.Statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }

        var report = await new CompanyOwnershipReportService(db).BuildAsync();

        Assert.True(report.TotalPayments >= seed.AllPaymentIds.Count);
        Assert.True(report.PaymentsWithoutCompany >= 3);
        Assert.Equal(report.TotalPayments, report.PaymentsWithCompany + report.PaymentsWithoutCompany);
        Assert.Contains(report.AmbiguousPaymentsByKind, x => x.PaymentKind == PaymentKind.SupplierPayment);
        Assert.Equal(
            report.TotalCashAccounts,
            report.CashAccountsWithCompany + report.CashAccountsWithoutCompany);
    }

    private static async Task<OwnershipSeed> SeedAsync(ApplicationDbContext db)
    {
        var companyA = NewCompany();
        var companyB = NewCompany();
        db.Companies.AddRange(companyA, companyB);

        var product = new Product
        {
            Code = Unique("P"),
            Name = Unique("Product"),
            UnitOfMeasure = "MT",
            IsActive = true
        };
        db.Products.Add(product);

        var supplier = new Supplier { Code = Unique("S"), Name = Unique("Supplier"), IsActive = true };
        db.Suppliers.Add(supplier);
        var customer = new Customer { Code = Unique("CU"), Name = Unique("Customer"), IsActive = true };
        db.Customers.Add(customer);
        var sarraf = new Sarraf { Name = Unique("Sarraf"), IsActive = true };
        db.Sarrafs.Add(sarraf);

        var cashAccount = new CashAccount
        {
            Code = Unique("CA"),
            Name = Unique("Cash"),
            AccountType = CashAccountType.Bank,
            Currency = "USD",
            IsActive = true
        };
        db.CashAccounts.Add(cashAccount);
        await db.SaveChangesAsync();

        var contractA = NewContract(companyA.Id, product.Id, supplier.Id);
        var contractA2 = NewContract(companyA.Id, product.Id, supplier.Id);
        var contractB = NewContract(companyB.Id, product.Id, supplier.Id);
        db.Contracts.AddRange(contractA, contractA2, contractB);
        await db.SaveChangesAsync();

        // Shipment whose contracts all belong to company A.
        var singleCompanyShipment = new Shipment { ShipmentCode = Unique("SH"), QuantityMt = 100m };
        // Shipment with contracts from two companies → ambiguous.
        var mixedShipment = new Shipment { ShipmentCode = Unique("SH"), QuantityMt = 100m };
        db.Shipments.AddRange(singleCompanyShipment, mixedShipment);
        await db.SaveChangesAsync();

        db.ShipmentContracts.AddRange(
            new ShipmentContract { ShipmentId = singleCompanyShipment.Id, ContractId = contractA.Id },
            new ShipmentContract { ShipmentId = singleCompanyShipment.Id, ContractId = contractA2.Id },
            new ShipmentContract { ShipmentId = mixedShipment.Id, ContractId = contractA.Id },
            new ShipmentContract { ShipmentId = mixedShipment.Id, ContractId = contractB.Id });

        var salesWithCompany = NewSale(customer.Id, product.Id);
        salesWithCompany.CompanyId = companyA.Id;
        var salesWithContract = NewSale(customer.Id, product.Id);
        salesWithContract.ContractId = contractA.Id;
        db.SalesTransactions.AddRange(salesWithCompany, salesWithContract);

        var expenseType = new ExpenseType { Code = Unique("ET"), Name = Unique("ET"), IsActive = true };
        db.ExpenseTypes.Add(expenseType);
        await db.SaveChangesAsync();

        var expenseWithContract = NewExpense(expenseType.Id);
        expenseWithContract.ContractId = contractA.Id;
        var expenseWithShipment = NewExpense(expenseType.Id);
        expenseWithShipment.ShipmentId = singleCompanyShipment.Id;
        db.ExpenseTransactions.AddRange(expenseWithContract, expenseWithShipment);
        await db.SaveChangesAsync();

        var contractBound = NewPayment(cashAccount.Id, supplier.Id);
        contractBound.ContractId = contractA.Id;

        var salesCompany = NewPayment(cashAccount.Id, supplier.Id);
        salesCompany.SalesTransactionId = salesWithCompany.Id;

        var salesContract = NewPayment(cashAccount.Id, supplier.Id);
        salesContract.SalesTransactionId = salesWithContract.Id;

        var expenseContract = NewPayment(cashAccount.Id, supplier.Id);
        expenseContract.ExpenseTransactionId = expenseWithContract.Id;

        var singleShipment = NewPayment(cashAccount.Id, supplier.Id);
        singleShipment.ShipmentId = singleCompanyShipment.Id;

        var expenseShipment = NewPayment(cashAccount.Id, supplier.Id);
        expenseShipment.ExpenseTransactionId = expenseWithShipment.Id;

        var free = NewPayment(cashAccount.Id, supplier.Id);

        var mixedShipmentPayment = NewPayment(cashAccount.Id, supplier.Id);
        mixedShipmentPayment.ShipmentId = mixedShipment.Id;

        var sarrafOnly = NewPayment(cashAccount.Id, supplierId: null);
        sarrafOnly.SarrafId = sarraf.Id;
        sarrafOnly.PaymentKind = PaymentKind.SupplierPayment;

        db.PaymentTransactions.AddRange(
            contractBound, salesCompany, salesContract, expenseContract,
            singleShipment, expenseShipment, free, mixedShipmentPayment, sarrafOnly);
        await db.SaveChangesAsync();

        return new OwnershipSeed(
            companyA,
            companyB,
            contractBound.Id,
            salesCompany.Id,
            salesContract.Id,
            expenseContract.Id,
            singleShipment.Id,
            expenseShipment.Id,
            free.Id,
            mixedShipmentPayment.Id,
            sarrafOnly.Id,
            [
                contractBound.Id, salesCompany.Id, salesContract.Id, expenseContract.Id,
                singleShipment.Id, expenseShipment.Id, free.Id, mixedShipmentPayment.Id, sarrafOnly.Id
            ]);
    }

    private static Company NewCompany()
        => new()
        {
            Code = Unique("C"),
            Name = Unique("Company"),
            Country = "AF",
            IsActive = true
        };

    private static Contract NewContract(int companyId, int productId, int supplierId)
        => new()
        {
            ContractNumber = Unique("CN"),
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = companyId,
            ProductId = productId,
            SupplierId = supplierId,
            ContractDate = new DateTime(2026, 7, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 100m,
            Currency = "USD"
        };

    private static SalesTransaction NewSale(int customerId, int productId)
        => new()
        {
            CustomerId = customerId,
            ProductId = productId,
            InvoiceNumber = Unique("INV"),
            SaleDate = new DateTime(2026, 7, 1),
            QuantityMt = 10m,
            Currency = "USD",
            UnitPriceInCurrency = 1m,
            UnitPriceUsd = 1m,
            TotalInCurrency = 10m,
            TotalUsd = 10m
        };

    private static ExpenseTransaction NewExpense(int expenseTypeId)
        => new()
        {
            ExpenseTypeId = expenseTypeId,
            ExpenseDate = new DateTime(2026, 7, 1),
            Amount = 5m,
            Currency = "USD",
            AmountUsd = 5m
        };

    private static PaymentTransaction NewPayment(int cashAccountId, int? supplierId)
        => new()
        {
            PaymentDate = new DateTime(2026, 7, 1),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = cashAccountId,
            SupplierId = supplierId,
            Amount = 100m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100m
        };

    private static string Unique(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, prefix.Length + 33)];

    private sealed record OwnershipSeed(
        Company CompanyA,
        Company CompanyB,
        int ContractBoundPaymentId,
        int SalesCompanyPaymentId,
        int SalesContractPaymentId,
        int ExpenseContractPaymentId,
        int SingleCompanyShipmentPaymentId,
        int ExpenseShipmentPaymentId,
        int FreePaymentId,
        int MixedShipmentPaymentId,
        int SarrafOnlyPaymentId,
        IReadOnlyList<int> AllPaymentIds);
}
