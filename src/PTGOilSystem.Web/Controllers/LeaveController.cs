using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.HumanResources;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>رخصتی‌ها: فهرست با درخواست‌های در انتظار، ثبت، تأیید/رد (یک مرحله) و لغو با دلیل.</summary>
[Authorize]
public class LeaveController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ILeaveService _leave;
    private readonly IHrFileStorage _files;

    public LeaveController(ApplicationDbContext db, ILeaveService leave, IHrFileStorage files)
    {
        _db = db;
        _leave = leave;
        _files = files;
    }

    public async Task<IActionResult> Index(string? status = null, int? employeeId = null)
    {
        var query = _db.LeaveRequests.AsNoTracking().AsQueryable();
        var statusFilter = status switch
        {
            "pending" => LeaveRequestStatus.Pending,
            "approved" => LeaveRequestStatus.Approved,
            "rejected" => LeaveRequestStatus.Rejected,
            "cancelled" => LeaveRequestStatus.Cancelled,
            _ => (LeaveRequestStatus?)null
        };
        if (statusFilter.HasValue) query = query.Where(r => r.Status == statusFilter.Value);
        if (employeeId.HasValue) query = query.Where(r => r.EmployeeId == employeeId.Value);

        var items = await query
            .OrderBy(r => r.Status == LeaveRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.FromDate)
            .Take(300)
            .Select(r => new LeaveRequestListItemViewModel
            {
                Id = r.Id,
                EmployeeId = r.EmployeeId,
                EmployeeName = r.Employee!.FullName,
                EmployeeCode = r.Employee.EmployeeCode,
                LeaveTypeName = r.LeaveType!.Name,
                IsPaid = r.LeaveType.IsPaid,
                FromDate = r.FromDate,
                ToDate = r.ToDate,
                IsHalfDay = r.IsHalfDay,
                TotalDays = r.TotalDays,
                Reason = r.Reason,
                Status = r.Status,
                DecisionNote = r.Status == LeaveRequestStatus.Rejected ? r.RejectionReason
                    : r.Status == LeaveRequestStatus.Cancelled ? r.CancellationReason : null,
                HasAttachment = r.AttachmentId != null
            })
            .ToListAsync();

        var today = AfghanistanBusinessClock.SystemToday;
        ViewBag.Employees = new SelectList(
            await _db.Employees.AsNoTracking().OrderBy(e => e.FullName).Select(e => new { e.Id, e.FullName }).ToListAsync(),
            "Id", "FullName", employeeId);

        return View(new LeaveIndexViewModel
        {
            Status = status,
            EmployeeId = employeeId,
            PendingCount = await _db.LeaveRequests.CountAsync(r => r.Status == LeaveRequestStatus.Pending),
            OnLeaveToday = await _db.LeaveRequests.CountAsync(r => r.Status == LeaveRequestStatus.Approved && r.FromDate <= today && r.ToDate >= today),
            CanManage = RoleAccessRules.CanManageData(User),
            Items = items
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? employeeId = null)
    {
        var model = new LeaveRequestFormViewModel { EmployeeId = employeeId ?? 0 };
        await PopulateAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(LeaveRequestFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await _leave.SubmitAsync(new LeaveRequestInput(
                    model.EmployeeId, model.LeaveTypeId, model.FromDate, model.ToDate, model.IsHalfDay, model.Reason, model.Attachment));
                TempData["ok"] = "درخواست رخصتی ثبت شد و در انتظار تأیید است.";
                return RedirectToAction(nameof(Index), new { status = "pending" });
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        await PopulateAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? returnUrl = null)
        => await DecideAsync(() => _leave.ApproveAsync(id, CurrentUserId()), "رخصتی تأیید شد و در حاضری ثبت شد.", returnUrl);

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? reason, string? returnUrl = null)
        => await DecideAsync(() => _leave.RejectAsync(id, CurrentUserId(), reason ?? ""), "درخواست رخصتی رد شد.", returnUrl);

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason, string? returnUrl = null)
        => await DecideAsync(() => _leave.CancelAsync(id, reason ?? ""), "رخصتی لغو شد.", returnUrl);

    public async Task<IActionResult> Attachment(int id)
    {
        var attachment = await _db.LeaveRequests.AsNoTracking()
            .Where(r => r.Id == id && r.Attachment != null)
            .Select(r => r.Attachment)
            .FirstOrDefaultAsync();
        if (attachment is null) return NotFound();

        var path = _files.ResolvePath(attachment);
        if (path is null) return NotFound();
        return PhysicalFile(path, attachment.ContentType ?? "application/octet-stream", attachment.OriginalFileName);
    }

    private async Task<IActionResult> DecideAsync(Func<Task> action, string ok, string? returnUrl)
    {
        try
        {
            await action();
            TempData["ok"] = ok;
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    private int? CurrentUserId()
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private async Task PopulateAsync(LeaveRequestFormViewModel model)
    {
        ViewBag.Employees = new SelectList(
            await _db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName)
                .Select(e => new { e.Id, Label = e.EmployeeCode + " - " + e.FullName }).ToListAsync(),
            "Id", "Label", model.EmployeeId);
        ViewBag.LeaveTypes = new SelectList(
            await _db.LeaveTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name)
                .Select(t => new { t.Id, Label = t.Name + (t.IsPaid ? "" : " (بدون معاش)") }).ToListAsync(),
            "Id", "Label", model.LeaveTypeId);
        if (model.EmployeeId > 0)
        {
            ViewBag.Balances = await _leave.GetBalancesAsync(model.EmployeeId, model.FromDate.Year);
        }
    }
}
