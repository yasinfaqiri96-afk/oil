using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// برگشتِ یک سند باید اثر همان سند را دقیقاً صفر کند.
///
/// چرا این تست لازم است: برگشتِ بارگیری/فروش/مصرف عمداً <c>SourceType</c> سند اصلی را نگه
/// می‌دارد تا ردیابی به همان سند حفظ شود، پس نوعِ سند به‌تنهایی نمی‌گوید این سطر برگشت است
/// و <see cref="CompanyFlowDirectionResolver"/> برای این سه نوع اصلاً به Debit/Credit نگاه
/// نمی‌کند. بدون نشانهٔ صریح، اصل و برگشت هر دو یک‌جهت خوانده می‌شدند و مانده طرف‌حساب
/// به‌جای صفر شدن، دو برابر می‌شد.
/// </summary>
public sealed class LedgerReversalNeutralityTests
{
    private static readonly DateTime Before = new(2026, 5, 1);
    private static readonly DateTime DocumentDate = new(2026, 5, 10);
    private static readonly DateTime ReversalDate = new(2026, 5, 20);

    // ============================================================ تشخیص صریح برگشت

    [Fact]
    public void ReversalMarker_IsExplicit_AndDoesNotDependOnLedgerSide()
    {
        // نویسندهٔ سطر برگشت و خوانندهٔ صورت‌حساب یک رشتهٔ واحد را می‌شناسند.
        Assert.Equal(CompanyFlowSourceTypes.ReversalReferenceSuffix, LedgerReversalWriter.CancelReferenceSuffix);
        Assert.Equal(CompanyFlowSourceTypes.ReversalReferenceSuffix, AssetRentLedgerFactory.CancelReferenceSuffix);

        Assert.True(CompanyFlowSourceTypes.IsReversal(CompanyFlowSourceTypes.Loading, "RWB-1-CANCEL"));
        Assert.True(CompanyFlowSourceTypes.IsReversal(CompanyFlowSourceTypes.Sale, "INV-9-CANCEL"));
        Assert.True(CompanyFlowSourceTypes.IsReversal(CompanyFlowSourceTypes.Expense, "EXP-3-CANCEL"));

        // سند اصلی هرگز برگشت خوانده نمی‌شود، حتی وقتی سمتش معکوس ثبت شده است.
        Assert.False(CompanyFlowSourceTypes.IsReversal(CompanyFlowSourceTypes.Loading, "RWB-1"));
        Assert.False(CompanyFlowSourceTypes.IsReversal(CompanyFlowSourceTypes.Expense, null));
    }

    // ============================================================ ۱) بارگیری + برگشت = ۰

    [Fact]
    public async Task Loading_PlusItsReversal_LeavesSupplierBalanceUnchanged()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedPurchaseAsync(db);

        var loading = new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Credit,
            AmountUsd = 500m,
            Currency = "USD",
            SupplierId = supplier.Id,
            ContractId = contract.Id,
            SourceType = CompanyFlowSourceTypes.Loading,
            SourceId = 11,
            Reference = "RWB-11",
            Description = "Loading"
        };
        db.LedgerEntries.AddRange(loading, Reversal(loading, "LOADING:11"));
        await db.SaveChangesAsync();

        var summary = await SupplierSummaryAsync(db, supplier.Id);

        Assert.Equal(0m, summary.ClosingBalance);
        Assert.Equal(500m, summary.TotalReceipt);
        Assert.Equal(500m, summary.TotalOutflow);
    }

    // ============================================================== ۲) مصرف + برگشت = ۰

    [Fact]
    public async Task Expense_PlusItsReversal_LeavesServiceProviderBalanceUnchanged()
    {
        await using var db = CreateDb();
        var provider = new ServiceProvider { Name = "Rail operator" };
        db.Add(provider);
        await db.SaveChangesAsync();

        var expense = new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Credit,
            AmountUsd = 923.08m,
            Currency = "USD",
            ServiceProviderId = provider.Id,
            SourceType = CompanyFlowSourceTypes.Expense,
            SourceId = 342,
            Reference = "EXP-342",
            Description = "Group expense"
        };
        db.LedgerEntries.AddRange(expense, Reversal(expense, "EXP-342"));
        await db.SaveChangesAsync();

        var summary = await SummaryAsync(db, PartyStatementPartyType.ServiceProvider, provider.Id);

        Assert.Equal(0m, summary.ClosingBalance);
        Assert.Equal(923.08m, summary.TotalReceipt);
        Assert.Equal(923.08m, summary.TotalOutflow);
    }

    // =============================================================== ۳) فروش + برگشت = ۰

    [Fact]
    public async Task Sale_PlusItsReversal_LeavesCustomerBalanceUnchanged()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Atlas Petroleum", Code = "CUST-1" };
        db.Add(customer);
        await db.SaveChangesAsync();

        var sale = new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Credit,
            AmountUsd = 1_000m,
            Currency = "USD",
            CustomerId = customer.Id,
            SourceType = CompanyFlowSourceTypes.Sale,
            SourceId = 7,
            Reference = "INV-7",
            Description = "Sale"
        };
        db.LedgerEntries.AddRange(sale, Reversal(sale, "SALE:7"));
        await db.SaveChangesAsync();

        var summary = await SummaryAsync(db, PartyStatementPartyType.Customer, customer.Id);

        Assert.Equal(0m, summary.ClosingBalance);
        Assert.Equal(1_000m, summary.TotalOutflow);
        Assert.Equal(1_000m, summary.TotalReceipt);
    }

    // ================================ ۴) مانده تجمعی به مقدار پیش از سند برمی‌گردد

    [Fact]
    public async Task RunningBalance_ReturnsToItsPreDocumentValue_AfterTheReversal()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedPurchaseAsync(db);

        // سند قبلی که باید دست‌نخورده بماند: بارگیری ۳۰۰ دلاری.
        var earlier = new LedgerEntry
        {
            EntryDate = Before,
            Side = LedgerSide.Credit,
            AmountUsd = 300m,
            Currency = "USD",
            SupplierId = supplier.Id,
            ContractId = contract.Id,
            SourceType = CompanyFlowSourceTypes.Loading,
            SourceId = 1,
            Reference = "RWB-1",
            Description = "Earlier loading"
        };
        var cancelled = new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Credit,
            AmountUsd = 500m,
            Currency = "USD",
            SupplierId = supplier.Id,
            ContractId = contract.Id,
            SourceType = CompanyFlowSourceTypes.Loading,
            SourceId = 2,
            Reference = "RWB-2",
            Description = "Loading to be cancelled"
        };
        db.LedgerEntries.AddRange(earlier, cancelled, Reversal(cancelled, "LOADING:2"));
        await db.SaveChangesAsync();

        var statement = await SupplierStatementAsync(db, supplier.Id);
        var rows = statement.Rows.Where(r => !r.IsOpeningBalance).OrderBy(r => r.Sequence).ToList();

        Assert.Equal(3, rows.Count);
        var beforeDocument = rows[0].RunningBalance;   // فقط سند قبلی
        var afterDocument = rows[1].RunningBalance;    // سند لغوشده هم اضافه شد
        var afterReversal = rows[2].RunningBalance;    // برگشت خورد

        Assert.Equal(-300m, beforeDocument);
        Assert.Equal(-800m, afterDocument);
        Assert.Equal(beforeDocument, afterReversal);
        Assert.Equal(-300m, statement.Summary.ClosingBalance);
    }

    // ======================= ۵) اسناد عادیِ برگشت‌نخورده هیچ تغییری نمی‌کنند

    [Fact]
    public async Task NormalDocuments_WithoutAReversal_KeepTheirExistingDirectionAndBalance()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedPurchaseAsync(db);

        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = Before,
                Side = LedgerSide.Credit,
                AmountUsd = 500m,
                Currency = "USD",
                SupplierId = supplier.Id,
                ContractId = contract.Id,
                SourceType = CompanyFlowSourceTypes.Loading,
                SourceId = 1,
                Reference = "RWB-1",
                Description = "Loading"
            },
            new LedgerEntry
            {
                EntryDate = DocumentDate,
                Side = LedgerSide.Debit,
                AmountUsd = 200m,
                Currency = "USD",
                SupplierId = supplier.Id,
                ContractId = contract.Id,
                SourceType = nameof(PaymentKind.SupplierPayment),
                SourceId = 2,
                Reference = "PAY-2",
                Description = "Payment"
            });
        await db.SaveChangesAsync();

        var summary = await SupplierSummaryAsync(db, supplier.Id);

        // بارگیری = رسید، پرداخت = برد، بیلانس = برد − رسید. دقیقاً مثل قبل از اصلاح.
        Assert.Equal(500m, summary.TotalReceipt);
        Assert.Equal(200m, summary.TotalOutflow);
        Assert.Equal(-300m, summary.ClosingBalance);
    }

    [Fact]
    public async Task ADocumentWhoseReferenceMerelyContainsCancel_IsStillTreatedAsOriginal()
    {
        await using var db = CreateDb();
        var (supplier, contract) = await SeedPurchaseAsync(db);

        // «CANCEL» داخل مرجع، نشانهٔ برگشت نیست؛ فقط پسوند پایانی شمرده می‌شود.
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = DocumentDate,
            Side = LedgerSide.Credit,
            AmountUsd = 400m,
            Currency = "USD",
            SupplierId = supplier.Id,
            ContractId = contract.Id,
            SourceType = CompanyFlowSourceTypes.Loading,
            SourceId = 5,
            Reference = "RWB-CANCEL-5",
            Description = "Loading with an odd reference"
        });
        await db.SaveChangesAsync();

        var summary = await SupplierSummaryAsync(db, supplier.Id);

        Assert.Equal(400m, summary.TotalReceipt);
        Assert.Equal(0m, summary.TotalOutflow);
        Assert.Equal(-400m, summary.ClosingBalance);
    }

    // ==================================================================== کمک‌کننده‌ها

    private static LedgerEntry Reversal(LedgerEntry original, string fallbackReference)
        => new()
        {
            EntryDate = ReversalDate,
            Side = original.Side == LedgerSide.Debit ? LedgerSide.Credit : LedgerSide.Debit,
            AmountUsd = original.AmountUsd,
            Currency = original.Currency,
            SourceType = original.SourceType,
            SourceId = original.SourceId,
            Reference = (original.Reference ?? fallbackReference) + LedgerReversalWriter.CancelReferenceSuffix,
            Description = $"Reversal of {original.SourceType} #{original.SourceId}",
            ContractId = original.ContractId,
            CustomerId = original.CustomerId,
            SupplierId = original.SupplierId,
            ServiceProviderId = original.ServiceProviderId
        };

    private static async Task<(Supplier Supplier, Contract Contract)> SeedPurchaseAsync(ApplicationDbContext db)
    {
        var company = new Company { Code = "C1", Name = "Company 1" };
        var supplier = new Supplier { Name = "Supplier one" };
        db.AddRange(company, supplier);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            ContractNumber = "P-REV",
            ContractType = ContractType.Purchase,
            CompanyId = company.Id,
            SupplierId = supplier.Id
        };
        db.Add(contract);
        await db.SaveChangesAsync();
        return (supplier, contract);
    }

    private static Task<PartyStatementResult> SupplierStatementAsync(ApplicationDbContext db, int supplierId)
        => StatementAsync(db, PartyStatementPartyType.Supplier, supplierId);

    private static async Task<PartyStatementSummary> SupplierSummaryAsync(ApplicationDbContext db, int supplierId)
        => (await SupplierStatementAsync(db, supplierId)).Summary;

    private static async Task<PartyStatementSummary> SummaryAsync(
        ApplicationDbContext db,
        PartyStatementPartyType partyType,
        int partyId)
        => (await StatementAsync(db, partyType, partyId)).Summary;

    private static Task<PartyStatementResult> StatementAsync(
        ApplicationDbContext db,
        PartyStatementPartyType partyType,
        int partyId)
        => BuildService(db).GetStatementAsync(
            new PartyRef(partyType, partyId),
            new PartyStatementFilter { IncludeOperationalColumns = false });

    private static PartyStatementReadService BuildService(ApplicationDbContext db)
        => new(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            Options.Create(new PartyStatementOptions()));

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
