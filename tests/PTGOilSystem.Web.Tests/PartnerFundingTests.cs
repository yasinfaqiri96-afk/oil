using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Partners;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «کدام شریک واقعاً این پرداخت را داد» — پرداختِ شریک، اثرش روی صورت‌حساب شریک،
/// و اثبات اینکه صندوق شرکت و سود/زیان دست‌نخورده می‌مانند.
///
/// سناریوی مرجع (همان مثال کاری): شرکا ۵۰/۵۰، خرید ۱۰۰٬۰۰۰ و مصرف ۲۰٬۰۰۰،
/// شریک A خرید را پرداخت می‌کند و شریک B مصارف را. سهم اقتصادی هرکس ۶۰٬۰۰۰ است،
/// پس A باید ۴۰٬۰۰۰ طلبکار و B باید ۴۰٬۰۰۰ بدهکار شود.
/// </summary>
public sealed class PartnerFundingTests
{
    private const decimal PurchaseUsd = 100_000m;
    private const decimal ExpenseUsd = 20_000m;

    // ————————————————— صورت‌حساب شریک —————————————————

    [Fact]
    public async Task PartnerStatement_SplitsCostByShare_AndCreditsEachPartnerOnlyForWhatTheyActuallyPaid()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerA, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));
        await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerB, ExpenseUsd,
            PaymentKind.ExpensePayment, "ExpensePayment", new DateTime(2026, 8, 6));

        var a = await BuildStatementAsync(db, scenario.PartnerA);
        var b = await BuildStatementAsync(db, scenario.PartnerB);

        // سهم اقتصادی هرکس ۶۰٬۰۰۰ (رسید)، پرداخت واقعی A برابر ۱۰۰٬۰۰۰ و B برابر ۲۰٬۰۰۰ (برد).
        Assert.Equal(60_000m, a.Summary.TotalReceipt);
        Assert.Equal(100_000m, a.Summary.TotalOutflow);
        Assert.Equal(40_000m, a.Summary.ClosingBalance);

        Assert.Equal(60_000m, b.Summary.TotalReceipt);
        Assert.Equal(20_000m, b.Summary.TotalOutflow);
        Assert.Equal(-40_000m, b.Summary.ClosingBalance);
    }

    [Fact]
    public async Task PartnerStatement_IgnoresCompanyFundedPayments()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        // شرکت هر دو مبلغ را از صندوق خودش پرداخت می‌کند.
        await AddCompanyFundedPaymentAsync(db, scenario, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));
        await AddCompanyFundedPaymentAsync(db, scenario, ExpenseUsd,
            PaymentKind.ExpensePayment, "ExpensePayment", new DateTime(2026, 8, 6));

        var a = await BuildStatementAsync(db, scenario.PartnerA);
        var b = await BuildStatementAsync(db, scenario.PartnerB);

        // هیچ شریکی از جیب خودش چیزی نداده: فقط سهم اقتصادی ۶۰٬۰۰۰ می‌ماند.
        Assert.Equal(0m, a.Summary.TotalOutflow);
        Assert.Equal(0m, b.Summary.TotalOutflow);
        Assert.Equal(-60_000m, a.Summary.ClosingBalance);
        Assert.Equal(-60_000m, b.Summary.ClosingBalance);
    }

    [Fact]
    public async Task PartnerPayment_BelongsEntirelyToThePayer_NotToTheOtherPartnerByShare()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerA, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));

        var a = await BuildStatementAsync(db, scenario.PartnerA);
        var b = await BuildStatementAsync(db, scenario.PartnerB);

        // کل ۱۰۰٬۰۰۰ به A، و صفر (نه ۵۰٬۰۰۰) به B.
        Assert.Equal(100_000m, a.Summary.TotalOutflow);
        Assert.Equal(0m, b.Summary.TotalOutflow);
    }

    [Fact]
    public async Task LegacyPaymentWithoutFundingSource_KeepsCompanyBehaviour()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        // رکوردِ قدیمی: FundingSource اصلاً ست نمی‌شود و باید Company بماند.
        var payment = new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 8, 5),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = scenario.CashAccountId,
            ContractId = scenario.ContractId,
            Amount = PurchaseUsd,
            Currency = "USD",
            AmountUsd = PurchaseUsd
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        await AttachLedgerAsync(db, payment, "SupplierPayment", LedgerSide.Debit, scenario.ContractId);

        Assert.Equal(PaymentFundingSource.Company, payment.FundingSource);
        Assert.Null(payment.PaidByPartnerId);

        var a = await BuildStatementAsync(db, scenario.PartnerA);
        Assert.Equal(0m, a.Summary.TotalOutflow);
        Assert.Equal(-60_000m, a.Summary.ClosingBalance);
    }

    [Fact]
    public async Task ReplacingThePayer_MovesTheWholeBalanceToTheNewPartner()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        var payment = await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerA, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));

        payment.PaidByPartnerId = scenario.PartnerB;
        await db.SaveChangesAsync();

        var a = await BuildStatementAsync(db, scenario.PartnerA);
        var b = await BuildStatementAsync(db, scenario.PartnerB);

        Assert.Equal(0m, a.Summary.TotalOutflow);
        Assert.Equal(100_000m, b.Summary.TotalOutflow);
        Assert.Equal(-60_000m, a.Summary.ClosingBalance);
        Assert.Equal(40_000m, b.Summary.ClosingBalance);
    }

    [Fact]
    public async Task RemovingAPartnerFundedPayment_RestoresTheEconomicOnlyBalance()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        var payment = await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerA, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));

        var ledger = await db.LedgerEntries.SingleAsync(l => l.Id == payment.LedgerEntryId);
        db.LedgerEntries.Remove(ledger);
        db.PaymentTransactions.Remove(payment);
        await db.SaveChangesAsync();

        // مانده مشتق است، پس با حذف سند خودبه‌خود اصلاح می‌شود؛ سند برگشتِ جداگانه لازم نیست.
        var a = await BuildStatementAsync(db, scenario.PartnerA);
        Assert.Equal(0m, a.Summary.TotalOutflow);
        Assert.Equal(-60_000m, a.Summary.ClosingBalance);
    }

    [Fact]
    public async Task ManagementBalanceReport_MatchesThePartnerStatement()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerA, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));
        await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerB, ExpenseUsd,
            PaymentKind.ExpensePayment, "ExpensePayment", new DateTime(2026, 8, 6));

        var balances = await new PartyBalanceReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService())
            .GetBalancesAsync(new ManagementReportFilterViewModel());

        var a = balances.Single(r => r.PartyType == PartyStatementPartyType.Partner && r.PartyId == scenario.PartnerA);
        var b = balances.Single(r => r.PartyType == PartyStatementPartyType.Partner && r.PartyId == scenario.PartnerB);

        Assert.Equal(40_000m, a.ClosingBalanceUsd);
        Assert.Equal(-40_000m, b.ClosingBalanceUsd);
    }

    // ————————————————— صندوق شرکت و سود/زیان —————————————————

    [Fact]
    public async Task PartnerFundedPayment_LeavesCompanyCashUntouched()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        var before = (await FinanceMetricCardsQuery.BuildAsync(db)).CashAccountsBalanceUsd;

        var payment = await AddPartnerFundedPaymentAsync(db, scenario, scenario.PartnerA, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));

        var after = (await FinanceMetricCardsQuery.BuildAsync(db)).CashAccountsBalanceUsd;

        Assert.Null(payment.CashAccountId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task CompanyFundedPayment_StillMovesCompanyCash()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);

        var before = (await FinanceMetricCardsQuery.BuildAsync(db)).CashAccountsBalanceUsd;

        var payment = await AddCompanyFundedPaymentAsync(db, scenario, PurchaseUsd,
            PaymentKind.SupplierPayment, "SupplierPayment", new DateTime(2026, 8, 5));

        var after = (await FinanceMetricCardsQuery.BuildAsync(db)).CashAccountsBalanceUsd;

        Assert.Equal(scenario.CashAccountId, payment.CashAccountId);
        Assert.Equal(before - PurchaseUsd, after);
    }

    [Fact]
    public async Task ProfitAndLoss_IsIdenticalWhicheverPartyFundsTheExpense()
    {
        await using var companyPaid = CreateDb();
        var companyScenario = await SeedPartnershipAsync(companyPaid);
        await AddCompanyFundedPaymentAsync(companyPaid, companyScenario, ExpenseUsd,
            PaymentKind.ExpensePayment, "ExpensePayment", new DateTime(2026, 8, 6));

        await using var partnerPaid = CreateDb();
        var partnerScenario = await SeedPartnershipAsync(partnerPaid);
        await AddPartnerFundedPaymentAsync(partnerPaid, partnerScenario, partnerScenario.PartnerB, ExpenseUsd,
            PaymentKind.ExpensePayment, "ExpensePayment", new DateTime(2026, 8, 6));

        var filter = new ManagementReportFilterViewModel();
        var companySnapshot = await new ProfitAndLossService(companyPaid).BuildCompanyAsync(filter);
        var partnerSnapshot = await new ProfitAndLossService(partnerPaid).BuildCompanyAsync(filter);

        Assert.Equal(ExpenseUsd, companySnapshot.OperatingExpenseUsd);
        Assert.Equal(companySnapshot.OperatingExpenseUsd, partnerSnapshot.OperatingExpenseUsd);
        Assert.Equal(companySnapshot.NetProfitUsd, partnerSnapshot.NetProfitUsd);
    }

    // ————————————————— اعتبارسنجی فرم روزنامچه —————————————————

    [Fact]
    public async Task Create_PartnerFunded_StoresThePayerAndSkipsTheCashAccount()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);
        db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 8, 5),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = scenario.PartnerA,
            SupplierId = scenario.SupplierId,
            ContractId = scenario.ContractId,
            Amount = PurchaseUsd,
            Currency = "USD",
            Reference = "PARTNER-A-1"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var saved = await db.PaymentTransactions.SingleAsync(p => p.Reference == "PARTNER-A-1");
        Assert.Equal(PaymentFundingSource.Partner, saved.FundingSource);
        Assert.Equal(scenario.PartnerA, saved.PaidByPartnerId);
        Assert.Null(saved.CashAccountId);
        // سند تأمین‌کننده همچنان ساخته می‌شود: بدهیِ تأمین‌کننده باید کم شود.
        Assert.NotNull(saved.LedgerEntryId);
    }

    [Fact]
    public async Task Create_PartnerFunded_RejectsAPartnerWhoIsNotOnTheContract()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);
        db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", IsActive = true });
        var outsider = new Partner { Code = "PAR-X", Name = "Outsider", IsActive = true };
        db.Partners.Add(outsider);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 8, 5),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = outsider.Id,
            SupplierId = scenario.SupplierId,
            ContractId = scenario.ContractId,
            Amount = PurchaseUsd,
            Currency = "USD",
            Reference = "OUTSIDER-1"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(PaymentCreateViewModel.PaidByPartnerId)));
        Assert.Empty(db.PaymentTransactions);
    }

    [Fact]
    public async Task Create_PartnerFunded_RejectsANonPartnershipContract()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);
        db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", IsActive = true });
        var personal = new Contract
        {
            ContractNumber = "PC-PERSONAL",
            ContractType = ContractType.Purchase,
            CompanyId = scenario.CompanyId,
            ProductId = scenario.ProductId,
            SupplierId = scenario.SupplierId,
            OwnershipType = ContractOwnershipType.Personal
        };
        db.Contracts.Add(personal);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 8, 5),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = scenario.PartnerA,
            SupplierId = scenario.SupplierId,
            ContractId = personal.Id,
            Amount = PurchaseUsd,
            Currency = "USD",
            Reference = "PERSONAL-1"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(PaymentCreateViewModel.ContractId)));
        Assert.Empty(db.PaymentTransactions);
    }

    [Fact]
    public async Task Create_PartnerFunded_RejectsAPaymentWithNoContract()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);
        db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 8, 5),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = scenario.PartnerA,
            SupplierId = scenario.SupplierId,
            Amount = PurchaseUsd,
            Currency = "USD",
            Reference = "NO-CONTRACT-1"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(PaymentCreateViewModel.ContractId)));
        Assert.Empty(db.PaymentTransactions);
    }

    [Fact]
    public async Task Create_CompanyFunded_StillRequiresACashAccount()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);
        db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 8, 5),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            FundingSource = PaymentFundingSource.Company,
            SupplierId = scenario.SupplierId,
            ContractId = scenario.ContractId,
            Amount = PurchaseUsd,
            Currency = "USD",
            Reference = "NO-CASH-1"
        });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(PaymentCreateViewModel.CashAccountId)));
        Assert.Empty(db.PaymentTransactions);
    }

    [Fact]
    public async Task Create_CompanyFunded_KeepsTheExistingCashAndLedgerBehaviour()
    {
        await using var db = CreateDb();
        var scenario = await SeedPartnershipAsync(db);
        db.Currencies.Add(new Currency { Code = "USD", Name = "US Dollar", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 8, 5),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            FundingSource = PaymentFundingSource.Company,
            CashAccountId = scenario.CashAccountId,
            SupplierId = scenario.SupplierId,
            ContractId = scenario.ContractId,
            Amount = PurchaseUsd,
            Currency = "USD",
            Reference = "COMPANY-1"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var saved = await db.PaymentTransactions.SingleAsync(p => p.Reference == "COMPANY-1");
        Assert.Equal(PaymentFundingSource.Company, saved.FundingSource);
        Assert.Null(saved.PaidByPartnerId);
        Assert.Equal(scenario.CashAccountId, saved.CashAccountId);
        Assert.NotNull(saved.LedgerEntryId);
    }

    // ————————————————— کمک‌کننده‌ها —————————————————

    private sealed record PartnershipScenario(
        int CompanyId,
        int ProductId,
        int SupplierId,
        int ContractId,
        int PartnerA,
        int PartnerB,
        int CashAccountId);

    /// <summary>
    /// قرارداد شراکتی ۵۰/۵۰ با خرید ۱۰۰٬۰۰۰ و مصرف ۲۰٬۰۰۰ روی دفتر — بدون هیچ پرداختی.
    /// </summary>
    private static async Task<PartnershipScenario> SeedPartnershipAsync(ApplicationDbContext db)
    {
        var company = new Company { Code = "PTG", Name = "PTG" };
        var product = new Product { Code = "GO", Name = "Gas Oil" };
        var supplier = new Supplier { Name = "Supplier A", IsActive = true };
        var partnerA = new Partner { Code = "PAR-A", Name = "Partner A", IsActive = true };
        var partnerB = new Partner { Code = "PAR-B", Name = "Partner B", IsActive = true };
        var cashAccount = new CashAccount { Code = "CASH", Name = "Main Cash", Currency = "USD", IsActive = true };
        db.AddRange(company, product, supplier, partnerA, partnerB, cashAccount);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = "PC-1",
            ContractName = "Partnership purchase",
            ContractType = ContractType.Purchase,
            CompanyId = company.Id,
            ProductId = product.Id,
            SupplierId = supplier.Id,
            OwnershipType = ContractOwnershipType.Partnership,
            QuantityMt = 1_000m
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = contract.Id, PartnerId = partnerA.Id, SharePercent = 50m },
            new ContractPartner { ContractId = contract.Id, PartnerId = partnerB.Id, SharePercent = 50m });

        // رویدادهای اقتصادی — همان‌هایی که امروز هم بر SharePercent تقسیم می‌شوند.
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 8, 1),
            Side = LedgerSide.Credit,
            AmountUsd = PurchaseUsd,
            Currency = "USD",
            ContractId = contract.Id,
            SupplierId = supplier.Id,
            SourceType = "Loading",
            SourceId = 1,
            Description = "Purchase"
        });

        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = 1,
            ContractId = contract.Id,
            ExpenseDate = new DateTime(2026, 8, 2),
            Amount = ExpenseUsd,
            Currency = "USD",
            AmountUsd = ExpenseUsd,
            Description = "Transport"
        };
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "TRN", Name = "Transport" });
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();

        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 8, 2),
            Side = LedgerSide.Debit,
            AmountUsd = ExpenseUsd,
            Currency = "USD",
            ContractId = contract.Id,
            SourceType = "Expense",
            SourceId = expense.Id,
            Description = "Transport"
        });
        await db.SaveChangesAsync();

        return new PartnershipScenario(
            company.Id, product.Id, supplier.Id, contract.Id, partnerA.Id, partnerB.Id, cashAccount.Id);
    }

    private static async Task<PaymentTransaction> AddPartnerFundedPaymentAsync(
        ApplicationDbContext db,
        PartnershipScenario scenario,
        int partnerId,
        decimal amountUsd,
        PaymentKind kind,
        string ledgerSourceType,
        DateTime date)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = date,
            Direction = PaymentDirection.Out,
            PaymentKind = kind,
            FundingSource = PaymentFundingSource.Partner,
            PaidByPartnerId = partnerId,
            CashAccountId = null,
            ContractId = scenario.ContractId,
            SupplierId = scenario.SupplierId,
            Amount = amountUsd,
            Currency = "USD",
            AmountUsd = amountUsd
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        await AttachLedgerAsync(db, payment, ledgerSourceType, LedgerSide.Debit, scenario.ContractId);
        return payment;
    }

    private static async Task<PaymentTransaction> AddCompanyFundedPaymentAsync(
        ApplicationDbContext db,
        PartnershipScenario scenario,
        decimal amountUsd,
        PaymentKind kind,
        string ledgerSourceType,
        DateTime date)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = date,
            Direction = PaymentDirection.Out,
            PaymentKind = kind,
            FundingSource = PaymentFundingSource.Company,
            CashAccountId = scenario.CashAccountId,
            ContractId = scenario.ContractId,
            SupplierId = scenario.SupplierId,
            Amount = amountUsd,
            Currency = "USD",
            AmountUsd = amountUsd
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        await AttachLedgerAsync(db, payment, ledgerSourceType, LedgerSide.Debit, scenario.ContractId);
        return payment;
    }

    private static async Task AttachLedgerAsync(
        ApplicationDbContext db,
        PaymentTransaction payment,
        string sourceType,
        LedgerSide side,
        int contractId)
    {
        var ledger = new LedgerEntry
        {
            EntryDate = payment.PaymentDate,
            Side = side,
            AmountUsd = payment.AmountUsd,
            Currency = "USD",
            ContractId = contractId,
            SupplierId = payment.SupplierId,
            SourceType = sourceType,
            SourceId = payment.Id,
            Description = sourceType
        };
        db.LedgerEntries.Add(ledger);
        await db.SaveChangesAsync();
        payment.LedgerEntryId = ledger.Id;
        await db.SaveChangesAsync();
    }

    private static Task<PartyStatementResult> BuildStatementAsync(ApplicationDbContext db, int partnerId)
        => new PartyStatementReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                Options.Create(new PartyStatementOptions()))
            .GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Partner, partnerId),
                new PartyStatementFilter { IncludeOperationalColumns = false });

    private static PaymentsController BuildPaymentsController(ApplicationDbContext db)
        => new(db, new PricingService(db), new AuditService(db), NullLogger<PaymentsController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new PartnerFundingTempDataProvider()),
            Url = new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()))
        };

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class PartnerFundingTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values)
        {
        }
    }
}
