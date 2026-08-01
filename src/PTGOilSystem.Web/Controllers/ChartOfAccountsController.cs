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
public sealed class ChartOfAccountsController(
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

    private async Task PopulateLookupsAsync(int ownerCompanyId, ChartOfAccountsCreateForm form, CancellationToken cancellationToken)
    {
        var parents = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == ownerCompanyId)
            .OrderBy(a => a.Code)
            .Select(a => new { a.Id, Label = a.Code + " - " + a.Name })
            .ToListAsync(cancellationToken);
        ViewBag.ParentAccounts = new SelectList(parents, "Id", "Label", form.ParentAccountId);

        ViewBag.AccountTypes = new SelectList(new[]
        {
            new { Value = (int)AccountType.Asset, Text = "دارایی" },
            new { Value = (int)AccountType.Liability, Text = "بدهی" },
            new { Value = (int)AccountType.Equity, Text = "سرمایه" },
            new { Value = (int)AccountType.Revenue, Text = "درآمد" },
            new { Value = (int)AccountType.Expense, Text = "مصرف" }
        }, "Value", "Text", (int)form.AccountType);

        ViewBag.NormalBalances = new SelectList(new[]
        {
            new { Value = (int)NormalBalance.Debit, Text = UiText.T(HttpContext, "بدهکار", "Debit") },
            new { Value = (int)NormalBalance.Credit, Text = UiText.T(HttpContext, "بستانکار", "Credit") }
        }, "Value", "Text", (int)form.NormalBalance);

        ViewBag.MonetaryTreatments = new SelectList(new[]
        {
            new { Value = (int)MonetaryTreatment.Unspecified, Text = "تعیین‌نشده" },
            new { Value = (int)MonetaryTreatment.Monetary, Text = "پولی" },
            new { Value = (int)MonetaryTreatment.NonMonetary, Text = "غیرپولی" }
        }, "Value", "Text", (int)form.MonetaryTreatment);
    }
}
