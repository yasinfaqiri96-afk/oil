using Microsoft.EntityFrameworkCore;
using PartnerSettlementImport;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// ورود پرداخت‌های واقعی بین دو شریک از فایل مبدأ (Payment.xlsx).
///
/// این تست‌ها روی همان فایلِ واقعیِ مخزن اجرا می‌شوند — هیچ مبلغی اینجا ساخته نمی‌شود.
/// چیزی که اثبات می‌شود: هر ردیف دقیقاً یک‌بار ثبت می‌شود، اجرای دوباره تکراری نمی‌سازد،
/// جهت‌ها از ستون T-Credit/T-Debit می‌آید، مانده نهایی از سرویس درمی‌آید نه از عدد ثابت،
/// و هیچ سند مالیِ دیگری (فروش/مصرف/پرداخت/لجر) ساخته نمی‌شود.
/// </summary>
public sealed class PartnerSettlementImportTests
{
    private const string ReferencePrefix = "PAYMENT3-2026";

    // ————————————————— خواندن فایل مبدأ —————————————————

    [Fact]
    public void SourceFile_Yields_Fifteen_Meaningful_Rows_With_Unique_References()
    {
        var rows = ReadSourceRows();

        Assert.Equal(15, rows.Count);
        Assert.All(rows, r => Assert.True(r.Amount > 0m));

        var planned = PlanFor(rows);
        Assert.Equal(15, planned.Select(p => p.Reference).Distinct(StringComparer.Ordinal).Count());
        Assert.All(planned, p => Assert.StartsWith(ReferencePrefix + "-R", p.Reference, StringComparison.Ordinal));
    }

    [Fact]
    public void Direction_Comes_From_The_Amount_Column_Not_From_The_Description()
    {
        var rows = ReadSourceRows();
        var planned = PlanFor(rows);

        foreach (var item in planned)
        {
            if (item.Source.Column == SourceColumn.TCredit)
            {
                Assert.Equal(FawadId, item.FromPartnerId);
                Assert.Equal(YusufId, item.ToPartnerId);
            }
            else
            {
                Assert.Equal(YusufId, item.FromPartnerId);
                Assert.Equal(FawadId, item.ToPartnerId);
            }
        }

        // ردیفِ «Paid to Mr Rafi Nosrati» هم T-Credit است، پس همان جهت را دارد و شریک تازه‌ای نمی‌سازد.
        var rafi = planned.Single(p => p.Description.Contains("Rafi Nosrati", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FawadId, rafi.FromPartnerId);
        Assert.Equal(YusufId, rafi.ToPartnerId);
    }

    [Fact]
    public void Totals_Match_The_Source_File_In_Both_Directions()
    {
        var rows = ReadSourceRows();

        var credit = rows.Where(r => r.Column == SourceColumn.TCredit).Sum(r => r.Amount);
        var debit = rows.Where(r => r.Column == SourceColumn.TDebit).Sum(r => r.Amount);

        // مقدارِ خامِ سلول double است؛ تبدیل به decimal حدود ۱۶ رقمِ بامعنا نگه می‌دارد.
        // پس تطابق تا زیرِ یک‌صدمِ سنت بررسی می‌شود و بعد روی دقتِ واقعیِ ذخیره (چهار رقم).
        Assert.True(Math.Abs(credit - 1_018_719.3460490464m) < 0.000_001m, credit.ToString());
        Assert.Equal(460_082m, debit);
        Assert.Equal(1_018_719.3460m, decimal.Round(credit, 4, MidpointRounding.AwayFromZero));
        Assert.Equal(558_637.3460m, decimal.Round(credit - debit, 4, MidpointRounding.AwayFromZero));

        // جهتِ خالص: از فواد به یوسف.
        Assert.True(credit > debit);
    }

    [Fact]
    public void Jalali_Dates_Are_The_Source_Of_Truth()
    {
        var rows = ReadSourceRows();

        Assert.Equal(new DateTime(2026, 4, 11), rows.Single(r => r.RowNumber == 1).SettlementDate);
        Assert.Equal(new DateTime(2026, 6, 15), rows.Single(r => r.RowNumber == 10).SettlementDate);
        Assert.Equal(new DateTime(2026, 7, 21), rows.Single(r => r.RowNumber == 14).SettlementDate);
        Assert.Equal(new DateTime(2026, 8, 4), rows.Single(r => r.RowNumber == 17).SettlementDate);
    }

    [Fact]
    public void Source_Note_Column_Is_Kept_As_Note_And_Never_Used_As_Amount()
    {
        var rows = ReadSourceRows();
        var planned = PlanFor(rows);

        var row15 = planned.Single(p => p.Source.RowNumber == 15);
        Assert.Contains("137330", row15.Description, StringComparison.Ordinal);
        Assert.Equal(136_239.7820m, row15.AmountUsd);
    }

    // ————————————————— ثبت روی دیتابیس —————————————————

    [Fact]
    public async Task Import_Writes_Every_Row_Exactly_Once()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);

        var inserted = await ImportAsync(db, scenario);

        Assert.Equal(15, inserted);
        Assert.Equal(15, await db.PartnerSettlements.CountAsync());
        Assert.Equal(15, await db.PartnerSettlements.Select(s => s.Reference).Distinct().CountAsync());
        Assert.All(await db.PartnerSettlements.ToListAsync(), s => Assert.Null(s.ContractId));
    }

    [Fact]
    public async Task Running_The_Import_Again_Creates_No_Duplicates()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);

        await ImportAsync(db, scenario);
        var secondRun = await ImportAsync(db, scenario);

        Assert.Equal(0, secondRun);
        Assert.Equal(15, await db.PartnerSettlements.CountAsync());

        var evaluation = await SettlementImporter.EvaluateAsync(db, PlanFor(ReadSourceRows()));
        Assert.Equal(0, evaluation.Statuses.Values.Count(v => v == PlannedStatus.New));
        Assert.Equal(15, evaluation.Statuses.Values.Count(v => v == PlannedStatus.Exists));
        Assert.Equal(0, evaluation.Statuses.Values.Count(v => v == PlannedStatus.Conflict));
        Assert.Empty(evaluation.Conflicts);
    }

    [Fact]
    public async Task A_Changed_Amount_On_An_Existing_Reference_Is_A_Conflict_And_Blocks_The_Write()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);
        await ImportAsync(db, scenario);

        var victim = await db.PartnerSettlements.OrderBy(s => s.Id).FirstAsync();
        victim.AmountUsd += 1m;
        await db.SaveChangesAsync();

        var planned = PlanFor(ReadSourceRows());
        var evaluation = await SettlementImporter.EvaluateAsync(db, planned);

        Assert.Equal(1, evaluation.Statuses.Values.Count(v => v == PlannedStatus.Conflict));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SettlementImporter.ApplyAsync(db, new AuditService(db), planned, evaluation));
        Assert.Equal(15, await db.PartnerSettlements.CountAsync());
    }

    [Fact]
    public async Task Stored_Totals_Match_The_File_In_Both_Directions()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);
        await ImportAsync(db, scenario);

        var fawadToYusuf = await db.PartnerSettlements
            .Where(s => s.FromPartnerId == scenario.FawadId && s.ToPartnerId == scenario.YusufId)
            .SumAsync(s => s.AmountUsd);
        var yusufToFawad = await db.PartnerSettlements
            .Where(s => s.FromPartnerId == scenario.YusufId && s.ToPartnerId == scenario.FawadId)
            .SumAsync(s => s.AmountUsd);

        // ستون دیتابیس numeric(18,4) است، پس مبالغ روی چهار رقم اعشار ثبت می‌شوند.
        Assert.Equal(1_018_719.3460m, fawadToYusuf);
        Assert.Equal(460_082.0000m, yusufToFawad);
        Assert.Equal(558_637.3460m, fawadToYusuf - yusufToFawad);
    }

    // ————————————————— اثر روی صورت‌حساب و پروفایل —————————————————

    [Fact]
    public async Task Combined_Statement_Flips_The_Direction_After_The_Import()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);

        var before = await BuildAsync(db, scenario);
        Assert.Equal(scenario.FawadId, before.DebtorPartnerId);

        await ImportAsync(db, scenario);
        var after = await BuildAsync(db, scenario);

        // فواد بیش از بدهی‌اش پرداخت کرده، پس جهت برمی‌گردد.
        Assert.Equal(scenario.YusufId, after.DebtorPartnerId);
        Assert.Equal(scenario.FawadId, after.CreditorPartnerId);
    }

    [Fact]
    public async Task Final_Amount_Comes_From_The_Service_Formula_Not_From_A_Constant()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);
        var before = await BuildAsync(db, scenario);
        var openingDue = before.AmountDueUsd;

        await ImportAsync(db, scenario);
        var after = await BuildAsync(db, scenario);

        var netSettlement = await db.PartnerSettlements
            .Where(s => !s.IsReversed && s.FromPartnerId == scenario.FawadId)
            .SumAsync(s => s.AmountUsd)
            - await db.PartnerSettlements
                .Where(s => !s.IsReversed && s.ToPartnerId == scenario.FawadId)
                .SumAsync(s => s.AmountUsd);

        // مانده نهایی = مابه‌التفاوتِ پرداختِ خالص و بدهیِ اولیه؛ همان چیزی که سرویس می‌سازد.
        Assert.Equal(
            decimal.Round(netSettlement - openingDue, 2, MidpointRounding.AwayFromZero),
            after.AmountDueUsd);

        // و همان عدد باید مستقیماً از فرمولِ NetPosition هم دربیاید.
        var debtor = after.Totals.Single(t => t.PartnerId == after.DebtorPartnerId);
        Assert.Equal(Math.Abs(debtor.NetPositionUsd), after.AmountDueUsd);
    }

    [Fact]
    public async Task Both_Partner_Profiles_See_The_Imported_Settlements()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);
        await ImportAsync(db, scenario);

        var service = new PartnershipStatementService(db);

        var fawad = await service.BuildForPartnerAsync(scenario.FawadId);
        var yusuf = await service.BuildForPartnerAsync(scenario.YusufId);

        Assert.NotNull(fawad);
        Assert.NotNull(yusuf);
        // سرویس ارقام را روی دو رقم اعشار گرد می‌کند.
        Assert.Equal(1_018_719.35m, fawad!.SettlementsPaidUsd);
        Assert.Equal(460_082.00m, fawad.SettlementsReceivedUsd);
        Assert.Equal(460_082.00m, yusuf!.SettlementsPaidUsd);
        Assert.Equal(1_018_719.35m, yusuf.SettlementsReceivedUsd);
    }

    [Fact]
    public async Task Partner_Account_Entries_Show_All_Fifteen_Settlement_Rows()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);
        await ImportAsync(db, scenario);

        var service = new PartnershipStatementService(db);
        var fawad = await service.BuildForPartnerAsync(scenario.FawadId);

        var settlementEntries = fawad!.Entries
            .Where(e => e.Kind == PartnershipStatementLineKind.PartnerSettlement)
            .ToList();

        Assert.Equal(15, settlementEntries.Count);
        Assert.Equal(15, settlementEntries.Select(e => e.Reference).Distinct().Count());
    }

    [Fact]
    public async Task Unreconciled_Residual_Is_Untouched_By_The_Import()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);

        var before = await BuildAsync(db, scenario);
        await ImportAsync(db, scenario);
        var after = await BuildAsync(db, scenario);

        // تسویهٔ بین شرکا در دو طرف با هم صفر می‌شود، پس تفاوتِ تطبیق با دفتر ثابت می‌ماند.
        Assert.Equal(before.UnreconciledResidualUsd, after.UnreconciledResidualUsd);
    }

    // ————————————————— چیزهایی که نباید تکان بخورند —————————————————

    [Fact]
    public async Task Import_Creates_No_Sale_Expense_Payment_Or_Ledger_Rows()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);

        var sales = await db.SalesTransactions.CountAsync();
        var expenses = await db.ExpenseTransactions.CountAsync();
        var payments = await db.PaymentTransactions.CountAsync();
        var ledger = await db.LedgerEntries.CountAsync();
        var loadings = await db.LoadingRegisters.CountAsync();
        var inventory = await db.InventoryMovements.CountAsync();

        await ImportAsync(db, scenario);

        Assert.Equal(sales, await db.SalesTransactions.CountAsync());
        Assert.Equal(expenses, await db.ExpenseTransactions.CountAsync());
        Assert.Equal(payments, await db.PaymentTransactions.CountAsync());
        Assert.Equal(ledger, await db.LedgerEntries.CountAsync());
        Assert.Equal(loadings, await db.LoadingRegisters.CountAsync());
        Assert.Equal(inventory, await db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Import_Leaves_Contract_Profit_And_Proceeds_Holder_Unchanged()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);

        var before = await BuildAsync(db, scenario);
        var beforeByContract = before.Contracts.ToDictionary(
            c => c.ContractId,
            c => (c.BookProfitUsd, c.SalesUsd, c.PurchaseCostUsd, c.OperationalExpenseUsd,
                  c.ProceedsHolderPartnerId,
                  Shares: c.Partners.ToDictionary(p => p.PartnerId, p => p.ProfitShareUsd),
                  Funding: c.Partners.ToDictionary(p => p.PartnerId, p => p.FundingUsd)));

        await ImportAsync(db, scenario);
        var after = await BuildAsync(db, scenario);

        foreach (var contract in after.Contracts)
        {
            var expected = beforeByContract[contract.ContractId];
            Assert.Equal(expected.BookProfitUsd, contract.BookProfitUsd);
            Assert.Equal(expected.SalesUsd, contract.SalesUsd);
            Assert.Equal(expected.PurchaseCostUsd, contract.PurchaseCostUsd);
            Assert.Equal(expected.OperationalExpenseUsd, contract.OperationalExpenseUsd);
            Assert.Equal(expected.ProceedsHolderPartnerId, contract.ProceedsHolderPartnerId);

            foreach (var partner in contract.Partners)
            {
                Assert.Equal(expected.Shares[partner.PartnerId], partner.ProfitShareUsd);
                Assert.Equal(expected.Funding[partner.PartnerId], partner.FundingUsd);
            }
        }
    }

    [Fact]
    public async Task Import_Writes_One_Audit_Entry_Per_Settlement()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);

        await ImportAsync(db, scenario);

        var audits = await db.AuditLogs
            .Where(a => a.EntityName == nameof(PartnerSettlement))
            .ToListAsync();

        Assert.Equal(15, audits.Count);
        Assert.All(audits, a => Assert.Equal(AuditAction.Insert.ToString(), a.Action));
    }

    // ————————————————— برگشتِ تسویه —————————————————

    [Fact]
    public async Task Reversing_A_Settlement_Removes_Its_Effect_But_Keeps_The_History()
    {
        await using var db = CreateDb();
        var scenario = await SeedAsync(db);
        await ImportAsync(db, scenario);

        var afterImport = await BuildAsync(db, scenario);

        var target = await db.PartnerSettlements.OrderBy(s => s.Id).FirstAsync();
        target.IsReversed = true;
        target.ReversedAtUtc = DateTime.UtcNow;
        target.ReversalReason = "تست برگشت";
        await db.SaveChangesAsync();

        var afterReversal = await BuildAsync(db, scenario);

        // مبلغ از مانده خارج می‌شود…
        Assert.Equal(
            decimal.Round(afterImport.Totals.Single(t => t.PartnerId == target.FromPartnerId).SettlementsPaidUsd - target.AmountUsd, 2),
            afterReversal.Totals.Single(t => t.PartnerId == target.FromPartnerId).SettlementsPaidUsd);

        // …ولی رکورد و تاریخچه‌اش سرِ جایش می‌ماند.
        Assert.Equal(15, await db.PartnerSettlements.CountAsync());
        Assert.Equal(afterImport.Settlements.Count, afterReversal.Settlements.Count);
        Assert.Contains(afterReversal.Settlements, s => s.Id == target.Id && s.IsReversed);
    }

    // ————————————————— helpers —————————————————

    private const int FawadId = 1;
    private const int YusufId = 2;

    private static IReadOnlyList<SettlementSourceRow> ReadSourceRows()
    {
        using var stream = File.OpenRead(SourceFilePath());
        return SettlementSourceReader.Read(stream);
    }

    private static string SourceFilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Payment.xlsx");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Payment.xlsx در ریشهٔ مخزن پیدا نشد.");
    }

    private static IReadOnlyList<PlannedSettlement> PlanFor(IReadOnlyList<SettlementSourceRow> rows)
        => SettlementImporter.Plan(
            rows,
            new Partner { Id = FawadId, Code = "PA001", Name = "گروپ کمپنی های فواد صدیقی" },
            new Partner { Id = YusufId, Code = "PA002", Name = "شرکت یوسف اسماعیل" },
            ReferencePrefix);

    private static async Task<int> ImportAsync(ApplicationDbContext db, Scenario scenario)
    {
        var fawad = await db.Partners.SingleAsync(p => p.Id == scenario.FawadId);
        var yusuf = await db.Partners.SingleAsync(p => p.Id == scenario.YusufId);

        var planned = SettlementImporter.Plan(ReadSourceRows(), fawad, yusuf, ReferencePrefix);
        var evaluation = await SettlementImporter.EvaluateAsync(db, planned);
        return await SettlementImporter.ApplyAsync(db, new AuditService(db), planned, evaluation);
    }

    private static async Task<PartnershipStatement> BuildAsync(ApplicationDbContext db, Scenario scenario)
    {
        var statement = await new PartnershipStatementService(db).BuildAsync(scenario.FawadId, scenario.YusufId);
        Assert.NotNull(statement);
        return statement!;
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Scenario(int FawadId, int YusufId, int ContractId);

    /// <summary>
    /// یک شراکتِ ۵۰/۵۰ با یک قراردادِ ساده که در آن عایدِ فروش نزد فواد مانده است، پس فواد
    /// بدهکارِ اولیه است و بدهی‌اش از خالصِ پرداخت‌های فایل کمتر است — تا برگشتِ جهت اثبات شود.
    /// ارقام صرفاً سناریوی تست‌اند، نه دادهٔ Production.
    /// </summary>
    private static async Task<Scenario> SeedAsync(ApplicationDbContext db)
    {
        var company = new Company { Code = "PTG", Name = "PTG" };
        var product = new Product { Code = "MO", Name = "Base Oil" };
        var supplier = new Supplier { Name = "Refinery", IsActive = true };
        var customer = new Customer { Name = "Buyer", IsActive = true };
        var fawad = new Partner { Code = "PA001", Name = "گروپ کمپنی های فواد صدیقی", IsActive = true };
        var yusuf = new Partner { Code = "PA002", Name = "شرکت یوسف اسماعیل", IsActive = true };
        db.AddRange(company, product, supplier, customer, fawad, yusuf);
        await db.SaveChangesAsync();

        var contract = NewPartnershipContract(company.Id, product.Id, supplier.Id, "P-016");
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        contract.SaleProceedsHolderPartnerId = fawad.Id;

        db.ContractPartners.AddRange(
            new ContractPartner { ContractId = contract.Id, PartnerId = fawad.Id, SharePercent = 50m },
            new ContractPartner { ContractId = contract.Id, PartnerId = yusuf.Id, SharePercent = 50m });

        db.SalesTransactions.Add(
            NewSale(company.Id, product.Id, customer.Id, "GSALE-16", 500m, 500_000m, contract.Id));

        await db.SaveChangesAsync();
        return new Scenario(fawad.Id, yusuf.Id, contract.Id);
    }

    private static Contract NewPartnershipContract(int companyId, int productId, int supplierId, string number)
        => new()
        {
            CompanyId = companyId,
            ProductId = productId,
            SupplierId = supplierId,
            ContractNumber = number,
            ContractDate = new DateTime(2026, 3, 1),
            OwnershipType = ContractOwnershipType.Partnership,
            Currency = "USD"
        };

    private static SalesTransaction NewSale(
        int companyId,
        int productId,
        int customerId,
        string number,
        decimal quantity,
        decimal amountUsd,
        int? sourceContractId)
        => new()
        {
            CompanyId = companyId,
            ProductId = productId,
            CustomerId = customerId,
            InvoiceNumber = number,
            SaleDate = new DateTime(2026, 7, 1),
            QuantityMt = quantity,
            UnitPriceUsd = quantity == 0m ? 0m : amountUsd / quantity,
            TotalUsd = amountUsd,
            Currency = "USD",
            TotalInCurrency = amountUsd,
            AppliedFxRateToUsd = 1m,
            SourcePurchaseContractId = sourceContractId
        };
}
