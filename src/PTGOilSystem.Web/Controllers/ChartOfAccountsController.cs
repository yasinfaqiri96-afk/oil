using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Accounting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
[Route("accounting/chart-of-accounts")]
public sealed partial class ChartOfAccountsController(
    IChartOfAccountsReadService service,
    ApplicationDbContext db,
    ISystemCompanyProvider systemCompany,
    IAuditService audit) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? q,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null,
        CancellationToken cancellationToken = default)
    {
        var model = await service.BuildAsync(q, page, cancellationToken, perPage);
        ViewData["PageSize"] = model.PageSize;
        ViewData["DefaultPageSize"] = 20;
        return View(model);
    }

    [HttpGet("create")]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken = default)
    {
        var ownerCompanyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);
        var form = new ChartOfAccountsCreateForm { IsActive = true };

        await PopulateLookupsAsync(ownerCompanyId, form, cancellationToken);
        return View(form);
    }

    [HttpPost("create")]
    [Authorize(Policy = AuthPolicies.ManageData)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ChartOfAccountsCreateForm form, CancellationToken cancellationToken = default)
    {
        // شرکت همیشه سمت سرور تعیین می‌شود؛ هیچ CompanyId از فرم پذیرفته نمی‌شود.
        var ownerCompanyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);

        Normalize(form);
        await ValidateAsync(ownerCompanyId, form, cancellationToken);

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(ownerCompanyId, form, cancellationToken);
            return View(form);
        }

        var account = new Account
        {
            CompanyId = ownerCompanyId,
            Code = form.Code,
            Name = form.Name,
            AccountType = form.AccountType,
            NormalBalance = form.NormalBalance,
            ParentAccountId = form.ParentAccountId,
            MonetaryTreatment = form.MonetaryTreatment,
            IsActive = form.IsActive
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAndSaveAsync(
            nameof(Account),
            account.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("CompanyId", account.CompanyId),
                ("Code", account.Code),
                ("Name", account.Name),
                ("AccountType", account.AccountType),
                ("NormalBalance", account.NormalBalance),
                ("ParentAccountId", account.ParentAccountId),
                ("MonetaryTreatment", account.MonetaryTreatment),
                ("IsActive", account.IsActive)));

        TempData["ok"] = "سرفصل حساب با موفقیت ثبت شد.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:int}/edit")]
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken = default)
    {
        var ownerCompanyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == ownerCompanyId, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        var (structureLocked, isSettingsAccount) = await GetEditLocksAsync(ownerCompanyId, account, cancellationToken);
        var form = new ChartOfAccountsEditForm
        {
            Id = account.Id,
            Code = account.Code,
            Name = account.Name,
            AccountType = account.AccountType,
            NormalBalance = account.NormalBalance,
            ParentAccountId = account.ParentAccountId,
            MonetaryTreatment = account.MonetaryTreatment,
            IsActive = account.IsActive,
            StructureLocked = structureLocked,
            IsSettingsAccount = isSettingsAccount
        };

        await PopulateLookupsAsync(ownerCompanyId, form.ParentAccountId, form.AccountType, form.NormalBalance, form.MonetaryTreatment, account.Id, cancellationToken);
        return View(form);
    }

    [HttpPost("{id:int}/edit")]
    [Authorize(Policy = AuthPolicies.ManageData)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ChartOfAccountsEditForm form, CancellationToken cancellationToken = default)
    {
        // شرکت و قفل‌ها همیشه سمت سرور تعیین می‌شوند؛ حساب شرکت دیگر قابل ویرایش نیست.
        var ownerCompanyId = await systemCompany.GetOwnerCompanyIdAsync(cancellationToken);
        var account = await db.Accounts
            .SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == ownerCompanyId, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        var (structureLocked, isSettingsAccount) = await GetEditLocksAsync(ownerCompanyId, account, cancellationToken);
        form.Id = account.Id;
        form.StructureLocked = structureLocked;
        form.IsSettingsAccount = isSettingsAccount;
        form.Code = (form.Code ?? string.Empty).Trim();
        form.Name = (form.Name ?? string.Empty).Trim();
        if (form.ParentAccountId is 0)
        {
            form.ParentAccountId = null;
        }

        // حسابِ دارای سند، تنظیمات یا نقش کنترلی: ساختار حسابداری (کد/نوع/مانده/طبقه پولی) دست نمی‌خورد.
        if (structureLocked)
        {
            form.Code = account.Code;
            form.AccountType = account.AccountType;
            form.NormalBalance = account.NormalBalance;
            form.MonetaryTreatment = account.MonetaryTreatment;
        }

        // Posting فقط روی حساب فعال انجام می‌شود؛ حساب تنظیمات حسابداری نباید غیرفعال شود.
        if (isSettingsAccount)
        {
            form.IsActive = true;
        }

        await ValidateEditAsync(ownerCompanyId, account.Id, form, cancellationToken);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(ownerCompanyId, form.ParentAccountId, form.AccountType, form.NormalBalance, form.MonetaryTreatment, account.Id, cancellationToken);
            return View(form);
        }

        var before = (account.Code, account.Name, account.AccountType, account.NormalBalance, account.ParentAccountId, account.MonetaryTreatment, account.IsActive);
        account.Code = form.Code;
        account.Name = form.Name;
        account.AccountType = form.AccountType;
        account.NormalBalance = form.NormalBalance;
        account.ParentAccountId = form.ParentAccountId;
        account.MonetaryTreatment = form.MonetaryTreatment;
        account.IsActive = form.IsActive;

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAndSaveAsync(
            nameof(Account),
            account.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("Code", before.Code, account.Code),
                ("Name", before.Name, account.Name),
                ("AccountType", before.AccountType, account.AccountType),
                ("NormalBalance", before.NormalBalance, account.NormalBalance),
                ("ParentAccountId", before.ParentAccountId, account.ParentAccountId),
                ("MonetaryTreatment", before.MonetaryTreatment, account.MonetaryTreatment),
                ("IsActive", before.IsActive, account.IsActive)));

        TempData["ok"] = "سرفصل حساب با موفقیت ویرایش شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<(bool StructureLocked, bool IsSettingsAccount)> GetEditLocksAsync(
        int ownerCompanyId, Account account, CancellationToken cancellationToken)
    {
        var isSettingsAccount = (await GetSettingsAccountIdsAsync(ownerCompanyId, cancellationToken)).Contains(account.Id);
        var hasJournalLines = await db.JournalEntryLines.AsNoTracking()
            .AnyAsync(l => l.AccountId == account.Id, cancellationToken);
        return (hasJournalLines || isSettingsAccount || account.IsControlAccount, isSettingsAccount);
    }

    // همهٔ ارجاع‌های AccountingSettings به Account از metadata مدل خوانده می‌شود تا ارجاع تازه جا نماند.
    private async Task<HashSet<int>> GetSettingsAccountIdsAsync(int ownerCompanyId, CancellationToken cancellationToken)
    {
        var settingsRows = await db.AccountingSettings.AsNoTracking()
            .Where(s => s.CompanyId == ownerCompanyId)
            .ToListAsync(cancellationToken);
        var accountReferences = db.Model.FindEntityType(typeof(AccountingSettings))!
            .GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Account))
            .Select(fk => fk.Properties[0].PropertyInfo)
            .OfType<System.Reflection.PropertyInfo>()
            .ToList();

        var ids = new HashSet<int>();
        foreach (var settings in settingsRows)
        {
            foreach (var property in accountReferences)
            {
                if (property.GetValue(settings) is int accountId)
                {
                    ids.Add(accountId);
                }
            }
        }

        return ids;
    }

    private async Task ValidateEditAsync(int ownerCompanyId, int accountId, ChartOfAccountsEditForm form, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(form.Code))
        {
            ModelState.AddModelError(nameof(form.Code), "کد حساب الزامی است.");
        }
        else if (await db.Accounts.AsNoTracking()
            .AnyAsync(a => a.CompanyId == ownerCompanyId && a.Code == form.Code && a.Id != accountId, cancellationToken))
        {
            ModelState.AddModelError(nameof(form.Code), "کد حساب تکراری است.");
        }

        if (string.IsNullOrWhiteSpace(form.Name))
        {
            ModelState.AddModelError(nameof(form.Name), "نام حساب الزامی است.");
        }

        if (form.ParentAccountId is not int parentId)
        {
            return;
        }

        // والد فقط از همان شرکت مالک، و نه خود حساب یا زیرمجموعه‌اش (جلوگیری از حلقه).
        var parentsById = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == ownerCompanyId)
            .Select(a => new { a.Id, a.ParentAccountId })
            .ToDictionaryAsync(a => a.Id, a => a.ParentAccountId, cancellationToken);
        if (!parentsById.ContainsKey(parentId))
        {
            ModelState.AddModelError(nameof(form.ParentAccountId), "حساب والد باید متعلق به شرکت مالک باشد.");
            return;
        }

        int? cursor = parentId;
        var visited = new HashSet<int>();
        while (cursor is int current && visited.Add(current))
        {
            if (current == accountId)
            {
                ModelState.AddModelError(nameof(form.ParentAccountId), "حساب والد نمی‌تواند خود حساب یا زیرمجموعهٔ آن باشد.");
                return;
            }

            cursor = parentsById.GetValueOrDefault(current);
        }
    }

    private static void Normalize(ChartOfAccountsCreateForm form)
    {
        form.Code = (form.Code ?? string.Empty).Trim();
        form.Name = (form.Name ?? string.Empty).Trim();
        if (form.ParentAccountId is 0)
        {
            form.ParentAccountId = null;
        }
    }

    private async Task ValidateAsync(int ownerCompanyId, ChartOfAccountsCreateForm form, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(form.Code))
        {
            ModelState.AddModelError(nameof(form.Code), "کد حساب الزامی است.");
        }
        else if (await db.Accounts.AsNoTracking()
            .AnyAsync(a => a.CompanyId == ownerCompanyId && a.Code == form.Code, cancellationToken))
        {
            ModelState.AddModelError(nameof(form.Code), "کد حساب تکراری است.");
        }

        if (string.IsNullOrWhiteSpace(form.Name))
        {
            ModelState.AddModelError(nameof(form.Name), "نام حساب الزامی است.");
        }

        // حساب والد فقط از حساب‌های همان شرکتِ مالک پذیرفته می‌شود؛ دسترسی به حسابِ شرکت دیگر بسته است.
        if (form.ParentAccountId is int parentId)
        {
            var parentIsOwned = await db.Accounts.AsNoTracking()
                .AnyAsync(a => a.Id == parentId && a.CompanyId == ownerCompanyId, cancellationToken);
            if (!parentIsOwned)
            {
                ModelState.AddModelError(nameof(form.ParentAccountId), "حساب والد باید متعلق به شرکت مالک باشد.");
            }
        }
    }

    private Task PopulateLookupsAsync(int ownerCompanyId, ChartOfAccountsCreateForm form, CancellationToken cancellationToken)
        => PopulateLookupsAsync(ownerCompanyId, form.ParentAccountId, form.AccountType, form.NormalBalance, form.MonetaryTreatment, null, cancellationToken);

    private async Task PopulateLookupsAsync(
        int ownerCompanyId,
        int? parentAccountId,
        AccountType accountType,
        NormalBalance normalBalance,
        MonetaryTreatment monetaryTreatment,
        int? excludeAccountId,
        CancellationToken cancellationToken)
    {
        var parents = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == ownerCompanyId && a.Id != excludeAccountId)
            .OrderBy(a => a.Code)
            .Select(a => new { a.Id, Label = a.Code + " - " + a.Name })
            .ToListAsync(cancellationToken);
        ViewBag.ParentAccounts = new SelectList(parents, "Id", "Label", parentAccountId);

        ViewBag.AccountTypes = new SelectList(new[]
        {
            new { Value = (int)AccountType.Asset, Text = "دارایی" },
            new { Value = (int)AccountType.Liability, Text = "بدهی" },
            new { Value = (int)AccountType.Equity, Text = "سرمایه" },
            new { Value = (int)AccountType.Revenue, Text = "درآمد" },
            new { Value = (int)AccountType.Expense, Text = "مصرف" }
        }, "Value", "Text", (int)accountType);

        ViewBag.NormalBalances = new SelectList(new[]
        {
            new { Value = (int)NormalBalance.Debit, Text = UiText.T(HttpContext, "بدهکار", "Debit") },
            new { Value = (int)NormalBalance.Credit, Text = UiText.T(HttpContext, "بستانکار", "Credit") }
        }, "Value", "Text", (int)normalBalance);

        ViewBag.MonetaryTreatments = new SelectList(new[]
        {
            new { Value = (int)MonetaryTreatment.Unspecified, Text = "تعیین‌نشده" },
            new { Value = (int)MonetaryTreatment.Monetary, Text = "پولی" },
            new { Value = (int)MonetaryTreatment.NonMonetary, Text = "غیرپولی" }
        }, "Value", "Text", (int)monetaryTreatment);
    }
}
