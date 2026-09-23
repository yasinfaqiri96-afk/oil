using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// یک چرخهٔ کاملِ مالی روی PostgreSQL: خرید → رسید → مصرف → ضایعه → فروش → دریافت از مشتری →
/// پرداخت به تأمین‌کننده → اثر ارزی → تخصیص سود شرکا. هر عدد از مرجعِ واحدش خوانده می‌شود و همهٔ
/// مصرف‌کننده‌ها باید با آن یکی باشند: نقد، ماندهٔ طرف‌حساب‌ها، ماندهٔ قرارداد، سود شرکت، سود
/// قرارداد، صورت‌حساب شراکت و ژورنال.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
[Trait("Category", "PostgreSql")]
[Trait("Category", "Integration")]
public sealed class FinancialLifecycleEndToEndTests(AccountingPostgreSqlFixture fixture)
{
    private static readonly DateTime LoadingDate = new(2026, 7, 2);
    private static readonly DateTime SaleDate = new(2026, 7, 10);

    [Fact]
    public async Task Every_Consumer_Reads_The_Same_Numbers_Across_A_Full_Contract_Lifecycle()
    {
        await using var db = fixture.CreateDbContext();
        var scope = await PaymentAccountingAdapterTests.CreateScopeAsync(db);
        var options = Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions
            {
                Purchase = true,
                InventoryReceipt = true,
                Sale = true,
                Cogs = true,
                Expense = true,
                CustomerReceipt = true,
                SupplierPayment = true,
                PartnershipProfitAllocation = true
            }
        });
        var posting = new AccountingPostingService(
            db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db));
        var numbers = new AccountingJournalNumberGenerator();
        var expenseAccounting = new ExpenseAccountingAdapter(
            db, posting, numbers, options, NullLogger<ExpenseAccountingAdapter>.Instance);
        var purchaseAccounting = new PurchaseAccountingAdapter(
            db, posting, numbers, new PricingService(db), new InventoryValuationService(db), options,
            NullLogger<PurchaseAccountingAdapter>.Instance);
        var salesAccounting = new SalesAccountingAdapter(
            db, posting, numbers, new InventoryValuationService(db), options, NullLogger<SalesAccountingAdapter>.Instance);
        var paymentAccounting = new PaymentAccountingAdapter(
            db, posting, numbers, new PaymentCompanyResolver(db), expenseAccounting, options,
            NullLogger<PaymentAccountingAdapter>.Instance);
        var profitAllocation = new PartnershipProfitAllocationAdapter(
            db, posting, numbers, new PartnershipStatementService(db), options,
            NullLogger<PartnershipProfitAllocationAdapter>.Instance);

        // ── قرارداد شراکتیِ ۵۰/۵۰ با قیمت نهایی ۵۰۰ ──
        var partnerA = new Partner { Code = PaymentAccountingAdapterTests.Unique("PA"), Name = PaymentAccountingAdapterTests.Unique("Partner"), IsActive = true };
        var partnerB = new Partner { Code = PaymentAccountingAdapterTests.Unique("PA"), Name = PaymentAccountingAdapterTests.Unique("Partner"), IsActive = true };
        db.Partners.AddRange(partnerA, partnerB);
        await db.SaveChangesAsync();
        var contract = await db.Contracts.SingleAsync(x => x.Id == scope.Contract.Id);
        contract.ManualFinalPriceUsd = 500m;
        contract.OwnershipType = ContractOwnershipType.Partnership;
        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = contract.Id, PartnerId = partnerA.Id, SharePercent = 50m, EffectiveFrom = new DateTime(2026, 1, 1) },
            new ContractPartner { ContractId = contract.Id, PartnerId = partnerB.Id, SharePercent = 50m, EffectiveFrom = new DateTime(2026, 1, 1) });
        await db.SaveChangesAsync();

        // ── ۱. خرید: ۱۰۰ تن × ۵۰۰ ──
        var loading = new LoadingRegister
        {
            ContractId = contract.Id,
            ProductId = scope.Product.Id,
            TransportType = LoadingTransportType.Truck,
            LoadingDate = LoadingDate,
            LoadedQuantityMt = 100m,
            LoadingPriceUsd = 500m,
            SettlementCurrencyCode = "USD"
        };
        db.LoadingRegisters.Add(loading);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(Ledger(SupplierLoadingLedger.SourceType, loading.Id, LedgerSide.Credit, 50_000m, LoadingDate,
            contract.Id, supplierId: scope.Supplier.Id));
        await db.SaveChangesAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await purchaseAccounting.TryPostPurchaseAsync(loading)).Status);

        // ── ۲. رسید به مخزن: ۹۸ تن؛ ۲ تن ضایعهٔ بارگیری ──
        var receipt = new LoadingReceipt
        {
            LoadingRegisterId = loading.Id,
            ReceiptDestination = LoadingReceiptDestination.ToInventory,
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            ReceiptDate = LoadingDate.AddDays(2),
            ReceivedQuantityMt = 98m
        };
        db.LoadingReceipts.Add(receipt);
        await db.SaveChangesAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await purchaseAccounting.TryPostInventoryReceiptAsync(receipt)).Status);
        db.InventoryMovements.Add(new InventoryMovement
        {
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            ProductId = scope.Product.Id,
            ContractId = contract.Id,
            LoadingReceiptId = receipt.Id,
            Direction = MovementDirection.In,
            MovementDate = receipt.ReceiptDate,
            QuantityMt = 98m
        });
        db.LossEvents.Add(new LossEvent
        {
            Stage = LossEventStage.LoadingDifference,
            ProductId = scope.Product.Id,
            EventDate = receipt.ReceiptDate,
            ExpectedQuantityMt = 100m,
            ActualQuantityMt = 98m,
            DifferenceQuantityMt = 2m,
            ChargeableLossMt = 2m,
            LoadingRegisterId = loading.Id,
            LoadingReceiptId = receipt.Id
        });
        await db.SaveChangesAsync();

        // ── ۳. مصرف: ۱۰۰۰ کرایه به خدمات‌دهنده ──
        var expenseType = new ExpenseType
        {
            Code = PaymentAccountingAdapterTests.Unique("ET"),
            Name = PaymentAccountingAdapterTests.Unique("Freight"),
            IsActive = true,
            PayableAccountKind = ExpensePayableKind.FreightPayable
        };
        db.ExpenseTypes.Add(expenseType);
        await db.SaveChangesAsync();
        var expense = new ExpenseTransaction
        {
            ExpenseDate = LoadingDate.AddDays(3),
            ExpenseTypeId = expenseType.Id,
            ContractId = contract.Id,
            ServiceProviderId = scope.ServiceProvider.Id,
            Amount = 1_000m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 1_000m
        };
        db.ExpenseTransactions.Add(expense);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(Ledger(LedgerEntryOwnership.ExpenseSourceType, expense.Id, LedgerSide.Credit, 1_000m,
            expense.ExpenseDate, contract.Id, serviceProviderId: scope.ServiceProvider.Id));
        await db.SaveChangesAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await expenseAccounting.TryPostExpenseAsync(expense)).Status);

        // ── ۴. فروش: ۶۰ تن × ۷۰۰ از مخزن ──
        var sale = new SalesTransaction
        {
            CompanyId = scope.Company.Id,
            CustomerId = scope.Customer.Id,
            ProductId = scope.Product.Id,
            SourcePurchaseContractId = contract.Id,
            InvoiceNumber = PaymentAccountingAdapterTests.Unique("INV"),
            SaleDate = SaleDate,
            QuantityMt = 60m,
            Currency = "USD",
            UnitPriceInCurrency = 700m,
            AppliedFxRateToUsd = 1m,
            UnitPriceUsd = 700m,
            TotalInCurrency = 42_000m,
            TotalUsd = 42_000m
        };
        db.SalesTransactions.Add(sale);
        await db.SaveChangesAsync();
        db.InventoryMovements.Add(new InventoryMovement
        {
            TerminalId = scope.Terminal.Id,
            StorageTankId = scope.Tank.Id,
            ProductId = scope.Product.Id,
            ContractId = contract.Id,
            SalesTransactionId = sale.Id,
            Direction = MovementDirection.Out,
            MovementDate = SaleDate,
            QuantityMt = 60m
        });
        db.LedgerEntries.Add(Ledger(LedgerEntryOwnership.SaleSourceType, sale.Id, LedgerSide.Credit, 42_000m, SaleDate,
            contract.Id, customerId: scope.Customer.Id));
        await db.SaveChangesAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await salesAccounting.TryPostSaleAsync(sale)).Status);
        Assert.Equal(PaymentPostingStatus.Posted, (await salesAccounting.TryPostCogsAsync(sale)).Status);

        // ── ۵. دریافت ۳۰٬۰۰۰ از مشتری و پرداخت ۴۰٬۰۰۰ به تأمین‌کننده ──
        var customerReceipt = await AddPaymentAsync(db, scope, PaymentKind.CustomerReceipt, PaymentDirection.In, 30_000m,
            p => p.CustomerId = scope.Customer.Id);
        var supplierPayment = await AddPaymentAsync(db, scope, PaymentKind.SupplierPayment, PaymentDirection.Out, 40_000m,
            p => p.SupplierId = scope.Supplier.Id);
        db.LedgerEntries.AddRange(
            Ledger(nameof(PaymentKind.CustomerReceipt), customerReceipt.Id, LedgerSide.Debit, 30_000m, SaleDate.AddDays(1),
                contract.Id, customerId: scope.Customer.Id),
            Ledger(nameof(PaymentKind.SupplierPayment), supplierPayment.Id, LedgerSide.Debit, 40_000m, SaleDate.AddDays(1),
                contract.Id, supplierId: scope.Supplier.Id));
        await db.SaveChangesAsync();
        Assert.Equal(PaymentPostingStatus.Posted, (await paymentAccounting.TryPostPaymentAsync(customerReceipt)).Status);
        Assert.Equal(PaymentPostingStatus.Posted, (await paymentAccounting.TryPostPaymentAsync(supplierPayment)).Status);

        // ── ۶. سود ارزیِ محقق ۲۰۰ روی همین قرارداد ──
        db.LedgerEntries.Add(Ledger(SupplierFxRecognitionService.GainLedgerSourceType, supplierPayment.Id, LedgerSide.Credit,
            200m, SaleDate.AddDays(2), contract.Id, supplierId: scope.Supplier.Id));
        await db.SaveChangesAsync();

        // ── ۷. تخصیص سود شرکا ──
        Assert.Equal(PaymentPostingStatus.Posted, (await profitAllocation.TryPostAllocationAsync(contract.Id)).Status);

        // ═══════════════ سود قرارداد: یک مرجع ═══════════════
        var economics = (await new ProfitAndLossService(db).BuildContractEconomicsAsync([contract.Id]))[contract.Id];
        Assert.Equal(60m, economics.SoldQuantityMt);
        Assert.Equal(42_000m, economics.RevenueUsd);
        Assert.Equal(0.6m, economics.SoldShareRatio);
        Assert.Equal(30_000m, economics.RealizedCostOfGoodsSoldUsd);
        Assert.Equal(1_000m, economics.GeneralExpenseCostUsd);
        Assert.Equal(1_000m, economics.LossCostUsd);
        Assert.Equal(1_200m, economics.RealizedOperationalCostUsd);
        Assert.Equal(200m, economics.RealizedFxNetUsd);
        Assert.Equal(11_000m, economics.RealizedNetProfitUsd);

        var reportRow = Assert.Single(Assert.IsType<ContractPnlReportViewModel>(Assert.IsType<ViewResult>(
                await new ReportsController(db).ContractPnl(new ManagementReportFilterViewModel { ContractId = contract.Id })).Model)
            .PurchaseRows);
        Assert.Equal(economics.RealizedNetProfitUsd, reportRow.RealizedNetProfitUsd);
        Assert.Equal(economics.LifecycleMarginUsd, reportRow.GrossMarginUsd);

        var partnership = (await new PartnershipStatementService(db).BuildForContractAsync(contract.Id))!;
        Assert.Equal(economics.RevenueUsd, partnership.SalesUsd);
        Assert.Equal(economics.RealizedNetProfitUsd, partnership.BookProfitUsd);
        Assert.Equal(partnership.BookProfitUsd, partnership.Partners.Sum(p => p.ProfitShareUsd));
        Assert.Equal(partnership.UnrealizedCostCarriedUsd, partnership.Partners.Sum(p => p.UnsoldCostShareUsd));

        // ژورنالِ تخصیص = همان سود، به همان سهم‌ها.
        var allocationLines = await db.JournalEntryLines.AsNoTracking()
            .Where(x => x.JournalEntry!.SourceModule == PartnershipProfitAllocationAdapter.SourceModule
                && x.JournalEntry.SourceEventId == PartnershipProfitAllocationAdapter.BuildSourceEventId(contract.Id)
                && x.PartyType == AccountingPartyType.Partner)
            .ToListAsync();
        Assert.Equal(partnership.BookProfitUsd, allocationLines.Sum(x => x.Credit - x.Debit));
        foreach (var partner in partnership.Partners)
        {
            Assert.Equal(partner.ProfitShareUsd, allocationLines.Where(x => x.PartyId == partner.PartnerId).Sum(x => x.Credit - x.Debit));
        }
        var discrepancy = (await profitAllocation.FindDiscrepanciesAsync()).Single(x => x.ContractId == contract.Id);
        Assert.Equal(PartnershipProfitAllocationDiscrepancy.Matches, discrepancy.Status);

        // ═══════════════ سود شرکت و ژورنال ═══════════════
        // سود شرکت فیلترِ شرکت ندارد (کلِ سیستم است) و دیتابیسِ این مجموعه بین تست‌ها مشترک است؛ پس
        // فروش با کالای یکتای همین تست و مصرف/ارز با قراردادِ همین تست محدود می‌شوند. تاریخ‌ها مثل
        // درخواستِ واقعی (UtcDateTimeModelBinder) UTC هستند.
        var fromUtc = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(new DateTime(2026, 12, 31), DateTimeKind.Utc);
        var company = await new ProfitAndLossService(db).BuildCompanyAsync(new ManagementReportFilterViewModel
        {
            FromDate = fromUtc,
            ToDate = toUtc,
            ProductId = scope.Product.Id
        });
        var companyForContract = await new ProfitAndLossService(db).BuildCompanyAsync(new ManagementReportFilterViewModel
        {
            FromDate = fromUtc,
            ToDate = toUtc,
            ContractId = contract.Id
        });
        var companyJournalLines = await db.JournalEntryLines.AsNoTracking()
            .Where(x => x.JournalEntry!.CompanyId == scope.Company.Id && x.JournalEntry.Status == JournalEntryStatus.Posted)
            .Select(x => new { x.AccountId, x.Debit, x.Credit, x.CashAccountId })
            .ToListAsync();
        Assert.Equal(companyJournalLines.Sum(x => x.Debit), companyJournalLines.Sum(x => x.Credit));
        Assert.Equal(42_000m, company.Sales.RevenueUsd);
        Assert.Equal(company.Sales.RevenueUsd,
            companyJournalLines.Where(x => x.AccountId == scope.Settings.SalesRevenueAccountId).Sum(x => x.Credit - x.Debit));
        Assert.Equal(company.Sales.CostOfGoodsSoldUsd,
            companyJournalLines.Where(x => x.AccountId == scope.Settings.CostOfGoodsSoldAccountId).Sum(x => x.Debit - x.Credit));
        Assert.Equal(1_000m, companyForContract.OperatingExpenseUsd);
        Assert.Equal(200m, companyForContract.ExchangeGainUsd);

        // ═══════════════ نقد ═══════════════
        var cash = Assert.Single(await new CashPositionReader(db).ReadAccountTotalsAsync([scope.CashAccount.Id], null));
        Assert.Equal(-10_000m, cash.BalanceUsd);
        Assert.Equal(cash.BalanceUsd,
            companyJournalLines.Where(x => x.CashAccountId == scope.CashAccount.Id).Sum(x => x.Debit - x.Credit));

        // ═══════════════ ماندهٔ طرف‌حساب‌ها و قرارداد ═══════════════
        var balances = await PartyBalanceReadService.CreateDefault(db).GetBalancesAsync(new ManagementReportFilterViewModel());
        var statements = PartyStatementReadService.CreateDefault(db);
        async Task<decimal> StatementClosingAsync(PartyStatementPartyType type, int id)
            => (await statements.GetStatementAsync(new PartyRef(type, id), new PartyStatementFilter { IncludeOperationalColumns = false }))
                .Summary.ClosingBalance;
        decimal BalanceOf(PartyStatementPartyType type, int id)
            => balances.Single(b => b.PartyType == type && b.PartyId == id).ClosingBalanceUsd;

        Assert.Equal(12_000m, BalanceOf(PartyStatementPartyType.Customer, scope.Customer.Id));
        Assert.Equal(-1_000m, BalanceOf(PartyStatementPartyType.ServiceProvider, scope.ServiceProvider.Id));
        Assert.Equal(BalanceOf(PartyStatementPartyType.Customer, scope.Customer.Id),
            await StatementClosingAsync(PartyStatementPartyType.Customer, scope.Customer.Id));
        Assert.Equal(BalanceOf(PartyStatementPartyType.Supplier, scope.Supplier.Id),
            await StatementClosingAsync(PartyStatementPartyType.Supplier, scope.Supplier.Id));
        Assert.Equal(BalanceOf(PartyStatementPartyType.ServiceProvider, scope.ServiceProvider.Id),
            await StatementClosingAsync(PartyStatementPartyType.ServiceProvider, scope.ServiceProvider.Id));

        var contractBalance = (await PartyBalanceReadService.CreateDefault(db).GetContractBalancesAsync([contract.Id]))[contract.Id];
        Assert.Equal(
            BalanceOf(PartyStatementPartyType.Customer, scope.Customer.Id)
                + BalanceOf(PartyStatementPartyType.Supplier, scope.Supplier.Id)
                + BalanceOf(PartyStatementPartyType.ServiceProvider, scope.ServiceProvider.Id),
            contractBalance.NetBalanceUsd);
        var contractStatement = Assert.IsType<ContractAccountStatementViewModel>(Assert.IsType<ViewResult>(
            await new AccountStatementsController(db, new PricingService(db), new AuditService(db)).Contract(contract.Id)).Model);
        Assert.Equal(contractBalance.NetBalanceUsd, contractStatement.Totals.BalanceUsd);
    }

    private static LedgerEntry Ledger(
        string sourceType,
        int sourceId,
        LedgerSide side,
        decimal amountUsd,
        DateTime date,
        int contractId,
        int? supplierId = null,
        int? customerId = null,
        int? serviceProviderId = null)
        => new()
        {
            EntryDate = date,
            Side = side,
            AmountUsd = amountUsd,
            Currency = "USD",
            SourceType = sourceType,
            SourceId = sourceId,
            ContractId = contractId,
            SupplierId = supplierId,
            CustomerId = customerId,
            ServiceProviderId = serviceProviderId,
            Description = sourceType
        };

    private static async Task<PaymentTransaction> AddPaymentAsync(
        ApplicationDbContext db,
        PaymentAccountingAdapterTests.PaymentScope scope,
        PaymentKind kind,
        PaymentDirection direction,
        decimal amountUsd,
        Action<PaymentTransaction> configure)
    {
        var payment = new PaymentTransaction
        {
            PaymentDate = SaleDate.AddDays(1),
            Direction = direction,
            PaymentKind = kind,
            CashAccountId = scope.CashAccount.Id,
            ContractId = scope.Contract.Id,
            FundingSource = PaymentFundingSource.Company,
            Amount = amountUsd,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd
        };
        configure(payment);
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }
}
