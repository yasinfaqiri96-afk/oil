using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class EmployeesController : Controller
{
    private const int IndexPageSize = 20;
    private const long MaxEmployeePhotoBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> AllowedEmployeePhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEmployeeSalaryService _salaryService;
    private readonly IWebHostEnvironment _environment;
    private readonly IPartyStatementReadService? _partyStatements;

    public EmployeesController(
        ApplicationDbContext db,
        IAuditService audit,
        IEmployeeSalaryService salaryService,
        IWebHostEnvironment environment,
        IPartyStatementReadService? partyStatements = null)
    {
        _db = db;
        _audit = audit;
        _salaryService = salaryService;
        _environment = environment;
        _partyStatements = partyStatements;
    }

    public async Task<IActionResult> Index([FromQuery] EmployeeIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        filter ??= new EmployeeIndexFilterViewModel();
        NormalizeFilter(filter);
        await PopulateLookupsAsync(filter: filter);

        var query = _db.Employees
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var keyword = filter.Query.Trim();
            query = query.Where(e =>
                e.EmployeeCode.Contains(keyword)
                || e.FullName.Contains(keyword)
                || (e.Phone != null && e.Phone.Contains(keyword))
                || (e.JobTitle != null && e.JobTitle.Contains(keyword))
                || (e.Department != null && e.Department.Contains(keyword)));
        }

        if (filter.EmployeeType.HasValue)
        {
            query = query.Where(e => e.EmployeeType == filter.EmployeeType.Value);
        }

        if (filter.SalaryType.HasValue)
        {
            query = query.Where(e => e.SalaryType == filter.SalaryType.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Department))
        {
            query = query.Where(e => e.Department != null && e.Department.Contains(filter.Department));
        }

        if (filter.IsActive.HasValue)
        {
            query = query.Where(e => e.IsActive == filter.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            query = query.Where(e => e.SalaryCurrency == filter.Currency);
        }

        var totalCount = await query.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var employees = await (page <= 0
                ? query.OrderByDescending(e => e.IsActive).ThenBy(e => e.EmployeeCode)
                : query
                    .OrderByDescending(e => e.IsActive)
                    .ThenBy(e => e.EmployeeCode)
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(e => new
            {
                e.Id,
                e.EmployeeCode,
                e.FullName,
                e.Phone,
                e.PhotoPath,
                e.JobTitle,
                e.Department,
                e.EmployeeType,
                e.SalaryType,
                e.BaseSalaryAmount,
                e.SalaryCurrency,
                e.IsActive,
                BalanceUsd = e.SalaryTransactions
                    .Where(t => !t.IsCancelled)
                    .Sum(t => (decimal?)(t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual
                        || t.TransactionType == EmployeeSalaryTransactionType.Bonus
                        || t.TransactionType == EmployeeSalaryTransactionType.Adjustment
                            ? t.AmountUsd
                            : t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment
                              || t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                              || t.TransactionType == EmployeeSalaryTransactionType.SalaryDeduction
                                ? -t.AmountUsd
                                : 0m)) ?? 0m
            })
            .ToListAsync();

        var items = employees
            .Select(e =>
            {
                return new EmployeeIndexItemViewModel
                {
                    Id = e.Id,
                    EmployeeCode = e.EmployeeCode,
                    FullName = e.FullName,
                    Phone = e.Phone,
                    PhotoPath = e.PhotoPath,
                    JobTitle = e.JobTitle,
                    Department = e.Department,
                    EmployeeType = e.EmployeeType,
                    EmployeeTypeName = EmployeeTypeLabels.ToPersian(e.EmployeeType),
                    SalaryType = e.SalaryType,
                    SalaryTypeName = EmployeeSalaryTypeLabels.ToPersian(e.SalaryType),
                    BaseSalaryAmount = e.BaseSalaryAmount,
                    SalaryCurrency = e.SalaryCurrency,
                    IsActive = e.IsActive,
                    BalanceUsd = e.BalanceUsd
                };
            })
            .ToList();

        return View(new EmployeeIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new EmployeeFormViewModel
        {
            HireDate = AfghanistanBusinessClock.SystemToday,
            SalaryCurrency = SystemCurrency.BaseCurrencyCode,
            IsActive = true
        };
        await PopulateLookupsAsync(form: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] EmployeeFormViewModel model, string? returnUrl = null)
    {
        NormalizeForm(model);
        ValidateEmployeePhoto(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        if (await _db.Employees.AnyAsync(e => e.EmployeeCode == model.EmployeeCode))
        {
            ModelState.AddModelError(nameof(model.EmployeeCode), "این کد کارمند قبلاً ثبت شده است.");
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        var employee = new Employee();
        ApplyForm(employee, model);
        employee.PhotoPath = await SaveEmployeePhotoAsync(model.PhotoFile);
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            nameof(Employee),
            employee.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("EmployeeCode", employee.EmployeeCode),
                ("FullName", employee.FullName),
                ("PhotoPath", employee.PhotoPath),
                ("EmployeeType", employee.EmployeeType),
                ("SalaryType", employee.SalaryType),
                ("BaseSalaryAmount", employee.BaseSalaryAmount),
                ("SalaryCurrency", employee.SalaryCurrency),
                ("HireDate", employee.HireDate),
                ("IsActive", employee.IsActive)));
        await _db.SaveChangesAsync();

        TempData["ok"] = "کارمند با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Details), new { id = employee.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        var model = ToForm(employee);
        await PopulateLookupsAsync(form: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Employees.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Employee), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [FromForm] EmployeeFormViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        NormalizeForm(model);
        ValidateEmployeePhoto(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        if (await _db.Employees.AnyAsync(e => e.Id != id && e.EmployeeCode == model.EmployeeCode))
        {
            ModelState.AddModelError(nameof(model.EmployeeCode), "این کد کارمند قبلاً برای کارمند دیگری ثبت شده است.");
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        var previous = new
        {
            employee.EmployeeCode,
            employee.FullName,
            employee.FatherName,
            employee.Phone,
            employee.Email,
            employee.PhotoPath,
            employee.NationalId,
            employee.Address,
            employee.JobTitle,
            employee.Department,
            employee.EmployeeType,
            employee.SalaryType,
            employee.BaseSalaryAmount,
            employee.SalaryCurrency,
            employee.HireDate,
            employee.EndDate,
            employee.IsActive,
            employee.Notes
        };

        ApplyForm(employee, model);
        var previousPhotoPath = employee.PhotoPath;
        var uploadedPhotoPath = await SaveEmployeePhotoAsync(model.PhotoFile);
        if (!string.IsNullOrWhiteSpace(uploadedPhotoPath))
        {
            employee.PhotoPath = uploadedPhotoPath;
        }

        await _audit.LogAsync(
            nameof(Employee),
            employee.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("EmployeeCode", previous.EmployeeCode, employee.EmployeeCode),
                ("FullName", previous.FullName, employee.FullName),
                ("FatherName", previous.FatherName, employee.FatherName),
                ("Phone", previous.Phone, employee.Phone),
                ("Email", previous.Email, employee.Email),
                ("PhotoPath", previous.PhotoPath, employee.PhotoPath),
                ("NationalId", previous.NationalId, employee.NationalId),
                ("Address", previous.Address, employee.Address),
                ("JobTitle", previous.JobTitle, employee.JobTitle),
                ("Department", previous.Department, employee.Department),
                ("EmployeeType", previous.EmployeeType, employee.EmployeeType),
                ("SalaryType", previous.SalaryType, employee.SalaryType),
                ("BaseSalaryAmount", previous.BaseSalaryAmount, employee.BaseSalaryAmount),
                ("SalaryCurrency", previous.SalaryCurrency, employee.SalaryCurrency),
                ("HireDate", previous.HireDate, employee.HireDate),
                ("EndDate", previous.EndDate, employee.EndDate),
                ("IsActive", previous.IsActive, employee.IsActive),
                ("Notes", previous.Notes, employee.Notes)));

        await _db.SaveChangesAsync();
        if (!string.IsNullOrWhiteSpace(uploadedPhotoPath))
        {
            DeleteEmployeePhoto(previousPhotoPath);
        }

        TempData["ok"] = "اطلاعات کارمند ویرایش شد.";
        return RedirectToAction(nameof(Details), new { id = employee.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var employee = await _db.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        var transactions = await _db.EmployeeSalaryTransactions
            .AsNoTracking()
            .Include(t => t.CashAccount)
            .Include(t => t.PaymentTransaction)
            .Include(t => t.LedgerEntry)
            .Where(t => t.EmployeeId == id)
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.Id)
            .ToListAsync();

        var transactionIds = transactions.Select(t => t.Id).ToList();
        var roznamchaPayments = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.CashAccount)
            .Include(p => p.Contract)
            .Include(p => p.Shipment)
            .Where(p => p.EmployeeId == id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Take(50)
            .Select(p => new PaymentListItemViewModel
            {
                Id = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = PaymentDirectionLabels.ToPersian(p.Direction),
                PaymentKind = p.PaymentKind,
                PaymentKindName = PaymentKindLabels.ToPersian(p.PaymentKind),
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : string.Empty,
                CashAccountCurrency = p.CashAccount != null ? p.CashAccount.Currency : p.Currency,
                CounterpartyTypeName = PaymentCounterpartyTypeLabels.ToPersian(PaymentCounterpartyType.Employee),
                CounterpartyName = employee.FullName,
                ContractNumber = p.Contract != null ? p.Contract.ContractNumber : null,
                ShipmentCode = p.Shipment != null ? p.Shipment.ShipmentCode : null,
                RelatedTo = p.Contract != null ? p.Contract.ContractNumber : p.Shipment != null ? p.Shipment.ShipmentCode : "—",
                Description = p.Description,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                LedgerEntryId = p.LedgerEntryId
            })
            .ToListAsync();

        var salaryPaymentIds = transactions
            .Where(t => t.PaymentTransactionId.HasValue)
            .Select(t => t.PaymentTransactionId!.Value)
            .ToHashSet();
        roznamchaPayments = roznamchaPayments
            .Where(p => !salaryPaymentIds.Contains(p.Id))
            .ToList();

        var auditItems = await _db.AuditLogs
            .AsNoTracking()
            .Where(a =>
                (a.EntityName == nameof(Employee) && a.EntityId == id)
                || (a.EntityName == nameof(EmployeeSalaryTransaction) && transactionIds.Contains(a.EntityId)))
            .OrderByDescending(a => a.ActionAtUtc)
            .Take(50)
            .Select(a => new EmployeeAuditItemViewModel
            {
                ActionAtUtc = a.ActionAtUtc,
                Action = a.Action,
                ActorUsername = a.ActorUsername,
                Description = a.Description,
                Diff = a.Diff
            })
            .ToListAsync();

        var model = new EmployeeDetailsViewModel
        {
            Id = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            FullName = employee.FullName,
            FatherName = employee.FatherName,
            Phone = employee.Phone,
            Email = employee.Email,
            PhotoPath = employee.PhotoPath,
            NationalId = employee.NationalId,
            Address = employee.Address,
            JobTitle = employee.JobTitle,
            Department = employee.Department,
            EmployeeType = employee.EmployeeType,
            EmployeeTypeName = EmployeeTypeLabels.ToPersian(employee.EmployeeType),
            SalaryType = employee.SalaryType,
            SalaryTypeName = EmployeeSalaryTypeLabels.ToPersian(employee.SalaryType),
            BaseSalaryAmount = employee.BaseSalaryAmount,
            SalaryCurrency = employee.SalaryCurrency,
            HireDate = employee.HireDate,
            EndDate = employee.EndDate,
            IsActive = employee.IsActive,
            Notes = employee.Notes,
            Summary = EmployeeSalarySummaryCalculator.FromTransactions(transactions),
            Transactions = transactions.Select(ToTransactionListItem).ToList(),
            RoznamchaPayments = roznamchaPayments,
            AuditItems = auditItems
        };
        if (_partyStatements is not null)
        {
            var statement = await _partyStatements.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Employee, id),
                new PartyStatementFilter { IncludeOperationalColumns = false },
                HttpContext.RequestAborted);
            ViewData["PartyStatementSummary"] = statement.Summary;
            ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        }
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateSalaryTransaction(int id, string? returnUrl = null)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        var now = AfghanistanBusinessClock.SystemToday;
        var model = new EmployeeSalaryTransactionCreateViewModel
        {
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName,
            TransactionDate = now,
            Currency = employee.SalaryCurrency,
            SalaryPeriodYear = now.Year,
            SalaryPeriodMonth = now.Month,
            ReturnUrl = returnUrl
        };
        await PopulateSalaryTransactionLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSalaryTransaction(int id, EmployeeSalaryTransactionCreateViewModel model)
    {
        if (id != model.EmployeeId)
        {
            return BadRequest();
        }

        NormalizeSalaryTransactionModel(model);
        if (!ModelState.IsValid)
        {
            await HydrateSalaryTransactionEmployeeAsync(model);
            await PopulateSalaryTransactionLookupsAsync(model);
            return View(model);
        }

        try
        {
            var transaction = await _salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
                model.EmployeeId,
                model.TransactionDate,
                model.TransactionType,
                model.Amount,
                model.Currency,
                model.AppliedFxRateToUsd,
                model.CashAccountId,
                model.Reference,
                model.Description,
                model.SalaryPeriodYear,
                model.SalaryPeriodMonth));

            TempData["ok"] = "تراکنش معاش ثبت شد.";
            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id = transaction.EmployeeId });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await HydrateSalaryTransactionEmployeeAsync(model);
            await PopulateSalaryTransactionLookupsAsync(model);
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSalaryTransaction(EmployeeSalaryTransactionCancelViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["err"] = "برای لغو تراکنش، دلیل لغو را وارد کنید.";
            return RedirectToAction(nameof(Details), new { id = model.EmployeeId });
        }

        try
        {
            await _salaryService.CancelAsync(model.TransactionId, model.CancellationReason);
            TempData["ok"] = "تراکنش معاش لغو شد. رکورد حذف نشد و برای بررسی مالی باقی ماند.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id = model.EmployeeId });
    }

    private async Task PopulateLookupsAsync(
        EmployeeFormViewModel? form = null,
        EmployeeIndexFilterViewModel? filter = null)
    {
        ViewBag.EmployeeTypes = Enum.GetValues<EmployeeType>()
            .Select(t => new SelectListItem
            {
                Value = ((int)t).ToString(),
                Text = EmployeeTypeLabels.ToPersian(t),
                Selected = (form?.EmployeeType ?? filter?.EmployeeType) == t
            })
            .ToList();

        ViewBag.SalaryTypes = Enum.GetValues<EmployeeSalaryType>()
            .Select(t => new SelectListItem
            {
                Value = ((int)t).ToString(),
                Text = EmployeeSalaryTypeLabels.ToPersian(t),
                Selected = (form?.SalaryType ?? filter?.SalaryType) == t
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
            form?.SalaryCurrency ?? filter?.Currency);

        ViewBag.Statuses = new List<SelectListItem>
        {
            new() { Value = "true", Text = "فعال", Selected = filter?.IsActive == true },
            new() { Value = "false", Text = "غیرفعال", Selected = filter?.IsActive == false }
        };
    }

    private async Task PopulateSalaryTransactionLookupsAsync(EmployeeSalaryTransactionCreateViewModel model)
    {
        ViewBag.TransactionTypes = Enum.GetValues<EmployeeSalaryTransactionType>()
            .Select(t => new SelectListItem
            {
                Value = ((int)t).ToString(),
                Text = EmployeeSalaryTransactionTypeLabels.ToPersian(t),
                Selected = model.TransactionType == t
            })
            .ToList();

        ViewBag.CashAccounts = new SelectList(
            await _db.CashAccounts
                .AsNoTracking()
                .Where(a => a.IsActive)
                .OrderBy(a => a.Code)
                .Select(a => new { a.Id, Label = a.Code + " - " + a.Name + " (" + a.Currency + ")" })
                .ToListAsync(),
            "Id",
            "Label",
            model.CashAccountId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            model.Currency);
    }

    private async Task HydrateSalaryTransactionEmployeeAsync(EmployeeSalaryTransactionCreateViewModel model)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == model.EmployeeId);
        if (employee is null)
        {
            return;
        }

        model.EmployeeCode = employee.EmployeeCode;
        model.EmployeeName = employee.FullName;
    }

    private bool TryGetLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        {
            localReturnUrl = returnUrl;
            return true;
        }

        localReturnUrl = string.Empty;
        return false;
    }

    private static EmployeeFormViewModel ToForm(Employee employee) => new()
    {
        Id = employee.Id,
        EmployeeCode = employee.EmployeeCode,
        FullName = employee.FullName,
        FatherName = employee.FatherName,
        Phone = employee.Phone,
        Email = employee.Email,
        PhotoPath = employee.PhotoPath,
        NationalId = employee.NationalId,
        Address = employee.Address,
        JobTitle = employee.JobTitle,
        Department = employee.Department,
        EmployeeType = employee.EmployeeType,
        SalaryType = employee.SalaryType,
        BaseSalaryAmount = employee.BaseSalaryAmount,
        SalaryCurrency = employee.SalaryCurrency,
        HireDate = employee.HireDate,
        EndDate = employee.EndDate,
        IsActive = employee.IsActive,
        Notes = employee.Notes
    };

    private static void ApplyForm(Employee employee, EmployeeFormViewModel model)
    {
        employee.EmployeeCode = model.EmployeeCode;
        employee.FullName = model.FullName;
        employee.FatherName = NormalizeText(model.FatherName);
        employee.Phone = NormalizeText(model.Phone);
        employee.Email = NormalizeText(model.Email);
        employee.NationalId = NormalizeText(model.NationalId);
        employee.Address = NormalizeText(model.Address);
        employee.JobTitle = NormalizeText(model.JobTitle);
        employee.Department = NormalizeText(model.Department);
        employee.EmployeeType = model.EmployeeType;
        employee.SalaryType = model.SalaryType;
        employee.BaseSalaryAmount = model.BaseSalaryAmount;
        employee.SalaryCurrency = SystemCurrency.Normalize(model.SalaryCurrency);
        employee.HireDate = model.HireDate.Date;
        employee.EndDate = model.EndDate?.Date;
        employee.IsActive = model.IsActive;
        employee.Notes = NormalizeText(model.Notes);
    }

    private static EmployeeSalaryTransactionListItemViewModel ToTransactionListItem(EmployeeSalaryTransaction transaction)
        => new()
        {
            Id = transaction.Id,
            EmployeeId = transaction.EmployeeId,
            EmployeeName = transaction.Employee?.FullName ?? "",
            EmployeeCode = transaction.Employee?.EmployeeCode ?? "",
            TransactionDate = transaction.TransactionDate,
            TransactionType = transaction.TransactionType,
            TransactionTypeName = EmployeeSalaryTransactionTypeLabels.ToPersian(transaction.TransactionType),
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            AmountUsd = transaction.AmountUsd,
            AppliedFxRateToUsd = transaction.AppliedFxRateToUsd,
            CashAccountName = transaction.CashAccount?.Name,
            PaymentTransactionId = transaction.PaymentTransactionId,
            LedgerEntryId = transaction.LedgerEntryId,
            Reference = transaction.Reference,
            Description = transaction.Description,
            SalaryPeriodYear = transaction.SalaryPeriodYear,
            SalaryPeriodMonth = transaction.SalaryPeriodMonth,
            IsCancelled = transaction.IsCancelled,
            CancellationReason = transaction.CancellationReason,
            CreatedAtUtc = transaction.CreatedAtUtc,
            CreatedByUserId = transaction.CreatedByUserId
        };

    private static void NormalizeFilter(EmployeeIndexFilterViewModel filter)
    {
        filter.Query = NormalizeText(filter.Query);
        filter.Department = NormalizeText(filter.Department);
        filter.Currency = string.IsNullOrWhiteSpace(filter.Currency) ? null : SystemCurrency.Normalize(filter.Currency);
    }

    private static void NormalizeForm(EmployeeFormViewModel model)
    {
        model.EmployeeCode = (model.EmployeeCode ?? string.Empty).Trim();
        model.FullName = (model.FullName ?? string.Empty).Trim();
        model.SalaryCurrency = string.IsNullOrWhiteSpace(model.SalaryCurrency)
            ? SystemCurrency.BaseCurrencyCode
            : SystemCurrency.Normalize(model.SalaryCurrency);
        model.HireDate = model.HireDate.Date;
        model.EndDate = model.EndDate?.Date;
    }

    private static void NormalizeSalaryTransactionModel(EmployeeSalaryTransactionCreateViewModel model)
    {
        model.TransactionDate = model.TransactionDate.Date;
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.Reference = NormalizeText(model.Reference);
        model.Description = NormalizeText(model.Description);
    }

    private void ValidateEmployeePhoto(EmployeeFormViewModel model)
    {
        if (model.PhotoFile is null || model.PhotoFile.Length == 0)
        {
            return;
        }

        if (model.PhotoFile.Length > MaxEmployeePhotoBytes)
        {
            ModelState.AddModelError(nameof(model.PhotoFile), "حجم عکس کارمند نباید بیشتر از 2MB باشد.");
            return;
        }

        var extension = Path.GetExtension(model.PhotoFile.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedEmployeePhotoExtensions.Contains(extension))
        {
            ModelState.AddModelError(nameof(model.PhotoFile), "فقط عکس با فرمت JPG، PNG یا WEBP قابل ثبت است.");
        }
    }

    private async Task<string?> SaveEmployeePhotoAsync(IFormFile? photoFile)
    {
        if (photoFile is null || photoFile.Length == 0)
        {
            return null;
        }

        var extension = Path.GetExtension(photoFile.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var webRootPath = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;
        var relativeDirectory = Path.Combine("uploads", "employees");
        var absoluteDirectory = Path.Combine(webRootPath, relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var absolutePath = Path.Combine(absoluteDirectory, fileName);
        await using var stream = System.IO.File.Create(absolutePath);
        await photoFile.CopyToAsync(stream);

        return "/" + relativeDirectory.Replace('\\', '/') + "/" + fileName;
    }

    private void DeleteEmployeePhoto(string? photoPath)
    {
        if (string.IsNullOrWhiteSpace(photoPath) || !photoPath.StartsWith("/uploads/employees/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = Path.GetFileName(photoPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var webRootPath = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;
        var absolutePath = Path.Combine(webRootPath, "uploads", "employees", fileName);
        if (System.IO.File.Exists(absolutePath))
        {
            System.IO.File.Delete(absolutePath);
        }
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
