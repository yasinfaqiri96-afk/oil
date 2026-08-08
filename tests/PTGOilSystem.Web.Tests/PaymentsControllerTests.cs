using System.Globalization;
using System.Text;
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
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Models.Balance;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Ledger;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Models.Sarrafs;
using PTGOilSystem.Web.Models.Suppliers;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class PaymentsControllerTests
{
    [Fact]
    public async Task Index_Separates_Missing_Usd_Equivalents_From_Today_Totals()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        var today = DateTime.UtcNow.AddHours(4.5).Date;
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = today,
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.ManualReceipt,
                CashAccountId = 2,
                Amount = 100m,
                Currency = "EUR",
                AmountUsd = 0m
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = today,
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.ManualPayment,
                CashAccountId = 1,
                Amount = 30m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 30m
            });
        await db.SaveChangesAsync();

        var result = Assert.IsType<ViewResult>(await BuildPaymentsController(db).Index());
        var model = Assert.IsType<PaymentIndexViewModel>(result.Model);

        Assert.Equal(0m, model.TodayReceiptUsd);
        Assert.Equal(30m, model.TodayPaymentUsd);
        Assert.Equal(1, model.TodayReceiptMissingUsdEquivalentCount);
        Assert.Equal(0, model.TodayPaymentMissingUsdEquivalentCount);
    }

    [Fact]
    public async Task Create_Get_Preselects_Contract_And_Preserves_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedCustomerSaleWithLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(
            contractId: 1,
            customerId: 1,
            returnUrl: "/ContractJourney/Details?contractId=1&tab=finance");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaymentCreateViewModel>(view.Model);
        Assert.Equal(1, model.ContractId);
        Assert.Equal(1, model.CustomerId);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=finance", model.ReturnUrl);
    }

    [Fact]
    public async Task Create_Post_Redirects_To_Local_ReturnUrl_When_Provided()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedCustomerSaleWithLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 1,
            ContractId = 1,
            Amount = 100m,
            Currency = "USD",
            Reference = "RCPT-RETURN",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=finance"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=finance", redirect.Url);
    }

    [Fact]
    public async Task Create_Get_WithSarrafId_Preselects_SarrafSettlement_PaymentFlow()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Noorzad Dubai", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(sarrafId: 1, returnUrl: "/Sarrafs/Details/1");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaymentCreateViewModel>(view.Model);
        Assert.Equal(1, model.SarrafId);
        Assert.Equal(PaymentKind.SarrafSettlement, model.PaymentKind);
        Assert.Equal(PaymentDirection.Out, model.Direction);
        Assert.Equal("/Sarrafs/Details/1", model.ReturnUrl);
    }

    [Fact]
    public async Task Create_Post_SarrafSettlement_Creates_OutPayment_With_CashAccount_Movement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Noorzad Dubai", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SarrafSettlement,
            CounterpartyType = PaymentCounterpartyType.Sarraf,
            CashAccountId = 1,
            SarrafId = 1,
            Amount = 1010m,
            Currency = "USD",
            Reference = "NOORZAD-BNK-1",
            Description = "Payment to sarraf"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var payment = await db.PaymentTransactions.SingleAsync();
        Assert.Equal(PaymentDirection.Out, payment.Direction);
        Assert.Equal(PaymentKind.SarrafSettlement, payment.PaymentKind);
        Assert.Equal(1, payment.CashAccountId);
        Assert.Equal(1, payment.SarrafId);
        Assert.Equal(1010m, payment.AmountUsd);

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == nameof(PaymentKind.SarrafSettlement));
        Assert.Equal(payment.Id, ledger.SourceId);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
        Assert.Equal("NOORZAD-BNK-1", ledger.Reference);
    }

    [Fact]
    public async Task Create_Post_Ignores_External_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedCustomerSaleWithLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 1,
            ContractId = 1,
            Amount = 100m,
            Currency = "USD",
            Reference = "RCPT-UNSAFE",
            ReturnUrl = "https://evil.com"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Get_Loads_Existing_Payment_And_Preserves_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedCustomerSaleWithLedger(db);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 50,
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 1,
            ContractId = 1,
            SalesTransactionId = 1,
            Amount = 1500m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1500m,
            Reference = "PAY-EDIT"
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Edit(50, returnUrl: "/ContractJourney/Details?contractId=1&tab=finance");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Create", view.ViewName);
        var model = Assert.IsType<PaymentCreateViewModel>(view.Model);
        Assert.Equal(50, model.Id);
        Assert.Equal(1, model.ContractId);
        Assert.Equal(1, model.CustomerId);
        Assert.Equal(1500m, model.Amount);
        Assert.Equal("PAY-EDIT", model.Reference);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=finance", model.ReturnUrl);
        Assert.Equal("Edit", view.ViewData["PaymentFormMode"]);
    }

    [Fact]
    public async Task Edit_Post_Updates_Payment_And_Linked_Ledger_Then_Returns_To_Local_ReturnUrl()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedCustomerSaleWithLedger(db);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 51,
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 1,
            ContractId = 1,
            SalesTransactionId = 1,
            Amount = 1500m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1500m,
            Reference = "PAY-BEFORE",
            LedgerEntryId = 151
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 151,
            EntryDate = new DateTime(2026, 4, 25),
            Side = LedgerSide.Debit,
            AmountUsd = 1500m,
            Currency = "USD",
            SourceAmount = 1500m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = new DateTime(2026, 4, 25),
            Description = "Old payment",
            SourceType = "CustomerReceipt",
            SourceId = 51,
            Reference = "PAY-BEFORE",
            ContractId = 1,
            CustomerId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        controller.Url = BuildUrlHelper();

        var result = await controller.Edit(51, new PaymentCreateViewModel
        {
            Id = 51,
            PaymentDate = new DateTime(2026, 4, 26),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 1,
            ContractId = 1,
            SalesTransactionId = 1,
            Amount = 1750m,
            Currency = "USD",
            Reference = "PAY-AFTER",
            Description = "Updated payment",
            ReturnUrl = "/ContractJourney/Details?contractId=1&tab=finance"
        });

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/ContractJourney/Details?contractId=1&tab=finance", redirect.Url);

        var payment = await db.PaymentTransactions.SingleAsync(p => p.Id == 51);
        var ledger = await db.LedgerEntries.SingleAsync(l => l.Id == 151);
        Assert.Equal(new DateTime(2026, 4, 26), payment.PaymentDate);
        Assert.Equal(1750m, payment.Amount);
        Assert.Equal(1750m, payment.AmountUsd);
        Assert.Equal("PAY-AFTER", payment.Reference);
        Assert.Equal(1750m, ledger.AmountUsd);
        Assert.Equal(1750m, ledger.SourceAmount);
        Assert.Equal("PAY-AFTER", ledger.Reference);
        Assert.Equal("CustomerReceipt", ledger.SourceType);
        Assert.Equal(51, ledger.SourceId);

        Assert.Contains(await db.AuditLogs.ToListAsync(),
            log => log.EntityName == nameof(PaymentTransaction) && log.Action == "Update");
        Assert.Contains(await db.AuditLogs.ToListAsync(),
            log => log.EntityName == nameof(LedgerEntry) && log.Action == "Update");
    }

    [Fact]
    public async Task Create_CustomerReceipt_Usd_Creates_Ledger_Sets_Linkage_And_Appears_In_Trace_Views()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedCustomerSaleWithLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CashAccountId = 1,
            CustomerId = 1,
            ContractId = 1,
            SalesTransactionId = 1,
            Amount = 1500m,
            Currency = "usd",
            Reference = "RCPT-001",
            Description = "Partial settlement"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var payment = await db.PaymentTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "CustomerReceipt" && l.SourceId == payment.Id);

        Assert.Equal(ledger.Id, payment.LedgerEntryId);
        Assert.Equal(1500m, payment.AmountUsd);
        Assert.Equal(1m, payment.AppliedFxRateToUsd);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
        Assert.Equal(1500m, ledger.SourceAmount);
        Assert.Equal("USD", ledger.SourceCurrencyCode);
        Assert.Equal(1, ledger.CustomerId);
        Assert.Equal(1, ledger.ContractId);
        Assert.Equal("RCPT-001", ledger.Reference);

        var audits = await db.AuditLogs.OrderBy(a => a.Id).ToListAsync();
        Assert.Contains(audits, log => log.EntityName == nameof(PaymentTransaction) && log.Action == "Insert");
        Assert.Contains(audits, log => log.EntityName == nameof(LedgerEntry) && log.Action == "Insert");

        var ledgerController = new LedgerController(db, NullLogger<LedgerController>.Instance);
        var ledgerDetails = await ledgerController.Details(ledger.Id);
        var ledgerView = Assert.IsType<ViewResult>(ledgerDetails);
        var ledgerModel = Assert.IsType<LedgerDetailsViewModel>(ledgerView.Model);
        Assert.NotNull(ledgerModel.SourceTrace);
        Assert.Equal("Payments", ledgerModel.SourceTrace!.ControllerName);
        Assert.Contains(ledgerModel.SourceTrace.Fields, field => field.Value.Contains("Main USD Bank"));

        var balanceController = new BalanceController(db);
        var balanceResult = await balanceController.CustomerDetails(1);
        var balanceRedirect = Assert.IsType<RedirectToActionResult>(balanceResult);
        Assert.Equal("Details", balanceRedirect.ActionName);
        Assert.Equal("Customers", balanceRedirect.ControllerName);
        Assert.Equal(1, balanceRedirect.RouteValues?["id"]);

        var statementsController = new AccountStatementsController(db, new PricingService(db), new AuditService(db));
        var statementResult = await statementsController.Index(new AccountStatementFilterViewModel { CustomerId = 1 });
        var statementView = Assert.IsType<ViewResult>(statementResult);
        var statementModel = Assert.IsType<AccountStatementIndexViewModel>(statementView.Model);
        Assert.Contains(statementModel.Items, row => row.SourceType == "CustomerReceipt" && row.Reference == "RCPT-001");
        Assert.Equal(3500m, statementModel.Items.Last().RunningBalanceUsd);
    }

    [Fact]
    public async Task Create_SupplierPayment_Usd_Creates_Ledger_And_Affects_Supplier_Balance()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSupplierOpeningLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 26),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 2,
            Amount = 700m,
            Currency = "USD",
            Reference = "SUP-001",
            Description = "Supplier settlement"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var payment = await db.PaymentTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "SupplierPayment" && l.SourceId == payment.Id);
        Assert.Equal(payment.LedgerEntryId, ledger.Id);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
        Assert.Equal(1, ledger.SupplierId);
        Assert.Equal(2, ledger.ContractId);

        var balanceController = new BalanceController(db);
        var balanceResult = await balanceController.SupplierDetails(1);
        var balanceRedirect = Assert.IsType<RedirectToActionResult>(balanceResult);
        Assert.Equal("Details", balanceRedirect.ActionName);
        Assert.Equal("Suppliers", balanceRedirect.ControllerName);
        Assert.Equal(1, balanceRedirect.RouteValues?["id"]);

        var statementsController = new AccountStatementsController(db, new PricingService(db), new AuditService(db));
        var statementResult = await statementsController.Index(new AccountStatementFilterViewModel { SupplierId = 1 });
        var statementView = Assert.IsType<ViewResult>(statementResult);
        var statementModel = Assert.IsType<AccountStatementIndexViewModel>(statementView.Model);
        Assert.Contains(statementModel.Items, row => row.SourceType == "SupplierPayment" && row.Reference == "SUP-001");
        Assert.Equal(1300m, statementModel.Items.Last().RunningBalanceUsd);
    }

    [Fact]
    public async Task Create_SupplierReceipt_Usd_Creates_Credit_Ledger_With_Supplier_Link()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSupplierOpeningLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 27),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.SupplierReceipt,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 2,
            Amount = 125m,
            Currency = "USD",
            Reference = "SUP-RCV-001"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var payment = await db.PaymentTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == nameof(PaymentKind.SupplierReceipt));
        Assert.Equal(payment.Id, ledger.SourceId);
        Assert.Equal(ledger.Id, payment.LedgerEntryId);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, payment.SupplierId);
        Assert.Equal(1, ledger.SupplierId);
    }

    [Fact]
    public async Task Create_CustomerPayment_Usd_Creates_Credit_Ledger_With_Customer_Link()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedCustomerSaleWithLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 27),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.CustomerPayment,
            CashAccountId = 1,
            CustomerId = 1,
            ContractId = 1,
            Amount = 75m,
            Currency = "USD",
            Reference = "CUST-PAY-001"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var payment = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.CustomerPayment);
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == nameof(PaymentKind.CustomerPayment));
        Assert.Equal(LedgerSide.Credit, ledger.Side);
        Assert.Equal(1, payment.CustomerId);
        Assert.Equal(1, ledger.CustomerId);
    }

    [Fact]
    public async Task Create_ServiceProviderPayment_Usd_Creates_Debit_Ledger_With_ServiceProvider_Link()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.ServiceProviders.Add(new PTGOilSystem.Web.Models.Entities.ServiceProvider
        {
            Id = 1,
            Name = "Railway Services A",
            ProviderType = ServiceProviderType.RailwayService,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 27),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.ServiceProviderPayment,
            CashAccountId = 1,
            ServiceProviderId = 1,
            Amount = 225m,
            Currency = "USD",
            Reference = "SP-PAY-001"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var payment = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.ServiceProviderPayment);
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == nameof(PaymentKind.ServiceProviderPayment));
        Assert.Equal(payment.Id, ledger.SourceId);
        Assert.Equal(ledger.Id, payment.LedgerEntryId);
        Assert.Equal(LedgerSide.Debit, ledger.Side);
        Assert.Equal(1, payment.ServiceProviderId);
        Assert.Equal(1, ledger.ServiceProviderId);
        Assert.Null(ledger.SupplierId);
        Assert.Null(ledger.CustomerId);
    }

    [Fact]
    public async Task Create_Payment_Requires_CashAccount()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 27),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 0,
            Amount = 50m,
            Currency = "USD",
            Reference = "NO-CASH"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Create_NonUsd_Uses_Daily_Fx_Rate_And_Stores_Fx_Trace()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.DailyFxRates.Add(new DailyFxRate
        {
            Id = 1,
            BaseCurrency = "EUR",
            QuoteCurrency = "USD",
            RateDate = new DateTime(2026, 4, 24),
            Rate = 1.2m,
            Source = "ECB"
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 2,
            Amount = 100m,
            Currency = "eur",
            Reference = "MAN-EUR-001",
            Description = "Manual EUR receipt"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var payment = await db.PaymentTransactions.SingleAsync();
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "ManualReceipt" && l.SourceId == payment.Id);

        Assert.Equal(120m, payment.AmountUsd);
        Assert.Equal(1.2m, payment.AppliedFxRateToUsd);
        Assert.Equal(120m, ledger.AmountUsd);
        Assert.Equal(100m, ledger.SourceAmount);
        Assert.Equal("EUR", ledger.SourceCurrencyCode);
        Assert.Equal(1.2m, ledger.AppliedFxRateToUsd);
        Assert.Equal(new DateTime(2026, 4, 24), ledger.AppliedFxRateDate);
        Assert.Equal(LedgerSide.Credit, ledger.Side);
    }

    [Fact]
    public async Task Create_Without_Fx_Returns_Clear_Error_And_Persists_Nothing()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 2,
            Amount = 100m,
            Currency = "EUR",
            Reference = "NO-FX-001",
            Description = "FX should fail"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaymentCreateViewModel>(view.Model);
        Assert.False(controller.ModelState.IsValid);
        var errors = controller.ModelState.Values.SelectMany(v => v.Errors).ToList();
        Assert.Contains(errors, error => error.ErrorMessage.Contains("EUR/USD"));
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    // قانون واحد نرخ: USD بدون نرخ — نرخ لازم نیست و AmountUsd برابر مبلغ است.
    [Fact]
    public async Task Create_Usd_Without_Rate_Sets_AmountUsd_Equal_To_Amount()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 1,
            Amount = 250m,
            Currency = "USD",
            Reference = "USD-NORATE-001"
            // DocumentCurrencyPerUsdRate و AppliedFxRateToUsd خالی
        });

        Assert.IsType<RedirectToActionResult>(result);

        var payment = await db.PaymentTransactions.SingleAsync();
        Assert.Equal(250m, payment.AmountUsd);
        Assert.Equal(1m, payment.AppliedFxRateToUsd);
    }

    // کاربر نرخ ساده «۱ دالر = ۷۷ روبل» را وارد می‌کند؛ سیستم AmountUsd = Amount / 77 را حساب می‌کند.
    [Fact]
    public async Task Create_NonUsd_With_DocumentRate_Divides_To_Usd()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Currencies.Add(new Currency { Id = 3, Code = "RUB", Name = "Russian Ruble", IsActive = true });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 3,
            Code = "CASH-RUB",
            Name = "RUB Cash",
            AccountType = CashAccountType.Bank,
            Currency = "RUB",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 3,
            Amount = 7700m,
            Currency = "RUB",
            DocumentCurrencyPerUsdRate = 77m,
            Reference = "RUB-77-001"
        });

        Assert.IsType<RedirectToActionResult>(result);

        var payment = await db.PaymentTransactions.SingleAsync();
        Assert.Equal(100m, payment.AmountUsd);
        Assert.Equal(1m / 77m, payment.AppliedFxRateToUsd);
    }

    // در ویرایش، نرخ قابل‌فهم «۷۷» بازسازی می‌شود نه نرخ معکوس داخلی «۰٫۰۱۲۹۸۷».
    [Fact]
    public async Task Edit_NonUsd_Reconstructs_Friendly_DocumentRate()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Currencies.Add(new Currency { Id = 3, Code = "RUB", Name = "Russian Ruble", IsActive = true });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 3,
            Code = "CASH-RUB",
            Name = "RUB Cash",
            AccountType = CashAccountType.Bank,
            Currency = "RUB",
            IsActive = true
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = 50,
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 3,
            Amount = 7700m,
            Currency = "RUB",
            AppliedFxRateToUsd = 1m / 77m,
            AmountUsd = 100m,
            Reference = "RUB-EDIT-001"
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Edit(50);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaymentCreateViewModel>(view.Model);
        Assert.NotNull(model.DocumentCurrencyPerUsdRate);
        Assert.Equal(77m, Math.Round(model.DocumentCurrencyPerUsdRate!.Value, 4));
    }

    // نرخ غیرمثبت برای ارز غیر USD نباید جای نرخ معتبر بنشیند؛ بدون نرخ روزانه، خطای روشن داده می‌شود.
    [Fact]
    public async Task Create_NonUsd_With_Zero_DocumentRate_Returns_Error()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Currencies.Add(new Currency { Id = 3, Code = "RUB", Name = "Russian Ruble", IsActive = true });
        db.CashAccounts.Add(new CashAccount
        {
            Id = 3,
            Code = "CASH-RUB",
            Name = "RUB Cash",
            AccountType = CashAccountType.Bank,
            Currency = "RUB",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 3,
            Amount = 7700m,
            Currency = "RUB",
            DocumentCurrencyPerUsdRate = 0m,
            Reference = "RUB-ZERO-001"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task MissingLedger_Includes_Payments_Without_Ledger()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = new DateTime(2026, 4, 20),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.ManualReceipt,
                CashAccountId = 1,
                Amount = 100m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 100m,
                Reference = "PAY-MISSING"
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = new DateTime(2026, 4, 21),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.ManualPayment,
                CashAccountId = 1,
                Amount = 50m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 50m,
                Reference = "PAY-OK",
                LedgerEntryId = 10
            });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 10,
            EntryDate = new DateTime(2026, 4, 21),
            Side = LedgerSide.Debit,
            AmountUsd = 50m,
            Currency = "USD",
            SourceAmount = 50m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = new DateTime(2026, 4, 21),
            Description = "Manual payment",
            SourceType = "ManualPayment",
            SourceId = 2,
            Reference = "PAY-OK"
        });
        await db.SaveChangesAsync();

        var controller = new ReconciliationController(db);

        var result = await controller.MissingLedger();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<MissingLedgerViewModel>(view.Model);
        Assert.Contains(model.PaymentsWithoutLedger, payment => payment.Reference == "PAY-MISSING");
        Assert.DoesNotContain(model.PaymentsWithoutLedger, payment => payment.Reference == "PAY-OK");
    }

    [Fact]
    public async Task Csv_Uses_Filter_And_Utf8_Bom()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = new DateTime(2026, 4, 20),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.ManualReceipt,
                CashAccountId = 1,
                Amount = 100m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 100m,
                Reference = "RCPT-CSV"
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = new DateTime(2026, 4, 21),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.ManualPayment,
                CashAccountId = 1,
                Amount = 80m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 80m,
                Reference = "PAY-OTHER"
            });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Csv(new PaymentIndexFilterViewModel
        {
            // بازهٔ تاریخ برای خروجی CSV الزامی است.
            FromDate = new DateTime(2026, 4, 20),
            ToDate = new DateTime(2026, 4, 21),
            Direction = PaymentDirection.In,
            Reference = "RCPT"
        });

        var (bytes, contentType) = await CsvResultTestHelper.ExecuteAsync(result);
        Assert.Equal("text/csv; charset=utf-8", contentType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("RCPT-CSV", csv);
        Assert.DoesNotContain("PAY-OTHER", csv);
    }

    [Fact]
    public async Task Index_Filters_By_CounterpartyType_Currency_And_Search()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = new DateTime(2026, 4, 20),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CashAccountId = 1,
                CustomerId = 1,
                Amount = 100m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 100m,
                Reference = "RCPT-KABUL",
                Description = "Kabul fuel settlement"
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = new DateTime(2026, 4, 21),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.SupplierPayment,
                CashAccountId = 1,
                SupplierId = 1,
                Amount = 80m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 80m,
                Reference = "SUP-OTHER"
            },
            new PaymentTransaction
            {
                Id = 3,
                PaymentDate = new DateTime(2026, 4, 22),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.ManualReceipt,
                CashAccountId = 2,
                Amount = 70m,
                Currency = "EUR",
                AppliedFxRateToUsd = 1.2m,
                AmountUsd = 84m,
                Reference = "EUR-OTHER"
            });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Index(new PaymentIndexFilterViewModel
        {
            CounterpartyType = PaymentCounterpartyType.Customer,
            Currency = "usd",
            Search = "Kabul"
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaymentIndexViewModel>(view.Model);
        var row = Assert.Single(model.Items);
        Assert.Equal("RCPT-KABUL", row.Reference);
        Assert.Equal("Customer A", row.CounterpartyName);
    }

    [Fact]
    public async Task Create_Get_Supplier_Filters_Contracts_To_Purchase_Contracts_For_Supplier()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier B" });
        db.Contracts.AddRange(
            new Contract
            {
                Id = 2,
                ContractNumber = "PUR-SUP-A",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 1),
                QuantityMt = 10m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 100m
            },
            new Contract
            {
                Id = 3,
                ContractNumber = "PUR-SUP-B",
                ContractType = ContractType.Purchase,
                CompanyId = 1,
                SupplierId = 2,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 1),
                QuantityMt = 10m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 100m
            },
            new Contract
            {
                Id = 4,
                ContractNumber = "SAL-CUST",
                ContractType = ContractType.Sale,
                CompanyId = 1,
                CustomerId = 1,
                ProductId = 1,
                ContractDate = new DateTime(2026, 4, 1),
                QuantityMt = 10m,
                PricingMethod = PricingMethod.Fixed,
                UnitPriceUsd = 100m
            });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(supplierId: 1);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaymentCreateViewModel>(view.Model);
        var contracts = Assert.IsAssignableFrom<IReadOnlyList<PaymentContractLookupItemViewModel>>(controller.ViewData["ContractCatalog"]);
        var contract = Assert.Single(contracts);
        Assert.Equal(2, contract.Id);
        Assert.Equal(ContractType.Purchase, contract.ContractType);
        Assert.Equal(1, contract.SupplierId);
    }

    [Fact]
    public async Task Create_Post_SupplierPayment_With_Contract_Preserves_Supplier_And_Contract_In_Payment_And_Ledger()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSupplierOpeningLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 2,
            Amount = 250m,
            Currency = "USD",
            Reference = "SUP-CONTRACT"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var payment = await db.PaymentTransactions.SingleAsync(p => p.Reference == "SUP-CONTRACT");
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "SupplierPayment" && l.SourceId == payment.Id);
        Assert.Equal(1, payment.SupplierId);
        Assert.Equal(2, payment.ContractId);
        Assert.Equal(1, ledger.SupplierId);
        Assert.Equal(2, ledger.ContractId);
    }

    [Fact]
    public async Task Create_Post_Rejects_SupplierPayment_When_Contract_Supplier_Mismatches()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier B" });
        db.Contracts.Add(new Contract
        {
            Id = 3,
            ContractNumber = "PUR-SUP-B",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 2,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 1),
            QuantityMt = 10m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 100m
        });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 25),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 3,
            Amount = 250m,
            Currency = "USD",
            Reference = "SUP-MISMATCH"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Supplier_And_Customer_Details_Expose_Roznamcha_Payments()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = 1,
                PaymentDate = new DateTime(2026, 4, 20),
                Direction = PaymentDirection.Out,
                PaymentKind = PaymentKind.SupplierPayment,
                CashAccountId = 1,
                SupplierId = 1,
                Amount = 100m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 100m,
                Reference = "SUP-PROFILE"
            },
            new PaymentTransaction
            {
                Id = 2,
                PaymentDate = new DateTime(2026, 4, 21),
                Direction = PaymentDirection.In,
                PaymentKind = PaymentKind.CustomerReceipt,
                CashAccountId = 1,
                CustomerId = 1,
                Amount = 150m,
                Currency = "USD",
                AppliedFxRateToUsd = 1m,
                AmountUsd = 150m,
                Reference = "CUST-PROFILE"
            });
        await db.SaveChangesAsync();

        var supplierController = new SuppliersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db));
        var supplierResult = await supplierController.Details(1);
        var supplierView = Assert.IsType<ViewResult>(supplierResult);
        // صفحهٔ تأمین‌کننده پرداخت‌ها را از طریق لیست نمایشیِ یکپارچهٔ مدل (PaymentLines) نشان می‌دهد.
        var supplierModel = Assert.IsType<PTGOilSystem.Web.Models.Suppliers.SupplierProfileViewModel>(supplierView.Model);
        Assert.Contains(supplierModel.PaymentLines, row => row.Reference == "SUP-PROFILE");

        var customerController = new CustomersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db));
        var customerResult = await customerController.Details(1);
        Assert.IsType<ViewResult>(customerResult);
        var customerRows = Assert.IsAssignableFrom<IReadOnlyList<PaymentListItemViewModel>>(customerController.ViewData["PaymentTransactions"]);
        Assert.Contains(customerRows, row => row.Reference == "CUST-PROFILE");
    }

    [Fact]
    public async Task Create_SupplierPayment_Persists_IsAdvancePayment_Flag_Without_Touching_Ledger()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        SeedSupplierOpeningLedger(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 4, 26),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            ContractId = 2,
            Amount = 500m,
            Currency = "USD",
            Reference = "ADV-001",
            Description = "Advance to supplier",
            IsAdvancePayment = true
        });

        Assert.IsType<RedirectToActionResult>(result);

        var payment = await db.PaymentTransactions.SingleAsync();
        Assert.Equal(true, payment.IsAdvancePayment);
        // فیلد فقط نمایشی است: مبلغ Ledger مثل قبل و برابر مبلغ پرداخت باقی می‌ماند.
        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "SupplierPayment" && l.SourceId == payment.Id);
        Assert.Equal(500m, ledger.AmountUsd);
    }

    [Fact]
    public async Task Hub_ReturnsReadOnlySummary_AndCreatesNoRows()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Hub();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TreasuryHubViewModel>(view.Model);
        Assert.Equal(0m, model.TodayReceiptUsd);
        Assert.Equal(0m, model.TodayPaymentUsd);
        Assert.Equal(0, model.SuspenseCount);
        Assert.Equal(0, model.PostedHawalaCount);
        Assert.Equal(0, model.NeedsReviewCount);

        // مرکز فقط read-only است: هیچ سند/Ledger ساخته نمی‌شود.
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Create_Get_WithSupplierAdvanceKind_PresetsAdvancePaymentDefaults()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(kind: "supplier-advance");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaymentCreateViewModel>(view.Model);
        Assert.Equal(PaymentDirection.Out, model.Direction);
        Assert.Equal(PaymentKind.SupplierPayment, model.PaymentKind);
        Assert.True(model.IsAdvancePayment);
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
    }

    [Fact]
    public async Task RubSupplierBalance_UsesRealLoadingAndViaSarrafSourceAmounts()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Currencies.Add(new Currency { Id = 3, Code = "RUB", Name = "Russian Ruble", IsActive = true });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "PUR-RUB-7700",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 6, 1),
            QuantityMt = 10m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 77m,
            SettlementCurrencyCode = "RUB",
            RubRatePolicy = RubSettlementRatePolicy.FixedContractRate,
            ContractRubPerUsdRate = 100m,
            ContractRubRateDate = new DateTime(2026, 6, 1),
            ContractRubRateSource = "Contract"
        });
        await db.SaveChangesAsync();

        var loadingResult = await BuildLoadingController(db).Create(new LoadingCreateViewModel
        {
            ContractId = 1,
            ProductId = 1,
            OriginLocationId = 1,
            TransportType = LoadingTransportType.Wagon,
            LoadingDate = new DateTime(2026, 6, 2),
            LoadedQuantityMt = 1m,
            LockContract = true,
            Rows =
            [
                new LoadingCreateRowViewModel
                {
                    RowKey = "rub-7700",
                    ContractId = 1,
                    LoadingDate = new DateTime(2026, 6, 2),
                    LoadedQuantityMt = 1m,
                    LoadingPriceUsd = 77m,
                    WagonNumber = "W-RUB-1"
                }
            ]
        });

        Assert.IsType<RedirectToActionResult>(loadingResult);

        var paymentResult = await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 3),
            PaymentMethod = PaymentMethod.ViaSarraf,
            SarrafId = 1,
            SupplierId = 1,
            ContractId = 1,
            SarrafSupplierAmount = 3000m,
            SarrafSupplierCurrency = "RUB",
            SarrafSupplierPerUsdRate = 100m,
            Reference = "RUB-PAY-3000"
        });

        Assert.IsType<RedirectToActionResult>(paymentResult);

        var loadingLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Loading");
        Assert.Equal(LedgerSide.Credit, loadingLedger.Side);
        Assert.Equal(77m, loadingLedger.AmountUsd);
        Assert.Equal(7700m, loadingLedger.SourceAmount);
        Assert.Equal("RUB", loadingLedger.SourceCurrencyCode);
        Assert.Equal(1, loadingLedger.SupplierId);
        Assert.Equal(1, loadingLedger.ContractId);

        var paymentLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType);
        Assert.Equal(LedgerSide.Debit, paymentLedger.Side);
        Assert.Equal(30m, paymentLedger.AmountUsd);
        Assert.Equal(3000m, paymentLedger.SourceAmount);
        Assert.Equal("RUB", paymentLedger.SourceCurrencyCode);
        Assert.Equal(0.01m, paymentLedger.AppliedFxRateToUsd);
        Assert.Equal(1, paymentLedger.SupplierId);
        Assert.Equal(1, paymentLedger.ContractId);
        Assert.Empty(await db.SarrafSettlements.ToListAsync());
        Assert.Empty(await db.PaymentTransactions.ToListAsync());

        var supplierResult = await new SuppliersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db))
            .Details(1, contractId: 1, tab: "statement");
        var supplierView = Assert.IsType<ViewResult>(supplierResult);
        var supplier = Assert.IsType<SupplierProfileViewModel>(supplierView.Model);
        var closingRow = Assert.Single(supplier.StatementRows.Where(r => r.Reference == "RUB-PAY-3000"));
        Assert.Equal(-4700m, closingRow.RunningBalanceRubEquivalent);
        Assert.Equal(30m, supplier.TotalPaidUsd);
        Assert.Equal(3000m, supplier.TotalPaidRub);
        Assert.Equal(47m, supplier.SupplierRemainingClaimUsd);
        Assert.Equal(4700m, supplier.SupplierRemainingClaimRub);
        Assert.Equal(new DateTime(2026, 6, 3), supplier.LastPaymentDate);

        var contract = Assert.Single(supplier.Contracts);
        Assert.Equal(30m, contract.PaidUsd);
        Assert.Equal(3000m, contract.PaidRub);
        Assert.Equal(47m, contract.LoadedValueBalanceUsd);
        Assert.Equal(4700m, contract.LoadedValueBalanceRub);

        var paymentLine = Assert.Single(supplier.PaymentLines);
        Assert.True(paymentLine.IsLedgerOnlyViaSarraf);
        Assert.Equal(3000m, paymentLine.Amount);
        Assert.Equal("RUB", paymentLine.Currency);
        Assert.Equal(30m, paymentLine.AmountUsd);
        Assert.Equal(paymentLedger.Id, paymentLine.LedgerEntryId);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_Rub_ReducesSupplierAndIncreasesSarrafPayable_SameCurrency_NoLegacySettlement()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier RUB", IsActive = true });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A", IsActive = true });
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);

        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            PaymentMethod = PaymentMethod.ViaSarraf,
            SarrafId = 1,
            SupplierId = 2,
            SarrafSupplierAmount = 920_000m,
            SarrafSupplierCurrency = "RUB",
            SarrafSupplierPerUsdRate = 92m,
            Reference = "HAWALA-001",
            Description = "Sarraf pays supplier"
        });

        Assert.IsType<RedirectToActionResult>(result);

        Assert.Empty(await db.SarrafSettlements.ToListAsync());

        var supplierLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType);
        Assert.Equal(LedgerSide.Debit, supplierLedger.Side);
        Assert.Equal(10_000m, supplierLedger.AmountUsd);
        Assert.Equal("RUB", supplierLedger.SourceCurrencyCode);
        Assert.Equal(920_000m, supplierLedger.SourceAmount);
        Assert.Equal(Math.Round(1m / 92m, 6, MidpointRounding.AwayFromZero), supplierLedger.AppliedFxRateToUsd);
        Assert.Equal(2, supplierLedger.SupplierId);

        var sarrafPayableLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType);
        Assert.Equal(LedgerSide.Credit, sarrafPayableLedger.Side);
        Assert.Equal(10_000m, sarrafPayableLedger.AmountUsd);
        Assert.Equal("RUB", sarrafPayableLedger.SourceCurrencyCode);
        Assert.Equal(920_000m, sarrafPayableLedger.SourceAmount);
        Assert.Equal(Math.Round(1m / 92m, 6, MidpointRounding.AwayFromZero), sarrafPayableLedger.AppliedFxRateToUsd);
        Assert.Equal(1, sarrafPayableLedger.SourceId);

        Assert.Equal(2, await db.LedgerEntries.CountAsync());
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
        Assert.Equal(0, await db.ThreeWaySettlements.CountAsync());

        var sarrafResult = await new SarrafsController(db).Details(1);
        var sarrafView = Assert.IsType<ViewResult>(sarrafResult);
        var sarraf = Assert.IsType<SarrafDetailsViewModel>(sarrafView.Model);
        Assert.Equal(10_000m, sarraf.ChargedUsd);
        Assert.Equal(10_000m, sarraf.PayableUsd);
        Assert.Contains(sarraf.StatementRows, r => r.Currency == "RUB" && r.SourceAmount == 920_000m);

        var indexResult = await controller.Index(new PaymentIndexFilterViewModel { SupplierId = 2 });
        var indexView = Assert.IsType<ViewResult>(indexResult);
        var index = Assert.IsType<PaymentIndexViewModel>(indexView.Model);
        var row = Assert.Single(index.Items);
        Assert.True(row.IsLedgerOnlyViaSarraf);
        Assert.Equal("HAWALA-001", row.Reference);
        Assert.Equal("Supplier RUB", row.CounterpartyName);
        Assert.Equal("RUB", row.Currency);
        Assert.Equal(920_000m, row.Amount);
        Assert.Equal(10_000m, row.AmountUsd);
        Assert.Equal(supplierLedger.Id, row.LedgerEntryId);
        Assert.Contains("Sarraf A", row.RelatedTo);
    }

    // ===================== تفاوت نرخ ارز حواله صراف (شیت commission فایل Petrogas) =====================
    // شرکت ارز را با یک نرخ می‌خرد و تأمین‌کننده همان ارز را با نرخ دیگری قبول می‌کند.
    // تفاوت، کمیسیون نقدی نیست: هیچ پول جداگانه‌ای پرداخت نشده، پس فقط مصرف ثبت می‌شود.

    private static async Task<PaymentsController> BuildDualRateFixtureAsync(ApplicationDbContext db)
    {
        SeedReferenceData(db);
        db.Suppliers.Add(new Supplier { Id = 2, Name = "SOLVEX FZE", IsActive = true });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Noorzad Dubai", IsActive = true });
        db.Contracts.Add(new Contract
        {
            Id = 7,
            ContractNumber = "20000-MT-DIESEL",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 2,
            ProductId = 1,
            QuantityMt = 20_000m,
            Currency = "USD",
            ContractDate = new DateTime(2025, 12, 1)
        });
        await db.SaveChangesAsync();
        return BuildPaymentsController(db);
    }

    private static PaymentCreateViewModel BuildDualRateModel(
        DateTime date,
        decimal rubAmount,
        decimal companyRate,
        decimal supplierRate,
        string reference,
        int? contractId = 7)
        => new()
        {
            PaymentDate = date,
            PaymentMethod = PaymentMethod.ViaSarraf,
            SarrafId = 1,
            SupplierId = 2,
            ContractId = contractId,
            SarrafSupplierAmount = rubAmount,
            SarrafSupplierCurrency = "RUB",
            SarrafSupplierPerUsdRate = supplierRate,
            SarrafCompanyPerUsdRate = companyRate,
            Reference = reference
        };

    [Fact]
    public async Task Create_Post_ViaSarraf_DualRate_Receipt665_MatchesExcelNumbers()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        var result = await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 76m, 79.3146m, "665"));
        Assert.IsType<RedirectToActionResult>(result);

        var settlement = await db.SarrafSettlements.SingleAsync();
        // ستون Our Payment شیت commission = 2,631,578.9474
        Assert.Equal(2_631_578.9474m, settlement.SarrafChargedAmountUsd);
        // ستون Their Received = T-Credit شیت 2026 = 2,521,603.8409
        Assert.Equal(2_521_603.8409m, settlement.SupplierAcceptedAmountUsd);
        // ستون Difference = 109,975.1065
        Assert.Equal(109_975.1065m, settlement.DifferenceAmountUsd);
        Assert.Equal(SarrafSettlementDifferenceType.SupplierShortfall, settlement.DifferenceType);
        Assert.Equal(SarrafSettlementDifferenceTreatment.AcceptedAmountOnly, settlement.DifferenceTreatment);
        Assert.Equal(DifferenceReason.FxDifference, settlement.DifferenceReason);

        // تست ۱ — حساب تأمین‌کننده فقط SupplierAcceptedUsd می‌گیرد.
        var supplierLedger = await db.LedgerEntries
            .SingleAsync(l => l.SourceType == SarrafSettlementService.SupplierLedgerSourceType);
        Assert.Equal(LedgerSide.Debit, supplierLedger.Side);
        Assert.Equal(2_521_603.8409m, supplierLedger.AmountUsd);
        Assert.Equal(2, supplierLedger.SupplierId);
        Assert.Equal(7, supplierLedger.ContractId);
        Assert.Equal("RUB", supplierLedger.SourceCurrencyCode);
        Assert.Equal(200_000_000m, supplierLedger.SourceAmount);

        // تست ۳ — تفاوت به‌عنوان مصرف ثبت شده است.
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(109_975.1065m, expense.AmountUsd);
        Assert.Equal("USD", expense.Currency);
        var expenseType = await db.ExpenseTypes.SingleAsync(t => t.Id == expense.ExpenseTypeId);
        Assert.Equal(PaymentsController.SarrafFxDifferenceExpenseCode, expenseType.Code);

        // تست ۴ — هیچ خروج نقدی ساخته نشده است.
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());

        // تست ۵ و ۶ — سطر مصرف نه SupplierId دارد نه ContractId، پس مالکیت تأمین‌کننده آن را برنمی‌دارد.
        var expenseLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense");
        Assert.Equal(LedgerSide.Debit, expenseLedger.Side);
        Assert.Equal(109_975.1065m, expenseLedger.AmountUsd);
        Assert.Null(expenseLedger.SupplierId);
        Assert.Null(expenseLedger.ContractId);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_DualRate_Receipt681_MatchesExcelNumbers()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        var result = await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 29), 300_000_000m, 76m, 77.6923m, "681"));
        Assert.IsType<RedirectToActionResult>(result);

        var settlement = await db.SarrafSettlements.SingleAsync();
        Assert.Equal(3_947_368.4211m, settlement.SarrafChargedAmountUsd);
        Assert.Equal(3_861_386.5209m, settlement.SupplierAcceptedAmountUsd);
        Assert.Equal(85_981.9002m, settlement.DifferenceAmountUsd);

        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(85_981.9002m, expense.AmountUsd);
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_DualRate_SupplierStatementExcludesFxDifference()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 76m, 79.3146m, "665"));

        // تست ۵ — صورت‌حساب تأمین‌کننده فقط مبلغ قبول‌شده را می‌بیند، نه تفاوت نرخ را.
        var supplierResult = await new SuppliersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db))
            .Details(2, tab: "statement");
        var supplierView = Assert.IsType<ViewResult>(supplierResult);
        var supplier = Assert.IsType<SupplierProfileViewModel>(supplierView.Model);

        Assert.All(supplier.StatementRows, r => Assert.NotEqual(109_975.1065m, r.OutflowUsd ?? 0m));
        Assert.Equal(2_521_603.8409m, supplier.StatementRows.Sum(r => r.OutflowUsd ?? 0m));

        // تست ۷ — پرداخت قرارداد فقط SupplierAcceptedUsd است و تفاوت مانده را جابه‌جا نمی‌کند.
        var contract = Assert.Single(supplier.Contracts);
        Assert.Equal(2_521_603.8409m, contract.PaidUsd);
        Assert.Equal(2_521_603.8409m, contract.LedgerOutflowUsd);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_DualRate_SarrafStatementUsesCompanyCost()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 76m, 79.3146m, "665"));

        // تست ۲ و ۶ — بدهی به صراف با نرخ خرید شرکت است، یعنی تفاوت نرخ داخل آن دیده می‌شود.
        var sarrafResult = await new SarrafsController(db).Details(1);
        var sarrafView = Assert.IsType<ViewResult>(sarrafResult);
        var sarraf = Assert.IsType<SarrafDetailsViewModel>(sarrafView.Model);
        Assert.Equal(2_631_578.9474m, sarraf.ChargedUsd);
        Assert.Equal(2_521_603.8409m, sarraf.AcceptedUsd);
        Assert.Equal(109_975.1065m, sarraf.SupplierShortfallUsd);
    }

    [Fact]
    public async Task PostSarrafFxDifference_IsIdempotent_ForSameSettlement()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 76m, 79.3146m, "665"));
        var settlement = await db.SarrafSettlements.SingleAsync();

        // تست ۹ — پردازش دوبارهٔ همان تسویه مصرف تکراری نمی‌سازد.
        await controller.PostSarrafFxDifferenceAsync(settlement);

        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(1, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
    }

    // ===================== سود تفاوت نرخ ارز (FxResult = Accepted − CompanyCost > 0) =====================

    [Fact]
    public async Task Create_Post_ViaSarraf_DualRate_Gain_CreatesIncomeNotExpense()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        // نرخ خرید شرکت ۸۲ (ارزان‌تر) در برابر نرخ قبول تأمین‌کننده ۷۹٫۳۱۴۶ → سود.
        var result = await controller.Create(
            BuildDualRateModel(new DateTime(2026, 1, 5), 200_000_000m, 82m, 79.3146m, "GAIN-1"));
        Assert.IsType<RedirectToActionResult>(result);

        var settlement = await db.SarrafSettlements.SingleAsync();
        Assert.Equal(2_439_024.3902m, settlement.SarrafChargedAmountUsd);   // CompanyCost = 200M / 82
        Assert.Equal(2_521_603.8409m, settlement.SupplierAcceptedAmountUsd); // 200M / 79.3146
        // DifferenceAmountUsd سرویس = CompanyCost − Accepted، پس برای سود منفی است.
        Assert.Equal(-82_579.4507m, settlement.DifferenceAmountUsd);

        // تست ۲ — نوع تفاوت باید Gain باشد، نه SupplierShortfall.
        Assert.Equal(SarrafSettlementDifferenceType.Gain, settlement.DifferenceType);
        Assert.Equal(DifferenceReason.FxDifference, settlement.DifferenceReason);

        // هیچ مصرفی ساخته نمی‌شود و هیچ سطر بدهکارِ هزینه‌ای وجود ندارد.
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));

        // درآمد: یک سطر بستانکار با مبلغ مثبت.
        var gainLedger = await db.LedgerEntries
            .SingleAsync(l => l.SourceType == PaymentsController.SarrafFxGainLedgerSourceType);
        Assert.Equal(LedgerSide.Credit, gainLedger.Side);
        Assert.Equal(82_579.4507m, gainLedger.AmountUsd);
        Assert.True(gainLedger.AmountUsd > 0m);
        Assert.Null(gainLedger.SupplierId);
        Assert.Null(gainLedger.ContractId);

        // تست ۴ — صندوق دست نمی‌خورد.
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_DualRate_Gain_KeepsSupplierSarrafAndContractCorrect()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        await controller.Create(
            BuildDualRateModel(new DateTime(2026, 1, 5), 200_000_000m, 82m, 79.3146m, "GAIN-1"));

        // تأمین‌کننده فقط مبلغ قبول‌شده؛ سود در صورت‌حساب او دیده نمی‌شود.
        var supplierResult = await new SuppliersController(db, new AuditService(db), new MasterDataDeleteSafetyService(db))
            .Details(2, tab: "statement");
        var supplier = Assert.IsType<SupplierProfileViewModel>(Assert.IsType<ViewResult>(supplierResult).Model);
        Assert.Equal(2_521_603.8409m, supplier.StatementRows.Sum(r => r.OutflowUsd ?? 0m));
        Assert.All(supplier.StatementRows, r => Assert.NotEqual(82_579.4507m, r.ReceiptUsd ?? 0m));

        // قرارداد فقط مبلغ قبول‌شده را پرداخت‌شده می‌بیند.
        var contract = Assert.Single(supplier.Contracts);
        Assert.Equal(2_521_603.8409m, contract.PaidUsd);
        Assert.Equal(2_521_603.8409m, contract.LedgerOutflowUsd);

        // صراف فقط هزینهٔ واقعی شرکت.
        var sarraf = Assert.IsType<SarrafDetailsViewModel>(
            Assert.IsType<ViewResult>(await new SarrafsController(db).Details(1)).Model);
        Assert.Equal(2_439_024.3902m, sarraf.ChargedUsd);
        Assert.Equal(82_579.4507m, sarraf.ExchangeGainUsd);
        Assert.Equal(0m, sarraf.SupplierShortfallUsd);
    }

    [Fact]
    public async Task PostSarrafFxDifference_WithNoDifference_CreatesNothing()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        // تست ۳ — هزینهٔ شرکت دقیقاً برابر مبلغ قبول‌شده.
        var settlement = new SarrafSettlement
        {
            SettlementDate = new DateTime(2026, 1, 5),
            SarrafId = 1,
            SupplierId = 2,
            SarrafCurrency = "RUB",
            SarrafRate = 79.3146m,
            SupplierRate = 79.3146m,
            DifferenceAmountUsd = 0m,
            DifferenceType = SarrafSettlementDifferenceType.None,
            DifferenceTreatment = SarrafSettlementDifferenceTreatment.AcceptedAmountOnly,
            Status = SarrafSettlementStatus.Posted
        };
        db.SarrafSettlements.Add(settlement);
        await db.SaveChangesAsync();

        await controller.PostSarrafFxDifferenceAsync(settlement);

        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
        Assert.Equal(0, await db.LedgerEntries.CountAsync(
            l => l.SourceType == PaymentsController.SarrafFxGainLedgerSourceType));
        Assert.Null(settlement.DifferenceReason);
    }

    [Fact]
    public async Task PostSarrafFxDifference_Gain_IsIdempotent()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        await controller.Create(
            BuildDualRateModel(new DateTime(2026, 1, 5), 200_000_000m, 82m, 79.3146m, "GAIN-1"));
        var settlement = await db.SarrafSettlements.SingleAsync();

        await controller.PostSarrafFxDifferenceAsync(settlement);

        Assert.Equal(1, await db.LedgerEntries.CountAsync(
            l => l.SourceType == PaymentsController.SarrafFxGainLedgerSourceType));
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
    }

    [Fact]
    public async Task PostSarrafFxDifference_FlippingLossToGain_ReplacesThePreviousRecord()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        // ابتدا ضرر ثبت می‌شود.
        await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 76m, 79.3146m, "665"));
        var settlement = await db.SarrafSettlements.SingleAsync();
        Assert.Equal(1, await db.ExpenseTransactions.CountAsync());

        // تست ۵ — جهت به سود برمی‌گردد: ثبت قبلی باید برداشته و سمت درست ساخته شود.
        settlement.DifferenceAmountUsd = -82_579.4507m;
        settlement.DifferenceType = SarrafSettlementDifferenceType.Gain;
        await db.SaveChangesAsync();

        await controller.PostSarrafFxDifferenceAsync(settlement);

        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
        var gainLedger = await db.LedgerEntries
            .SingleAsync(l => l.SourceType == PaymentsController.SarrafFxGainLedgerSourceType);
        Assert.Equal(LedgerSide.Credit, gainLedger.Side);
        Assert.Equal(82_579.4507m, gainLedger.AmountUsd);
    }

    [Fact]
    public async Task FxLossExpense_IsNotAttachedToTheContract_SoPnlDoesNotCountItTwice()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 76m, 79.3146m, "665"));

        // سود و زیان مدیریتی، تفاوت نرخ را از SarrafSettlement.DifferenceType می‌خواند؛
        // اگر این مصرف به قرارداد بسته می‌شد، همان ضرر بار دوم در GeneralExpenseCostUsd می‌آمد.
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Null(expense.ContractId);
        Assert.Equal(109_975.1065m, expense.AmountUsd);

        var settlement = await db.SarrafSettlements.SingleAsync();
        Assert.Equal(SarrafSettlementDifferenceType.SupplierShortfall, settlement.DifferenceType);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_SingleRate_KeepsLegacyPath_NoFxDifference()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        // نرخ شرکت برابر نرخ تأمین‌کننده: رفتار قبلی دست‌نخورده می‌ماند.
        var result = await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 79.3146m, 79.3146m, "665-A"));
        Assert.IsType<RedirectToActionResult>(result);

        Assert.Empty(await db.SarrafSettlements.ToListAsync());
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(1, await db.LedgerEntries.CountAsync(
            l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType));
    }

    // ===================== صورت‌حساب رسمی در مسیر تک‌نرخی =====================
    // مسیر legacy دو سطر می‌سازد: پرداخت تأمین‌کننده (Debit) و بدهی صراف (Credit). سطر دوم
    // ContractId دارد ولی هیچ FK طرف‌حسابی ندارد، پس پیش‌تر مالکیت تأمین‌کننده آن را هم
    // برمی‌داشت و مانده را صفر می‌کرد.

    private static PartyStatementReadService BuildStatementService(ApplicationDbContext db)
        => new(db, new PartyStatementPolicyResolver(), new CompanyFlowDirectionResolver(), new CompanyFlowBalanceService(), Options.Create(new PartyStatementOptions()));

    [Fact]
    public async Task Create_Post_ViaSarraf_SingleRate_SupplierStatementShowsOnlyThePaymentRow()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        // نرخ خرید شرکت خالی → مسیر تک‌نرخی legacy.
        var model = BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 0m, 79.3146m, "665-C");
        model.SarrafCompanyPerUsdRate = null;
        Assert.IsType<RedirectToActionResult>(await controller.Create(model));

        var statement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, 2),
            new PartyStatementFilter());

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(PaymentsController.ViaSarrafSupplierLedgerSourceType, row.SourceType);
        // پرداخت به تأمین‌کننده از طریق صراف = برد ⇒ بیلانس مثبت (طلب/پیش‌پرداخت شرکت).
        Assert.Equal(2_521_603.8409m, row.OutflowBase);
        Assert.Equal(2_521_603.8409m, statement.Summary.ClosingBalance);
        Assert.DoesNotContain(statement.Rows,
            r => r.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType);

        // هر دو سطر سر جای خودشان‌اند؛ فقط انتساب خواندنی اصلاح شده است.
        Assert.Equal(1, await db.LedgerEntries.CountAsync(
            l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType
                && l.Side == LedgerSide.Credit
                && l.ContractId == 7));
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_SingleRate_SarrafStatementKeepsTheFullPayable()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        var model = BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 0m, 79.3146m, "665-C");
        model.SarrafCompanyPerUsdRate = null;
        Assert.IsType<RedirectToActionResult>(await controller.Create(model));

        var statement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Sarraf, 1),
            new PartyStatementFilter());

        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(PaymentsController.ViaSarrafPayableLedgerSourceType, row.SourceType);
        // بدهی شرکت به صراف: صراف ارزش را فراهم کرده ⇒ رسید ⇒ بیلانس منفی (بدهکاریم).
        Assert.Equal(2_521_603.8409m, row.ReceiptBase);
        Assert.Equal(-2_521_603.8409m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_EqualRates_BehaveExactlyLikeSingleRate()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        // دو نرخ برابر: هنوز مسیر تک‌نرخی است و هیچ سود/زیانی ساخته نمی‌شود.
        Assert.IsType<RedirectToActionResult>(await controller.Create(
            BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 79.3146m, 79.3146m, "665-D")));

        Assert.Empty(await db.SarrafSettlements.ToListAsync());
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(0, await db.LedgerEntries.CountAsync(
            l => l.SourceType == PaymentsController.SarrafFxGainLedgerSourceType));

        var supplierStatement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, 2),
            new PartyStatementFilter());
        var supplierRow = Assert.Single(supplierStatement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(PaymentsController.ViaSarrafSupplierLedgerSourceType, supplierRow.SourceType);
        Assert.Equal(2_521_603.8409m, supplierStatement.Summary.ClosingBalance);

        var sarrafStatement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Sarraf, 1),
            new PartyStatementFilter());
        Assert.Equal(-2_521_603.8409m, sarrafStatement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_Commission_DoesNotLeakIntoSupplierStatement()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        var model = BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 79.3146m, 79.3146m, "665-E");
        model.CommissionEnabled = true;
        model.CommissionType = PaymentCommissionType.Fixed;
        model.CommissionFixedAmount = 500m;
        model.CommissionCurrency = "USD";
        Assert.IsType<RedirectToActionResult>(await controller.Create(model));

        // تأمین‌کننده فقط سطر پرداخت را می‌بیند: نه بدهی صراف، نه کمیسیون.
        var supplierStatement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, 2),
            new PartyStatementFilter());
        var supplierRow = Assert.Single(supplierStatement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(PaymentsController.ViaSarrafSupplierLedgerSourceType, supplierRow.SourceType);
        Assert.Equal(2_521_603.8409m, supplierStatement.Summary.ClosingBalance);

        // صراف هر دو بدهی را نگه می‌دارد: اصل حواله + کمیسیون.
        var sarrafStatement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Sarraf, 1),
            new PartyStatementFilter());
        Assert.Equal(2, sarrafStatement.Rows.Count(r =>
            r.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType));
        Assert.Equal(-2_522_103.8409m, sarrafStatement.Summary.ClosingBalance);
    }

    [Theory]
    // ضرر: نرخ خرید شرکت ۷۶ — سود: نرخ خرید شرکت ۸۲. در هر دو حالت صورت‌حساب رسمی
    // تأمین‌کننده فقط مبلغ قبول‌شده را می‌بیند و رفتار دو‌نرخی دست‌نخورده می‌ماند.
    [InlineData("76", "2631578.9474")]
    [InlineData("82", "2439024.3902")]
    public async Task Create_Post_ViaSarraf_DualRate_StatementsStayUnchanged(
        string companyRate,
        string expectedSarrafClosing)
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        Assert.IsType<RedirectToActionResult>(await controller.Create(
            BuildDualRateModel(
                new DateTime(2025, 12, 23),
                200_000_000m,
                decimal.Parse(companyRate, CultureInfo.InvariantCulture),
                79.3146m,
                "665-F")));

        var supplierStatement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, 2),
            new PartyStatementFilter());
        var supplierRow = Assert.Single(supplierStatement.Rows.Where(r => !r.IsOpeningBalance));
        Assert.Equal(SarrafSettlementService.SupplierLedgerSourceType, supplierRow.SourceType);
        Assert.Equal(2_521_603.8409m, supplierStatement.Summary.ClosingBalance);

        var sarrafStatement = await BuildStatementService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Sarraf, 1),
            new PartyStatementFilter());
        Assert.Equal(
            -decimal.Parse(expectedSarrafClosing, CultureInfo.InvariantCulture),
            sarrafStatement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_Commission_ExpenseLedgerIsNotSupplierOwned()
    {
        await using var db = new ApplicationDbContext(NewDbOptions());
        var controller = await BuildDualRateFixtureAsync(db);

        // کمیسیون واقعی (نه تفاوت نرخ) — رفتار قبلی حفظ می‌شود، فقط سطر مصرف دیگر
        // به حساب تأمین‌کننده نمی‌چسبد.
        var model = BuildDualRateModel(new DateTime(2025, 12, 23), 200_000_000m, 79.3146m, 79.3146m, "665-B");
        model.CommissionEnabled = true;
        model.CommissionType = PaymentCommissionType.Fixed;
        model.CommissionFixedAmount = 500m;
        model.CommissionCurrency = "USD";

        Assert.IsType<RedirectToActionResult>(await controller.Create(model));

        var expenseLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == "Expense");
        Assert.Null(expenseLedger.SupplierId);
        Assert.Null(expenseLedger.ContractId);

        // تست ۸ — کمیسیون همچنان به بدهی صراف اضافه می‌شود و صندوق دست نمی‌خورد.
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
        Assert.Equal(2, await db.LedgerEntries.CountAsync(
            l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType));
        var commissionExpense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(500m, commissionExpense.AmountUsd);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_Usd_ReducesSupplierAndIncreasesSarrafPayable_SameCurrency()
    {
        var options = NewDbOptions();

        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier USD", IsActive = true });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A", IsActive = true });
        await db.SaveChangesAsync();

        var result = await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            PaymentMethod = PaymentMethod.ViaSarraf,
            SarrafId = 1,
            SupplierId = 2,
            SarrafSupplierAmount = 1500m,
            SarrafSupplierCurrency = "USD",
            Reference = "USD-H-001"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(await db.SarrafSettlements.ToListAsync());

        var supplierLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == PaymentsController.ViaSarrafSupplierLedgerSourceType);
        Assert.Equal(1500m, supplierLedger.SourceAmount);
        Assert.Equal("USD", supplierLedger.SourceCurrencyCode);
        Assert.Equal(2, supplierLedger.SupplierId);

        var sarrafPayableLedger = await db.LedgerEntries.SingleAsync(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType);
        Assert.Equal(1500m, sarrafPayableLedger.SourceAmount);
        Assert.Equal("USD", sarrafPayableLedger.SourceCurrencyCode);
        Assert.Equal(1, sarrafPayableLedger.SourceId);
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
    }

    // ===================== کمیسیون =====================

    [Fact]
    public async Task Create_Post_Cash_NoCommission_DoesNotCreateExpense()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var result = await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            Reference = "NC-1"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(1, await db.PaymentTransactions.CountAsync());
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
        var main = await db.PaymentTransactions.SingleAsync();
        Assert.Null(main.RelatedExpenseTransactionId);
    }

    [Fact]
    public async Task Create_Post_Cash_PercentCommission_CreatesExpenseAndCashOut_NoDoubleCount()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var result = await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            Reference = "PC-1",
            CommissionEnabled = true,
            CommissionType = PaymentCommissionType.Percent,
            CommissionPercent = 1m
        });

        Assert.IsType<RedirectToActionResult>(result);

        var main = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.SupplierPayment);
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(10m, expense.Amount);
        Assert.Equal("USD", expense.Currency);
        Assert.Equal(main.Id, expense.RelatedPaymentTransactionId);
        Assert.Equal(expense.Id, main.RelatedExpenseTransactionId);

        var cashOut = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.CommissionPayment);
        Assert.Equal(PaymentDirection.Out, cashOut.Direction);
        Assert.Equal(10m, cashOut.Amount);
        Assert.Equal(1, cashOut.CashAccountId);
        Assert.Equal(expense.Id, cashOut.ExpenseTransactionId);

        // P&L single-count: دقیقاً یک لِجر SourceType="Expense"؛ خروج نقدی جدا (CommissionPayment).
        Assert.Equal(1, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));
        Assert.Equal(1, await db.LedgerEntries.CountAsync(l => l.SourceType == nameof(PaymentKind.CommissionPayment)));

        // مانده صندوق: پرداخت اصلی ۱۰۰۰ + کمیسیون ۱۰ = ۱۰۱۰ خروج.
        var totalOut = await db.PaymentTransactions
            .Where(p => p.Direction == PaymentDirection.Out)
            .SumAsync(p => p.Amount);
        Assert.Equal(1010m, totalOut);
    }

    [Fact]
    public async Task Create_Post_Bank_FixedCommission_CreatesExpenseAndCashOut()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var result = await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 2000m,
            Currency = "USD",
            Reference = "FX-1",
            CommissionEnabled = true,
            CommissionType = PaymentCommissionType.Fixed,
            CommissionFixedAmount = 50m,
            CommissionCurrency = "USD"
        });

        Assert.IsType<RedirectToActionResult>(result);
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(50m, expense.Amount);
        var cashOut = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.CommissionPayment);
        Assert.Equal(50m, cashOut.Amount);
        Assert.Equal(1, cashOut.CashAccountId);
    }

    [Fact]
    public async Task Create_Post_ViaSarraf_WithCommission_AddsToSarrafPayable_NoCashOut()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        db.Suppliers.Add(new Supplier { Id = 2, Name = "Supplier USD", IsActive = true });
        db.Sarrafs.Add(new Sarraf { Id = 1, Name = "Sarraf A", IsActive = true });
        await db.SaveChangesAsync();

        var result = await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            PaymentMethod = PaymentMethod.ViaSarraf,
            SarrafId = 1,
            SupplierId = 2,
            SarrafSupplierAmount = 1500m,
            SarrafSupplierCurrency = "USD",
            Reference = "VS-C-1",
            CommissionEnabled = true,
            CommissionType = PaymentCommissionType.Fixed,
            CommissionFixedAmount = 30m,
            CommissionCurrency = "USD"
        });

        Assert.IsType<RedirectToActionResult>(result);
        // ViaSarraf: هیچ PaymentTransaction (نه اصلی نه کمیسیون). صندوق دست نمی‌خورد.
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());

        // کمیسیون به‌عنوان مصرف واقعی ثبت شده.
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(30m, expense.Amount);
        Assert.Equal(1, await db.LedgerEntries.CountAsync(l => l.SourceType == "Expense"));

        // بدهی صراف: پرداخت اصلی ۱۵۰۰ + کمیسیون ۳۰.
        var payableLedgers = await db.LedgerEntries
            .Where(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType)
            .ToListAsync();
        Assert.Equal(2, payableLedgers.Count);
        Assert.Equal(1530m, payableLedgers.Sum(l => l.SourceAmount ?? 0m));
    }

    [Theory]
    [InlineData(PaymentCommissionType.Percent, -1.0, null, "منفی درصد")]
    [InlineData(PaymentCommissionType.Percent, 150.0, null, "درصد بیش از ۱۰۰")]
    [InlineData(PaymentCommissionType.Fixed, null, -5.0, "مبلغ منفی")]
    public async Task Create_Post_InvalidCommission_ReturnsView_NoRecords(
        PaymentCommissionType type, double? percent, double? fixedAmount, string _)
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            CommissionEnabled = true,
            CommissionType = type,
            CommissionPercent = percent.HasValue ? (decimal)percent.Value : null,
            CommissionFixedAmount = fixedAmount.HasValue ? (decimal)fixedAmount.Value : null,
            CommissionCurrency = "USD"
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(0, await db.PaymentTransactions.CountAsync());
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
    }

    [Fact]
    public async Task Create_Post_CommissionEnabled_WithoutType_IsInvalid()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        var controller = BuildPaymentsController(db);
        var result = await controller.Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            CommissionEnabled = true,
            CommissionType = null
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
    }

    [Fact]
    public async Task Edit_Post_DisableCommission_RemovesCommissionRecords()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            CommissionEnabled = true,
            CommissionType = PaymentCommissionType.Percent,
            CommissionPercent = 1m
        });

        var main = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.SupplierPayment);
        Assert.NotNull(main.RelatedExpenseTransactionId);

        var editResult = await BuildPaymentsController(db).Edit(main.Id, new PaymentCreateViewModel
        {
            Id = main.Id,
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            CommissionEnabled = false
        });

        Assert.IsType<RedirectToActionResult>(editResult);
        var reloaded = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.SupplierPayment);
        Assert.Null(reloaded.RelatedExpenseTransactionId);
        Assert.Equal(0, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(0, await db.PaymentTransactions.CountAsync(p => p.PaymentKind == PaymentKind.CommissionPayment));
    }

    [Fact]
    public async Task Edit_Post_ChangeCommission_UpdatesWithoutDuplicate()
    {
        var options = NewDbOptions();
        await using var db = new ApplicationDbContext(options);
        SeedReferenceData(db);
        await db.SaveChangesAsync();

        await BuildPaymentsController(db).Create(new PaymentCreateViewModel
        {
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            CommissionEnabled = true,
            CommissionType = PaymentCommissionType.Fixed,
            CommissionFixedAmount = 20m,
            CommissionCurrency = "USD"
        });

        var main = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.SupplierPayment);

        await BuildPaymentsController(db).Edit(main.Id, new PaymentCreateViewModel
        {
            Id = main.Id,
            PaymentDate = new DateTime(2026, 6, 8),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CounterpartyType = PaymentCounterpartyType.Supplier,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 1000m,
            Currency = "USD",
            CommissionEnabled = true,
            CommissionType = PaymentCommissionType.Fixed,
            CommissionFixedAmount = 35m,
            CommissionCurrency = "USD"
        });

        // بدون duplicate: دقیقاً یک مصرف کمیسیون و یک خروج نقدی، با مبلغ به‌روزشده.
        var expense = await db.ExpenseTransactions.SingleAsync();
        Assert.Equal(35m, expense.Amount);
        var cashOut = await db.PaymentTransactions.SingleAsync(p => p.PaymentKind == PaymentKind.CommissionPayment);
        Assert.Equal(35m, cashOut.Amount);
    }

    private static PaymentsController BuildPaymentsController(ApplicationDbContext db)
        => new(db, new PricingService(db), new AuditService(db), NullLogger<PaymentsController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static LoadingController BuildLoadingController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new TestTempDataProvider())
        };

    private static IUrlHelper BuildUrlHelper()
        => new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));

    private static DbContextOptions<ApplicationDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static void SeedReferenceData(ApplicationDbContext db)
    {
        db.Currencies.AddRange(
            new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", IsActive = true },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil" });
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A" });
        db.Locations.Add(new Location { Id = 1, Name = "Herat" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "PORT", Name = "Port" });
        db.CashAccounts.AddRange(
            new CashAccount
            {
                Id = 1,
                Code = "BANK-USD",
                Name = "Main USD Bank",
                AccountType = CashAccountType.Bank,
                Currency = "USD",
                IsActive = true
            },
            new CashAccount
            {
                Id = 2,
                Code = "BANK-EUR",
                Name = "Main EUR Bank",
                AccountType = CashAccountType.Bank,
                Currency = "EUR",
                IsActive = true
            });
    }

    private static void SeedCustomerSaleWithLedger(ApplicationDbContext db)
    {
        db.Contracts.Add(new Contract
        {
            Id = 1,
            ContractNumber = "SAL-001",
            ContractType = ContractType.Sale,
            CompanyId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        db.SalesTransactions.Add(new SalesTransaction
        {
            Id = 1,
            CompanyId = 1,
            ContractId = 1,
            CustomerId = 1,
            ProductId = 1,
            DestinationLocationId = 1,
            InvoiceNumber = "INV-001",
            SaleDate = new DateTime(2026, 4, 20),
            QuantityMt = 10m,
            UnitPriceUsd = 500m,
            TotalUsd = 5000m
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 100,
            EntryDate = new DateTime(2026, 4, 20),
            Side = LedgerSide.Credit,
            AmountUsd = 5000m,
            Currency = "USD",
            SourceAmount = 5000m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = new DateTime(2026, 4, 20),
            Description = "Sale ledger",
            SourceType = "Sale",
            SourceId = 1,
            Reference = "INV-001",
            ContractId = 1,
            CustomerId = 1
        });
    }

    private static void SeedSupplierOpeningLedger(ApplicationDbContext db)
    {
        db.Contracts.Add(new Contract
        {
            Id = 2,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            SupplierId = 1,
            ProductId = 1,
            ContractDate = new DateTime(2026, 4, 20),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 450m
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 200,
            EntryDate = new DateTime(2026, 4, 20),
            Side = LedgerSide.Credit,
            AmountUsd = 2000m,
            Currency = "USD",
            SourceAmount = 2000m,
            SourceCurrencyCode = "USD",
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = new DateTime(2026, 4, 20),
            Description = "Opening supplier balance",
            SourceType = "OpeningBalance",
            SourceId = 200,
            Reference = "SUP-OPEN",
            ContractId = 2,
            SupplierId = 1
        });
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
