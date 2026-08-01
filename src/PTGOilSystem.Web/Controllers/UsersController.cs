using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Auth;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Helpers;

namespace PTGOilSystem.Web.Controllers;

[Authorize(Policy = AuthPolicies.AdminOnly)]
public class UsersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IUserService _users;
    private readonly IAuditService _audit;

    public UsersController(ApplicationDbContext db, IUserService users, IAuditService audit)
    {
        _db = db;
        _users = users;
        _audit = audit;
    }

    private async Task PopulateRolesAsync(int? selectedRoleId = null)
    {
        var roles = await _db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync();

        ViewBag.Roles = new SelectList(roles, "Id", "Name", selectedRoleId);
    }

    public async Task<IActionResult> Index(string? q, string? status, DateTime? date, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.Users.Include(u => u.Role).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(u => u.Username.Contains(q) || u.FullName.Contains(q));
        if (status == "active")
            query = query.Where(u => u.IsActive);
        else if (status == "inactive")
            query = query.Where(u => !u.IsActive);
        if (date.HasValue)
        {
            var dayStart = date.Value.Date;
            var dayEnd = dayStart.AddDays(1);
            query = query.Where(u => u.CreatedAtUtc >= dayStart && u.CreatedAtUtc < dayEnd);
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
        ViewData["status"] = status;
        ViewData["date"] = date?.ToString("yyyy-MM-dd");
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;
        await PopulateRolesAsync();

        return View(await query
            .OrderBy(u => u.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var user = await _db.Users.Include(u => u.Role).AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        ViewBag.IsLastActiveAdmin = await IsLastActiveAdminAsync(user);
        return View(user);
    }

    public async Task<IActionResult> Create()
    {
        await PopulateRolesAsync();
        return View(new UserCreateViewModel { IsActive = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserCreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await PopulateRolesAsync(model.RoleId);
            return View(model);
        }

        try
        {
            var user = await _users.CreateUserAsync(
                model.Username,
                model.FullName,
                model.Password,
                model.RoleId);

            if (!model.IsActive)
            {
                user.IsActive = false;
                await _db.SaveChangesAsync();
            }

            var roleName = await _db.Roles
                .Where(r => r.Id == user.RoleId)
                .Select(r => r.Name)
                .FirstOrDefaultAsync();

            await _audit.LogAndSaveAsync(
                nameof(User),
                user.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("Username", user.Username),
                    ("FullName", user.FullName),
                    ("Role", roleName),
                    ("IsActive", user.IsActive)));

            TempData["ok"] = "کاربر با موفقیت ثبت شد.";
            return RedirectToAction(nameof(Details), new { id = user.Id });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateRolesAsync(model.RoleId);
            return View(model);
        }
    }

    public async Task<IActionResult> Edit(int id)
    {
        var user = await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        await PopulateRolesAsync(user.RoleId);
        return View(new UserEditViewModel
        {
            Id = user.Id,
            Username = user.Username,
            FullName = user.FullName,
            RoleId = user.RoleId,
            IsActive = user.IsActive
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, UserEditViewModel model)
    {
        if (id != model.Id) return BadRequest();
        if (!ModelState.IsValid)
        {
            await PopulateRolesAsync(model.RoleId);
            return View(model);
        }

        var user = await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        if (await _db.Users.AnyAsync(u => u.Id != id && u.Username == model.Username.Trim()))
            ModelState.AddModelError(nameof(model.Username), "این نام کاربری قبلاً ثبت شده است.");

        var newRole = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == model.RoleId);
        if (newRole is null)
            ModelState.AddModelError(nameof(model.RoleId), "نقش انتخاب‌شده معتبر نیست.");

        if (newRole is not null && await WouldLeaveSystemWithoutAdminAsync(user, newRole.Name, model.IsActive))
            ModelState.AddModelError(string.Empty, "غیرفعال کردن یا تغییر نقش آخرین مدیر سیستم مجاز نیست.");

        if (!ModelState.IsValid)
        {
            await PopulateRolesAsync(model.RoleId);
            return View(model);
        }

        var diff = AuditDiffFormatter.ForUpdate(
            ("Username", user.Username, model.Username.Trim()),
            ("FullName", user.FullName, model.FullName.Trim()),
            ("Role", user.Role?.Name, newRole!.Name),
            ("IsActive", user.IsActive, model.IsActive));

        user.Username = model.Username.Trim();
        user.FullName = model.FullName.Trim();
        user.RoleId = model.RoleId;
        user.IsActive = model.IsActive;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(User), user.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "اطلاعات کاربر با موفقیت به‌روزرسانی شد.";
        return RedirectToAction(nameof(Details), new { id = user.Id });
    }

    public async Task<IActionResult> ResetPassword(int id)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        return View(new ResetPasswordViewModel
        {
            UserId = user.Id,
            Username = user.Username
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordViewModel model)
    {
        if (id != model.UserId) return BadRequest();

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();

        if (!ModelState.IsValid)
        {
            model.Username = user.Username;
            return View(model);
        }

        try
        {
            await _users.ResetPasswordAsync(id, model.NewPassword);
            await _audit.LogAndSaveAsync(
                nameof(User),
                id,
                AuditAction.Update,
                diff: $"PasswordResetByAdmin: Username={user.Username}");

            TempData["ok"] = "رمز عبور کاربر با موفقیت بازنشانی شد.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            model.Username = user.Username;
            return View(model);
        }
    }

    private async Task<bool> IsLastActiveAdminAsync(User user)
    {
        if (!user.IsActive) return false;

        var roleName = user.Role?.Name ?? await _db.Roles
            .Where(r => r.Id == user.RoleId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync();

        if (roleName != AuthRoles.Admin) return false;

        var activeAdminCount = await _db.Users
            .Include(u => u.Role)
            .CountAsync(u => u.IsActive && u.Role != null && u.Role.Name == AuthRoles.Admin);

        return activeAdminCount <= 1;
    }

    private async Task<bool> WouldLeaveSystemWithoutAdminAsync(User existingUser, string nextRoleName, bool nextIsActive)
    {
        if (!await IsLastActiveAdminAsync(existingUser))
            return false;

        return !nextIsActive || nextRoleName != AuthRoles.Admin;
    }
}
