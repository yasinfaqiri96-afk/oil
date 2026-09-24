using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.HumanResources;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// قراردادهای کاری. فهرست با هشدارِ «رو به انقضا»، ثبت، تمدید (قراردادِ تازه)، فسخ با دلیل و
/// ویرایشِ فقط یادداشت/پیوست. مبلغ معاش فقط با «دیدن معاش» نمایش داده می‌شود.
/// </summary>
[Authorize]
public class EmploymentContractsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IEmploymentContractService _contracts;
    private readonly IHrFileStorage _files;

    public EmploymentContractsController(ApplicationDbContext db, IEmploymentContractService contracts, IHrFileStorage files)
    {
        _db = db;
        _contracts = contracts;
        _files = files;
    }

    private bool CanViewSalary => User is not null && RoleAccessRules.CanViewEmployeeSalary(User);
    private bool CanManageSalary => User is not null && RoleAccessRules.CanManageEmployeeSalary(User);

    public async Task<IActionResult> Index(string? q = null, string? status = null, bool expiring = false)
    {
        var today = AfghanistanBusinessClock.SystemToday;
        var soon = today.AddDays(EmploymentContractLabels.ExpiringSoonDays);
        var query = _db.EmploymentContracts.AsNoTracking().AsQueryable();

        var keyword = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (keyword is not null)
        {
            query = query.Where(c => c.ContractNumber.Contains(keyword)
                || c.Employee!.FullName.Contains(keyword)
                || c.Employee.EmployeeCode.Contains(keyword));
        }

        // «منقضی» شاملِ قراردادِ فعالی است که تاریخش گذشته.
        query = status switch
        {
            "active" => query.Where(c => c.Status == EmploymentContractStatus.Active && (c.EndDate == null || c.EndDate >= today)),
            "expired" => query.Where(c => c.Status == EmploymentContractStatus.Expired
                || (c.Status == EmploymentContractStatus.Active && c.EndDate != null && c.EndDate < today)),
            "terminated" => query.Where(c => c.Status == EmploymentContractStatus.Terminated),
            _ => query
        };
        if (expiring)
        {
            query = query.Where(c => c.Status == EmploymentContractStatus.Active && c.EndDate != null && c.EndDate >= today && c.EndDate <= soon);
        }

        var canViewSalary = CanViewSalary;
        var rows = await query
            .OrderBy(c => c.Status)
            .ThenBy(c => c.EndDate == null)
            .ThenBy(c => c.EndDate)
            .ThenByDescending(c => c.StartDate)
            .Take(500)
            .Select(c => new
            {
                c.Id, c.EmployeeId, EmployeeName = c.Employee!.FullName, c.Employee.EmployeeCode, c.ContractNumber,
                c.ContractType, c.StartDate, c.EndDate, c.Status,
                DepartmentName = c.Department != null ? c.Department.Name : null,
                PositionName = c.Position != null ? c.Position.Name : null,
                c.BaseSalary, c.Currency
            })
            .ToListAsync();

        var items = rows.Select(c =>
        {
            var effective = c.Status == EmploymentContractStatus.Active && c.EndDate.HasValue && c.EndDate.Value.Date < today
                ? EmploymentContractStatus.Expired
                : c.Status;
            return new EmploymentContractListItemViewModel
            {
                Id = c.Id,
                EmployeeId = c.EmployeeId,
                EmployeeName = c.EmployeeName,
                EmployeeCode = c.EmployeeCode,
                ContractNumber = c.ContractNumber,
                ContractType = c.ContractType,
                StartDate = c.StartDate,
                EndDate = c.EndDate,
                Status = effective,
                DepartmentName = c.DepartmentName,
                PositionName = c.PositionName,
                BaseSalary = canViewSalary ? c.BaseSalary : null,
                Currency = canViewSalary ? c.Currency : null,
                IsExpiringSoon = EmploymentContractLabels.IsExpiringSoon(effective, c.EndDate, today)
            };
        }).ToList();

        return View(new EmploymentContractIndexViewModel
        {
            Query = keyword,
            Status = status,
            ExpiringOnly = expiring,
            CanViewSalary = canViewSalary,
            ActiveCount = await _db.EmploymentContracts.CountAsync(c => c.Status == EmploymentContractStatus.Active && (c.EndDate == null || c.EndDate >= today)),
            ExpiringSoonCount = await _db.EmploymentContracts.CountAsync(c => c.Status == EmploymentContractStatus.Active && c.EndDate != null && c.EndDate >= today && c.EndDate <= soon),
            ActiveEmployeesWithoutContract = await _db.Employees.CountAsync(e => e.IsActive
                && !_db.EmploymentContracts.Any(c => c.EmployeeId == e.Id && c.Status == EmploymentContractStatus.Active)),
            Items = items
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var contract = await _db.EmploymentContracts.AsNoTracking()
            .Include(c => c.Employee)
            .Include(c => c.Department)
            .Include(c => c.Position)
            .Include(c => c.Attachment)
            .Include(c => c.RenewedFromContract)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (contract is null) return NotFound();

        var today = AfghanistanBusinessClock.SystemToday;
        var canViewSalary = CanViewSalary;
        var history = await _db.EmploymentContracts.AsNoTracking()
            .Where(c => c.EmployeeId == contract.EmployeeId)
            .OrderByDescending(c => c.StartDate)
            .Select(c => new EmploymentContractListItemViewModel
            {
                Id = c.Id,
                EmployeeId = c.EmployeeId,
                ContractNumber = c.ContractNumber,
                ContractType = c.ContractType,
                StartDate = c.StartDate,
                EndDate = c.EndDate,
                Status = c.Status,
                DepartmentName = c.Department != null ? c.Department.Name : null,
                PositionName = c.Position != null ? c.Position.Name : null,
                BaseSalary = canViewSalary ? c.BaseSalary : null,
                Currency = canViewSalary ? c.Currency : null
            })
            .ToListAsync();

        var effective = contract.EffectiveStatus(today);
        return View(new EmploymentContractDetailsViewModel
        {
            Contract = contract,
            EffectiveStatus = effective,
            IsExpiringSoon = EmploymentContractLabels.IsExpiringSoon(effective, contract.EndDate, today),
            CanViewSalary = canViewSalary,
            CanManage = RoleAccessRules.CanManageData(User),
            History = history
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? employeeId = null)
    {
        var model = new EmploymentContractFormViewModel { EmployeeId = employeeId ?? 0 };
        if (employeeId.HasValue)
        {
            var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId.Value);
            if (employee is not null)
            {
                model.DepartmentId = employee.DepartmentId;
                model.PositionId = employee.PositionId;
                model.BaseSalary = employee.BaseSalaryAmount;
                model.Currency = employee.SalaryCurrency;
            }
        }

        await PopulateLookupsAsync(model);
        return View("Form", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(EmploymentContractFormViewModel model)
    {
        model.RenewFromContractId = null;
        if (ModelState.IsValid)
        {
            try
            {
                var contract = await _contracts.CreateAsync(model.EmployeeId, ToTerms(model), CanManageSalary);
                TempData["ok"] = "قرارداد ثبت شد.";
                return RedirectToAction(nameof(Details), new { id = contract.Id });
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        await PopulateLookupsAsync(model);
        return View("Form", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Renew(int id)
    {
        var previous = await _db.EmploymentContracts.AsNoTracking().Include(c => c.Employee).FirstOrDefaultAsync(c => c.Id == id);
        if (previous is null) return NotFound();
        if (previous.Status != EmploymentContractStatus.Active)
        {
            TempData["err"] = "فقط قراردادِ فعال تمدید می‌شود.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var model = new EmploymentContractFormViewModel
        {
            RenewFromContractId = previous.Id,
            EmployeeId = previous.EmployeeId,
            EmployeeName = previous.Employee?.FullName,
            ContractType = previous.ContractType,
            StartDate = (previous.EndDate ?? AfghanistanBusinessClock.SystemToday).Date.AddDays(previous.EndDate.HasValue ? 1 : 0),
            DepartmentId = previous.DepartmentId,
            PositionId = previous.PositionId,
            BaseSalary = previous.BaseSalary,
            Currency = previous.Currency,
            WorkingDaysPerWeek = previous.WorkingDaysPerWeek,
            WorkingHoursPerDay = previous.WorkingHoursPerDay
        };
        await PopulateLookupsAsync(model);
        return View("Form", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Renew(int id, EmploymentContractFormViewModel model)
    {
        model.RenewFromContractId = id;
        if (ModelState.IsValid)
        {
            try
            {
                var contract = await _contracts.RenewAsync(id, ToTerms(model), CanManageSalary);
                TempData["ok"] = "قرارداد تمدید شد؛ قراردادِ قبلی در تاریخچه ماند.";
                return RedirectToAction(nameof(Details), new { id = contract.Id });
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        await PopulateLookupsAsync(model);
        return View("Form", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Terminate(int id, DateTime terminatedOn, string? reason)
    {
        try
        {
            await _contracts.TerminateAsync(id, terminatedOn, reason ?? "");
            TempData["ok"] = "قرارداد فسخ شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateNotes(int id, string? notes, IFormFile? attachment)
    {
        try
        {
            await _contracts.UpdateNotesAsync(id, notes, attachment);
            TempData["ok"] = "یادداشت/فایلِ قرارداد ذخیره شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>فایلِ قرارداد معمولاً مبلغ معاش دارد؛ فقط با «دیدن معاش» باز می‌شود.</summary>
    [Authorize(Policy = AuthPolicies.HrViewSalary)]
    public async Task<IActionResult> Attachment(int id)
    {
        var attachment = await _db.EmploymentContracts.AsNoTracking()
            .Where(c => c.Id == id && c.Attachment != null)
            .Select(c => c.Attachment)
            .FirstOrDefaultAsync();
        if (attachment is null) return NotFound();

        var path = _files.ResolvePath(attachment);
        if (path is null) return NotFound();

        return PhysicalFile(path, attachment.ContentType ?? "application/octet-stream", attachment.OriginalFileName);
    }

    private static ContractTerms ToTerms(EmploymentContractFormViewModel model)
        => new(
            model.ContractNumber,
            model.ContractType,
            model.StartDate,
            model.EndDate,
            model.DepartmentId,
            model.PositionId,
            model.BaseSalary,
            model.Currency,
            model.WorkingDaysPerWeek,
            model.WorkingHoursPerDay,
            model.Notes,
            model.Attachment);

    private async Task PopulateLookupsAsync(EmploymentContractFormViewModel model)
    {
        if (model.EmployeeId > 0 && string.IsNullOrWhiteSpace(model.EmployeeName))
        {
            model.EmployeeName = await _db.Employees.AsNoTracking()
                .Where(e => e.Id == model.EmployeeId)
                .Select(e => e.EmployeeCode + " - " + e.FullName)
                .FirstOrDefaultAsync();
        }

        ViewBag.CanManageSalary = CanManageSalary;
        ViewBag.Employees = new SelectList(
            await _db.Employees.AsNoTracking()
                .Where(e => e.IsActive || e.Id == model.EmployeeId)
                .OrderBy(e => e.FullName)
                .Select(e => new { e.Id, Label = e.EmployeeCode + " - " + e.FullName })
                .ToListAsync(),
            "Id", "Label", model.EmployeeId);
        ViewBag.Departments = new SelectList(
            await _db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(),
            "Id", "Name", model.DepartmentId);
        ViewBag.Positions = new SelectList(
            await _db.Positions.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(),
            "Id", "Name", model.PositionId);
        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
        ViewBag.ContractTypes = Enum.GetValues<EmploymentContractType>()
            .Select(t => new SelectListItem(EmploymentContractLabels.Type(t), ((int)t).ToString(), t == model.ContractType))
            .ToList();
    }
}
