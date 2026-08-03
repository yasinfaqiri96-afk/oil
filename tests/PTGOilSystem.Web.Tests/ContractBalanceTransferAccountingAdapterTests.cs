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
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class ContractBalanceTransferAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime TransferDate = new(2026, 7, 15);

    [Fact]
    public void SourceEventId_Format_Is_Stable()
    {
        Assert.Equal(
            "ContractBalanceTransfer:42:Created",
            ContractBalanceTransferAccountingAdapter.BuildSourceEventId(42));
    }

    [Fact]
    public async Task Service_DualWrite_Posts_Balanced_Journal_With_Supplier_Party_Mapping()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, seedSourceBalanceUsd: 1000m);
        var service = CreateService(db, accountingEnabled: true, pilotEnabled: true);

        var transfer = await service.CreateAsync(NewRequest(scope, amountUsd: 250m));

        var legacyEntries = await db.LedgerEntries
            .AsNoTracking()
            .Where(x => x.SourceType == ContractBalanceTransferService.LedgerSourceType
                && x.SourceId == transfer.Id)
            .ToListAsync();
        Assert.Equal(2, legacyEntries.Count);

        var journal = await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceModule == ContractBalanceTransferAccountingAdapter.SourceModule
                && x.SourceEventId == ContractBalanceTransferAccountingAdapter.BuildSourceEventId(transfer.Id));

        Assert.Equal(JournalEntryStatus.Posted, journal.Status);
        Assert.Equal(scope.Company.Id, journal.CompanyId);
        Assert.Equal(2, journal.Lines.Count);

        var debitLine = journal.Lines.Single(x => x.Debit > 0m);
        var creditLine = journal.Lines.Single(x => x.Credit > 0m);
        var payableAccountId = scope.Settings.AccountsPayableAccountId;

        Assert.Equal(payableAccountId, debitLine.AccountId);
        Assert.Equal(payableAccountId, creditLine.AccountId);
        Assert.Equal(AccountingPartyType.Supplier, debitLine.PartyType);
        Assert.Equal(AccountingPartyType.Supplier, creditLine.PartyType);
        Assert.Equal(scope.Supplier.Id, debitLine.PartyId);
        Assert.Equal(scope.Supplier.Id, creditLine.PartyId);
        Assert.Equal(scope.FromContract.Id, debitLine.ContractId);
        Assert.Equal(scope.ToContract.Id, creditLine.ContractId);

        // Reconciliation: legacy ledger totals must equal journal totals.
        Assert.Equal(transfer.AmountUsd, debitLine.Debit);
        Assert.Equal(transfer.AmountUsd, creditLine.Credit);
        Assert.Equal(legacyEntries.Sum(x => x.AmountUsd) / 2m, journal.Lines.Sum(x => x.Debit));
        Assert.Equal(journal.Lines.Sum(x => x.Debit), journal.Lines.Sum(x => x.Credit));
    }

    [Fact]
    public async Task Adapter_Retry_Returns_Duplicate_And_Creates_No_Second_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, seedSourceBalanceUsd: 1000m);
        var service = CreateService(db, accountingEnabled: true, pilotEnabled: true);
        var transfer = await service.CreateAsync(NewRequest(scope, amountUsd: 100m));

        var adapter = CreateAdapter(db, accountingEnabled: true, pilotEnabled: true);
        var retry = await adapter.TryPostAsync(transfer, scope.FromContract, scope.ToContract);

        Assert.Equal(ContractBalanceTransferPostingStatus.Duplicate, retry.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", retry.Reason);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ContractBalanceTransferAccountingAdapter.BuildSourceEventId(transfer.Id)));
    }

    [Fact]
    public async Task Service_Keeps_Legacy_Path_When_Pilot_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, seedSourceBalanceUsd: 1000m);
        var service = CreateService(db, accountingEnabled: true, pilotEnabled: false);

        var transfer = await service.CreateAsync(NewRequest(scope, amountUsd: 100m));

        Assert.Equal(2, await db.LedgerEntries.CountAsync(
            x => x.SourceType == ContractBalanceTransferService.LedgerSourceType
                && x.SourceId == transfer.Id));
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == ContractBalanceTransferAccountingAdapter.BuildSourceEventId(transfer.Id)));
    }

    [Fact]
    public async Task Service_Rolls_Back_Transfer_And_Legacy_When_Posting_Fails_In_Closed_Period()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, seedSourceBalanceUsd: 1000m);
        scope.Period.Status = FiscalPeriodStatus.Closed;
        scope.Period.LockedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var service = CreateService(db, accountingEnabled: true, pilotEnabled: true);
        var error = await Assert.ThrowsAsync<AccountingValidationException>(
            () => service.CreateAsync(NewRequest(scope, amountUsd: 100m)));
        Assert.Equal("PERIOD_HARD_LOCKED", error.Code);

        await using var verifyDb = fixture.CreateDbContext();
        Assert.Equal(0, await verifyDb.ContractBalanceTransfers.CountAsync(
            x => x.FromContractId == scope.FromContract.Id));
        Assert.Equal(0, await verifyDb.LedgerEntries.CountAsync(
            x => x.SourceType == ContractBalanceTransferService.LedgerSourceType
                && x.ContractId == scope.FromContract.Id));
        Assert.Equal(0, await verifyDb.JournalEntries.CountAsync(
            x => x.CompanyId == scope.Company.Id
                && x.SourceModule == ContractBalanceTransferAccountingAdapter.SourceModule));
    }

    [Fact]
    public async Task Adapter_Skips_When_Accounting_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var adapter = CreateAdapter(db, accountingEnabled: false, pilotEnabled: true);

        var result = await adapter.TryPostAsync(
            NewTransfer(scope, amountUsd: 100m),
            scope.FromContract,
            scope.ToContract);

        Assert.Equal(ContractBalanceTransferPostingStatus.Skipped, result.Status);
        Assert.Equal("ACCOUNTING_DISABLED", result.Reason);
    }

    [Fact]
    public async Task Adapter_Skips_When_Pilot_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var adapter = CreateAdapter(db, accountingEnabled: true, pilotEnabled: false);

        var result = await adapter.TryPostAsync(
            NewTransfer(scope, amountUsd: 100m),
            scope.FromContract,
            scope.ToContract);

        Assert.Equal(ContractBalanceTransferPostingStatus.Skipped, result.Status);
        Assert.Equal("PILOT_DISABLED", result.Reason);
    }

    [Fact]
    public async Task Adapter_Skips_Non_Purchase_Contracts()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        scope.ToContract.ContractType = ContractType.Sale;

        await AssertSkipAsync(db, scope, "CONTRACT_TYPE_NOT_PURCHASE");
    }

    [Fact]
    public async Task Adapter_Skips_Cross_Company_Contracts()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        scope.ToContract.CompanyId = scope.Company.Id + 1;

        await AssertSkipAsync(db, scope, "COMPANY_MISMATCH");
    }

    [Fact]
    public async Task Adapter_Skips_Supplier_Mismatch()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        scope.ToContract.SupplierId = scope.Supplier.Id + 1;

        await AssertSkipAsync(db, scope, "SUPPLIER_MISMATCH");
    }

    [Fact]
    public async Task Adapter_Skips_Contract_Currency_Mismatch()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        scope.ToContract.Currency = "RUB";

        await AssertSkipAsync(db, scope, "CONTRACT_CURRENCY_MISMATCH");
    }

    [Fact]
    public async Task Adapter_Skips_Settlement_Currency_Mismatch()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        scope.ToContract.SettlementCurrencyCode = "RUB";

        await AssertSkipAsync(db, scope, "SETTLEMENT_CURRENCY_MISMATCH");
    }

    [Fact]
    public async Task Adapter_Skips_Non_Positive_Amount()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var adapter = CreateAdapter(db, accountingEnabled: true, pilotEnabled: true);

        var result = await adapter.TryPostAsync(
            NewTransfer(scope, amountUsd: 0m),
            scope.FromContract,
            scope.ToContract);

        Assert.Equal(ContractBalanceTransferPostingStatus.Skipped, result.Status);
        Assert.Equal("INVALID_TRANSFER_AMOUNT", result.Reason);
    }

    [Fact]
    public async Task Adapter_Skips_When_Accounting_Settings_Missing()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, seedAccountingSettings: false);

        await AssertSkipAsync(db, scope, "ACCOUNTING_SETTINGS_MISSING");
    }

    [Fact]
    public async Task Adapter_Skips_When_Accounts_Payable_Is_Inactive()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payable = await db.Accounts.SingleAsync(x => x.Id == scope.Settings.AccountsPayableAccountId);
        payable.IsActive = false;
        await db.SaveChangesAsync();

        await AssertSkipAsync(db, scope, "ACCOUNTING_SETTINGS_INVALID_ACCOUNTS");
    }

    private static async Task AssertSkipAsync(
        ApplicationDbContext db,
        AdapterScope scope,
        string expectedReason)
    {
        var adapter = CreateAdapter(db, accountingEnabled: true, pilotEnabled: true);
        var result = await adapter.TryPostAsync(
            NewTransfer(scope, amountUsd: 100m),
            scope.FromContract,
            scope.ToContract);

        Assert.Equal(ContractBalanceTransferPostingStatus.Skipped, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.CompanyId == scope.Company.Id
                && x.SourceModule == ContractBalanceTransferAccountingAdapter.SourceModule));
    }

    private static ContractBalanceTransferService CreateService(
        ApplicationDbContext db,
        bool accountingEnabled,
        bool pilotEnabled)
        => new(db, CreateAdapter(db, accountingEnabled, pilotEnabled));

    private static ContractBalanceTransferAccountingAdapter CreateAdapter(
        ApplicationDbContext db,
        bool accountingEnabled,
        bool pilotEnabled)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = accountingEnabled,
            Pilots = new AccountingPilotOptions { ContractBalanceTransfer = pilotEnabled }
        });
        return new ContractBalanceTransferAccountingAdapter(
            db,
            new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            options,
            NullLogger<ContractBalanceTransferAccountingAdapter>.Instance);
    }

    private static ContractBalanceTransferCreateRequest NewRequest(AdapterScope scope, decimal amountUsd)
        => new(
            TransferDate,
            scope.FromContract.Id,
            scope.ToContract.Id,
            amountUsd,
            "USD",
            1m,
            TransferDate,
            null,
            null,
            null,
            null,
            null);

    private static ContractBalanceTransfer NewTransfer(AdapterScope scope, decimal amountUsd)
        => new()
        {
            TransferDate = TransferDate,
            FromContractId = scope.FromContract.Id,
            ToContractId = scope.ToContract.Id,
            AmountOriginal = amountUsd,
            CurrencyCode = "USD",
            FxRateToUsd = 1m,
            AmountUsd = amountUsd
        };

    private static async Task<AdapterScope> CreateScopeAsync(
        ApplicationDbContext db,
        decimal seedSourceBalanceUsd = 0m,
        bool seedAccountingSettings = true)
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

        AccountingSettings? settings = null;
        if (seedAccountingSettings)
        {
            await new AccountingChartSeeder(
                db,
                Options.Create(new AccountingOptions { DefaultFunctionalCurrencyCode = "USD" })).SeedAsync();
            settings = await db.AccountingSettings.SingleAsync(x => x.CompanyId == company.Id);
        }

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
        await db.SaveChangesAsync();

        var fromContract = NewContract(company.Id, product.Id, supplier.Id);
        var toContract = NewContract(company.Id, product.Id, supplier.Id);
        db.Contracts.AddRange(fromContract, toContract);
        await db.SaveChangesAsync();

        if (seedSourceBalanceUsd > 0m)
        {
            db.LedgerEntries.Add(new LedgerEntry
            {
                EntryDate = new DateTime(2026, 7, 1),
                Side = LedgerSide.Credit,
                AmountUsd = seedSourceBalanceUsd,
                Currency = "USD",
                SourceAmount = seedSourceBalanceUsd,
                SourceCurrencyCode = "USD",
                AppliedFxRateToUsd = 1m,
                AppliedFxRateDate = new DateTime(2026, 7, 1),
                Description = "Test seed credit",
                SourceType = nameof(PaymentKind.SupplierPayment),
                SourceId = 9999,
                ContractId = fromContract.Id
            });
            await db.SaveChangesAsync();
        }

        return new AdapterScope(company, supplier, fromContract, toContract, period, settings!);
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

    private sealed record AdapterScope(
        Company Company,
        Supplier Supplier,
        Contract FromContract,
        Contract ToContract,
        FiscalPeriod Period,
        AccountingSettings Settings);
}
