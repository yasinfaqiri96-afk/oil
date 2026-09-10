using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// A partnership finances its contracts two ways, and only one of them moves the company's money.
/// When a partner pays a bill out of his own pocket, or keeps the customer's payment instead of
/// banking it, the debt is still settled and the receivable still collected — but no company cash
/// moved. These prove the ledger says exactly that: the payable and the receivable clear, cash is
/// untouched, and what the partner is owed or owes lands on one partner current control account
/// identified by the party on the line, never by a per-partner account.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class PartnerCurrentAccountingTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime PaymentDate = new(2026, 7, 14);

    // ───────────────────────── company money ─────────────────────────

    [Fact]
    public async Task Company_Funded_Supplier_Payment_Clears_The_Payable_Out_Of_Cash()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.SupplierPayment;
            p.Direction = PaymentDirection.Out;
            p.SupplierId = scope.Supplier.Id;
            p.AmountUsd = p.Amount = 4_000m;
        });

        var result = await CreateAdapter(db, Pilots(supplierPayment: true)).TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        var journal = await LoadJournalAsync(db, payment.Id);

        var payable = journal.Lines.Single(x => x.AccountId == scope.Settings.AccountsPayableAccountId);
        var cash = journal.Lines.Single(x => x.AccountId == scope.Settings.CashBankControlAccountId);
        Assert.Equal(4_000m, payable.Debit);
        Assert.Equal(4_000m, cash.Credit);
        Assert.Equal(scope.CashAccount.Id, cash.CashAccountId);
        Assert.Null(cash.PartyType);
    }

    [Fact]
    public async Task Company_Funded_Service_Provider_Payment_Debits_The_Carrier_Payable_And_Cash()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ServiceProviderPayment;
            p.Direction = PaymentDirection.Out;
            p.ServiceProviderId = scope.ServiceProvider.Id;
            p.AmountUsd = p.Amount = 1_250m;
        });

        var result = await CreateAdapter(db, Pilots(expensePayment: true)).TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        Assert.Equal(PaymentAccountingEventKind.ServiceProviderPayment, result.EventKind);

        var journal = await LoadJournalAsync(db, payment.Id);
        var payable = journal.Lines.Single(x => x.AccountId == scope.Settings.FreightPayableAccountId);
        var cash = journal.Lines.Single(x => x.AccountId == scope.Settings.CashBankControlAccountId);

        Assert.Equal(1_250m, payable.Debit);
        Assert.Equal(AccountingPartyType.ServiceProvider, payable.PartyType);
        Assert.Equal(scope.ServiceProvider.Id, payable.PartyId);
        Assert.Equal(1_250m, cash.Credit);
    }

    // ───────────────────────── partner money ─────────────────────────

    [Fact]
    public async Task Partner_Funded_Payment_Credits_The_Partner_And_Never_Company_Cash()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var partner = await AddPartnerAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ServiceProviderPayment;
            p.Direction = PaymentDirection.Out;
            p.ServiceProviderId = scope.ServiceProvider.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = partner.Id;
            p.CashAccountId = null;
            p.AmountUsd = p.Amount = 900m;
        });

        var result = await CreateAdapter(db, Pilots(expensePayment: true)).TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        var journal = await LoadJournalAsync(db, payment.Id);

        var payable = journal.Lines.Single(x => x.AccountId == scope.Settings.FreightPayableAccountId);
        var partnerLine = journal.Lines.Single(
            x => x.AccountId == scope.Settings.PartnerCurrentAccountId!.Value);

        Assert.Equal(900m, payable.Debit);
        Assert.Equal(900m, partnerLine.Credit);
        Assert.Equal(AccountingPartyType.Partner, partnerLine.PartyType);
        Assert.Equal(partner.Id, partnerLine.PartyId);

        // The company's till never opened, so no line may name it.
        Assert.DoesNotContain(journal.Lines, x => x.AccountId == scope.Settings.CashBankControlAccountId);
        Assert.All(journal.Lines, x => Assert.Null(x.CashAccountId));
    }

    [Fact]
    public async Task Partner_Held_Customer_Receipt_Clears_The_Receivable_Without_Company_Cash()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var partner = await AddPartnerAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.CustomerReceipt;
            p.Direction = PaymentDirection.In;
            p.CustomerId = scope.Customer.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = partner.Id;
            p.CashAccountId = null;
            p.AmountUsd = p.Amount = 12_000m;
        });

        var result = await CreateAdapter(db, Pilots(customerReceipt: true)).TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        var journal = await LoadJournalAsync(db, payment.Id);

        var receivable = journal.Lines.Single(
            x => x.AccountId == scope.Settings.AccountsReceivableAccountId);
        var partnerLine = journal.Lines.Single(
            x => x.AccountId == scope.Settings.PartnerCurrentAccountId!.Value);

        // The customer paid, so the receivable is gone; the money simply stopped at the partner.
        Assert.Equal(12_000m, receivable.Credit);
        Assert.Equal(AccountingPartyType.Customer, receivable.PartyType);
        Assert.Equal(12_000m, partnerLine.Debit);
        Assert.Equal(AccountingPartyType.Partner, partnerLine.PartyType);
        Assert.Equal(partner.Id, partnerLine.PartyId);
        Assert.DoesNotContain(journal.Lines, x => x.AccountId == scope.Settings.CashBankControlAccountId);
    }

    [Fact]
    public async Task Partner_Money_With_No_Named_Partner_Is_Still_Refused()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.SupplierPayment;
            p.Direction = PaymentDirection.Out;
            p.SupplierId = scope.Supplier.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = null;
        });

        var result = await CreateAdapter(db, Pilots(supplierPayment: true)).TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal(PaymentAccountingAdapter.PartnerFundedSkipReason, result.Reason);
        Assert.Equal(0, await db.JournalEntries.CountAsync(
            x => x.SourceEventId == PaymentAccountingAdapter.BuildCreatedSourceEventId(payment.Id)));
    }

    [Fact]
    public async Task Partner_Money_Skips_When_The_Partner_Current_Account_Is_Not_Configured()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var partner = await AddPartnerAsync(db);

        // A settings row written before the account existed: the mapping simply has nowhere to go.
        var settings = await db.AccountingSettings.SingleAsync(x => x.CompanyId == scope.Company.Id);
        settings.PartnerCurrentAccountId = null;
        await db.SaveChangesAsync();

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.SupplierPayment;
            p.Direction = PaymentDirection.Out;
            p.SupplierId = scope.Supplier.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = partner.Id;
            p.CashAccountId = null;
        });

        var result = await CreateAdapter(db, Pilots(supplierPayment: true)).TryPostPaymentAsync(payment);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal(PaymentAccountingAdapter.PartnerCurrentMissingSkipReason, result.Reason);
    }

    [Fact]
    public async Task Reversing_A_Partner_Funded_Payment_Undoes_Its_Partner_Current_Effect()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var partner = await AddPartnerAsync(db);
        var adapter = CreateAdapter(db, Pilots(expensePayment: true));
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ServiceProviderPayment;
            p.Direction = PaymentDirection.Out;
            p.ServiceProviderId = scope.ServiceProvider.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = partner.Id;
            p.CashAccountId = null;
            p.AmountUsd = p.Amount = 700m;
        });

        await adapter.TryPostPaymentAsync(payment);
        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostPaymentReversalAsync(payment)).Status);

        var net = await PartnerCurrentNetCreditAsync(db, scope, partner.Id);
        Assert.Equal(0m, net);

        // A second reversal must not write a third journal.
        Assert.Equal(PaymentPostingStatus.Duplicate, (await adapter.TryPostPaymentReversalAsync(payment)).Status);
        Assert.Equal(0m, await PartnerCurrentNetCreditAsync(db, scope, partner.Id));
    }

    [Fact]
    public async Task An_Unrelated_Partner_Is_Never_Touched()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var payer = await AddPartnerAsync(db);
        var bystander = await AddPartnerAsync(db);

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ServiceProviderPayment;
            p.Direction = PaymentDirection.Out;
            p.ServiceProviderId = scope.ServiceProvider.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = payer.Id;
            p.CashAccountId = null;
            p.AmountUsd = p.Amount = 500m;
        });

        await CreateAdapter(db, Pilots(expensePayment: true)).TryPostPaymentAsync(payment);

        Assert.Equal(500m, await PartnerCurrentNetCreditAsync(db, scope, payer.Id));
        Assert.Equal(0m, await PartnerCurrentNetCreditAsync(db, scope, bystander.Id));
    }

    // ───────────────────────── expense payable ─────────────────────────

    [Fact]
    public async Task An_Accrued_Expense_And_Its_Payment_Leave_The_Payable_At_Zero()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var partner = await AddPartnerAsync(db);
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = Pilots(expense: true, expensePayment: true)
        });
        var posting = new AccountingPostingService(
            db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db));
        var expenseAdapter = new ExpenseAccountingAdapter(
            db, posting, new AccountingJournalNumberGenerator(), options,
            NullLogger<ExpenseAccountingAdapter>.Instance);

        var expense = await AddExpenseAsync(db, scope, 1_500m);
        Assert.Equal(PaymentPostingStatus.Posted, (await expenseAdapter.TryPostExpenseAsync(expense)).Status);

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ServiceProviderPayment;
            p.Direction = PaymentDirection.Out;
            p.ServiceProviderId = scope.ServiceProvider.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = partner.Id;
            p.CashAccountId = null;
            p.AmountUsd = p.Amount = 1_500m;
        });
        await CreateAdapter(db, Pilots(expense: true, expensePayment: true)).TryPostPaymentAsync(payment);

        // The expense raised the carrier's payable and the payment settled it, to the cent.
        var payableNet = await AccountNetDebitAsync(db, scope, scope.Settings.FreightPayableAccountId);
        Assert.Equal(0m, payableNet);

        // The cost itself stays in the P&L exactly once.
        var expenseNet = await AccountNetDebitAsync(db, scope, scope.Settings.GeneralExpenseAccountId);
        Assert.Equal(1_500m, expenseNet);
    }

    // ───────────────────────── rounding policy ─────────────────────────

    [Fact]
    public void The_Residual_Cent_Goes_To_One_Partner_And_The_Sum_Is_Exact()
    {
        // 97,181.01 split fifty-fifty is 48,590.505 each. Rounding both up would hand the partners
        // one cent more than the contract actually earned.
        var settled = PartnerProfitAllocationPolicy.Settle(
            new Dictionary<int, decimal> { [7] = 48_590.505m, [3] = 48_590.505m });

        Assert.Equal(97_181.01m, settled.Values.Sum());
        Assert.Equal(48_590.50m, settled[3]);
        Assert.Equal(48_590.51m, settled[7]);
    }

    [Fact]
    public void The_Carrier_Of_The_Residual_Does_Not_Depend_On_Ordering()
    {
        var raw = new Dictionary<int, decimal> { [7] = 48_590.505m, [3] = 48_590.505m };
        var reversed = new Dictionary<int, decimal> { [3] = 48_590.505m, [7] = 48_590.505m };

        var first = PartnerProfitAllocationPolicy.Settle(raw);
        var second = PartnerProfitAllocationPolicy.Settle(reversed);

        Assert.Equal(first[3], second[3]);
        Assert.Equal(first[7], second[7]);
    }

    // ───────────────────────── profit allocation ─────────────────────────

    [Fact]
    public async Task Profit_Allocation_Credits_Both_Partners_And_Sums_Exactly_To_The_Profit()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var (yusuf, fawad) = await MakePartnershipAsync(db, scope, salesUsd: 1_000.01m);

        var result = await CreateAllocationAdapter(db).TryPostAllocationAsync(scope.Contract.Id);

        Assert.Equal(PaymentPostingStatus.Posted, result.Status);
        var journal = result.Journal!;

        // Retained Earnings carries the whole profit; the partners carry their halves.
        var retained = journal.Lines.Single(x => x.AccountId == scope.Settings.RetainedEarningsAccountId);
        Assert.Equal(1_000.01m, retained.Debit);

        var partnerLines = journal.Lines
            .Where(x => x.AccountId == scope.Settings.PartnerCurrentAccountId!.Value)
            .ToList();
        Assert.Equal(2, partnerLines.Count);
        Assert.Equal(1_000.01m, partnerLines.Sum(x => x.Credit));
        Assert.All(partnerLines, x => Assert.Equal(AccountingPartyType.Partner, x.PartyType));

        // The odd cent goes to exactly one of them, and the journal still balances.
        var lower = partnerLines.Single(x => x.PartyId == Math.Min(yusuf.Id, fawad.Id));
        var higher = partnerLines.Single(x => x.PartyId == Math.Max(yusuf.Id, fawad.Id));
        Assert.Equal(500.00m, lower.Credit);
        Assert.Equal(500.01m, higher.Credit);
        Assert.Equal(journal.Lines.Sum(x => x.Debit), journal.Lines.Sum(x => x.Credit));
    }

    [Fact]
    public async Task Profit_Allocation_Runs_Once_However_Often_It_Is_Called()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        await MakePartnershipAsync(db, scope, salesUsd: 800m);
        var adapter = CreateAllocationAdapter(db);

        Assert.Equal(PaymentPostingStatus.Posted, (await adapter.TryPostAllocationAsync(scope.Contract.Id)).Status);
        var second = await adapter.TryPostAllocationAsync(scope.Contract.Id);

        Assert.Equal(PaymentPostingStatus.Duplicate, second.Status);
        Assert.Equal(1, await db.JournalEntries.CountAsync(x =>
            x.SourceEventId == PartnershipProfitAllocationAdapter.BuildSourceEventId(scope.Contract.Id)));
    }

    [Fact]
    public async Task A_Contract_That_Is_Not_A_Partnership_Is_Never_Allocated()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);

        // The scope's contract is owned outright, so there is no partner to allocate anything to.
        var result = await CreateAllocationAdapter(db).TryPostAllocationAsync(scope.Contract.Id);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("CONTRACT_NOT_PARTNERSHIP", result.Reason);
    }

    // ───────────────────────── reconciliation ─────────────────────────

    [Fact]
    public async Task Ledger_Matches_The_Statement_Exactly_When_Only_Partner_Money_Was_Used()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var (yusuf, _) = await MakePartnershipAsync(db, scope, salesUsd: 1_000.01m);

        // Yusuf paid a bill out of his own pocket; nothing came out of the company.
        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ServiceProviderPayment;
            p.Direction = PaymentDirection.Out;
            p.ServiceProviderId = scope.ServiceProvider.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = yusuf.Id;
            p.CashAccountId = null;
            p.AmountUsd = p.Amount = 300m;
        });
        await CreateAdapter(db, Pilots(expensePayment: true)).TryPostPaymentAsync(payment);
        await CreateAllocationAdapter(db).TryPostAllocationAsync(scope.Contract.Id);

        var report = await new PartnerCurrentReconciliationService(
                db, new PartnershipStatementService(db))
            .BuildAsync(scope.Contract.Id);

        Assert.True(report.IsFullyReconciled);
        Assert.All(report.Rows, row =>
        {
            Assert.Equal(0m, row.DifferenceUsd);
            Assert.Null(row.DifferenceReason);
            Assert.Equal(0m, row.CompanyFundedContributionUsd);
        });

        var yusufRow = report.Rows.Single(x => x.PartnerId == yusuf.Id);
        Assert.Equal(300m, yusufRow.PartnerFundedContributionUsd);
        Assert.Equal(yusufRow.StatementNetPositionUsd, yusufRow.LedgerPartnerCurrentUsd);
    }

    [Fact]
    public async Task A_Company_Funded_Contribution_Is_Reported_As_A_Named_Difference_Not_Plugged()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var (_, fawad) = await MakePartnershipAsync(db, scope, salesUsd: 1_000.01m);

        // The company itself paid, and the company's books belong to Fawad. The statement calls
        // that his contribution; the ledger only ever saw cash leave and a payable close.
        var company = await db.Companies.SingleAsync(x => x.Id == scope.Company.Id);
        company.OwnerPartnerId = fawad.Id;
        await db.SaveChangesAsync();

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.SupplierPayment;
            p.Direction = PaymentDirection.Out;
            p.SupplierId = scope.Supplier.Id;
            p.AmountUsd = p.Amount = 2_500m;
        });
        await CreateAdapter(db, Pilots(supplierPayment: true)).TryPostPaymentAsync(payment);
        await CreateAllocationAdapter(db).TryPostAllocationAsync(scope.Contract.Id);

        var report = await new PartnerCurrentReconciliationService(
                db, new PartnershipStatementService(db))
            .BuildAsync(scope.Contract.Id);

        var fawadRow = report.Rows.Single(x => x.PartnerId == fawad.Id);

        Assert.Equal(2_500m, fawadRow.CompanyFundedContributionUsd);
        Assert.Equal(0m, fawadRow.PartnerFundedContributionUsd);
        Assert.Equal("COMPANY_FUNDED_CONTRIBUTION_NOT_IN_LEDGER", fawadRow.DifferenceReason);

        // Named, and fully explained: nothing unaccounted for is left over.
        Assert.Equal(0m, fawadRow.DifferenceUsd);
        Assert.Equal(
            fawadRow.StatementNetPositionUsd,
            fawadRow.LedgerPartnerCurrentUsd + fawadRow.CompanyFundedContributionUsd);
    }

    [Fact]
    public async Task Another_Contract_Does_Not_Leak_Into_This_One()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var (yusuf, _) = await MakePartnershipAsync(db, scope, salesUsd: 600m);
        var other = await MakeSecondPartnershipContractAsync(db, scope, yusuf);

        var payment = await AddPaymentAsync(db, scope, p =>
        {
            p.PaymentKind = PaymentKind.ServiceProviderPayment;
            p.Direction = PaymentDirection.Out;
            p.ServiceProviderId = scope.ServiceProvider.Id;
            p.ContractId = other.Id;
            p.FundingSource = PaymentFundingSource.Partner;
            p.PaidByPartnerId = yusuf.Id;
            p.CashAccountId = null;
            p.AmountUsd = p.Amount = 450m;
        });
        await CreateAdapter(db, Pilots(expensePayment: true)).TryPostPaymentAsync(payment);

        var report = await new PartnerCurrentReconciliationService(
                db, new PartnershipStatementService(db))
            .BuildAsync(scope.Contract.Id);

        // The money belongs to the other contract, so this contract's rows must not see it.
        Assert.All(report.Rows, row => Assert.Equal(0m, row.LedgerPartnerCurrentUsd));
        Assert.All(report.Rows, row => Assert.Equal(0m, row.PartnerFundedContributionUsd));
    }

    // ───────────────────────── helpers ─────────────────────────

    private static PartnershipProfitAllocationAdapter CreateAllocationAdapter(ApplicationDbContext db)
    {
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { PartnershipProfitAllocation = true }
        });
        return new PartnershipProfitAllocationAdapter(
            db,
            new AccountingPostingService(
                db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db)),
            new AccountingJournalNumberGenerator(),
            new PartnershipStatementService(db),
            options,
            NullLogger<PartnershipProfitAllocationAdapter>.Instance);
    }

    /// <summary>
    /// Turns the scope's contract into a fifty-fifty partnership whose only book event is one
    /// sale, so the book profit is exactly the sale amount and the split is easy to reason about.
    /// </summary>
    private static async Task<(Partner Yusuf, Partner Fawad)> MakePartnershipAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal salesUsd)
    {
        var first = await AddPartnerAsync(db);
        var second = await AddPartnerAsync(db);

        // The database refuses a partnership with no shares, so the type and the shares have to
        // land in the same SaveChanges.
        var contract = await db.Contracts.SingleAsync(x => x.Id == scope.Contract.Id);
        contract.OwnershipType = ContractOwnershipType.Partnership;

        db.ContractPartners.AddRange(
            new ContractPartner
            {
                ContractId = contract.Id,
                PartnerId = first.Id,
                SharePercent = 50m,
                EffectiveFrom = new DateTime(2026, 1, 1)
            },
            new ContractPartner
            {
                ContractId = contract.Id,
                PartnerId = second.Id,
                SharePercent = 50m,
                EffectiveFrom = new DateTime(2026, 1, 1)
            });

        db.SalesTransactions.Add(new SalesTransaction
        {
            SaleDate = PaymentDate,
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            SourcePurchaseContractId = contract.Id,
            CompanyId = scope.Company.Id,
            InvoiceNumber = PaymentAccountingAdapterTests.Unique("INV"),
            QuantityMt = 10m,
            UnitPriceUsd = salesUsd / 10m,
            TotalUsd = salesUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m
        });
        await db.SaveChangesAsync();

        return (first, second);
    }

    private static async Task<Contract> MakeSecondPartnershipContractAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        Partner partner)
    {
        var contract = new Contract
        {
            ContractNumber = PaymentAccountingAdapterTests.Unique("CN2"),
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            OwnershipType = ContractOwnershipType.Personal,
            CompanyId = scope.Company.Id,
            ProductId = scope.Product.Id,
            SupplierId = scope.Supplier.Id,
            ContractDate = new DateTime(2026, 7, 1),
            PricingMethod = PricingMethod.ManualFinalPrice,
            QuantityMt = 50m,
            Currency = "USD",
            SettlementCurrencyCode = "USD"
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        contract.OwnershipType = ContractOwnershipType.Partnership;
        db.ContractPartners.Add(new ContractPartner
        {
            ContractId = contract.Id,
            PartnerId = partner.Id,
            SharePercent = 100m,
            EffectiveFrom = new DateTime(2026, 1, 1)
        });
        await db.SaveChangesAsync();
        return contract;
    }

    private static AccountingPilotOptions Pilots(
        bool customerReceipt = false,
        bool supplierPayment = false,
        bool expense = false,
        bool expensePayment = false)
        => new()
        {
            CustomerReceipt = customerReceipt,
            SupplierPayment = supplierPayment,
            Expense = expense,
            ExpensePayment = expensePayment
        };

    private static PaymentAccountingAdapter CreateAdapter(
        ApplicationDbContext db,
        AccountingPilotOptions pilots)
        => PaymentAccountingAdapterTests.CreateAdapter(db, pilots);

    private static async Task<JournalEntry> LoadJournalAsync(ApplicationDbContext db, int paymentId)
        => await db.JournalEntries
            .AsNoTracking()
            .Include(x => x.Lines)
            .SingleAsync(x => x.SourceModule == PaymentAccountingAdapter.SourceModule
                && x.SourceEventId == PaymentAccountingAdapter.BuildCreatedSourceEventId(paymentId));

    /// <summary>Credit minus debit — positive means the partnership owes this partner.</summary>
    private static async Task<decimal> PartnerCurrentNetCreditAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        int partnerId)
    {
        var lines = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == scope.Settings.PartnerCurrentAccountId!.Value
                && x.PartyType == AccountingPartyType.Partner
                && x.PartyId == partnerId)
            .Select(x => new { x.Debit, x.Credit })
            .ToListAsync();

        return lines.Sum(x => x.Credit) - lines.Sum(x => x.Debit);
    }

    private static async Task<decimal> AccountNetDebitAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        int accountId)
    {
        var lines = await db.JournalEntryLines
            .AsNoTracking()
            .Where(x => x.AccountId == accountId && x.JournalEntry!.CompanyId == scope.Company.Id)
            .Select(x => new { x.Debit, x.Credit })
            .ToListAsync();

        return lines.Sum(x => x.Debit) - lines.Sum(x => x.Credit);
    }

    private static async Task<Partner> AddPartnerAsync(ApplicationDbContext db)
    {
        var partner = new Partner
        {
            Code = PaymentAccountingAdapterTests.Unique("PTC"),
            Name = PaymentAccountingAdapterTests.Unique("PT"),
            IsActive = true
        };
        db.Partners.Add(partner);
        await db.SaveChangesAsync();
        return partner;
    }

    private static async Task<ExpenseTransaction> AddExpenseAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        decimal amountUsd)
    {
        var type = new ExpenseType
        {
            Code = PaymentAccountingAdapterTests.Unique("ET"),
            Name = PaymentAccountingAdapterTests.Unique("ExpenseType"),
            IsActive = true,
            // A carrier's cost, so it accrues to the same payable their payment settles.
            PayableAccountKind = ExpensePayableKind.FreightPayable
        };
        db.ExpenseTypes.Add(type);
        await db.SaveChangesAsync();

        var expense = new ExpenseTransaction
        {
            ExpenseDate = PaymentDate,
            ExpenseTypeId = type.Id,
            ContractId = scope.Contract.Id,
            ServiceProviderId = scope.ServiceProvider.Id,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd
        };
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    private static async Task<PaymentTransaction> AddPaymentAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        Action<PaymentTransaction> configure)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = PaymentDate,
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = scope.CashAccount.Id,
            ContractId = scope.Contract.Id,
            FundingSource = PaymentFundingSource.Company,
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
}
