using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// انتساب سطر دفتر کل به طرف‌حساب — «قرارداد» به‌تنهایی طرفِ معامله نیست.
///
/// سطری که هیچ FK طرف‌حسابی ندارد فقط وقتی از راه قرارداد به تأمین‌کننده/مشتری می‌رسد که
/// واقعاً پول یا تعهدِ همان طرف باشد. پرداختِ نقدی این‌طور نیست: طرفِ واقعی‌اش را با FK خودش
/// حمل می‌کند و اگر نداشته باشد یعنی ندارد. پیش از این، «پرداخت کرایه موتر» که فقط ContractId
/// داشت بدهیِ تأمین‌کنندهٔ همان قرارداد را کم می‌کرد، و «مصرف» روی قرارداد فروش مطالبات مشتری
/// را باد می‌کرد (قرینهٔ AUD-04 در سمت مشتری وجود نداشت).
/// </summary>
public sealed class LedgerPartyAttributionTests
{
    private static readonly DateTime DocumentDate = new(2026, 5, 10);

    [Fact]
    public async Task TruckPaymentOnAPurchaseContract_DoesNotTouchTheContractSupplier()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedPurchaseAsync(db);

        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Debit,
            AmountUsd = 300m,
            Currency = "USD",
            ContractId = contract.Id,
            SourceType = nameof(PaymentKind.TruckPayment),
            SourceId = 1,
            Reference = "PAY-1",
            Description = "کرایه راننده"
        });
        await db.SaveChangesAsync();

        var summary = await SummaryAsync(db, PartyStatementPartyType.Supplier, supplier.Id);

        Assert.Equal(0m, summary.TotalOutflow);
        Assert.Equal(0m, summary.ClosingBalance);
    }

    [Fact]
    public async Task SupplierPaymentWithoutAnExplicitSupplier_StillReachesTheContractSupplier()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedPurchaseAsync(db);

        // سطرهای قدیمیِ «پرداخت به تأمین‌کننده» که SupplierId ندارند باید دقیقاً مثل قبل
        // خوانده شوند؛ فهرست جدید فقط اسنادِ نقدیِ غیرتأمین‌کننده را کنار می‌گذارد.
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Debit,
            AmountUsd = 700m,
            Currency = "USD",
            ContractId = contract.Id,
            SourceType = nameof(PaymentKind.SupplierPayment),
            SourceId = 2,
            Reference = "PAY-2",
            Description = "پرداخت به تأمین‌کننده"
        });
        await db.SaveChangesAsync();

        var summary = await SummaryAsync(db, PartyStatementPartyType.Supplier, supplier.Id);

        Assert.Equal(700m, summary.TotalOutflow);
        Assert.Equal(700m, summary.ClosingBalance);
    }

    [Fact]
    public async Task ExpenseOnASalesContract_DoesNotTouchTheContractCustomer()
    {
        await using var db = CreateDb();
        var (customer, contract) = await SeedSaleAsync(db);

        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Credit,
            AmountUsd = 400m,
            Currency = "USD",
            ContractId = contract.Id,
            SourceType = CompanyFlowSourceTypes.Expense,
            SourceId = 5,
            Reference = "EXP-5",
            Description = "هزینه حمل"
        });
        await db.SaveChangesAsync();

        var summary = await SummaryAsync(db, PartyStatementPartyType.Customer, customer.Id);

        Assert.Equal(0m, summary.TotalReceipt);
        Assert.Equal(0m, summary.ClosingBalance);
    }

    [Fact]
    public async Task ExplicitCustomerRowsOnASalesContract_AreStillRead()
    {
        await using var db = CreateDb();
        var (customer, contract) = await SeedSaleAsync(db);

        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Debit,
            AmountUsd = 900m,
            Currency = "USD",
            CustomerId = customer.Id,
            ContractId = contract.Id,
            SourceType = nameof(PaymentKind.CustomerReceipt),
            SourceId = 6,
            Reference = "REC-6",
            Description = "دریافت از مشتری"
        });
        await db.SaveChangesAsync();

        var summary = await SummaryAsync(db, PartyStatementPartyType.Customer, customer.Id);

        // دریافت از مشتری: Debit روی حساب دریافتنی = رسید، پس بیلانس منفی می‌شود.
        Assert.Equal(900m, summary.TotalReceipt);
        Assert.Equal(-900m, summary.ClosingBalance);
    }

    private static async Task<(Supplier Supplier, Contract Contract)> SeedPurchaseAsync(ApplicationDbContext db)
    {
        var company = new Company { Code = "CA", Name = "Company A" };
        var supplier = new Supplier { Name = "Supplier A" };
        db.AddRange(company, supplier);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = "P-ATTR",
            ContractType = ContractType.Purchase,
            CompanyId = company.Id,
            SupplierId = supplier.Id
        };
        db.Add(contract);
        await db.SaveChangesAsync();
        return (supplier, contract);
    }

    private static async Task<(Customer Customer, Contract Contract)> SeedSaleAsync(ApplicationDbContext db)
    {
        var company = new Company { Code = "CB", Name = "Company B" };
        var customer = new Customer { Name = "Customer B" };
        db.AddRange(company, customer);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = "S-ATTR",
            ContractType = ContractType.Sale,
            CompanyId = company.Id,
            CustomerId = customer.Id
        };
        db.Add(contract);
        await db.SaveChangesAsync();
        return (customer, contract);
    }

    private static async Task<PartyStatementSummary> SummaryAsync(
        ApplicationDbContext db,
        PartyStatementPartyType partyType,
        int partyId)
        => (await new PartyStatementReadService(
                db,
                new PartyStatementPolicyResolver(),
                new CompanyFlowDirectionResolver(),
                new CompanyFlowBalanceService(),
                Options.Create(new PartyStatementOptions()),
                new PartyDirectory(db))
            .GetStatementAsync(
                new PartyRef(partyType, partyId),
                new PartyStatementFilter { IncludeOperationalColumns = false }))
            .Summary;

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
