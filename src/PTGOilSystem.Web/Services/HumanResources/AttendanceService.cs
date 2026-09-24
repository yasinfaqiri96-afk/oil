using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.HumanResources;

public sealed record AttendanceEntry(
    int EmployeeId,
    AttendanceStatus? Status,
    TimeSpan? CheckIn,
    TimeSpan? CheckOut,
    string? Notes);

public sealed record AttendanceSaveResult(int Created, int Corrected, int Unchanged);

public interface IAttendanceService
{
    /// <summary>
    /// حاضریِ یک روز را یکجا ذخیره می‌کند. سطرِ تازه بدونِ دلیل ثبت می‌شود؛ تغییرِ سطرِ موجود
    /// «اصلاح» است و بدونِ دلیل رد می‌شود. همه یا هیچ.
    /// </summary>
    Task<AttendanceSaveResult> SaveDayAsync(DateTime date, IReadOnlyList<AttendanceEntry> entries, string? correctionReason, CancellationToken ct = default);
}

public sealed class AttendanceService(ApplicationDbContext db, IHrCalendarService calendar, IAuditService audit) : IAttendanceService
{
    public async Task<AttendanceSaveResult> SaveDayAsync(
        DateTime date,
        IReadOnlyList<AttendanceEntry> entries,
        string? correctionReason,
        CancellationToken ct = default)
    {
        var day = date.Date;
        var reason = string.IsNullOrWhiteSpace(correctionReason) ? null : correctionReason.Trim();
        if (reason?.Length > 500) reason = reason[..500];

        var settings = await calendar.GetSettingsAsync(ct);
        var employeeIds = entries.Select(e => e.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);
        var existing = await db.DailyAttendances
            .Where(a => a.Date == day && employeeIds.Contains(a.EmployeeId))
            .ToDictionaryAsync(a => a.EmployeeId, ct);

        var created = 0;
        var corrected = 0;
        var unchanged = 0;
        var pendingAudits = new List<(DailyAttendance Row, Snapshot? Before)>();

        foreach (var entry in entries)
        {
            if (!entry.Status.HasValue)
            {
                unchanged++;
                continue;
            }

            if (!employees.TryGetValue(entry.EmployeeId, out var employee))
                throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");

            existing.TryGetValue(entry.EmployeeId, out var row);
            if (row?.LeaveRequestId is not null)
            {
                // روزِ رخصتیِ تأییدشده فقط با لغوِ همان رخصتی عوض می‌شود.
                if (row.Status != entry.Status.Value)
                    throw new BusinessRuleException("HR_ATTENDANCE_LEAVE_LOCKED",
                        $"حاضریِ {employee.FullName} در این روز از رخصتیِ تأییدشده آمده است؛ برای تغییر، رخصتی را لغو کنید.");
                unchanged++;
                continue;
            }

            if (entry.Status.Value == AttendanceStatus.Leave)
                throw new BusinessRuleException("HR_ATTENDANCE_LEAVE_MANUAL",
                    $"«رخصتی» برای {employee.FullName} فقط از درخواستِ رخصتیِ تأییدشده ثبت می‌شود.");

            var isPresent = entry.Status.Value == AttendanceStatus.Present;
            var checkIn = isPresent ? entry.CheckIn : null;
            var checkOut = isPresent ? entry.CheckOut : null;
            if (checkIn.HasValue && checkOut.HasValue && checkOut.Value < checkIn.Value)
                throw new BusinessRuleException("HR_ATTENDANCE_TIMES", $"ساعتِ خروجِ {employee.FullName} پیش از ورود است.");

            var minutesLate = isPresent ? HrCalendarService.LateMinutes(settings, checkIn) : null;
            var notes = string.IsNullOrWhiteSpace(entry.Notes) ? null : entry.Notes.Trim();
            if (notes?.Length > 500) notes = notes[..500];

            if (row is null)
            {
                if (!employee.IsActive)
                    throw new BusinessRuleException("EMPLOYEE_INACTIVE", $"برای کارمندِ غیرفعال ({employee.FullName}) حاضری ثبت نمی‌شود.");
                await EnsureNotLockedAsync(employee, day, ct);

                row = new DailyAttendance
                {
                    EmployeeId = employee.Id,
                    Date = day,
                    Status = entry.Status.Value,
                    CheckIn = checkIn,
                    CheckOut = checkOut,
                    MinutesLate = minutesLate,
                    Notes = notes
                };
                db.DailyAttendances.Add(row);
                pendingAudits.Add((row, null));
                created++;
                continue;
            }

            var changed = row.Status != entry.Status.Value
                || row.CheckIn != checkIn
                || row.CheckOut != checkOut
                || !string.Equals(row.Notes, notes, StringComparison.Ordinal);
            if (!changed)
            {
                unchanged++;
                continue;
            }

            if (reason is null)
                throw new BusinessRuleException("HR_ATTENDANCE_CORRECTION_REASON",
                    "حاضریِ ثبت‌شده تغییر کرده است؛ دلیلِ اصلاح را بنویسید.");
            await EnsureNotLockedAsync(employee, day, ct);

            var before = new Snapshot(row.Status, row.CheckIn, row.CheckOut, row.MinutesLate, row.Notes);
            row.Status = entry.Status.Value;
            row.CheckIn = checkIn;
            row.CheckOut = checkOut;
            row.MinutesLate = minutesLate;
            row.Notes = notes;
            row.LastCorrectionReason = reason;
            pendingAudits.Add((row, before));
            corrected++;
        }

        await db.SaveChangesAsync(ct);

        foreach (var (row, before) in pendingAudits)
        {
            if (before is null)
            {
                await audit.LogAsync(nameof(DailyAttendance), row.Id, AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("EmployeeId", row.EmployeeId),
                        ("Date", row.Date),
                        ("Status", row.Status),
                        ("CheckIn", row.CheckIn),
                        ("CheckOut", row.CheckOut),
                        ("MinutesLate", row.MinutesLate)));
            }
            else
            {
                await audit.LogAsync(nameof(DailyAttendance), row.Id, AuditAction.Update,
                    diff: AuditDiffFormatter.ForUpdate(
                        ("Status", before.Status, row.Status),
                        ("CheckIn", before.CheckIn, row.CheckIn),
                        ("CheckOut", before.CheckOut, row.CheckOut),
                        ("MinutesLate", before.MinutesLate, row.MinutesLate),
                        ("Notes", before.Notes, row.Notes),
                        ("CorrectionReason", (object?)null, row.LastCorrectionReason)));
            }
        }

        await db.SaveChangesAsync(ct);
        return new AttendanceSaveResult(created, corrected, unchanged);
    }

    private sealed record Snapshot(AttendanceStatus Status, TimeSpan? CheckIn, TimeSpan? CheckOut, int? MinutesLate, string? Notes);

    private async Task EnsureNotLockedAsync(Employee employee, DateTime day, CancellationToken ct)
    {
        var lockedThrough = await PayrollLocks.LatestFinalizedMonthEndAsync(db, employee.Id, ct);
        if (lockedThrough.HasValue && day <= lockedThrough.Value)
            throw new BusinessRuleException("HR_ATTENDANCE_PAYROLL_LOCKED",
                $"معاشِ {employee.FullName} تا {lockedThrough.Value:yyyy-MM-dd} نهایی شده؛ حاضریِ این روز قفل است.");
    }
}
