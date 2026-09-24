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

/// <summary>
/// حاضری: ثبتِ سریعِ روزانه (همهٔ کارمندانِ فعال در یک جدول) و جدولِ ماهانه. دستگاهِ انگشت‌نگار
/// عمداً در این مرحله نیست.
/// </summary>
[Authorize]
public class AttendanceController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAttendanceService _attendance;
    private readonly IHrCalendarService _calendar;

    public AttendanceController(ApplicationDbContext db, IAttendanceService attendance, IHrCalendarService calendar)
    {
        _db = db;
        _attendance = attendance;
        _calendar = calendar;
    }

    public async Task<IActionResult> Index(DateTime? date = null, int? departmentId = null)
    {
        var day = (date ?? AfghanistanBusinessClock.SystemToday).Date;
        return View(await BuildDayAsync(day, departmentId));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(DateTime date, int? departmentId, List<AttendanceRowInput> rows, string? correctionReason)
    {
        try
        {
            var result = await _attendance.SaveDayAsync(
                date,
                (rows ?? []).Select(r => new AttendanceEntry(r.EmployeeId, r.Status, r.CheckIn, r.CheckOut, r.Notes)).ToList(),
                correctionReason);
            TempData["ok"] = $"حاضری ذخیره شد: {result.Created:N0} ثبت تازه، {result.Corrected:N0} اصلاح.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { date = date.ToString("yyyy-MM-dd"), departmentId });
    }

    public async Task<IActionResult> Monthly(int? year = null, int? month = null)
    {
        var today = AfghanistanBusinessClock.SystemToday;
        var y = year is >= 2000 and <= 2100 ? year.Value : today.Year;
        var m = month is >= 1 and <= 12 ? month.Value : today.Month;
        var start = new DateTime(y, m, 1);
        var end = new DateTime(y, m, DateTime.DaysInMonth(y, m));

        var records = await _db.DailyAttendances.AsNoTracking()
            .Where(a => a.Date >= start && a.Date <= end)
            .Select(a => new { a.EmployeeId, a.Date, a.Status, a.MinutesLate })
            .ToListAsync();
        var recordedIds = records.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.IsActive || recordedIds.Contains(e.Id))
            .OrderBy(e => e.FullName)
            .Select(e => new { e.Id, e.FullName, e.EmployeeCode })
            .ToListAsync();

        var settings = await _calendar.GetSettingsAsync();
        var holidays = await _calendar.HolidaysAsync(start, end);
        var offDays = Enumerable.Range(1, end.Day)
            .Where(d => settings.OffDays().Contains(new DateTime(y, m, d).DayOfWeek) || holidays.Contains(new DateTime(y, m, d)))
            .ToHashSet();

        var rows = employees.Select(e =>
        {
            var mine = records.Where(r => r.EmployeeId == e.Id).ToList();
            return new AttendanceMonthRowViewModel
            {
                EmployeeId = e.Id,
                EmployeeName = e.FullName,
                EmployeeCode = e.EmployeeCode,
                Days = mine.ToDictionary(r => r.Date.Day, r => r.Status),
                LateDays = mine.Where(r => r.MinutesLate > 0).Select(r => r.Date.Day).ToHashSet(),
                Present = mine.Count(r => r.Status == AttendanceStatus.Present),
                Absent = mine.Count(r => r.Status == AttendanceStatus.Absent),
                Leave = mine.Count(r => r.Status == AttendanceStatus.Leave),
                Late = mine.Count(r => r.MinutesLate > 0)
            };
        }).ToList();

        return View(new AttendanceMonthViewModel { Year = y, Month = m, OffDays = offDays, Rows = rows });
    }

    private async Task<AttendanceDayViewModel> BuildDayAsync(DateTime day, int? departmentId)
    {
        var records = await _db.DailyAttendances.AsNoTracking()
            .Where(a => a.Date == day)
            .ToDictionaryAsync(a => a.EmployeeId);
        var recordedIds = records.Keys.ToList();

        var employeesQuery = _db.Employees.AsNoTracking()
            .Where(e => (e.IsActive && e.HireDate <= day) || recordedIds.Contains(e.Id));
        if (departmentId.HasValue)
        {
            employeesQuery = employeesQuery.Where(e => e.DepartmentId == departmentId);
        }

        var employees = await employeesQuery
            .OrderBy(e => e.FullName)
            .Select(e => new { e.Id, e.FullName, e.EmployeeCode, DepartmentName = e.DepartmentRef != null ? e.DepartmentRef.Name : e.Department })
            .ToListAsync();

        var settings = await _calendar.GetSettingsAsync();
        var holiday = await _db.HrHolidays.AsNoTracking().FirstOrDefaultAsync(h => h.Date == day);

        ViewBag.Departments = new SelectList(
            await _db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(),
            "Id", "Name", departmentId);

        return new AttendanceDayViewModel
        {
            Date = day,
            DepartmentId = departmentId,
            IsWeeklyOff = settings.OffDays().Contains(day.DayOfWeek),
            HolidayName = holiday?.Name,
            CanEdit = RoleAccessRules.CanManageData(User),
            Rows = employees.Select(e =>
            {
                records.TryGetValue(e.Id, out var r);
                return new AttendanceDayRowViewModel
                {
                    EmployeeId = e.Id,
                    EmployeeName = e.FullName,
                    EmployeeCode = e.EmployeeCode,
                    DepartmentName = e.DepartmentName,
                    Status = r?.Status,
                    CheckIn = r?.CheckIn,
                    CheckOut = r?.CheckOut,
                    MinutesLate = r?.MinutesLate,
                    Notes = r?.Notes,
                    IsRecorded = r is not null,
                    LockedByLeave = r?.LeaveRequestId is not null,
                    LastCorrectionReason = r?.LastCorrectionReason
                };
            }).ToList()
        };
    }
}
