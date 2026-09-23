using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Balance;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.ContractClosure;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «ماندهٔ قرارداد» یک مرجع دارد: جمعِ ماندهٔ رسمیِ طرف‌حساب‌های همان قرارداد
/// (<see cref="IPartyBalanceReadService.GetContractBalancesAsync"/>). گزارش مانده قراردادها،
/// پروندهٔ قرارداد و بستن قرارداد باید همان عدد و همان علامت را بدهند:
/// مثبت = طلب شرکت، منفی = بدهی شرکت.
/// </summary>
public sealed class ContractBalanceCanonicalTests
{
    private const int ContractId = 1;

    [Fact]
    public async Task Balance_Report_And_Closure_Use_The_Same_Official_Per_Party_Balances()
    {
        await using var db = await SeededDbAsync();
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Port Services" });
        db.LedgerEntries.AddRange(
            // بار تحویل شد (تعهد 1000 به تأمین‌کننده) و 400 پرداخت شد ⇒ 600 بدهی.
            Ledger(1, CompanyFlowSourceTypes.Loading, LedgerSide.Credit, 1000m, supplierId: 1),
            Ledger(2, nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, 400m, supplierId: 1),
            // خدمت بندر دریافت شد و هنوز پرداخت نشده ⇒ 100 بدهی.
            Ledger(3, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 100m, serviceProviderId: 1));
        await db.SaveChangesAsync();

        var official = (await PartyBalanceReadService.CreateDefault(db).GetContractBalancesAsync([ContractId]))[ContractId];
        var supplierStatement = await PartyStatementReadService.CreateDefault(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, 1),
            new PartyStatementFilter { ContractId = ContractId, IncludeOperationalColumns = false });
        var report = Assert.IsType<ContractsBalanceViewModel>(Assert.IsType<ViewResult>(
            await new BalanceController(db) { ControllerContext = NewContext() }.Contracts()).Model);
        var closure = await new ContractClosureService(db, new AuditService(db), new StockService(db)).EvaluateAsync(ContractId);

        Assert.Equal(-700m, official.NetBalanceUsd);
        Assert.Equal(-600m, official.Parties.Single(p => p.PartyType == PartyStatementPartyType.Supplier).ClosingBalanceUsd);
        Assert.Equal(supplierStatement.Summary.ClosingBalance,
            official.Parties.Single(p => p.PartyType == PartyStatementPartyType.Supplier).ClosingBalanceUsd);
        Assert.Equal(official.NetBalanceUsd, Assert.Single(report.Items).BaseBalanceUsd);
        Assert.Equal(official.NetBalanceUsd, report.FilteredBalanceUsd);
        Assert.False(closure!.CanClose);
        var supplierBlocker = Assert.Single(closure.Blockers, b => b.Message.Contains("Supplier A"));
        Assert.Contains("600.00 USD", supplierBlocker.Message);
        Assert.Contains("قابل پرداخت", supplierBlocker.Message);
        var providerBlocker = Assert.Single(closure.Blockers, b => b.Message.Contains("Port Services"));
        Assert.Contains("100.00 USD", providerBlocker.Message);
    }

    [Fact]
    public async Task Contract_Account_Statement_Closing_Equals_The_Official_Contract_Balance()
    {
        await using var db = await SeededDbAsync();
        db.ServiceProviders.Add(new ServiceProvider { Id = 1, Name = "Port Services" });
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Loading, LedgerSide.Credit, 1000m, supplierId: 1),
            Ledger(2, nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, 400m, supplierId: 1),
            Ledger(3, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 100m, serviceProviderId: 1));
        await db.SaveChangesAsync();

        var official = (await PartyBalanceReadService.CreateDefault(db).GetContractBalancesAsync([ContractId]))[ContractId];
        var statement = Assert.IsType<PTGOilSystem.Web.Models.AccountStatements.ContractAccountStatementViewModel>(
            Assert.IsType<ViewResult>(await new AccountStatementsController(db, new PricingService(db), new AuditService(db))
                .Contract(ContractId)).Model);

        // «بیلانس فعلی» صورت‌حساب قرارداد همان ماندهٔ رسمیِ قرارداد است (علامت: مثبت = طلب شرکت).
        // تنها تفاوتِ عمدی: پای «بدهی به صراف» که صورت‌حساب قرارداد به حساب صراف می‌سپارد.
        Assert.Equal(-700m, statement.Totals.BalanceUsd);
        Assert.Equal(official.NetBalanceUsd, statement.Totals.BalanceUsd);
    }

    [Fact]
    public async Task Closure_Allows_A_Contract_The_Official_Statement_Shows_As_Settled()
    {
        await using var db = await SeededDbAsync();
        // سطرِ بارگیریِ قدیمی بدون SupplierId (انتساب از راهِ قرارداد) و پرداختِ با SupplierId.
        // صورت‌حساب رسمی هر دو را مال همان تأمین‌کننده می‌داند ⇒ تسویه. فرمولِ خامِ قبلی آن‌ها را
        // دو گروهِ جدا می‌دید و قرارداد را باز نگه می‌داشت.
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Loading, LedgerSide.Credit, 1000m),
            Ledger(2, nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, 1000m, supplierId: 1));
        await db.SaveChangesAsync();

        var official = (await PartyBalanceReadService.CreateDefault(db).GetContractBalancesAsync([ContractId]))[ContractId];
        var closure = await new ContractClosureService(db, new AuditService(db), new StockService(db)).EvaluateAsync(ContractId);

        Assert.Equal(0m, official.NetBalanceUsd);
        Assert.DoesNotContain(closure!.Blockers, b => b.Kind == ContractClosureBlockerKind.Balance);
    }

    [Fact]
    public async Task Closure_Blocks_A_Contract_The_Official_Statement_Shows_As_Open()
    {
        await using var db = await SeededDbAsync();
        // دو سندِ کرایهٔ راننده؛ یکی با سمتِ قدیمیِ Debit. Debit/Credit خام صفر می‌شد، ولی از نظر
        // صورت‌حسابِ رسمی هر دو «خدمتِ دریافت‌شده» است ⇒ شرکت 1000 به راننده بدهکار است.
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Expense, LedgerSide.Credit, 500m, driverId: 1),
            Ledger(2, CompanyFlowSourceTypes.Expense, LedgerSide.Debit, 500m, driverId: 1));
        await db.SaveChangesAsync();

        var driverStatement = await PartyStatementReadService.CreateDefault(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Driver, 1),
            new PartyStatementFilter { ContractId = ContractId, IncludeOperationalColumns = false });
        var closure = await new ContractClosureService(db, new AuditService(db), new StockService(db)).EvaluateAsync(ContractId);

        Assert.Equal(-1000m, driverStatement.Summary.ClosingBalance);
        var blocker = Assert.Single(closure!.Blockers, b => b.Message.Contains("Driver A"));
        Assert.Contains("1,000.00 USD", blocker.Message);
        Assert.Contains("قابل پرداخت", blocker.Message);
    }

    [Fact]
    public async Task Customer_Debt_On_A_Contract_Is_A_Receivable_Blocker()
    {
        await using var db = await SeededDbAsync();
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.LedgerEntries.AddRange(
            Ledger(1, CompanyFlowSourceTypes.Sale, LedgerSide.Credit, 900m, customerId: 1),
            Ledger(2, nameof(PaymentKind.CustomerReceipt), LedgerSide.Debit, 300m, customerId: 1));
        await db.SaveChangesAsync();

        var official = (await PartyBalanceReadService.CreateDefault(db).GetContractBalancesAsync([ContractId]))[ContractId];
        var closure = await new ContractClosureService(db, new AuditService(db), new StockService(db)).EvaluateAsync(ContractId);

        Assert.Equal(600m, official.NetBalanceUsd);
        var blocker = Assert.Single(closure!.Blockers, b => b.Message.Contains("Customer A"));
        Assert.Contains("قابل دریافت", blocker.Message);
    }

    private static async Task<ApplicationDbContext> SeededDbAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Units.Add(new Unit { Id = 1, Code = "MT", Name = "Metric Ton", Symbol = "MT", IsActive = true });
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", IsActive = true });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", UnitId = 1, UnitOfMeasure = "MT", IsActive = true });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A", IsActive = true });
        db.Drivers.Add(new Driver { Id = 1, FullName = "Driver A" });
        db.Contracts.Add(new Contract
        {
            Id = ContractId,
            ContractName = "قرارداد PUR-001",
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            Status = ContractStatus.Active,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 1, 1),
            QuantityMt = 100m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static LedgerEntry Ledger(
        int id,
        string sourceType,
        LedgerSide side,
        decimal amountUsd,
        int? supplierId = null,
        int? customerId = null,
        int? serviceProviderId = null,
        int? driverId = null)
        => new()
        {
            Id = id,
            EntryDate = new DateTime(2026, 2, id),
            Side = side,
            AmountUsd = amountUsd,
            SourceType = sourceType,
            SourceId = id,
            ContractId = ContractId,
            SupplierId = supplierId,
            CustomerId = customerId,
            ServiceProviderId = serviceProviderId,
            DriverId = driverId,
            Description = sourceType
        };

    private static ControllerContext NewContext() => new() { HttpContext = new DefaultHttpContext() };
}
