using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class CashAccountsController : Controller
{
    private const int IndexPageSize = 20;
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IMemoryCache? _summaryCache;

    public CashAccountsController(ApplicationDbContext db, IAuditService audit, IMemoryCache? summaryCache = null)
    {
        _db = db;
        _audit = audit;
        _summaryCache = summaryCache;
    }

    private async Task PopulateCurrenciesAsync(string? currentCurrency = null)
    {
        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new
                {
                    c.Code,
                    DisplayName = string.IsNullOrWhiteSpace(c.NamePersian)
                        ? $"{c.Code} - {c.Name}"
                        : $"{c.Code} - {c.NamePersian}"
                })
                .ToListAsync(),
            "Code",
            "DisplayName",
            SystemCurrency.Normalize(currentCurrency));
    }

    private void PopulateIndexLookups(CashAccountIndexFilterViewModel filter)
    {
        ViewBag.AccountTypes = new SelectList(
            Enum.GetValues<CashAccountType>()
                .Select(value => new
                {
                    Value = value,
                    Text = CashAccountTypeLabels.ToPersian(value)
                })
                .ToList(),
            "Value",
            "Text",
            filter.AccountType);

        ViewBag.Statuses = new SelectList(
            new[]
            {
                new { Value = "true", Text = "فعال" },
                new { Value = "false", Text = "غیرفعال" }
            },
            "Value",
            "Text",
            filter.IsActive?.ToString().ToLowerInvariant());
    }

    private static IQueryable<CashAccount> ApplyIndexFilter(
        IQueryable<CashAccount> query,
        CashAccountIndexFilterViewModel filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var term = filter.Query.Trim();
            query = query.Where(a =>
                a.Code.Contains(term)
                || a.Name.Contains(term)
                || (a.AccountNumber != null && a.AccountNumber.Contains(term))
                || (a.BankName != null && a.BankName.Contains(term))
                || (a.Notes != null && a.Notes.Contains(term)));
        }

        if (filter.AccountType.HasValue)
        {
            query = query.Where(a => a.AccountType == filter.AccountType.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            var currency = SystemCurrency.Normalize(filter.Currency);
            query = query.Where(a => a.Currency == currency);
        }

        if (filter.IsActive.HasValue)
        {
            query = query.Where(a => a.IsActive == filter.IsActive.Value);
        }

        return query;
    }

    public async Task<IActionResult> Index([FromQuery] CashAccountIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new CashAccountIndexFilterViewModel();

        await PopulateCurrenciesAsync(filter.Currency);
        PopulateIndexLookups(filter);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var query = ApplyIndexFilter(_db.CashAccounts.AsNoTracking(), filter);
        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderBy(a => a.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return View(new CashAccountIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            FinanceMetrics = await FinanceMetricCardsQuery.BuildAsync(_db, _summaryCache)
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Csv([FromQuery] CashAccountIndexFilterViewModel? filter = null)
    {
        var items = await ApplyIndexFilter(_db.CashAccounts.AsNoTracking(), filter ?? new CashAccountIndexFilterViewModel())
            .OrderBy(a => a.Code)
            .ToListAsync();

        return CsvExportSupport.File(this, "cash-accounts.csv",
            ["Code", "Name", "Type", "Currency", "Status", "AccountNumber", "BankName", "Branch", "Notes"],
            items.Select(item => new[]
            {
                item.Code,
                item.Name,
                CashAccountTypeLabels.ToPersian(item.AccountType),
                item.Currency,
                item.IsActive ? "فعال" : "غیرفعال",
                item.AccountNumber,
                item.BankName,
                item.Branch,
                item.Notes
            }));
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, [FromQuery] CashAccountIndexFilterViewModel? filter = null)
    {
        filter ??= new CashAccountIndexFilterViewModel();
        var items = await ApplyIndexFilter(_db.CashAccounts.AsNoTracking(), filter)
            .OrderBy(account => account.Code)
            .ToListAsync(HttpContext.RequestAborted);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_Cash_Accounts",
            TitleFa = "حساب‌های نقدی و بانکی",
            TitleEn = "Cash and Bank Accounts",
            KnownRowCount = items.Count,
            Filters =
            [
                new("جستجو", "Search", filter.Query),
                new("ارز", "Currency", filter.Currency),
                new("نوع حساب", "Account type", filter.AccountType?.ToString()),
                new("وضعیت", "Status", filter.IsActive?.ToString())
            ],
            Columns =
            [
                new("کد", "Code", Width: 13), new("نام", "Name", Width: 22), new("نوع حساب", "Account type", Width: 16),
                new("ارز", "Currency", Width: 10), new("وضعیت", "Status", Width: 12), new("شماره حساب", "Account number", Width: 20),
                new("بانک", "Bank", Width: 20), new("شعبه", "Branch", Width: 17), new("یادداشت", "Notes", Width: 28, Wrap: true)
            ],
            Rows = items.Select(item => new TabularExportRow(
            [
                TabularExportCell.Text(item.Code), TabularExportCell.Text(item.Name), TabularExportCell.Text(CashAccountTypeLabels.ToPersian(item.AccountType)),
                TabularExportCell.Text(item.Currency), TabularExportCell.Text(item.IsActive ? "فعال" : "غیرفعال"),
                TabularExportCell.Text(item.AccountNumber), TabularExportCell.Text(item.BankName), TabularExportCell.Text(item.Branch),
                TabularExportCell.Text(item.Notes)
            ]))
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (item is null)
        {
            return NotFound();
        }

        var transactions = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Customer)
            .Include(p => p.Supplier)
            .Include(p => p.Employee)
            .Include(p => p.Driver)
            .Include(p => p.Contract)
            .Where(p => p.CashAccountId == id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Take(100)
            .Select(p => new PaymentListItemViewModel
            {
                Id = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = PaymentDirectionLabels.ToPersian(p.Direction),
                PaymentKind = p.PaymentKind,
                PaymentKindName = PaymentKindLabels.ToPersian(p.PaymentKind),
                CashAccountName = item.Name,
                CashAccountCurrency = item.Currency,
                CounterpartyName = p.Customer != null ? p.Customer.Name : p.Supplier != null ? p.Supplier.Name : p.Employee != null ? p.Employee.FullName : p.Driver != null ? p.Driver.FullName : "متفرقه",
                ContractNumber = p.Contract != null ? p.Contract.ContractNumber : null,
                RelatedTo = p.Contract != null ? p.Contract.ContractNumber : "—",
                Description = p.Description,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                LedgerEntryId = p.LedgerEntryId
            })
            .ToListAsync();
        var totalIn = transactions.Where(t => t.Direction == PaymentDirection.In).Sum(t => t.Amount);
        var totalOut = transactions.Where(t => t.Direction == PaymentDirection.Out).Sum(t => t.Amount);
        ViewBag.PaymentTransactions = transactions;
        ViewBag.TotalIn = totalIn;
        ViewBag.TotalOut = totalOut;
        ViewBag.ClosingBalance = totalIn - totalOut;

        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new CashAccount
        {
            Currency = SystemCurrency.BaseCurrencyCode,
            IsActive = true,
            AccountType = CashAccountType.Bank
        };

        await PopulateCurrenciesAsync(model.Currency);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CashAccount model)
    {
        Normalize(model);
        await ValidateAsync(model, model.Id);

        if (!ModelState.IsValid)
        {
            await PopulateCurrenciesAsync(model.Currency);
            return View(model);
        }

        _db.CashAccounts.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(CashAccount),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Code", model.Code),
                ("Name", model.Name),
                ("AccountType", model.AccountType),
                ("Currency", model.Currency),
                ("IsActive", model.IsActive),
                ("AccountNumber", model.AccountNumber),
                ("BankName", model.BankName),
                ("Branch", model.Branch),
                ("Notes", model.Notes)));

        TempData["ok"] = "حساب نقد / بانک با موفقیت ثبت شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.CashAccounts.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null)
        {
            return NotFound();
        }

        await PopulateCurrenciesAsync(item.Currency);
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.CashAccounts.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(CashAccount), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, CashAccount model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        Normalize(model);
        await ValidateAsync(model, id);

        if (!ModelState.IsValid)
        {
            await PopulateCurrenciesAsync(model.Currency);
            return View(model);
        }

        var existing = await _db.CashAccounts.FirstOrDefaultAsync(x => x.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        var diff = AuditDiffFormatter.ForUpdate(
            ("Code", existing.Code, model.Code),
            ("Name", existing.Name, model.Name),
            ("AccountType", existing.AccountType, model.AccountType),
            ("Currency", existing.Currency, model.Currency),
            ("IsActive", existing.IsActive, model.IsActive),
            ("AccountNumber", existing.AccountNumber, model.AccountNumber),
            ("BankName", existing.BankName, model.BankName),
            ("Branch", existing.Branch, model.Branch),
            ("Notes", existing.Notes, model.Notes));

        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.AccountType = model.AccountType;
        existing.Currency = model.Currency;
        existing.IsActive = model.IsActive;
        existing.AccountNumber = model.AccountNumber;
        existing.BankName = model.BankName;
        existing.Branch = model.Branch;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(CashAccount), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش حساب نقد / بانک انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(CashAccount model, int currentId)
    {
        if (string.IsNullOrWhiteSpace(model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "کد حساب الزامی است.");
        }

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "نام حساب الزامی است.");
        }

        if (string.IsNullOrWhiteSpace(model.Currency))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز حساب الزامی است.");
        }
        else
        {
            var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
            if (hasActiveCurrencies)
            {
                var normalizedCurrency = SystemCurrency.Normalize(model.Currency);
                var isValidCurrency = await _db.Currencies.AsNoTracking()
                    .AnyAsync(c => c.IsActive && c.Code == normalizedCurrency);
                if (!isValidCurrency)
                {
                    ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده در master data وجود ندارد یا غیرفعال است.");
                }
            }
        }

        if (await _db.CashAccounts.AnyAsync(a => a.Id != currentId && a.Code == model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "کد حساب تکراری است.");
        }
    }

    private static void Normalize(CashAccount model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.AccountNumber = string.IsNullOrWhiteSpace(model.AccountNumber) ? null : model.AccountNumber.Trim();
        model.BankName = string.IsNullOrWhiteSpace(model.BankName) ? null : model.BankName.Trim();
        model.Branch = string.IsNullOrWhiteSpace(model.Branch) ? null : model.Branch.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.CashAccounts.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new { a.Code, a.Name, AccountType = (int)a.AccountType, a.Currency, a.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }
}
