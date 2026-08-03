using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// Stage 4 — receipts and payments. Every mapping is asserted against a real PostgreSQL
/// journal, and every skip path is asserted to leave the legacy row untouched.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class PaymentAccountingAdapterTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime PaymentDate = new(2026, 7, 15);

    [Fact]
    public void SourceEventId_Format_Is_Stable()
        => Assert.Equal("Payment:7:Created", PaymentAccountingAdapter.BuildCreatedSourceEventId(7));

    [Theory]
    [InlineData(PaymentKind.CustomerReceipt, null, null, PaymentAccountingEventKind.CustomerReceipt)]
    [InlineData(PaymentKind.CustomerReceipt, null, true, PaymentAccountingEventKind.CustomerAdvance)]
    [InlineData(PaymentKind.CustomerReceipt, null, false, PaymentAccountingEventKind.CustomerReceipt)]
    [InlineData(PaymentKind.SupplierPayment, null, null, PaymentAccountingEventKind.SupplierPayment)]
    [InlineData(PaymentKind.SupplierPayment, true, null, PaymentAccountingEventKind.SupplierPrepayment)]
    [InlineData(PaymentKind.SupplierPayment, false, null, PaymentAccountingEventKind.SupplierPayment)]
    [InlineData(PaymentKind.SarrafSettlement, null, null, PaymentAccountingEventKind.SarrafCashPayment)]
    [InlineData(PaymentKind.ExpensePayment, null, null, PaymentAccountingEventKind.ExpensePayment)]
    [InlineData(PaymentKind.CommissionPayment, null, null, PaymentAccountingEventKind.CommissionPayment)]
    public void ResolveEventKind_Uses_Payment_Nature_Not_LedgerSide(
        PaymentKind paymentKind,
        bool? isAdvancePayment,
        bool? isCustomerAdvance,
        PaymentAccountingEventKind expected)
    {
        var payment = new PaymentTransaction
        {
            PaymentKind = paymentKind,
            IsAdvancePayment = isAdvancePayment,
            IsCustomerAdvance = isCustomerAdvance
        };

        Assert.Equal(expected, PaymentAccountingAdapter.ResolveEventKind(payment));
    }

    [Theory]
    [InlineData(PaymentKind.ManualPayment)]
    [InlineData(PaymentKind.ManualReceipt)]
    [InlineData(PaymentKind.TruckPayment)]
    [InlineData(PaymentKind.EmployeeSalaryPayment)]
    [InlineData(PaymentKind.EmployeeSalaryAdvance)]
    [InlineData(PaymentKind.EmployeeReturn)]
    [InlineData(PaymentKind.SupplierReceipt)]
    [InlineData(PaymentKind.CustomerPayment)]
    [InlineData(PaymentKind.ServiceProviderPayment)]
    public void ResolveEventKind_Leaves_Unproven_Kinds_Unmapped(PaymentKind paymentKind)
        => Assert.Null(PaymentAccountingAdapter.ResolveEventKind(
            new PaymentTransaction { PaymentKind = paymentKind }));

    [Fact]
    public async Task CustomerReceipt_Debits_Cash_And_Credits_Receivable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(PaymentAccountingEventKind.CustomerReceipt, result.EventKind);

        var journal = await LoadJournalAsync(db, payment.Id);
        var (cashLine, partyLine) = SplitLines(journal, scope.Settings.CashBankControlAccountId);

        Assert.Equal(payment.AmountUsd, cashLine.Debit);
        Assert.Equal(scope.CashAccount.Id, cashLine.CashAccountId);
        Assert.Equal(scope.Settings.AccountsReceivableAccountId, partyLine.AccountId);
        Assert.Equal(payment.AmountUsd, partyLine.Credit);
        Assert.Equal(AccountingPartyType.Customer, partyLine.PartyType);
        Assert.Equal(scope.Customer.Id, partyLine.PartyId);
        AssertBalanced(journal, payment.AmountUsd);
    }

    [Fact]
    public async Task CustomerAdvance_Credits_Customer_Advance_Instead_Of_Receivable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
            p.IsCustomerAdvance = true;
        });

        var result = await CreateAdapter(db, PilotsFor(customerAdvance: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(PaymentAccountingEventKind.CustomerAdvance, result.EventKind);

        var journal = await LoadJournalAsync(db, payment.Id);
        var (cashLine, partyLine) = SplitLines(journal, scope.Settings.CashBankControlAccountId);

        Assert.Equal(payment.AmountUsd, cashLine.Debit);
        Assert.Equal(scope.Settings.CustomerAdvanceAccountId, partyLine.AccountId);
        Assert.Equal(payment.AmountUsd, partyLine.Credit);
        Assert.Equal(AccountingPartyType.Customer, partyLine.PartyType);
        AssertBalanced(journal, payment.AmountUsd);
    }

    [Fact]
    public async Task SupplierPayment_Debits_Payable_And_Credits_Cash()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.SupplierPayment;
            p.Direction = PaymentDirection.Out;
            p.SupplierId = scope.Supplier.Id;
            p.ContractId = scope.Contract.Id;
        });

        var result = await CreateAdapter(db, PilotsFor(supplierPayment: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, payment.Id);
        var (cashLine, partyLine) = SplitLines(journal, scope.Settings.CashBankControlAccountId);

        Assert.Equal(payment.AmountUsd, cashLine.Credit);
        Assert.Equal(scope.CashAccount.Id, cashLine.CashAccountId);
        Assert.Equal(scope.Settings.AccountsPayableAccountId, partyLine.AccountId);
        Assert.Equal(payment.AmountUsd, partyLine.Debit);
        Assert.Equal(AccountingPartyType.Supplier, partyLine.PartyType);
        Assert.Equal(scope.Supplier.Id, partyLine.PartyId);
        Assert.Equal(scope.Contract.Id, partyLine.ContractId);
        AssertBalanced(journal, payment.AmountUsd);
    }

    [Fact]
    public async Task SupplierPrepayment_Debits_Prepayment_Instead_Of_Payable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.SupplierPayment;
            p.Direction = PaymentDirection.Out;
            p.SupplierId = scope.Supplier.Id;
            p.ContractId = scope.Contract.Id;
            p.IsAdvancePayment = true;
        });

        var result = await CreateAdapter(db, PilotsFor(supplierPrepayment: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(PaymentAccountingEventKind.SupplierPrepayment, result.EventKind);

        var journal = await LoadJournalAsync(db, payment.Id);
        var (cashLine, partyLine) = SplitLines(journal, scope.Settings.CashBankControlAccountId);

        Assert.Equal(payment.AmountUsd, cashLine.Credit);
        Assert.Equal(scope.Settings.SupplierPrepaymentAccountId, partyLine.AccountId);
        Assert.Equal(payment.AmountUsd, partyLine.Debit);
        AssertBalanced(journal, payment.AmountUsd);
    }

    [Fact]
    public async Task SarrafCashPayment_Debits_Payable_With_Sarraf_Party()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.SarrafSettlement;
            p.Direction = PaymentDirection.Out;
            p.SarrafId = scope.Sarraf.Id;
            p.ContractId = scope.Contract.Id;
        });

        var result = await CreateAdapter(db, PilotsFor(sarrafPayment: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, payment.Id);
        var (cashLine, partyLine) = SplitLines(journal, scope.Settings.CashBankControlAccountId);

        Assert.Equal(payment.AmountUsd, cashLine.Credit);
        Assert.Equal(scope.Settings.AccountsPayableAccountId, partyLine.AccountId);
        Assert.Equal(AccountingPartyType.Sarraf, partyLine.PartyType);
        Assert.Equal(scope.Sarraf.Id, partyLine.PartyId);
        AssertBalanced(journal, payment.AmountUsd);
    }

    [Fact]
    public async Task NonUsd_Payment_Carries_The_Currency_Pair_On_Both_Lines()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, paymentCurrency: "RUB");
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
            p.Amount = 9_200m;
            p.Currency = "RUB";
            p.AppliedFxRateToUsd = 0.010870m;
            p.AmountUsd = decimal.Round(9_200m * 0.010870m, 4, MidpointRounding.AwayFromZero);
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true))
            .TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);

        var journal = await LoadJournalAsync(db, payment.Id);
        Assert.All(journal.Lines, line =>
        {
            Assert.Equal("RUB", line.TransactionCurrencyCode);
            Assert.Equal(9_200m, line.TransactionAmount);
            Assert.Equal(0.010870m, line.ExchangeRate);
        });
        AssertBalanced(journal, payment.AmountUsd);
    }

    [Fact]
    public async Task Duplicate_Source_Event_Does_Not_Create_A_Second_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
        });
        var adapter = CreateAdapter(db, PilotsFor(customerReceipt: true));

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPaymentAsync(payment)).Status);
        var second = await adapter.TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal("DUPLICATE_SOURCE_EVENT", second.Reason);
        Assert.Equal(1, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PaymentAccountingAdapter.BuildCreatedSourceEventId(payment.Id)));
    }

    [Fact]
    public async Task Skips_When_Accounting_Is_Disabled()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true), accountingEnabled: false)
            .TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "ACCOUNTING_DISABLED");
    }

    [Fact]
    public async Task Skips_When_Its_Own_Pilot_Flag_Is_Off_Even_If_Another_Is_On()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
        });

        // Supplier pilots on, customer receipt off: the flags must be independent.
        var result = await CreateAdapter(db, PilotsFor(supplierPayment: true, supplierPrepayment: true))
            .TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "PILOT_DISABLED");
    }

    [Fact]
    public async Task Skips_Unsupported_Payment_Kind()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ManualPayment;
            p.Direction = PaymentDirection.Out;
        });

        var result = await CreateAdapter(db, AllPilotsOn()).TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "UNSUPPORTED_PAYMENT_KIND");
    }

    [Fact]
    public async Task Skips_When_Company_Is_Not_Provable()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
            p.ContractId = null;
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true)).TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "PAYMENT_COMPANY_UNKNOWN");
    }

    [Fact]
    public async Task Skips_When_Direction_Contradicts_The_Event_Kind()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.Out;
            p.CustomerId = scope.Customer.Id;
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true)).TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "DIRECTION_MISMATCH");
    }

    [Fact]
    public async Task Skips_When_The_Party_Is_Missing()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = null;
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true)).TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "PARTY_MISSING");
    }

    [Fact]
    public async Task Skips_When_AmountUsd_Drifted_From_The_Currency_Pair()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db, paymentCurrency: "RUB");
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
            p.Amount = 9_200m;
            p.Currency = "RUB";
            p.AppliedFxRateToUsd = 0.010870m;
            // Legacy value derived by division, which the currency pair cannot reproduce.
            p.AmountUsd = 100m;
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true)).TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "INVALID_PAYMENT_CONVERSION");
    }

    [Fact]
    public async Task Skips_When_The_Cash_Account_Belongs_To_Another_Company()
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

        scope.CashAccount.CompanyId = otherCompany.Id;
        await db.SaveChangesAsync();

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
        });

        var result = await CreateAdapter(db, PilotsFor(customerReceipt: true)).TryPostPaymentAsync(payment);

        await AssertSkippedAsync(db, payment, result, "CASH_ACCOUNT_COMPANY_MISMATCH");
    }

    [Fact]
    public async Task Posting_Into_A_Closed_Period_Throws_And_Writes_No_Journal()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
        });

        scope.Period.Status = FiscalPeriodStatus.Closed;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<AccountingValidationException>(
            () => CreateAdapter(db, PilotsFor(customerReceipt: true)).TryPostPaymentAsync(payment));

        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PaymentAccountingAdapter.BuildCreatedSourceEventId(payment.Id)));
    }

    private static async Task AssertSkippedAsync(
        ApplicationDbContext db,
        PaymentTransaction payment,
        PaymentAccountingResult result,
        string expectedReason)
    {
        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Journal);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PaymentAccountingAdapter.BuildCreatedSourceEventId(payment.Id)));
    }

    private static void AssertBalanced(JournalEntry journal, decimal expectedTotalUsd)
    {
        Assert.Equal(JournalEntryStatus.Posted, journal.Status);
        Assert.Equal(2, journal.Lines.Count);
        Assert.Equal(expectedTotalUsd, journal.Lines.Sum(x => x.Debit));
        Assert.Equal(expectedTotalUsd, journal.Lines.Sum(x => x.Credit));
    }

    private static (JournalEntryLine CashLine, JournalEntryLine PartyLine) SplitLines(
        JournalEntry journal,
        int cashBankControlAccountId)
    {
        // The seeded chart gives every settings slot a distinct account, so the cash line is
        // the only one on the cash/bank control account.
        var cashLine = journal.Lines.Single(x => x.AccountId == cashBankControlAccountId);
        var partyLine = journal.Lines.Single(x => x.Id != cashLine.Id);
        return (cashLine, partyLine);
    }

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, int paymentId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceModule == PaymentAccountingAdapter.SourceModule
                && x.SourceEventId == PaymentAccountingAdapter.BuildCreatedSourceEventId(paymentId));

    internal static AccountingPilotOptions PilotsFor(
        bool customerReceipt = false,
        bool customerAdvance = false,
        bool supplierPayment = false,
        bool supplierPrepayment = false,
        bool sarrafPayment = false,
        bool expense = false,
        bool expensePayment = false,
        bool commissionPayment = false)
        => new()
        {
            CustomerReceipt = customerReceipt,
            CustomerAdvance = customerAdvance,
            SupplierPayment = supplierPayment,
            SupplierPrepayment = supplierPrepayment,
            SarrafPayment = sarrafPayment,
            Expense = expense,
            ExpensePayment = expensePayment,
            CommissionPayment = commissionPayment
        };

    private static AccountingPilotOptions AllPilotsOn()
        => PilotsFor(true, true, true, true, true, true, true, true);

    internal static PaymentAccountingAdapter CreateAdapter(
        ApplicationDbContext db,
        AccountingPilotOptions pilots,
        bool accountingEnabled = true)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = accountingEnabled,
            Pilots = pilots
        });
        var posting = new AccountingPostingService(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db));
        return new PaymentAccountingAdapter(
            db,
            posting,
            new AccountingJournalNumberGenerator(),
            new PaymentCompanyResolver(db),
            new ExpenseAccountingAdapter(
                db,
                posting,
                new AccountingJournalNumberGenerator(),
                options,
                NullLogger<ExpenseAccountingAdapter>.Instance),
            options,
            NullLogger<PaymentAccountingAdapter>.Instance);
    }

    private static async Task<PaymentTransaction> AddPaymentAsync(
        ApplicationDbContext db,
        PaymentScope scope,
        Action<PaymentTransaction> configure)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = PaymentDate,
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = scope.CashAccount.Id,
            ContractId = scope.Contract.Id,
            Amount = 250m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 250m
        };
        configure(payment);

        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    internal static async Task<PaymentScope> CreateScopeAsync(
        ApplicationDbContext db,
        string paymentCurrency = "USD")
    {
        var company = new Company
        {
            Code = Unique("C"),
            Name = Unique("Company"),
            Country = "AF",
            IsActive = true,
            IsSystemOwner = true
        };
        // دیتابیسِ این مجموعه بین تست‌ها مشترک است و تست‌ها sequential اجرا می‌شوند؛ پیش از ثبتِ
        // مالکِ این تست، مالکِ تستِ قبلی را demote می‌کنیم تا قیدِ «حداکثر یک مالک» نقض نشود.
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

        // سالِ مالی باید تمامِ دوازده ماه را بپوشاند: reversal با تاریخِ «امروز» ثبت می‌شود، نه با
        // تاریخِ رویدادِ اصلی، و اگر فقط ماهِ رویداد باز باشد تست‌ها با رسیدنِ ماهِ بعد می‌شکنند.
        var periods = Enumerable.Range(1, 12)
            .Select(month => new FiscalPeriod
            {
                CompanyId = company.Id,
                FiscalYearId = year.Id,
                PeriodNumber = month,
                Name = new DateTime(2026, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                StartDate = new DateTime(2026, month, 1),
                EndDate = new DateTime(2026, month, DateTime.DaysInMonth(2026, month)),
                Status = FiscalPeriodStatus.Open
            })
            .ToList();
        db.FiscalPeriods.AddRange(periods);

        // دوره‌ای که تست‌های قفلِ دوره روی آن کار می‌کنند، همان ماهِ رویدادهای ثابتِ این مجموعه است.
        var period = periods[6];

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

        var serviceProvider = new PTGOilSystem.Web.Models.Entities.ServiceProvider
        {
            Code = Unique("SP"),
            Name = Unique("ServiceProvider"),
            IsActive = true
        };
        db.ServiceProviders.Add(serviceProvider);

        var driver = new Driver { FullName = Unique("Driver"), IsActive = true };
        db.Drivers.Add(driver);

        var terminal = new Terminal { Code = Unique("T"), Name = Unique("Terminal"), IsActive = true };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();

        var tank = new StorageTank
        {
            TerminalId = terminal.Id,
            TankCode = Unique("TK"),
            DisplayName = Unique("Tank"),
            CapacityMt = 1_000m,
            IsActive = true
        };
        db.StorageTanks.Add(tank);

        var cashAccount = new CashAccount
        {
            Code = Unique("CA"),
            Name = Unique("Cash"),
            AccountType = CashAccountType.Bank,
            Currency = paymentCurrency,
            IsActive = true
        };
        db.CashAccounts.Add(cashAccount);

        // The posting service validates transaction currencies against the Currencies table
        // when it is non-empty, so the payment currency must be registered.
        foreach (var code in new[] { "USD", paymentCurrency }.Distinct())
        {
            if (!await db.Currencies.AnyAsync(x => x.Code == code && x.IsActive))
                db.Currencies.Add(new Currency { Code = code, Name = code, IsActive = true });
        }

        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = Unique("CN"),
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            ContractDate = new DateTime(2026, 7, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 100m,
            Currency = "USD",
            SettlementCurrencyCode = "USD"
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        return new PaymentScope(
            company, supplier, customer, sarraf, serviceProvider, driver,
            product, contract, cashAccount, terminal, tank, period, settings);
    }

    internal static string Unique(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, prefix.Length + 33)];

    internal sealed record PaymentScope(
        Company Company,
        Supplier Supplier,
        Customer Customer,
        Sarraf Sarraf,
        PTGOilSystem.Web.Models.Entities.ServiceProvider ServiceProvider,
        Driver Driver,
        Product Product,
        Contract Contract,
        CashAccount CashAccount,
        Terminal Terminal,
        StorageTank Tank,
        FiscalPeriod Period,
        AccountingSettings Settings);
}
