using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Auth;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;

namespace PTGOilSystem.Web.Controllers;

[Authorize(Policy = AuthPolicies.AdminOnly)]
public class RolesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;

    public RolesController(ApplicationDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(string? q = null)
        => await RolesIndexViewAsync(q);

    private async Task<IActionResult> RolesIndexViewAsync(string? q = null)
    {
        var query = q?.Trim();
        var rolesQuery = _db.Roles.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            rolesQuery = rolesQuery.Where(r =>
                r.Name.Contains(query) ||
                (r.Description != null && r.Description.Contains(query)));
        }

        var roles = await rolesQuery.OrderBy(r => r.Name).ToListAsync();

        ViewData["q"] = query;
        return View(roles);
    }

    public IActionResult Create()
    {
        PopulateRoleNavigationOptions();
        ViewData["ModalDesignSystemAssets"] = true;
        return View(new RoleCreateViewModel
        {
            AllowedNavigationItems = [RoleNavigationKeys.Dashboard]
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RoleCreateViewModel model)
    {
        var normalizedName = model.Name?.Trim() ?? "";
        var normalizedDescription = string.IsNullOrWhiteSpace(model.Description)
            ? null
            : model.Description.Trim();

        if (normalizedName.Contains(",", StringComparison.Ordinal))
            ModelState.AddModelError(nameof(model.Name), "نام نقش نباید کامه داشته باشد.");

        var normalizedUpper = normalizedName.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedName)
            && await _db.Roles.AnyAsync(r => r.Name.ToUpper() == normalizedUpper))
        {
            ModelState.AddModelError(nameof(model.Name), "این نقش قبلاً ثبت شده است.");
        }

        var allowedNavigation = NormalizeRoleAccess(model);

        if (!ModelState.IsValid)
        {
            model.Name = normalizedName;
            model.Description = normalizedDescription;
            PopulateRoleNavigationOptions();
            ViewData["ModalDesignSystemAssets"] = true;
            return View(model);
        }

        var role = new Role
        {
            Name = normalizedName,
            Description = normalizedDescription,
            CanManageData = model.CanManageData,
            CanManageUsers = model.CanManageUsers,
            AllowedNavigationItems = RoleAccessRules.SerializeNavigation(allowedNavigation),
            GrantedPermissions = RoleAccessRules.SerializeGrantedPermissions(model.GrantedPermissions)
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(
            nameof(Role),
            role.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Name", role.Name),
                ("Description", role.Description),
                ("CanManageData", role.CanManageData),
                ("CanManageUsers", role.CanManageUsers),
                ("AllowedNavigationItems", role.AllowedNavigationItems),
                ("GrantedPermissions", role.GrantedPermissions)));

        TempData["ok"] = "نقش جدید با موفقیت ثبت شد.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(int id)
    {
        var role = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        ViewBag.UserCount = await _db.Users.CountAsync(u => u.RoleId == id);
        ViewBag.AllowedNavigationLabels = RoleAccessRules.ResolveNavigationForRole(role)
            .Select(key => RoleAccessRules.NavigationItems.FirstOrDefault(item => item.Key == key)?.Label ?? key)
            .ToArray();
        ViewBag.GrantedPermissionLabels = RoleAccessRules.ResolveGrantedPermissions(role)
            .Select(key => RoleAccessRules.GrantablePermissions.First(p => p.Key == key).Label)
            .ToArray();
        return View(role);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var role = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        PopulateRoleNavigationOptions();
        ViewData["ModalDesignSystemAssets"] = true;
        return View(ToEditViewModel(role));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, RoleEditViewModel model)
    {
        if (id != model.Id) return BadRequest();

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        var normalizedName = model.Name?.Trim() ?? "";
        var normalizedDescription = string.IsNullOrWhiteSpace(model.Description)
            ? null
            : model.Description.Trim();
        var isBuiltInRole = AuthRoles.AllRoles.Contains(role.Name, StringComparer.Ordinal);
        var isAdminRole = string.Equals(role.Name, AuthRoles.Admin, StringComparison.Ordinal);

        if (isBuiltInRole && !string.Equals(normalizedName, role.Name, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(model.Name), "نام نقش اصلی سیستم قابل تغییر نیست.");
        }

        if (normalizedName.Contains(",", StringComparison.Ordinal))
            ModelState.AddModelError(nameof(model.Name), "نام نقش نباید کامه داشته باشد.");

        var normalizedUpper = normalizedName.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedName)
            && await _db.Roles.AnyAsync(r => r.Id != id && r.Name.ToUpper() == normalizedUpper))
        {
            ModelState.AddModelError(nameof(model.Name), "این نقش قبلاً ثبت شده است.");
        }

        var allowedNavigation = NormalizeRoleAccess(model);
        if (isAdminRole)
        {
            model.Name = AuthRoles.Admin;
            model.CanManageData = true;
            model.CanManageUsers = true;
            allowedNavigation = RoleAccessRules.AllNavigationKeys.ToArray();
        }

        if (!ModelState.IsValid)
        {
            model.Name = normalizedName;
            model.Description = normalizedDescription;
            model.IsBuiltInAdmin = isAdminRole;
            PopulateRoleNavigationOptions();
            ViewData["ModalDesignSystemAssets"] = true;
            return View(model);
        }

        var nextAllowedNavigation = RoleAccessRules.SerializeNavigation(allowedNavigation);
        // نقش Admin همه را ضمنی دارد؛ ستونش خالی می‌ماند تا منبعِ دوم ساخته نشود.
        var nextGrantedPermissions = isAdminRole
            ? null
            : RoleAccessRules.SerializeGrantedPermissions(model.GrantedPermissions);
        var diff = AuditDiffFormatter.ForUpdate(
            ("Name", role.Name, normalizedName),
            ("Description", role.Description, normalizedDescription),
            ("CanManageData", role.CanManageData, model.CanManageData),
            ("CanManageUsers", role.CanManageUsers, model.CanManageUsers),
            ("AllowedNavigationItems", role.AllowedNavigationItems, nextAllowedNavigation),
            ("GrantedPermissions", role.GrantedPermissions, nextGrantedPermissions));

        role.Name = isAdminRole ? AuthRoles.Admin : normalizedName;
        role.Description = normalizedDescription;
        role.CanManageData = model.CanManageData;
        role.CanManageUsers = model.CanManageUsers;
        role.AllowedNavigationItems = nextAllowedNavigation;
        role.GrantedPermissions = nextGrantedPermissions;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Role), role.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "تنظیمات نقش با موفقیت به‌روزرسانی شد.";
        return RedirectToAction(nameof(Details), new { id = role.Id });
    }

    private static RoleEditViewModel ToEditViewModel(Role role)
    {
        var isAdminRole = string.Equals(role.Name, AuthRoles.Admin, StringComparison.Ordinal);
        return new RoleEditViewModel
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            CanManageData = isAdminRole || role.CanManageData,
            CanManageUsers = isAdminRole || role.CanManageUsers,
            AllowedNavigationItems = RoleAccessRules.ResolveNavigationForRole(role).ToArray(),
            GrantedPermissions = RoleAccessRules.ResolveGrantedPermissions(role),
            IsBuiltInAdmin = isAdminRole
        };
    }

    private void PopulateRoleNavigationOptions()
        => ViewData["RoleNavigationItems"] = RoleAccessRules.NavigationItems;

    private static string[] NormalizeRoleAccess(RoleCreateViewModel model)
    {
        var selected = RoleAccessRules.NormalizeNavigation(model.AllowedNavigationItems);
        var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedSet.Contains(RoleNavigationKeys.Management))
        {
            model.CanManageUsers = true;
        }

        if (model.CanManageUsers)
        {
            selectedSet.Add(RoleNavigationKeys.Management);
        }

        return RoleAccessRules.NormalizeNavigation(selectedSet);
    }
}
