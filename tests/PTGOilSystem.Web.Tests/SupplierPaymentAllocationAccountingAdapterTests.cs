using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

[Collection(AccountingPostgreSqlCollection.CollectionName)]
public sealed class SupplierPaymentAllocationAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime AllocationDate = new(2026, 7, 15);

    [Fact]
    public void SourceEventId_Formats_Are_Stable()
    {
        Assert.Equal(
            "SupplierPaymentAllocation:7:Created",
            SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(7));
        Assert.Equal(
            "SupplierPaymentAllocation:7:Reversed",
            SupplierPaymentAllocationAccountingAdapter.BuildReversedSourceEventId(7));
    }

    [Fact]
    public async Task Service_DualWrite_Posts_Prepayment_Journal_With_Correct_Mapping()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var service = CreateService(db, pilotEnabled: true);

        var allocation = await service.CreateAsync(NewRequest(scope, amount: 250m));

        var legacyEntries = await db.LedgerEntries
            .AsNoTracking()
            .Where(x => x.SourceType == SupplierPaymentAllocationService.LedgerSourceType
                && x.SourceId == allocation.Id)
            .ToListAsync();
        Assert.Equal(2, legacyEntries.Count);

        var journal = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceModule == SupplierPaymentAllocationAccountingAdapter.SourceModule
                && x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(allocation.Id));

        Assert.Equal(JournalEntryStatus.Posted, journal.Status);
        Assert.Equal(scope.Company.Id, journal.CompanyId);
        Assert.Equal(2, journal.Lines.Count);

        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);
        var prepaymentAccountId = scope.Settings.SupplierPrepaymentAccountId;

        Assert.Equal(prepaymentAccountId, debitLine.AccountId);
        Assert.Equal(prepaymentAccountId, creditLine.AccountId);
        Assert.Equal(AccountingPartyType.Supplier, debitLine.PartyType);
        Assert.Equal(AccountingPartyType.Supplier, creditLine.PartyType);
        Assert.Equal(scope.Supplier.Id, debitLine.PartyId);
        Assert.Equal(scope.Supplier.Id, creditLine.PartyId);
        Assert.Equal(scope.DestinationContract.Id, debitLine.ContractId);
        Assert.Null(creditLine.ContractId);

        // Reconciliation: legacy rows and journal carry the same USD book amount.
        Assert.Equal(allocation.AllocatedBookAmountUsd, debitLine.Debit);
        Assert.Equal(allocation.AllocatedBookAmountUsd, creditLine.Credit);
        Assert.All(legacyEntries, x => Assert.Equal(allocation.AllocatedBookAmountUsd, x.AmountUsd));
        Assert.Equal(journal.Lines.Sum(x => x.Debit), journal.Lines.Sum(x => x.Credit));
    }

    [Fact]
    public async Task Service_Keeps_Legacy_Path_When_Pilot_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var service = CreateService(db, pilotEnabled: false);

        var allocation = await service.CreateAsync(NewRequest(scope, amount: 100m));

        Assert.Equal(2, await db.LedgerEntries.CountAsync(
            x => x.SourceType == SupplierPaymentAllocationService.LedgerSourceType
                && x.SourceId == allocation.Id));
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(allocation.Id)));
    }

    [Fact]
    public async Task Adapter_Skips_Free_Prepayment_Because_Company_Is_Not_Provable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, paymentBoundToContract: false);
        var service = CreateService(db, pilotEnabled: true);

        var allocation = await service.CreateAsync(NewRequest(scope, amount: 100m));

        // Legacy continues; no journal because company ownership is unknown pre-Stage-3.
        Assert.Equal(2, await db.LedgerEntries.CountAsync(
            x => x.SourceType == SupplierPaymentAllocationService.LedgerSourceType
                && x.SourceId == allocation.Id));
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(allocation.Id)));

        var adapter = CreateAdapter(db, pilotEnabled: true);
        var result = await adapter.TryPostAllocationAsync(allocation, scope.Payment, scope.DestinationContract);
        Assert.Equal(SupplierPaymentAllocationPostingStatus.Skipped, result.Status);
        Assert.Equal("PAYMENT_COMPANY_UNKNOWN", result.Reason);
    }

    [Fact]
    public async Task Adapter_Skips_When_Payment_Contract_Company_Differs()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);

        var otherCompany = new Company
        {
            Code = Unique("C"),
            Name = Unique("Company"),
            Country = "AF",
            IsActive = true
        };
        db.Companies.Add(otherCompany);
        await db.SaveChangesAsync();
        var foreignContract = NewContract(otherCompany.Id, scope.Product.Id, scope.Supplier.Id);
        db.Contracts.Add(foreignContract);
        await db.SaveChangesAsync();
        scope.Payment.ContractId = foreignContract.Id;
        await db.SaveChangesAsync();

        var adapter = CreateAdapter(db, pilotEnabled: true);
        var result = await adapter.TryPostAllocationAsync(
            NewUnsavedAllocation(scope, amount: 100m),
            scope.Payment,
            scope.DestinationContract);

        Assert.Equal(SupplierPaymentAllocationPostingStatus.Skipped, result.Status);
        Assert.Equal("COMPANY_MISMATCH", result.Reason);
    }

    [Fact]
    public async Task Adapter_Retry_Returns_Duplicate_And_Creates_No_Second_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var service = CreateService(db, pilotEnabled: true);
        var allocation = await service.CreateAsync(NewRequest(scope, amount: 100m));

        var adapter = CreateAdapter(db, pilotEnabled: true);
        var retry = await adapter.TryPostAllocationAsync(allocation, scope.Payment, scope.DestinationContract);

        Assert.Equal(SupplierPaymentAllocationPostingStatus.Duplicate, retry.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(allocation.Id)));
    }

    [Fact]
    public async Task Reversal_Posts_Official_Reversal_Journal_And_Keeps_Original()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var service = CreateService(db, pilotEnabled: true);
        var allocation = await service.CreateAsync(NewRequest(scope, amount: 100m));

        await service.ReverseAsync(new SupplierPaymentAllocationReverseRequest(
            allocation.Id, "test reversal", "tester"));

        var original = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(allocation.Id));
        var reversal = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildReversedSourceEventId(allocation.Id));

        Assert.Equal(JournalEntryStatus.Posted, original.Status);
        Assert.True(reversal.IsReversal);
        Assert.Equal(original.Id, reversal.ReversalOfJournalEntryId);
        Assert.Equal(original.Lines.Sum(x => x.Debit), reversal.Lines.Sum(x => x.Credit));
        Assert.Equal(original.Lines.Sum(x => x.Credit), reversal.Lines.Sum(x => x.Debit));

        Assert.Equal(2, await db.LedgerEntries.CountAsync(
            x => x.SourceType == SupplierPaymentAllocationService.ReversalLedgerSourceType
                && x.SourceId == allocation.Id));
    }

    [Fact]
    public async Task Reversal_Retry_Returns_Duplicate()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var service = CreateService(db, pilotEnabled: true);
        var allocation = await service.CreateAsync(NewRequest(scope, amount: 100m));
        await service.ReverseAsync(new SupplierPaymentAllocationReverseRequest(
            allocation.Id, "test reversal", "tester"));

        var adapter = CreateAdapter(db, pilotEnabled: true);
        var retry = await adapter.TryPostReversalAsync(allocation, scope.Payment, scope.DestinationContract);

        Assert.Equal(SupplierPaymentAllocationPostingStatus.Duplicate, retry.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildReversedSourceEventId(allocation.Id)));
    }

    [Fact]
    public async Task Legacy_Second_Reversal_Is_Still_Rejected()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var service = CreateService(db, pilotEnabled: true);
        var allocation = await service.CreateAsync(NewRequest(scope, amount: 100m));
        await service.ReverseAsync(new SupplierPaymentAllocationReverseRequest(
            allocation.Id, "first", "tester"));

        await Assert.ThrowsAsync<PTGOilSystem.Web.Services.Exceptions.BusinessRuleException>(
            () => service.ReverseAsync(new SupplierPaymentAllocationReverseRequest(
                allocation.Id, "second", "tester")));
    }

    [Fact]
    public async Task Reversal_Skips_When_Original_Journal_Was_Never_Posted()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var legacyOnlyService = CreateService(db, pilotEnabled: false);
        var allocation = await legacyOnlyService.CreateAsync(NewRequest(scope, amount: 100m));

        var pilotService = CreateService(db, pilotEnabled: true);
        await pilotService.ReverseAsync(new SupplierPaymentAllocationReverseRequest(
            allocation.Id, "test reversal", "tester"));

        // Legacy reversal proceeded; no reversal journal because no original journal exists.
        Assert.Equal(2, await db.LedgerEntries.CountAsync(
            x => x.SourceType == SupplierPaymentAllocationService.ReversalLedgerSourceType
                && x.SourceId == allocation.Id));
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildReversedSourceEventId(allocation.Id)));
    }

    [Fact]
    public async Task Service_Rolls_Back_Allocation_And_Legacy_When_Posting_Fails_In_Closed_Period()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        scope.Period.Status = FiscalPeriodStatus.Closed;
        scope.Period.LockedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var service = CreateService(db, pilotEnabled: true);
        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => service.CreateAsync(NewRequest(scope, amount: 100m)));
        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);

        await using var verifyDb = fixture.CreateDbContext();
        Assert.Equal(0, await verifyDb.SupplierPaymentAllocations.CountAsync(
            x => x.PaymentTransactionId == scope.Payment.Id));
        Assert.Equal(0, await verifyDb.LedgerEntries.CountAsync(
            x => x.SourceType == SupplierPaymentAllocationService.LedgerSourceType
                && x.SupplierId == scope.Supplier.Id));
        Assert.Equal(0, await verifyDb.JournalEntries.CountAsync(
            x => x.CompanyId == scope.Company.Id
                && x.SourceModule == SupplierPaymentAllocationAccountingAdapter.SourceModule));
    }

    [Fact]
    public async Task NonUsd_Payment_Posts_With_Exact_Transaction_Currency_Data()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, paymentCurrency: "RUB", paymentFxRateToUsd: 0.0125m);
        var service = CreateService(db, pilotEnabled: true);

        // 8000 RUB × 0.0125 = 100.0000 USD book amount.
        var allocation = await service.CreateAsync(NewRequest(scope, amount: 8000m));
        Assert.Equal(100m, allocation.AllocatedBookAmountUsd);

        var journal = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(allocation.Id));
        Assert.All(journal.Lines, line =>
        {
            Assert.Equal("RUB", line.TransactionCurrencyCode);
            Assert.Equal(8000m, line.TransactionAmount);
            Assert.Equal(0.0125m, line.ExchangeRate);
        });
        Assert.Equal(100m, journal.Lines.Sum(x => x.Debit));
    }

    // بازارزیابی با نرخ روز تخصیص: دفتر رسمی هم باید سه‌سطری و متوازن بماند.
    [Fact]
    public async Task Revalued_Allocation_Posts_Balanced_Journal_With_Exchange_Line()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, paymentCurrency: "RUB", paymentFxRateToUsd: 0.0125m);
        var service = CreateService(db, pilotEnabled: true);

        // 8000 RUB: ارزش تاریخی 100 USD (۱$=۸۰)، ارزش روز تخصیص 80 USD (۱$=۱۰۰) → زیان 20 USD.
        var allocation = await service.CreateAsync(new SupplierPaymentAllocationCreateRequest(
            scope.Payment.Id, scope.DestinationContract.Id, AllocationDate, 8000m, 1m, null, null, "tester", 100m));

        Assert.Equal(100m, allocation.AllocatedBookAmountUsd);
        Assert.Equal(80m, allocation.AllocatedValueUsdAtAllocation);
        Assert.Equal(-20m, allocation.ExchangeDifferenceUsd);

        var journal = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceEventId == SupplierPaymentAllocationAccountingAdapter.BuildCreatedSourceEventId(allocation.Id));

        Assert.Equal(3, journal.Lines.Count);
        Assert.Equal(journal.Lines.Sum(x => x.Credit), journal.Lines.Sum(x => x.Debit));
        Assert.Equal(100m, journal.Lines.Sum(x => x.Credit));

        // سطر زیان تسعیر: دلاری، نرخ ۱، بدون طرف‌حساب.
        var exchangeLine = Assert.Single(journal.Lines, x => x.TransactionCurrencyCode == "USD");
        Assert.Equal(20m, exchangeLine.Debit);
        Assert.Equal(1m, exchangeLine.ExchangeRate);
        Assert.Null(exchangeLine.PartyId);
    }

    private static SupplierPaymentAllocationService CreateService(
        ApplicationDbContext db,
        bool pilotEnabled)
        => new(db, CreateAdapter(db, pilotEnabled));

    private static SupplierPaymentAllocationAccountingAdapter CreateAdapter(
        ApplicationDbContext db,
        bool pilotEnabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { SupplierPaymentAllocation = pilotEnabled }
        });
        return new SupplierPaymentAllocationAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            options,
            NullLogger<SupplierPaymentAllocationAccountingAdapter>.Instance);
    }

    private static SupplierPaymentAllocationCreateRequest NewRequest(AllocationScope scope, decimal amount)
        => new(
            scope.Payment.Id,
            scope.DestinationContract.Id,
            AllocationDate,
            amount,
            1m,
            null,
            null,
            "tester");

    private static SupplierPaymentAllocation NewUnsavedAllocation(AllocationScope scope, decimal amount)
        => new()
        {
            PaymentTransactionId = scope.Payment.Id,
            ContractId = scope.DestinationContract.Id,
            AllocationDate = AllocationDate,
            AllocatedPaymentAmount = amount,
            PaymentCurrencyCode = scope.Payment.Currency,
            PaymentFxRateToUsd = scope.Payment.AppliedFxRateToUsd ?? 1m,
            AllocatedBookAmountUsd = decimal.Round(
                amount * (scope.Payment.AppliedFxRateToUsd ?? 1m), 4, MidpointRounding.AwayFromZero),
            // بدون بازارزیابی: نرخ روز تخصیص همان نرخ روز پرداخت است، پس اختلاف تسعیر صفر می‌ماند.
            PaymentCurrencyFxRateToUsdAtAllocation = scope.Payment.AppliedFxRateToUsd ?? 1m,
            PaymentCurrencyPerUsdRateAtAllocation = decimal.Round(
                1m / (scope.Payment.AppliedFxRateToUsd ?? 1m), 6, MidpointRounding.AwayFromZero),
            AllocatedValueUsdAtAllocation = decimal.Round(
                amount * (scope.Payment.AppliedFxRateToUsd ?? 1m), 4, MidpointRounding.AwayFromZero)
        };

    private static async Task<AllocationScope> CreateScopeAsync(
        ApplicationDbContext db,
        bool paymentBoundToContract = true,
        string paymentCurrency = "USD",
        decimal? paymentFxRateToUsd = null)
    {
        var company = new Company
        {
            Code = Unique("C"),
            Name = Unique("Company"),
            Country = "AF",
            IsActive = true,
            IsSystemOwner = true
        };
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Companies\" SET \"IsSystemOwner\" = FALSE WHERE \"IsSystemOwner\" = TRUE");
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        await new AccountingChartSeeder(
            db,
            Options.Create(new AccountingOptions { DefaultFunctionalCurrencyCode = "USD" })).SeedAsync();
        var settings = await db.AccountingSettings.SingleAsync(x => x.CompanyId == company.Id);

        var year = new FiscalYear
        {
            CompanyId = company.Id,
            Name = Unique("FY"),
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 12, 31),
            Status = FiscalYearStatus.Open,
            IsCurrent = true
        };
        db.FiscalYears.Add(year);
        await db.SaveChangesAsync();

        var period = new FiscalPeriod
        {
            CompanyId = company.Id,
            FiscalYearId = year.Id,
            PeriodNumber = 7,
            Name = "July 2026",
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 31),
            Status = FiscalPeriodStatus.Open
        };
        db.FiscalPeriods.Add(period);

        var product = new Product
        {
            Code = Unique("P"),
            Name = Unique("Product"),
            UnitOfMeasure = "MT",
            IsActive = true
        };
        db.Products.Add(product);

        var supplier = new Supplier
        {
            Code = Unique("S"),
            Name = Unique("Supplier"),
            IsActive = true
        };
        db.Suppliers.Add(supplier);

        var cashAccount = new CashAccount
        {
            Code = Unique("CA"),
            Name = Unique("Cash"),
            AccountType = CashAccountType.Bank,
            Currency = paymentCurrency,
            IsActive = true
        };
        db.CashAccounts.Add(cashAccount);

        // The posting service validates transaction currencies against the Currencies
        // table when it is non-empty, so make sure the payment currency is registered.
        if (!await db.Currencies.AnyAsync(x => x.Code == paymentCurrency && x.IsActive))
        {
            db.Currencies.Add(new Currency { Code = paymentCurrency, Name = paymentCurrency, IsActive = true });
        }

        await db.SaveChangesAsync();

        var sourceContract = NewContract(company.Id, product.Id, supplier.Id);
        var destinationContract = NewContract(company.Id, product.Id, supplier.Id);
        db.Contracts.AddRange(sourceContract, destinationContract);
        await db.SaveChangesAsync();

        var fxRate = paymentFxRateToUsd ?? 1m;
        var paymentAmount = 1_000_000m;
        var payment = new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 7, 1),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = cashAccount.Id,
            SupplierId = supplier.Id,
            ContractId = paymentBoundToContract ? sourceContract.Id : null,
            Amount = paymentAmount,
            Currency = paymentCurrency,
            AppliedFxRateToUsd = fxRate,
            AmountUsd = decimal.Round(paymentAmount * fxRate, 4, MidpointRounding.AwayFromZero)
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        return new AllocationScope(
            company, supplier, product, sourceContract, destinationContract, payment, period, settings);
    }

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
            Currency = "USD",
            SettlementCurrencyCode = "USD"
        };

    private static string Unique(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, prefix.Length + 33)];

    private sealed record AllocationScope(
        Company Company,
        Supplier Supplier,
        Product Product,
        Contract SourceContract,
        Contract DestinationContract,
        PaymentTransaction Payment,
        FiscalPeriod Period,
        AccountingSettings Settings);
}
