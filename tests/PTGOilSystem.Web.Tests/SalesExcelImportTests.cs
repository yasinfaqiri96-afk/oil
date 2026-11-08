using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Imports;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Operations;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class SalesExcelImportTests
{
    private static readonly DateTime Day = new(2025, 12, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Headers = ["ImportKey", "ContractNumber", "SaleDateSolar", "BuyerName", "TicketSerial", "TruckNumber", "DriverName", "QuantityMt", "UnitPriceUSD", "SourceAmountUSD", "CalculatedAmountUSD", "DifferenceUSD", "SourceLocationNote", "OriginalDescription", "OriginalExcelRow", "ReviewStatus"];

    [Fact]
    public void Workbook_Reads_173_Independent_Rows_23_Reviews_And_Ignores_Balance_Sheets()
    {
        using var file = Workbook(173, (i, values) => { if (i <= 23) values[15] = "بازبینی پیش از ثبت"; });
        var rows = SalesExcelWorkbookParser.Parse(file);
        Assert.Equal(173, rows.Count);
        Assert.Equal(23, rows.Count(r => r.RequiresReview));
        Assert.Equal(1730m, rows.Sum(r => r.SourceAmountUsd));
        Assert.Equal(Day, rows[0].SaleDate);
        Assert.Equal("موتر", rows[0].TruckNumber);
        Assert.Equal("راننده", rows[0].DriverName);
    }

    [Fact]
    public void Four_Shared_Tickets_Are_Preserved_As_Eight_Sales_With_Independent_Keys()
    {
        using var file = Workbook(8, (i, v) => v[4] = ((i + 1) / 2).ToString());
        var rows = SalesExcelWorkbookParser.Parse(file);
        Assert.Equal(8, rows.Count); Assert.Equal(4, rows.Select(r => r.TicketSerial).Distinct().Count());
        Assert.All(rows, r => Assert.True(r.RequiresReview));
        Assert.Equal(8, rows.Select(r => SalesExcelWorkbookParser.Token(r.ImportKey)).Distinct().Count());
    }

    [Fact]
    public void Review_Sheet_Reasons_Are_Joined_By_ImportKey()
    {
        using var file = Workbook(2, review: true);
        var rows = SalesExcelWorkbookParser.Parse(file);
        Assert.True(rows[0].RequiresReview);
        Assert.Contains("واسطه", rows[0].ReviewReason);
        Assert.False(rows[1].RequiresReview);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, true)]
    [InlineData(7, false)]
    public void Preparation_Derived_Formulas_Are_Checked_Without_Executing_Input_Formulas(int column, bool accepted)
    {
        using var file = Workbook(1, formulaColumn: column);
        if (accepted) Assert.Single(SalesExcelWorkbookParser.Parse(file));
        else Assert.Throws<InvalidDataException>(() => SalesExcelWorkbookParser.Parse(file));
    }

    [Theory]
    [InlineData(0, "s-1")] // case-insensitive ImportKey collision
    [InlineData(7, "0")]
    [InlineData(8, "-1")]
    [InlineData(9, "11")]
    [InlineData(10, "11")]
    [InlineData(11, "1")]
    [InlineData(2, "31/12/1404")]
    [InlineData(2, "1404/10/4")]
    public void Invalid_Workbook_Fails_Closed(int column, string value)
    {
        using var file = Workbook(2, (i, v) => { if (i == 2) v[column] = value; });
        Assert.Throws<InvalidDataException>(() => SalesExcelWorkbookParser.Parse(file));
    }

    [Theory]
    [InlineData("۴/۱۰/۱۴۰۴")]
    [InlineData("٤/١٠/١٤٠٤")]
    [InlineData("4/10/1404")]
    public void Solar_Dates_Use_Day_Month_Year_And_Utc(string date)
    { Assert.Equal(Day, SalesExcelWorkbookParser.ParseSolar(date)); Assert.Equal(DateTimeKind.Utc, SalesExcelWorkbookParser.ParseSolar(date).Kind); }

    [Fact]
    public void Historical_Timeline_Combines_Rows_And_Rejects_A_Temporary_Future_Deficit()
    {
        var existing = new[] { new SalesExcelImportWorkflow.TimelinePoint(Day.AddDays(-1), 1, 10),
            new SalesExcelImportWorkflow.TimelinePoint(Day.AddDays(1), 2, -8),
            new SalesExcelImportWorkflow.TimelinePoint(Day.AddDays(2), 3, 20) };
        Assert.True(SalesExcelImportWorkflow.TimelineValid(existing, [Row("A") with { QuantityMt = 1 }]));
        Assert.False(SalesExcelImportWorkflow.TimelineValid(existing, [Row("A") with { QuantityMt = 3 }]));
        Assert.False(SalesExcelImportWorkflow.TimelineValid(existing, [Row("A") with { QuantityMt = 2 }, Row("B") with { QuantityMt = 2 }]));
    }

    [Theory]
    [InlineData(nameof(SalesController.PreviewSalesExcel))]
    [InlineData(nameof(SalesController.ValidateSalesExcel))]
    [InlineData(nameof(SalesController.CommitSalesExcel))]
    public void Import_Posts_Require_Permission_And_Antiforgery(string name)
    {
        var method = typeof(SalesController).GetMethod(name)!;
        Assert.Single(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        Assert.Equal(AuthPolicies.ManageData, Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Policy);
    }

    [Fact]
    public async Task Preview_Validation_Does_Not_Write_And_Requires_Review_Mapping_And_Current_Buyer()
    {
        await using var db = InMemory();
        db.Customers.Add(new Customer { Id = 1, Name = "مشتری", IsActive = true }); await db.SaveChangesAsync();
        var workflow = Workflow(db);
        var row = Row("A") with { ReviewReason = "واسطه" };
        var plan = Plan(row);
        var errors = await workflow.ValidateAsync(plan, Sources());
        Assert.Contains(errors["A"], x => x.Contains("خریدار"));
        Assert.Contains(errors["A"], x => x.Contains("تأیید"));
        Assert.Contains(errors["A"], x => x.Contains("منبع"));
        Assert.Empty(db.ProcessedFormTokens); Assert.Empty(db.SalesTransactions); Assert.Empty(db.LedgerEntries);
        plan = plan with { Mappings = [Mapping("A") with { ReviewConfirmed = true, ReviewNote = "طرف اصلی بررسی شد" }] };
        Assert.Empty(await workflow.ValidateAsync(plan, Sources()));
        db.Customers.Single().IsActive = false; await db.SaveChangesAsync();
        Assert.NotEmpty(await workflow.ValidateAsync(plan, Sources()));
    }

    [Fact]
    public async Task Preview_Respects_The_Existing_Operational_Period_Lock()
    {
        await using var db = InMemory();
        db.Customers.Add(new Customer { Id = 1, Name = "Buyer" });
        db.OperationalPeriodLocks.Add(new() { IsActive = true, LockedThroughDate = Day, Reason = "Closed" });
        await db.SaveChangesAsync();
        var plan = Plan(Row("A")) with { Mappings = [Mapping("A")] };
        Assert.Contains((await Workflow(db).ValidateAsync(plan, Sources()))["A"], x => x.Contains("دورهٔ عملیاتی بسته"));
        Assert.Empty(db.SalesTransactions); Assert.Empty(db.ProcessedFormTokens);
    }

    [Fact]
    public async Task Preview_Fails_On_Consumed_Key_And_Missing_Or_Duplicate_Mappings()
    {
        await using var db = InMemory();
        db.Customers.Add(new Customer { Id = 1, Name = "مشتری" });
        db.ProcessedFormTokens.Add(new() { Token = SalesExcelWorkbookParser.Token("A"), Purpose = "Sales.ExcelImport" });
        await db.SaveChangesAsync();
        var plan = Plan(Row("A")) with { Mappings = [Mapping("A")] };
        Assert.Contains((await Workflow(db).ValidateAsync(plan, Sources()))["A"], x => x.Contains("قبلاً"));
        Assert.Contains("", await Workflow(db).ValidateAsync(plan with { Mappings = [] }, Sources()));
        Assert.Contains("", await Workflow(db).ValidateAsync(plan with { Mappings = [Mapping("A"), Mapping("A")] }, Sources()));
    }

    [Fact]
    public async Task Stock_Preview_Checks_Combined_Historical_Quantity_And_Future_Outbound()
    {
        await using var db = InMemory();
        db.Customers.Add(new Customer { Id = 1, Name = "Buyer" });
        db.Companies.Add(new Company { Id = 1, Code = "C", Name = "Company" });
        db.Products.Add(new Product { Id = 1, Code = "P", Name = "Product" });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T", Name = "Terminal" });
        db.StorageTanks.Add(new StorageTank { Id = 1, TerminalId = 1, ProductId = 1, TankCode = "Tank" });
        db.Contracts.Add(new Contract { Id = 1, CompanyId = 1, ProductId = 1, ContractNumber = "001-2026", ContractType = ContractType.Purchase, ContractDate = Day.AddDays(-10) });
        db.InventoryMovements.AddRange(
            new() { ProductId = 1, ContractId = 1, TerminalId = 1, StorageTankId = 1, MovementDate = Day.AddDays(-1), QuantityMt = 10, Direction = MovementDirection.In },
            new() { ProductId = 1, ContractId = 1, TerminalId = 1, StorageTankId = 1, MovementDate = Day.AddDays(1), QuantityMt = 8, Direction = MovementDirection.Out },
            new() { ProductId = 1, ContractId = 1, TerminalId = 1, StorageTankId = 1, MovementDate = Day.AddDays(2), QuantityMt = 20, Direction = MovementDirection.In });
        await db.SaveChangesAsync();
        const string key = "Stock:1-1-1-1";
        var sources = new Dictionary<string, GroupSaleSourceItem> { [key] = new() { Key = key, Kind = GroupSaleSourceKind.TerminalStock,
            ProductId = 1, TerminalId = 1, StorageTankId = 1, CompanyId = 1, SourcePurchaseContractId = 1 } };
        var plan = Plan(Row("A") with { QuantityMt = 3 }) with { Mappings = [Mapping("A") with { SourceKey = key }] };
        var errors = await Workflow(db).ValidateAsync(plan, sources);
        Assert.Contains(errors["A"], e => e.Contains("منفی"));
        plan = Plan(Row("A") with { QuantityMt = 6 }, Row("B") with { QuantityMt = 6 }) with {
            Mappings = [Mapping("A") with { SourceKey = key }, Mapping("B") with { SourceKey = key }] };
        errors = await Workflow(db).ValidateAsync(plan, sources);
        Assert.Contains(errors["B"], e => e.Contains("مجموع"));
        Assert.Empty(db.SalesTransactions); Assert.Empty(db.ProcessedFormTokens); Assert.Equal(3, db.InventoryMovements.Count());
    }

    [Theory]
    [InlineData(false, true, "1", false)] // no final confirmation
    [InlineData(true, false, "1", false)] // not validated
    [InlineData(true, true, "other-user", false)]
    [InlineData(true, true, "1", true)] // expired
    [InlineData(true, true, "tampered", false)]
    public async Task Final_Post_Cannot_Bypass_User_Binding_Expiry_Or_Confirmation(bool confirm, bool ready, string userId, bool expired)
    {
        await using var db = InMemory();
        var services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test")) };
        var controller = new SalesController(db, new StockService(db), new CurrencyConversionService(new PricingService(db)),
            new AuditService(db), NullLogger<SalesController>.Instance) { ControllerContext = new() { HttpContext = context },
                TempData = new TempDataDictionary(context, new TestTempDataProvider()) };
        var plan = Plan(Row("A")) with { UserId = userId, Ready = ready, ExpiresUtc = expired ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(10) };
        var manifest = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Sales.ExcelImport.Manifest.v1").Protect(JsonSerializer.Serialize(plan));
        if (userId == "tampered") manifest = manifest[..^8] + "invalid!";
        // Invalid final posts must fail before any database write. Use no mappings so
        // the error page also has no current source to guess or silently select.
        await controller.CommitSalesExcel(manifest, confirm);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(db.ProcessedFormTokens); Assert.Empty(db.SalesTransactions); Assert.Empty(db.LedgerEntries);
    }

    [Fact]
    public async Task Whole_File_Rolls_Back_Earlier_Sales_And_Keys_When_A_Later_Row_Fails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Customers.Add(new Customer { Id = 1, Name = "Buyer" }); await db.SaveChangesAsync();
        var plan = Plan(Row("A"), Row("B")) with { Ready = true, Mappings = [Mapping("A"), Mapping("B")] };
        var posted = 0;
        async Task<SalesTransaction> Create(SalesExcelRow row, SalesExcelMapping _, GroupSaleSourceItem __)
        {
            if (++posted == 2) throw new BusinessRuleException("TEST_FINANCIAL_FAILURE", "journal failed");
            return await CreateSuccess(db, row);
        }
        await Assert.ThrowsAsync<BusinessRuleException>(() => Workflow(db).CommitAsync(plan, () => Task.FromResult(Sources()), Create));
        Assert.Equal(2, posted); Assert.Empty(await db.SalesTransactions.ToListAsync()); Assert.Empty(await db.ProcessedFormTokens.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
        var ids = await Workflow(db).CommitAsync(plan, () => Task.FromResult(Sources()), (row, _, _) => CreateSuccess(db, row));
        Assert.Equal(2, ids.Count); Assert.Equal(2, await db.ProcessedFormTokens.CountAsync());
        Assert.All(await db.ProcessedFormTokens.ToListAsync(), t => Assert.Contains(t.ReferenceId!.Value, ids));
        await Assert.ThrowsAsync<BusinessRuleException>(() => Workflow(db).CommitAsync(plan, () => Task.FromResult(Sources()), (row, _, _) => CreateSuccess(db, row)));
        Assert.Equal(2, await db.SalesTransactions.CountAsync());
        Assert.Equal(2, await db.LedgerEntries.CountAsync(l => l.SourceType == "Sale"));
    }

    private static async Task<SalesTransaction> CreateSuccess(ApplicationDbContext db, SalesExcelRow row)
    {
        var sale = new SalesTransaction { InvoiceNumber = row.ImportKey, CustomerId = 1, ProductId = 1, SaleDate = row.SaleDate,
            QuantityMt = row.QuantityMt, TotalUsd = 10, TotalInCurrency = 10 };
        db.SalesTransactions.Add(sale); await db.SaveChangesAsync();
        var conversion = await new CurrencyConversionService(new PricingService(db)).ResolveToBaseAsync("USD", row.SaleDate);
        new LedgerPostingService(db).Post(SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, null));
        await db.SaveChangesAsync();
        return sale;
    }

    [Fact]
    public async Task ImportKey_Markers_Do_Not_Expire_With_Ordinary_Form_Tokens()
    {
        await using var db = InMemory();
        db.ProcessedFormTokens.AddRange(
            new() { Token = SalesExcelWorkbookParser.Token("A"), Purpose = "Sales.ExcelImport", ConsumedAtUtc = DateTime.UtcNow.AddYears(-3) },
            new() { Token = "ordinary-expired", Purpose = "Example.Create", ConsumedAtUtc = DateTime.UtcNow.AddYears(-3) });
        await db.SaveChangesAsync();
        Assert.Equal(1, await new ProcessedFormTokenRetentionService(db).PurgeExpiredAsync());
        Assert.Equal("Sales.ExcelImport", Assert.Single(db.ProcessedFormTokens).Purpose);
    }
    private static ApplicationDbContext InMemory() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static SalesExcelImportWorkflow Workflow(ApplicationDbContext db) => new(db, new CargoSourceQueryService(db), new FormTokenGuard(db));
    private static SalesExcelRow Row(string key) => new() { ImportKey = key, ExcelRow = key == "A" ? 2 : 3, SaleDate = Day, QuantityMt = 1, UnitPriceUsd = 10, SourceAmountUsd = 10 };
    private static SalesExcelPlan Plan(params SalesExcelRow[] rows) => new() { UserId = "1", Rows = rows.ToList(), Mappings = rows.Select(r => new SalesExcelMapping { ImportKey = r.ImportKey }).ToList() };
    private static SalesExcelMapping Mapping(string key) => new() { ImportKey = key, BuyerKey = "C:1", SourceKey = "Dispatch:1" };
    private static Dictionary<string, GroupSaleSourceItem> Sources() => new() { ["Dispatch:1"] = new() { Key = "Dispatch:1", Kind = GroupSaleSourceKind.TruckDispatch, Id = 1, MoveDate = Day.AddDays(-1), AvailableMt = 100 } };

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static MemoryStream Workbook(int count, Action<int, string[]>? mutate = null, bool review = false, int? formulaColumn = null)
    {
        var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var book = doc.AddWorkbookPart(); book.Workbook = new Workbook(); var sheets = book.Workbook.AppendChild(new Sheets());
            void Sheet(string name, IEnumerable<string[]> rows)
            {
                var part = book.AddNewPart<WorksheetPart>(); var data = new SheetData(); part.Worksheet = new Worksheet(data);
                sheets.Append(new Sheet { Name = name, SheetId = (uint)sheets.ChildElements.Count + 1, Id = book.GetIdOfPart(part) });
                uint ri = 0;
                foreach (var values in rows)
                {
                    var row = new Row { RowIndex = ++ri };
                    for (var c = 0; c < values.Length; c++)
                        row.Append(formulaColumn == c && name == "فروشات_آماده" && ri > 1
                            ? new Cell { CellReference = $"{(char)('A' + c)}{ri}", CellFormula = new CellFormula(c == 10 ? $"ROUND(H{ri}*I{ri},2)" : c == 11 ? $"ROUND(J{ri}-K{ri},2)" : "1"), CellValue = new CellValue(values[c]) }
                            : new Cell { CellReference = $"{(char)('A' + c)}{ri}", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(values[c])) });
                    data.Append(row);
                }
            }
            var rows = new List<string[]> { Headers };
            for (var i = 1; i <= count; i++)
            {
                string[] values = [$"S-{i}", "001-2026", "4/10/1404", "خریدار", $"T-{i}", "موتر", "راننده", "1", "10", "10", "10", "0", "وارده", "فروش", i.ToString(), "آماده برای تطبیق منبع"];
                mutate?.Invoke(i, values); rows.Add(values);
            }
            Sheet("فروشات_آماده", rows);
            Sheet("تطبیق_بیلانس", [["تعدیل", "9619"], ["فروش جداگانه", "544.96"]]);
            if (review) Sheet("موارد_بررسی", [["شناسه", "", "", "", "", "", "", "دلیل"], ["S-1", "", "", "", "", "", "", "تأیید طرف اصلی/واسطه"]]);
            book.Workbook.Save();
        }
        stream.Position = 0; return stream;
    }
}
