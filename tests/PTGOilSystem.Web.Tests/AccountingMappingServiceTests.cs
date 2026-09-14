using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class AccountingMappingServiceTests
{
    private const int OwnerCompanyId = 10;
    private const int OtherCompanyId = 20;

    [Fact]
    public void Catalog_CoversEveryAccountReferenceOfAccountingSettings()
    {
        using var db = NewDb();
        var settingsAccountProperties = db.Model.FindEntityType(typeof(AccountingSettings))!
            .GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .Select(fk => fk.Properties[0].Name)
            .ToHashSet();

        var catalogKeys = AccountingMappingCatalog.Roles.Select(r => r.Key).ToList();

        Assert.Equal(catalogKeys.Count, catalogKeys.Distinct().Count());
        Assert.True(settingsAccountProperties.SetEquals(catalogKeys));
    }

    [Fact]
    public async Task Build_SeededChart_ShowsEveryRoleMappedAndHealthy()
    {
        await using var db = await NewSeededDbAsync();

        var model = await NewService(db).BuildAsync();

        Assert.True(model.SettingsExist);
        Assert.Equal(AccountingMappingCatalog.Roles.Count, model.Rows.Count);
        Assert.All(model.Rows, row => Assert.Equal(AccountingMappingIssue.None, row.Issue));
        Assert.All(model.Rows, row => Assert.False(row.IsLocked));
        foreach (var section in new[] { "صندوق و بانک", "طرف حساب‌ها", "فروش", "موجودی", "کالای در راه", "بهای تمام‌شده" })
            Assert.Contains(model.Rows, row => row.Section == section);

        var cash = model.Rows.Single(r => r.Key == nameof(AccountingSettings.CashBankControlAccountId));
        Assert.Equal("1100", cash.AccountCode);
    }

    [Fact]
    public async Task Build_NoSettings_ReturnsEmptySettingsState()
    {
        await using var db = NewDb();
        db.Companies.Add(NewCompany(OwnerCompanyId, isOwner: true));
        await db.SaveChangesAsync();

        var model = await NewService(db).BuildAsync();

        Assert.Equal(OwnerCompanyId, model.OwnerCompanyId);
        Assert.False(model.SettingsExist);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task Update_RemapsUnusedRole_ToNewChildAccountOfSameType()
    {
        await using var db = await NewSeededDbAsync();
        var cashControl = await AccountAsync(db, "1100");
        var kabulCash = AddAccount(db, OwnerCompanyId, "1110", "Kabul Cash", AccountType.Asset, cashControl.Id);
        await db.SaveChangesAsync();

        var result = await NewService(db).UpdateAsync(nameof(AccountingSettings.CashBankControlAccountId), kabulCash.Id);

        Assert.True(result.Succeeded, result.Message);
        var settings = await db.AccountingSettings.AsNoTracking().SingleAsync();
        Assert.Equal(kabulCash.Id, settings.CashBankControlAccountId);

        var row = (await NewService(db).BuildAsync()).Rows
            .Single(r => r.Key == nameof(AccountingSettings.CashBankControlAccountId));
        Assert.Equal("1110", row.AccountCode);
        Assert.Equal(AccountingMappingIssue.None, row.Issue);
    }

    [Fact]
    public async Task Update_RejectsAccountOfWrongType()
    {
        await using var db = await NewSeededDbAsync();
        var asset = AddAccount(db, OwnerCompanyId, "1120", "Some Asset", AccountType.Asset);
        await db.SaveChangesAsync();
        var before = (await db.AccountingSettings.AsNoTracking().SingleAsync()).SalesRevenueAccountId;

        var result = await NewService(db).UpdateAsync(nameof(AccountingSettings.SalesRevenueAccountId), asset.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(before, (await db.AccountingSettings.AsNoTracking().SingleAsync()).SalesRevenueAccountId);
    }

    [Fact]
    public async Task Update_RejectsAccountAlreadyUsedByAnotherRole()
    {
        await using var db = await NewSeededDbAsync();
        var inventory = await AccountAsync(db, "1300");
        var before = (await db.AccountingSettings.AsNoTracking().SingleAsync()).AccountsReceivableAccountId;

        var result = await NewService(db).UpdateAsync(nameof(AccountingSettings.AccountsReceivableAccountId), inventory.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(before, (await db.AccountingSettings.AsNoTracking().SingleAsync()).AccountsReceivableAccountId);
    }

    [Fact]
    public async Task Update_RejectsForeignAndInactiveAccounts()
    {
        await using var db = await NewSeededDbAsync();
        var foreign = AddAccount(db, OtherCompanyId, "1130", "Foreign Asset", AccountType.Asset);
        var inactive = AddAccount(db, OwnerCompanyId, "1140", "Closed Bank", AccountType.Asset);
        inactive.IsActive = false;
        await db.SaveChangesAsync();
        var service = NewService(db);

        var foreignResult = await service.UpdateAsync(nameof(AccountingSettings.CashBankControlAccountId), foreign.Id);
        var inactiveResult = await service.UpdateAsync(nameof(AccountingSettings.CashBankControlAccountId), inactive.Id);

        Assert.False(foreignResult.Succeeded);
        Assert.False(inactiveResult.Succeeded);
        Assert.Equal((await AccountAsync(db, "1100")).Id,
            (await db.AccountingSettings.AsNoTracking().SingleAsync()).CashBankControlAccountId);
    }

    [Fact]
    public async Task Update_LocksRoleWhoseCurrentAccountHasJournals()
    {
        await using var db = await NewSeededDbAsync();
        var receivable = await AccountAsync(db, "1200");
        var replacement = AddAccount(db, OwnerCompanyId, "1210", "Receivable - Local", AccountType.Asset);
        db.JournalEntries.Add(new JournalEntry
        {
            CompanyId = OwnerCompanyId,
            JournalNumber = "JV-1",
            SourceModule = "Test",
            Status = JournalEntryStatus.Posted,
            Lines =
            [
                new JournalEntryLine
                {
                    LineNumber = 1, AccountId = receivable.Id, Debit = 10m,
                    TransactionCurrencyCode = "USD", TransactionAmount = 10m, ExchangeRate = 1m
                }
            ]
        });
        await db.SaveChangesAsync();

        var result = await NewService(db).UpdateAsync(nameof(AccountingSettings.AccountsReceivableAccountId), replacement.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(receivable.Id, (await db.AccountingSettings.AsNoTracking().SingleAsync()).AccountsReceivableAccountId);
        var row = (await NewService(db).BuildAsync()).Rows
            .Single(r => r.Key == nameof(AccountingSettings.AccountsReceivableAccountId));
        Assert.True(row.IsLocked);
    }

    [Fact]
    public async Task Update_RejectsUnknownRole()
    {
        await using var db = await NewSeededDbAsync();

        var result = await NewService(db).UpdateAsync("NotARole", 1);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Build_FlagsInactiveMappedAccount()
    {
        await using var db = await NewSeededDbAsync();
        var cogs = await db.Accounts.SingleAsync(a => a.CompanyId == OwnerCompanyId && a.Code == "5100");
        cogs.IsActive = false;
        await db.SaveChangesAsync();

        var row = (await NewService(db).BuildAsync()).Rows
            .Single(r => r.Key == nameof(AccountingSettings.CostOfGoodsSoldAccountId));

        Assert.Equal(AccountingMappingIssue.Inactive, row.Issue);
    }

    [Fact]
    public async Task Controller_MappingPost_RedirectsBackToMapping()
    {
        await using var db = await NewSeededDbAsync();
        var bank = AddAccount(db, OwnerCompanyId, "1150", "Main Bank", AccountType.Asset);
        await db.SaveChangesAsync();
        var controller = NewController(db);

        var result = await controller.Mapping(
            nameof(AccountingSettings.CashBankControlAccountId), bank.Id, NewService(db));

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ChartOfAccountsController.Mapping), redirect.ActionName);
        Assert.Equal(bank.Id, (await db.AccountingSettings.AsNoTracking().SingleAsync()).CashBankControlAccountId);
    }

    private static async Task<ApplicationDbContext> NewSeededDbAsync()
    {
        var db = NewDb();
        db.Companies.AddRange(
            NewCompany(OwnerCompanyId, isOwner: true),
            NewCompany(OtherCompanyId, isOwner: false));
        await db.SaveChangesAsync();

        // فقط شرکت مالک تنظیمات می‌گیرد تا حساب‌های شرکت دیگر با شرکت مالک قاطی نشوند.
        await new AccountingChartSeeder(db, Options.Create(new AccountingOptions())).SeedAsync();
        db.AccountingSettings.RemoveRange(db.AccountingSettings.Where(s => s.CompanyId == OtherCompanyId));
        await db.SaveChangesAsync();
        return db;
    }

    private static Task<Account> AccountAsync(ApplicationDbContext db, string code)
        => db.Accounts.AsNoTracking().SingleAsync(a => a.CompanyId == OwnerCompanyId && a.Code == code);

    private static Account AddAccount(
        ApplicationDbContext db, int companyId, string code, string name, AccountType type, int? parentId = null)
    {
        var account = new Account
        {
            CompanyId = companyId,
            Code = code,
            Name = name,
            AccountType = type,
            NormalBalance = type is AccountType.Asset or AccountType.Expense ? NormalBalance.Debit : NormalBalance.Credit,
            ParentAccountId = parentId,
            IsActive = true
        };
        db.Accounts.Add(account);
        return account;
    }

    private static AccountingMappingService NewService(ApplicationDbContext db)
        => new(db, new SystemCompanyProvider(db), new NoOpAuditService());

    private static ChartOfAccountsController NewController(ApplicationDbContext db)
    {
        var controller = new ChartOfAccountsController(
            new ChartOfAccountsReadService(db, new SystemCompanyProvider(db)),
            db,
            new SystemCompanyProvider(db),
            new NoOpAuditService());
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new NoOpTempDataProvider());
        return controller;
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Company NewCompany(int id, bool isOwner)
        => new() { Id = id, Code = $"C{id}", Name = $"C{id}", Country = "AF", IsActive = true, IsSystemOwner = isOwner };

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogAndSaveAsync(string entityName, int entityId, AuditAction action, int? actorUserId = null, string? diff = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogActivityAsync(AuditLogEntryInput entry, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogActivityAndSaveAsync(AuditLogEntryInput entry, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context)
            => new Dictionary<string, object?>();

        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object?> values) { }
    }
}
