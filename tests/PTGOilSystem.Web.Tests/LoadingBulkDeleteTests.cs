using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Loading;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// «حذف گروهی بارگیری» راه بازگشت از یک ایمپورت اکسل اشتباه است.
/// این تست‌ها تضمین می‌کنند حذف فقط وقتی رخ می‌دهد که هیچ کاری پایین‌دستِ بارگیری انجام نشده باشد،
/// و گاردها در POST دوباره از دیتابیس خوانده می‌شوند — نه از فرم.
/// </summary>
public class LoadingBulkDeleteTests
{
    private const int ContractId = 1;

    [Fact]
    public async Task BulkDelete_Get_Without_Contract_Lists_Nothing()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);

        var view = Assert.IsType<ViewResult>(await BuildController(db).BulkDelete());
        var model = Assert.IsType<LoadingBulkDeleteViewModel>(view.Model);

        Assert.False(model.HasSearched);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task BulkDelete_Get_Marks_Clean_Rows_Deletable_And_Used_Rows_Locked()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);
        await AddLoadingAsync(db, id: 11, imported: true);
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 11,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 6, 2),
            ReceivedQuantityMt = 20m
        });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await BuildController(db).BulkDelete(contractId: ContractId));
        var model = Assert.IsType<LoadingBulkDeleteViewModel>(view.Model);

        Assert.True(model.HasSearched);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(1, model.DeletableCount);
        Assert.Equal(1, model.BlockedCount);

        var clean = model.Rows.Single(r => r.Id == 10);
        Assert.True(clean.CanDelete);
        Assert.Empty(clean.Blockers);

        var used = model.Rows.Single(r => r.Id == 11);
        Assert.False(used.CanDelete);
        Assert.Contains("رسید ثبت شده", used.Blockers);
    }

    [Fact]
    public async Task BulkDelete_Get_OnlyImported_Filter_Hides_Manual_Rows()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);
        await AddLoadingAsync(db, id: 11, imported: false);

        var onlyImported = Assert.IsType<LoadingBulkDeleteViewModel>(
            Assert.IsType<ViewResult>(await BuildController(db).BulkDelete(contractId: ContractId)).Model);
        Assert.Equal(10, Assert.Single(onlyImported.Rows).Id);

        var all = Assert.IsType<LoadingBulkDeleteViewModel>(
            Assert.IsType<ViewResult>(
                await BuildController(db).BulkDelete(contractId: ContractId, onlyImported: false)).Model);
        Assert.Equal(2, all.Rows.Count);
    }

    [Fact]
    public async Task BulkDelete_Post_Removes_Clean_Loading_With_Its_Ledger_ExpenseLines_And_Audit()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);
        db.LoadingExpenseLines.Add(new LoadingExpenseLine
        {
            Id = 1,
            LoadingRegisterId = 10,
            ExpenseTypeId = 1,
            AmountUsd = 100m
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = 1,
            EntryDate = new DateTime(2026, 6, 1),
            Side = LedgerSide.Credit,
            AmountUsd = 5_000m,
            SourceType = "Loading",
            SourceId = 10,
            ContractId = ContractId,
            SupplierId = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var result = await controller.BulkDeleteConfirm([10], contractId: ContractId);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Empty(await db.LoadingRegisters.ToListAsync());
        Assert.Empty(await db.LoadingExpenseLines.ToListAsync());
        Assert.Empty(await db.LedgerEntries.Where(l => l.SourceType == "Loading").ToListAsync());

        var audit = await db.AuditLogs
            .Where(a => a.EntityName == nameof(LoadingRegister) && a.EntityId == 10)
            .ToListAsync();
        Assert.Single(audit);
        Assert.Contains("Delete", audit[0].Action);
    }

    [Fact]
    public async Task BulkDelete_Post_Never_Deletes_A_Loading_That_Has_A_Receipt()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);
        db.LoadingReceipts.Add(new LoadingReceipt
        {
            Id = 1,
            LoadingRegisterId = 10,
            TerminalId = 1,
            ReceiptDate = new DateTime(2026, 6, 2),
            ReceivedQuantityMt = 20m
        });
        await db.SaveChangesAsync();

        // شناسه مستقیم پست می‌شود — گارد باید سمت سرور دوباره ارزیابی شود، نه از روی فرم.
        var controller = BuildController(db);
        await controller.BulkDeleteConfirm([10], contractId: ContractId);

        Assert.Single(await db.LoadingRegisters.ToListAsync());
        Assert.Single(await db.LoadingReceipts.ToListAsync());
        Assert.Empty(await db.AuditLogs.Where(a => a.EntityName == nameof(LoadingRegister)).ToListAsync());
    }

    [Fact]
    public async Task BulkDelete_Post_Blocks_On_Active_Expense_But_Not_On_A_Cancelled_One()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);   // مصرف فعال ⇒ قفل
        await AddLoadingAsync(db, id: 11, imported: true);   // مصرف لغوشده ⇒ آزاد
        db.ExpenseTransactions.AddRange(
            new ExpenseTransaction
            {
                Id = 1,
                LoadingRegisterId = 10,
                ExpenseTypeId = 1,
                ExpenseDate = new DateTime(2026, 6, 1),
                AmountUsd = 250m,
                IsCancelled = false
            },
            new ExpenseTransaction
            {
                Id = 2,
                LoadingRegisterId = 11,
                ExpenseTypeId = 1,
                ExpenseDate = new DateTime(2026, 6, 1),
                AmountUsd = 250m,
                IsCancelled = true
            });
        await db.SaveChangesAsync();

        await BuildController(db).BulkDeleteConfirm([10, 11], contractId: ContractId);

        var remaining = await db.LoadingRegisters.Select(l => l.Id).ToListAsync();
        Assert.Equal([10], remaining);
    }

    [Fact]
    public async Task BulkDelete_Post_Without_Selection_Deletes_Nothing()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);

        var result = await BuildController(db).BulkDeleteConfirm([], contractId: ContractId);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Single(await db.LoadingRegisters.ToListAsync());
    }

    [Fact]
    public async Task BulkDelete_Post_Deletes_Only_The_Selected_Rows()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddLoadingAsync(db, id: 10, imported: true);
        await AddLoadingAsync(db, id: 11, imported: true);
        await AddLoadingAsync(db, id: 12, imported: true);

        await BuildController(db).BulkDeleteConfirm([10, 12], contractId: ContractId);

        Assert.Equal([11], await db.LoadingRegisters.Select(l => l.Id).ToListAsync());
    }

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LoadingController BuildController(ApplicationDbContext db)
        => new(db, new AuditService(db), NullLogger<LoadingController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider()),
            Url = new UrlHelper(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()))
        };

    private static async Task SeedAsync(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier A" });
        db.Products.Add(new Product { Id = 1, Code = "GO", Name = "Gas Oil", IsActive = true });
        db.Terminals.Add(new Terminal { Id = 1, Code = "T1", Name = "Terminal 1" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "LOAD-TRANSPORT", Name = "Transport" });
        db.Contracts.Add(new Contract
        {
            Id = ContractId,
            ContractNumber = "PUR-001",
            ContractType = ContractType.Purchase,
            CompanyId = 1,
            ProductId = 1,
            SupplierId = 1,
            ContractDate = new DateTime(2026, 5, 1),
            QuantityMt = 1_000m,
            PricingMethod = PricingMethod.Fixed,
            UnitPriceUsd = 500m
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddLoadingAsync(ApplicationDbContext db, int id, bool imported)
    {
        db.LoadingRegisters.Add(new LoadingRegister
        {
            Id = id,
            ContractId = ContractId,
            ProductId = 1,
            LoadingDate = new DateTime(2026, 6, 1),
            LoadedQuantityMt = 20m,
            LoadingPriceUsd = 500m,
            TransportType = LoadingTransportType.Wagon,
            WagonNumber = $"W-{id}",
            ImportUniqueKey = imported ? $"KEY-{id}" : null,
            CreatedAtUtc = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _data = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            => _data = new Dictionary<string, object>(values);
    }
}
