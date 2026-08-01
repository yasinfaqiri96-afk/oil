using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class ExpenseRulesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IExpenseRuleEngine _engine;
    private readonly IAuditService _audit;
    private readonly ILogger<ExpenseRulesController> _logger;

    public ExpenseRulesController(
        ApplicationDbContext db,
        IExpenseRuleEngine engine,
        IAuditService audit,
        ILogger<ExpenseRulesController> logger)
    {
        _db = db;
        _engine = engine;
        _audit = audit;
        _logger = logger;
    }

    private async Task PopulateLookupsAsync(
        ExpenseRuleEditViewModel? editModel = null,
        ExpenseRuleGenerateExpenseViewModel? generateModel = null)
    {
        ViewBag.ExpenseTypes = new SelectList(
            await _db.ExpenseTypes
                .AsNoTracking()
                .Where(e => e.IsActive)
                .OrderBy(e => e.Category)
                .ThenBy(e => e.Code)
                .Select(e => new
                {
                    e.Id,
                    DisplayName = (e.NamePersian ?? e.Name) + " (" + e.Code + ")"
                })
                .ToListAsync(),
            "Id",
            "DisplayName",
            editModel?.ExpenseTypeId);

        ViewBag.CalculationKinds = ExpenseRuleCalculationKinds.All
            .Select(kind => new SelectListItem
            {
                Value = kind,
                Text = kind,
                Selected = string.Equals(editModel?.CalculationKind, kind, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            editModel?.Currency);

        var contractLookupRows = await _db.Contracts
            .AsNoTracking()
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            contractLookupRows
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            generateModel?.ContractId);

        var shipments = await _db.Shipments
            .AsNoTracking()
            .OrderByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.ShipmentCode
            })
            .ToListAsync();
        ViewBag.Shipments = shipments
            .Select(s => new SelectListItem
            {
                Value = s.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(s.ShipmentCode) ? $"Shipment #{s.Id}" : s.ShipmentCode,
                Selected = generateModel?.ShipmentId == s.Id
            })
            .ToList();

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .OrderByDescending(d => d.DispatchDate)
            .ThenByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id,
                d.DispatchDate,
                TruckPlateNumber = d.Truck != null ? d.Truck.PlateNumber : null,
                Truck = d.Truck == null ? null : new { d.Truck.PlateNumber }
            })
            .ToListAsync();
        ViewBag.TruckDispatches = dispatches
            .Select(d => new SelectListItem
            {
                Value = d.Id.ToString(),
                Text = $"#{d.Id} - {(d.Truck?.PlateNumber ?? "بدون پلاک")} - {DateDisplay.Date(d.DispatchDate)}",
                Selected = generateModel?.TruckDispatchId == d.Id
            })
            .ToList();
    }

    private void PrepareFormView(string title, string postAction, string submitText)
    {
        ViewData["Title"] = title;
        ViewData["PostAction"] = postAction;
        ViewData["SubmitText"] = submitText;
    }

    public async Task<IActionResult> Index(string? q, string? calculationKind, bool? isActive)
    {
        var query = _db.ExpenseRules
            .Include(r => r.ExpenseType)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            query = query.Where(r =>
                r.Name.Contains(search)
                || r.CalculationKind.Contains(search)
                || r.Currency.Contains(search)
                || (r.ExpenseType != null
                    && ((r.ExpenseType.NamePersian != null && r.ExpenseType.NamePersian.Contains(search))
                        || r.ExpenseType.Name.Contains(search)
                        || r.ExpenseType.Code.Contains(search))));
        }

        if (!string.IsNullOrWhiteSpace(calculationKind))
        {
            var kind = calculationKind.Trim();
            query = query.Where(r => r.CalculationKind == kind);
        }

        if (isActive.HasValue)
        {
            query = query.Where(r => r.IsActive == isActive.Value);
        }

        ViewData["q"] = q;
        ViewData["calculationKind"] = calculationKind;
        ViewData["isActive"] = isActive;

        var items = await query
            .OrderByDescending(r => r.IsActive)
            .ThenBy(r => r.Name)
            .Select(r => new ExpenseRuleListItemViewModel
            {
                Id = r.Id,
                Name = r.Name,
                ExpenseTypeName = r.ExpenseType != null ? (r.ExpenseType.NamePersian ?? r.ExpenseType.Name) : string.Empty,
                CalculationKind = r.CalculationKind,
                Amount = r.Amount,
                Currency = r.Currency,
                IsActive = r.IsActive,
                UsageCount = _db.ExpenseTransactions.Count(e => e.ExpenseRuleId == r.Id)
            })
            .ToListAsync();

        return View(new ExpenseRuleIndexViewModel
        {
            Items = items
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new ExpenseRuleEditViewModel
        {
            CalculationKind = ExpenseRuleCalculationKinds.PerMt,
            Currency = SystemCurrency.BaseCurrencyCode,
            IsActive = true
        };

        PrepareFormView("ثبت Rule مصرف", nameof(Create), "ثبت Rule");
        await PopulateLookupsAsync(editModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ExpenseRuleEditViewModel model, string? returnUrl = null)
    {
        var normalized = NormalizeRuleModel(model);
        await ValidateRuleModelAsync(normalized);

        if (!ModelState.IsValid)
        {
            PrepareFormView("ثبت Rule مصرف", nameof(Create), "ثبت Rule");
            await PopulateLookupsAsync(editModel: normalized);
            return View(normalized);
        }

        try
        {
            var rule = new ExpenseRule
            {
                Name = normalized.Name,
                ExpenseTypeId = normalized.ExpenseTypeId,
                CalculationKind = normalized.CalculationKind,
                Amount = normalized.Amount,
                Currency = normalized.Currency,
                IsActive = normalized.IsActive
            };

            _db.ExpenseRules.Add(rule);
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(ExpenseRule),
                rule.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("Name", rule.Name),
                    ("ExpenseTypeId", rule.ExpenseTypeId),
                    ("CalculationKind", rule.CalculationKind),
                    ("Amount", rule.Amount),
                    ("Currency", rule.Currency),
                    ("IsActive", rule.IsActive)));

            TempData["ok"] = "Expense Rule با موفقیت ثبت شد.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id = rule.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create expense rule.");
            ModelState.AddModelError(string.Empty, "ثبت Expense Rule انجام نشد. دوباره تلاش کنید.");
        }

        PrepareFormView("ثبت Rule مصرف", nameof(Create), "ثبت Rule");
        await PopulateLookupsAsync(editModel: normalized);
        return View(normalized);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var rule = await _db.ExpenseRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (rule is null)
        {
            return NotFound();
        }

        var model = new ExpenseRuleEditViewModel
        {
            Id = rule.Id,
            Name = rule.Name,
            ExpenseTypeId = rule.ExpenseTypeId,
            CalculationKind = rule.CalculationKind,
            Amount = rule.Amount,
            Currency = rule.Currency,
            IsActive = rule.IsActive
        };

        PrepareFormView("ویرایش Rule مصرف", nameof(Edit), "ذخیره تغییرات");
        await PopulateLookupsAsync(editModel: model);
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ExpenseRuleEditViewModel model)
    {
        if (id != model.Id)
        {
            return NotFound();
        }

        var normalized = NormalizeRuleModel(model);
        await ValidateRuleModelAsync(normalized);

        var rule = await _db.ExpenseRules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            PrepareFormView("ویرایش Rule مصرف", nameof(Edit), "ذخیره تغییرات");
            await PopulateLookupsAsync(editModel: normalized);
            return View("Create", normalized);
        }

        try
        {
            var beforeName = rule.Name;
            var beforeExpenseTypeId = rule.ExpenseTypeId;
            var beforeKind = rule.CalculationKind;
            var beforeAmount = rule.Amount;
            var beforeCurrency = rule.Currency;
            var beforeIsActive = rule.IsActive;

            rule.Name = normalized.Name;
            rule.ExpenseTypeId = normalized.ExpenseTypeId;
            rule.CalculationKind = normalized.CalculationKind;
            rule.Amount = normalized.Amount;
            rule.Currency = normalized.Currency;
            rule.IsActive = normalized.IsActive;

            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(ExpenseRule),
                rule.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Name", beforeName, rule.Name),
                    ("ExpenseTypeId", beforeExpenseTypeId, rule.ExpenseTypeId),
                    ("CalculationKind", beforeKind, rule.CalculationKind),
                    ("Amount", beforeAmount, rule.Amount),
                    ("Currency", beforeCurrency, rule.Currency),
                    ("IsActive", beforeIsActive, rule.IsActive)));

            TempData["ok"] = "Expense Rule با موفقیت به‌روزرسانی شد.";
            return RedirectToAction(nameof(Details), new { id = rule.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit expense rule {ExpenseRuleId}.", id);
            ModelState.AddModelError(string.Empty, "ویرایش Expense Rule انجام نشد. دوباره تلاش کنید.");
        }

        PrepareFormView("ویرایش Rule مصرف", nameof(Edit), "ذخیره تغییرات");
        await PopulateLookupsAsync(editModel: normalized);
        return View("Create", normalized);
    }

    public async Task<IActionResult> Details(int id)
        => await BuildDetailsViewAsync(id);

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateExpense(int id, ExpenseRuleGenerateExpenseViewModel model)
    {
        var rule = await _db.ExpenseRules
            .Include(r => r.ExpenseType)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (rule is null)
        {
            return NotFound();
        }

        try
        {
            await _engine.GenerateExpenseAsync(
                rule,
                new ExpenseRuleGenerationRequest
                {
                    ExpenseDate = model.ExpenseDate,
                    ContractId = model.ContractId,
                    ShipmentId = model.ShipmentId,
                    TruckDispatchId = model.TruckDispatchId,
                    QuantityMt = model.QuantityMt,
                    BaseAmountUsd = model.BaseAmountUsd,
                    AppliedFxRateToUsd = model.AppliedFxRateToUsd,
                    Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim()
                });

            TempData["ok"] = "Expense از روی Rule با موفقیت تولید شد.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate expense from rule {ExpenseRuleId}.", id);
            ModelState.AddModelError(string.Empty, "تولید Expense از روی Rule انجام نشد. دوباره تلاش کنید.");
        }

        return await BuildDetailsViewAsync(id, model);
    }

    private async Task<IActionResult> BuildDetailsViewAsync(int id, ExpenseRuleGenerateExpenseViewModel? generation = null)
    {
        var rule = await _db.ExpenseRules
            .Include(r => r.ExpenseType)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (rule is null)
        {
            return NotFound();
        }

        generation ??= new ExpenseRuleGenerateExpenseViewModel
        {
            ExpenseDate = AfghanistanBusinessClock.SystemToday
        };

        await PopulateLookupsAsync(generateModel: generation);

        var generatedExpenses = await _db.ExpenseTransactions
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .AsNoTracking()
            .Where(e => e.ExpenseRuleId == id)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .Take(20)
            .Select(e => new ExpenseRuleGeneratedExpenseItemViewModel
            {
                Id = e.Id,
                ExpenseDate = e.ExpenseDate,
                Amount = e.Amount,
                Currency = e.Currency,
                AppliedFxRateToUsd = e.AppliedFxRateToUsd,
                AmountUsd = e.AmountUsd,
                ContractNumber = e.Contract != null ? e.Contract.ContractNumber : null,
                ShipmentCode = e.Shipment != null ? e.Shipment.ShipmentCode : null,
                TruckDispatchLabel = e.TruckDispatch != null
                    ? $"#{e.TruckDispatch.Id} - {(e.TruckDispatch.Truck != null ? e.TruckDispatch.Truck.PlateNumber : "بدون پلاک")}"
                    : null,
                Description = e.Description
            })
            .ToListAsync();

        return View(new ExpenseRuleDetailsViewModel
        {
            Id = rule.Id,
            Name = rule.Name,
            ExpenseTypeName = rule.ExpenseType != null ? (rule.ExpenseType.NamePersian ?? rule.ExpenseType.Name) : string.Empty,
            CalculationKind = rule.CalculationKind,
            Amount = rule.Amount,
            Currency = rule.Currency,
            IsActive = rule.IsActive,
            CanGenerateExpense = rule.IsActive,
            Generation = generation,
            GeneratedExpenses = generatedExpenses
        });
    }

    private async Task ValidateRuleModelAsync(ExpenseRuleEditViewModel model)
    {
        if (!ExpenseRuleCalculationKinds.All.Any(kind => string.Equals(kind, model.CalculationKind, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(model.CalculationKind), "نوع محاسبه انتخاب‌شده معتبر نیست.");
        }

        var expenseType = await _db.ExpenseTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == model.ExpenseTypeId && e.IsActive);
        if (expenseType is null)
        {
            ModelState.AddModelError(nameof(model.ExpenseTypeId), "نوع مصرف انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }
    }

    private static ExpenseRuleEditViewModel NormalizeRuleModel(ExpenseRuleEditViewModel model)
    {
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.CalculationKind = ExpenseRuleCalculationKinds.All
            .FirstOrDefault(kind => string.Equals(kind, model.CalculationKind?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? model.CalculationKind?.Trim()
            ?? ExpenseRuleCalculationKinds.PerMt;

        return model;
    }
}
