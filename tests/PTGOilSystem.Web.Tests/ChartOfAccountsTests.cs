using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class ChartOfAccountsTests
{
    private const int OwnerCompanyId = 10;
    private const int OtherCompanyId = 20;

    [Fact]
    public async Task BuildAsync_NoOwnerConfigured_ReturnsEmptyOwnerState()
    {
        await using var db = NewDb();
        // یک شرکت بدون IsSystemOwner: هنوز مالکی تعیین نشده است.
        db.Companies.Add(NewCompany(OwnerCompanyId, "OWNER", isOwner: false));
        db.Accounts.Add(NewAccount(1, OwnerCompanyId, "1100", "Cash"));
        await db.SaveChangesAsync();

        var model = await NewReadService(db).BuildAsync(null, 1);

        Assert.Null(model.OwnerCompanyId);
        Assert.Empty(model.Items);
        Assert.Equal(0, model.TotalCount);
    }

    [Fact]
    public async Task BuildAsync_OwnerConfigured_ReturnsOnlyOwnerAccounts_AndProjectsParent()
    {
        await using var db = NewDb();
        db.Companies.AddRange(
            NewCompany(OwnerCompanyId, "OWNER", isOwner: true),
            NewCompany(OtherCompanyId, "OTHER", isOwner: false));
        var parent = NewAccount(1, OwnerCompanyId, "1100", "Cash Control");
        db.Accounts.AddRange(
            parent,
            NewAccount(2, OwnerCompanyId, "1110", "Petty Cash", parent),
            NewAccount(3, OtherCompanyId, "1100", "Other Company Cash"));
        await db.SaveChangesAsync();

        var model = await NewReadService(db).BuildAsync(search: "Petty", page: 1);

        Assert.Equal(OwnerCompanyId, model.OwnerCompanyId);
        var row = Assert.Single(model.Items);
        Assert.Equal(OwnerCompanyId, row.CompanyId);
        Assert.Equal("1110", row.Code);
        Assert.Equal("1100", row.ParentCode);
        Assert.DoesNotContain(model.Items, item => item.CompanyId == OtherCompanyId);
    }

    [Fact]
    public async Task Create_Post_RecordsOwnerCompany_WithNoCompanyIdFromUser()
    {
        await using var db = NewDb();
        db.Companies.Add(NewCompany(OwnerCompanyId, "OWNER", isOwner: true));
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var form = new ChartOfAccountsCreateForm { Code = "4100", Name = "Sales", AccountType = AccountType.Revenue, NormalBalance = NormalBalance.Credit };

        var result = await controller.Create(form);

        Assert.IsType<RedirectToActionResult>(result);
        var account = Assert.Single(db.Accounts);
        Assert.Equal(OwnerCompanyId, account.CompanyId);
        Assert.Equal("4100", account.Code);
    }

    [Fact]
    public async Task Create_Post_RejectsForeignParentAccount()
    {
        await using var db = NewDb();
        db.Companies.AddRange(
            NewCompany(OwnerCompanyId, "OWNER", isOwner: true),
            NewCompany(OtherCompanyId, "OTHER", isOwner: false));
        db.Accounts.Add(NewAccount(99, OtherCompanyId, "1000", "Foreign Root"));
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var form = new ChartOfAccountsCreateForm { Code = "1010", Name = "Child", ParentAccountId = 99 };

        var result = await controller.Create(form);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(ChartOfAccountsCreateForm.ParentAccountId)));
        Assert.Empty(db.Accounts.Where(a => a.CompanyId == OwnerCompanyId));
    }

    [Fact]
    public async Task Create_Post_RejectsDuplicateCodeWithinOwner()
    {
        await using var db = NewDb();
        db.Companies.Add(NewCompany(OwnerCompanyId, "OWNER", isOwner: true));
        db.Accounts.Add(NewAccount(1, OwnerCompanyId, "1100", "Cash"));
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var form = new ChartOfAccountsCreateForm { Code = "1100", Name = "Duplicate" };

        var result = await controller.Create(form);

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ChartOfAccountsCreateForm.Code)));
        Assert.Single(db.Accounts);
    }

    [Fact]
    public async Task Edit_Post_UpdatesNameParentAndStructure_WhenAccountUnused()
    {
        await using var db = NewDb();
        db.Companies.Add(NewCompany(OwnerCompanyId, "OWNER", isOwner: true));
        db.Accounts.AddRange(
            NewAccount(1, OwnerCompanyId, "1100", "Cash"),
            NewAccount(2, OwnerCompanyId, "1110", "Old name"));
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var form = new ChartOfAccountsEditForm
        {
            Code = "1120", Name = "New name", ParentAccountId = 1,
            AccountType = AccountType.Expense, NormalBalance = NormalBalance.Debit, IsActive = false
        };

        var result = await controller.Edit(2, form);

        Assert.IsType<RedirectToActionResult>(result);
        var account = await db.Accounts.SingleAsync(a => a.Id == 2);
        Assert.Equal("1120", account.Code);
        Assert.Equal("New name", account.Name);
        Assert.Equal(1, account.ParentAccountId);
        Assert.Equal(AccountType.Expense, account.AccountType);
        Assert.False(account.IsActive);
    }

    [Fact]
    public async Task Edit_Post_ControlAccount_KeepsStructureLocked()
    {
        await using var db = NewDb();
        db.Companies.Add(NewCompany(OwnerCompanyId, "OWNER", isOwner: true));
        var control = NewAccount(1, OwnerCompanyId, "1200", "Accounts Receivable");
        control.IsControlAccount = true;
        db.Accounts.Add(control);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var form = new ChartOfAccountsEditForm
        {
            Code = "9999", Name = "AR renamed", AccountType = AccountType.Revenue,
            NormalBalance = NormalBalance.Credit, IsActive = true
        };

        var result = await controller.Edit(1, form);

        Assert.IsType<RedirectToActionResult>(result);
        var account = await db.Accounts.SingleAsync(a => a.Id == 1);
        Assert.Equal("AR renamed", account.Name);
        Assert.Equal("1200", account.Code);
        Assert.Equal(AccountType.Asset, account.AccountType);
        Assert.Equal(NormalBalance.Debit, account.NormalBalance);
    }

    [Fact]
    public async Task Edit_Post_RejectsDescendantAsParent()
    {
        await using var db = NewDb();
        db.Companies.Add(NewCompany(OwnerCompanyId, "OWNER", isOwner: true));
        var root = NewAccount(1, OwnerCompanyId, "1100", "Root");
        db.Accounts.AddRange(root, NewAccount(2, OwnerCompanyId, "1110", "Child", root));
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var form = new ChartOfAccountsEditForm { Code = "1100", Name = "Root", ParentAccountId = 2, IsActive = true };

        var result = await controller.Edit(1, form);

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(ChartOfAccountsEditForm.ParentAccountId)));
        Assert.Null((await db.Accounts.SingleAsync(a => a.Id == 1)).ParentAccountId);
    }

    [Fact]
    public async Task Edit_Post_RejectsDuplicateCode_AndForeignAccount()
    {
        await using var db = NewDb();
        db.Companies.AddRange(
            NewCompany(OwnerCompanyId, "OWNER", isOwner: true),
            NewCompany(OtherCompanyId, "OTHER", isOwner: false));
        db.Accounts.AddRange(
            NewAccount(1, OwnerCompanyId, "1100", "Cash"),
            NewAccount(2, OwnerCompanyId, "1110", "Bank"),
            NewAccount(3, OtherCompanyId, "1100", "Foreign"));
        await db.SaveChangesAsync();

        var controller = NewController(db);

        var duplicate = await controller.Edit(2, new ChartOfAccountsEditForm { Code = "1100", Name = "Bank", IsActive = true });
        Assert.IsType<ViewResult>(duplicate);
        Assert.True(controller.ModelState.ContainsKey(nameof(ChartOfAccountsEditForm.Code)));

        var foreign = await NewController(db).Edit(3, new ChartOfAccountsEditForm { Code = "1100", Name = "Hijack", IsActive = true });
        Assert.IsType<NotFoundResult>(foreign);
        Assert.Equal("Foreign", (await db.Accounts.SingleAsync(a => a.Id == 3)).Name);
    }

    [Fact]
    public void Implementation_IsOwnerScoped_AndHasNoCompanySelectionUi()
    {
        var service = ReadRepoFile("src/PTGOilSystem.Web/Services/Accounting/ChartOfAccountsReadService.cs");
        var indexView = ReadRepoFile("src/PTGOilSystem.Web/Views/ChartOfAccounts/Index.cshtml");
        var createView = ReadRepoFile("src/PTGOilSystem.Web/Views/ChartOfAccounts/Create.cshtml");

        Assert.Contains("FindOwnerCompanyIdAsync", service);
        Assert.DoesNotContain("LedgerEntries", service);
        // فیلتر/انتخاب شرکت از UI حذف شده است.
        Assert.DoesNotContain("name=\"companyId\"", indexView);
        Assert.DoesNotContain("asp-for=\"CompanyId\"", createView);
    }

    private static ChartOfAccountsReadService NewReadService(ApplicationDbContext db)
        => new(db, new SystemCompanyProvider(db));

    private static ChartOfAccountsController NewController(ApplicationDbContext db)
    {
        var controller = new ChartOfAccountsController(
            NewReadService(db), db, new SystemCompanyProvider(db), new NoOpAuditService());
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new NoOpTempDataProvider());
        return controller;
    }

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Company NewCompany(int id, string code, bool isOwner)
        => new() { Id = id, Code = code, Name = code, Country = "AF", IsActive = true, IsSystemOwner = isOwner };

    private static Account NewAccount(int id, int companyId, string code, string name, Account? parent = null)
        => new()
        {
            Id = id,
            CompanyId = companyId,
            Code = code,
            Name = name,
            AccountType = AccountType.Asset,
            NormalBalance = NormalBalance.Debit,
            ParentAccountId = parent?.Id,
            ParentAccount = parent,
            IsActive = true
        };

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(GetRepoPath(relativePath));

    private static string GetRepoPath(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(sourceFilePath) ?? string.Empty
                 })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, normalizedPath);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Repository file not found: {relativePath}");
    }

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
