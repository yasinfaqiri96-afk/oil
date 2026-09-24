using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «بخش‌ها و بست‌ها» — یک صفحه با دو تب. بخش/بستی که کارمند دارد حذف نمی‌شود، فقط غیرفعال.
/// تغییرِ نام به متنِ قدیمیِ کارمندانِ وصل‌شده هم می‌رسد تا جستجوی قدیمی درست بماند.
/// </summary>
[Authorize]
public class HrOrganizationController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public HrOrganizationController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(string? tab = null, string? q = null)
    {
        var activeTab = tab == "positions" ? "positions" : "departments";
        var keyword = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var departmentsQuery = _db.Departments.AsNoTracking();
        var positionsQuery = _db.Positions.AsNoTracking();
        if (keyword is not null)
        {
            departmentsQuery = departmentsQuery.Where(d => d.Name.Contains(keyword) || (d.Code != null && d.Code.Contains(keyword)));
            positionsQuery = positionsQuery.Where(p => p.Name.Contains(keyword)
                || (p.Department != null && p.Department.Name.Contains(keyword)));
        }

        var departments = await departmentsQuery
            .OrderByDescending(d => d.IsActive).ThenBy(d => d.Name)
            .Select(d => new DepartmentListItemViewModel
            {
                Id = d.Id,
                Name = d.Name,
                Code = d.Code,
                Description = d.Description,
                IsActive = d.IsActive,
                PositionCount = d.Positions.Count,
                ActiveEmployeeCount = _db.Employees.Count(e => e.DepartmentId == d.Id && e.IsActive)
            })
            .ToListAsync();

        var positions = await positionsQuery
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.Name)
            .Select(p => new PositionListItemViewModel
            {
                Id = p.Id,
                Name = p.Name,
                DepartmentName = p.Department != null ? p.Department.Name : null,
                Description = p.Description,
                IsActive = p.IsActive,
                ActiveEmployeeCount = _db.Employees.Count(e => e.PositionId == p.Id && e.IsActive)
            })
            .ToListAsync();

        var unlinked = await _db.Employees.AsNoTracking().CountAsync(e =>
            (e.DepartmentId == null && e.Department != null && e.Department != "")
            || (e.PositionId == null && e.JobTitle != null && e.JobTitle != ""));

        return View(new HrOrganizationIndexViewModel
        {
            Tab = activeTab,
            Query = keyword,
            Departments = departments,
            Positions = positions,
            UnlinkedEmployeeCount = unlinked
        });
    }

    // ---------------- بخش ----------------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> EditDepartment(int? id)
    {
        if (id is null)
        {
            return View("DepartmentForm", new DepartmentFormViewModel());
        }

        var department = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (department is null) return NotFound();

        return View("DepartmentForm", new DepartmentFormViewModel
        {
            Id = department.Id,
            Name = department.Name,
            Code = department.Code,
            Description = department.Description,
            IsActive = department.IsActive
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditDepartment(DepartmentFormViewModel model)
    {
        model.Name = (model.Name ?? "").Trim();
        model.Code = Normalize(model.Code);
        model.Description = Normalize(model.Description);

        if (!string.IsNullOrWhiteSpace(model.Name)
            && await _db.Departments.AnyAsync(d => d.Id != model.Id && d.Name.ToUpper() == model.Name.ToUpper()))
        {
            ModelState.AddModelError(nameof(model.Name), "بخشی با همین نام قبلاً ثبت شده است.");
        }

        if (!ModelState.IsValid)
        {
            return View("DepartmentForm", model);
        }

        if (model.Id == 0)
        {
            var created = new Department
            {
                Name = model.Name,
                Code = model.Code,
                Description = model.Description,
                IsActive = model.IsActive
            };
            _db.Departments.Add(created);
            await _db.SaveChangesAsync();
            await _audit.LogAndSaveAsync(nameof(Models.Entities.Department), created.Id, AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(("Name", created.Name), ("Code", created.Code), ("IsActive", created.IsActive)));
            TempData["ok"] = "بخش ثبت شد.";
            return RedirectToAction(nameof(Index), new { tab = "departments" });
        }

        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == model.Id);
        if (department is null) return NotFound();

        var previous = new { department.Name, department.Code, department.Description, department.IsActive };
        department.Name = model.Name;
        department.Code = model.Code;
        department.Description = model.Description;
        department.IsActive = model.IsActive;

        if (!string.Equals(previous.Name, department.Name, StringComparison.Ordinal))
        {
            var linked = await _db.Employees.Where(e => e.DepartmentId == department.Id).ToListAsync();
            foreach (var employee in linked)
            {
                employee.Department = department.Name;
            }
        }

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Models.Entities.Department), department.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("Name", previous.Name, department.Name),
                ("Code", previous.Code, department.Code),
                ("Description", previous.Description, department.Description),
                ("IsActive", previous.IsActive, department.IsActive)));

        TempData["ok"] = "بخش ویرایش شد.";
        return RedirectToAction(nameof(Index), new { tab = "departments" });
    }

    // ---------------- بست ----------------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> EditPosition(int? id, int? departmentId = null)
    {
        PositionFormViewModel model;
        if (id is null)
        {
            model = new PositionFormViewModel { DepartmentId = departmentId };
        }
        else
        {
            var position = await _db.Positions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (position is null) return NotFound();
            model = new PositionFormViewModel
            {
                Id = position.Id,
                Name = position.Name,
                DepartmentId = position.DepartmentId,
                Description = position.Description,
                IsActive = position.IsActive
            };
        }

        await PopulateDepartmentsAsync(model.DepartmentId);
        return View("PositionForm", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPosition(PositionFormViewModel model)
    {
        model.Name = (model.Name ?? "").Trim();
        model.Description = Normalize(model.Description);

        if (!string.IsNullOrWhiteSpace(model.Name)
            && await _db.Positions.AnyAsync(p => p.Id != model.Id && p.Name.ToUpper() == model.Name.ToUpper()))
        {
            ModelState.AddModelError(nameof(model.Name), "بستی با همین نام قبلاً ثبت شده است.");
        }

        if (model.DepartmentId.HasValue && !await _db.Departments.AnyAsync(d => d.Id == model.DepartmentId))
        {
            ModelState.AddModelError(nameof(model.DepartmentId), "بخش انتخاب‌شده معتبر نیست.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateDepartmentsAsync(model.DepartmentId);
            return View("PositionForm", model);
        }

        if (model.Id == 0)
        {
            var created = new Position
            {
                Name = model.Name,
                DepartmentId = model.DepartmentId,
                Description = model.Description,
                IsActive = model.IsActive
            };
            _db.Positions.Add(created);
            await _db.SaveChangesAsync();
            await _audit.LogAndSaveAsync(nameof(Models.Entities.Position), created.Id, AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(("Name", created.Name), ("DepartmentId", created.DepartmentId), ("IsActive", created.IsActive)));
            TempData["ok"] = "بست ثبت شد.";
            return RedirectToAction(nameof(Index), new { tab = "positions" });
        }

        var position = await _db.Positions.FirstOrDefaultAsync(p => p.Id == model.Id);
        if (position is null) return NotFound();

        var previous = new { position.Name, position.DepartmentId, position.Description, position.IsActive };
        position.Name = model.Name;
        position.DepartmentId = model.DepartmentId;
        position.Description = model.Description;
        position.IsActive = model.IsActive;

        if (!string.Equals(previous.Name, position.Name, StringComparison.Ordinal))
        {
            var linked = await _db.Employees.Where(e => e.PositionId == position.Id).ToListAsync();
            foreach (var employee in linked)
            {
                employee.JobTitle = position.Name;
            }
        }

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Models.Entities.Position), position.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("Name", previous.Name, position.Name),
                ("DepartmentId", previous.DepartmentId, position.DepartmentId),
                ("Description", previous.Description, position.Description),
                ("IsActive", previous.IsActive, position.IsActive)));

        TempData["ok"] = "بست ویرایش شد.";
        return RedirectToAction(nameof(Index), new { tab = "positions" });
    }

    /// <summary>
    /// کارمندانی که فقط متنِ قدیمیِ بخش/بست دارند، با تطبیقِ دقیقِ نام (بعد از حذفِ فاصلهٔ اضافه)
    /// به بخش/بستِ تعریف‌شده وصل می‌شوند. متنی که بخش/بستِ هم‌نام ندارد دست نمی‌خورد تا کاربر
    /// خودش تعیین کند؛ هیچ تطبیقِ تقریبی یا ساختِ خودکار انجام نمی‌شود.
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkLegacyText()
    {
        var departments = await _db.Departments.AsNoTracking().ToListAsync();
        var positions = await _db.Positions.AsNoTracking().ToListAsync();
        var employees = await _db.Employees
            .Where(e => (e.DepartmentId == null && e.Department != null) || (e.PositionId == null && e.JobTitle != null))
            .ToListAsync();

        var linked = 0;
        foreach (var employee in employees)
        {
            var changed = false;
            if (employee.DepartmentId is null && !string.IsNullOrWhiteSpace(employee.Department))
            {
                var match = departments.FirstOrDefault(d => string.Equals(d.Name, employee.Department.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    employee.DepartmentId = match.Id;
                    changed = true;
                }
            }

            if (employee.PositionId is null && !string.IsNullOrWhiteSpace(employee.JobTitle))
            {
                var match = positions.FirstOrDefault(p => string.Equals(p.Name, employee.JobTitle.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    employee.PositionId = match.Id;
                    changed = true;
                }
            }

            if (changed)
            {
                linked++;
                await _audit.LogAsync(nameof(Employee), employee.Id, AuditAction.Update,
                    diff: AuditDiffFormatter.ForUpdate(
                        ("DepartmentId", (int?)null, employee.DepartmentId),
                        ("PositionId", (int?)null, employee.PositionId)));
            }
        }

        await _db.SaveChangesAsync();
        TempData["ok"] = linked == 0
            ? "کارمندی با نامِ بخش/بستِ منطبق پیدا نشد."
            : $"{linked:N0} کارمند به بخش/بستِ هم‌نام وصل شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateDepartmentsAsync(int? selected)
    {
        ViewBag.Departments = new SelectList(
            await _db.Departments.AsNoTracking()
                .Where(d => d.IsActive || d.Id == selected)
                .OrderBy(d => d.Name)
                .Select(d => new { d.Id, d.Name })
                .ToListAsync(),
            "Id",
            "Name",
            selected);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
