using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.HumanResources;

public sealed record LeaveRequestInput(
    int EmployeeId,
    int LeaveTypeId,
    DateTime FromDate,
    DateTime ToDate,
    bool IsHalfDay,
    string? Reason,
    IFormFile? Attachment);

public sealed record LeaveBalance(int LeaveTypeId, string LeaveTypeName, decimal? AllowanceDays, decimal UsedDays, decimal PendingDays)
{
    public decimal? RemainingDays => AllowanceDays.HasValue ? AllowanceDays.Value - UsedDays : null;
}

public interface ILeaveService
{
    Task<LeaveRequest> SubmitAsync(LeaveRequestInput input, CancellationToken ct = default);
    Task ApproveAsync(int leaveRequestId, int? approverUserId, CancellationToken ct = default);
    Task RejectAsync(int leaveRequestId, int? approverUserId, string reason, CancellationToken ct = default);
    Task CancelAsync(int leaveRequestId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<LeaveBalance>> GetBalancesAsync(int employeeId, int year, CancellationToken ct = default);
    Task<decimal> CountLeaveDaysAsync(DateTime from, DateTime to, bool isHalfDay, CancellationToken ct = default);
}

/// <summary>
/// رخصتی با یک مرحله تأیید. قواعد: تاریخِ درست، کارمندِ فعال، بدونِ هم‌پوشانی با رخصتیِ
/// در انتظار/تأییدشده، و اگر نوعِ رخصتی سهمیه دارد، مانده کافی (در ثبت و دوباره در تأیید).
/// تأیید، روزهای کاریِ بازه را در حاضری «رخصتی» می‌کند؛ لغوِ رخصتیِ تأییدشده همان سطرها را برمی‌دارد
/// (با Audit). در ماهی که معاشش نهایی شده، تأیید یا لغو ممکن نیست.
/// </summary>
public sealed class LeaveService(
    ApplicationDbContext db,
    IHrCalendarService calendar,
    IHrFileStorage files,
    IAuditService audit) : ILeaveService
{
    public async Task<decimal> CountLeaveDaysAsync(DateTime from, DateTime to, bool isHalfDay, CancellationToken ct = default)
    {
        var days = await calendar.WorkingDaysAsync(from, to, ct);
        if (isHalfDay)
            return days.Count == 1 ? 0.5m : 0m;
        return days.Count;
    }

    public async Task<LeaveRequest> SubmitAsync(LeaveRequestInput input, CancellationToken ct = default)
    {
        var from = input.FromDate.Date;
        var to = input.ToDate.Date;
        if (to < from)
            throw new BusinessRuleException("HR_LEAVE_DATES", "تاریخِ پایانِ رخصتی پیش از شروع است.");
        if (input.IsHalfDay && from != to)
            throw new BusinessRuleException("HR_LEAVE_HALF_DAY", "نیم‌روز فقط برای یک روز ثبت می‌شود.");
        if ((to - from).TotalDays > 366)
            throw new BusinessRuleException("HR_LEAVE_TOO_LONG", "بازهٔ رخصتی بیش از یک سال است.");

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == input.EmployeeId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");
        if (!employee.IsActive)
            throw new BusinessRuleException("EMPLOYEE_INACTIVE", "برای کارمندِ غیرفعال رخصتی ثبت نمی‌شود.");

        var type = await db.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.LeaveTypeId && t.IsActive, ct)
            ?? throw new BusinessRuleException("HR_LEAVE_TYPE_INVALID", "نوعِ رخصتی معتبر نیست.");

        await EnsureNoOverlapAsync(input.EmployeeId, from, to, excludeId: null, ct);

        var totalDays = await CountLeaveDaysAsync(from, to, input.IsHalfDay, ct);
        if (totalDays <= 0m)
            throw new BusinessRuleException("HR_LEAVE_NO_WORKING_DAYS", "در این بازه روزِ کاری نیست (همه رخصتیِ هفته یا رسمی است).");

        await EnsureBalanceAsync(employee.Id, type, from, totalDays, excludeId: null, includePending: true, ct);

        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        var request = new LeaveRequest
        {
            EmployeeId = employee.Id,
            LeaveTypeId = type.Id,
            FromDate = from,
            ToDate = to,
            IsHalfDay = input.IsHalfDay,
            TotalDays = totalDays,
            Reason = reason?.Length > 1000 ? reason[..1000] : reason,
            Status = LeaveRequestStatus.Pending
        };
        if (input.Attachment is { Length: > 0 })
        {
            request.Attachment = await files.SaveAsync(employee.Id, HrAttachmentOwner.LeaveRequest, input.Attachment, ct);
        }

        db.LeaveRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await audit.LogAndSaveAsync(nameof(LeaveRequest), request.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("EmployeeId", request.EmployeeId),
                ("LeaveTypeId", request.LeaveTypeId),
                ("FromDate", request.FromDate),
                ("ToDate", request.ToDate),
                ("IsHalfDay", request.IsHalfDay),
                ("TotalDays", request.TotalDays)));
        return request;
    }

    public async Task ApproveAsync(int leaveRequestId, int? approverUserId, CancellationToken ct = default)
    {
        var request = await db.LeaveRequests.Include(r => r.LeaveType).Include(r => r.Employee)
            .FirstOrDefaultAsync(r => r.Id == leaveRequestId, ct)
            ?? throw new BusinessRuleException("HR_LEAVE_NOT_FOUND", "درخواستِ رخصتی پیدا نشد.");
        if (request.Status != LeaveRequestStatus.Pending)
            throw new BusinessRuleException("HR_LEAVE_NOT_PENDING", "فقط درخواستِ در انتظار تأیید می‌شود.");

        await EnsureNoOverlapAsync(request.EmployeeId, request.FromDate, request.ToDate, request.Id, ct);
        await EnsureBalanceAsync(request.EmployeeId, request.LeaveType!, request.FromDate, request.TotalDays, request.Id, includePending: false, ct);
        await EnsureNotLockedAsync(request.Employee!, request.FromDate, ct);

        await InTransactionAsync(async () =>
        {
            request.Status = LeaveRequestStatus.Approved;
            request.DecidedByUserId = approverUserId;
            request.DecidedAtUtc = DateTime.UtcNow;

            var days = await calendar.WorkingDaysAsync(request.FromDate, request.ToDate, ct);
            var existing = await db.DailyAttendances
                .Where(a => a.EmployeeId == request.EmployeeId && a.Date >= request.FromDate && a.Date <= request.ToDate)
                .ToDictionaryAsync(a => a.Date, ct);
            var note = request.IsHalfDay ? "رخصتیِ نیم‌روز" : null;
            foreach (var day in days)
            {
                if (existing.TryGetValue(day, out var row))
                {
                    var before = row.Status;
                    row.Status = AttendanceStatus.Leave;
                    row.LeaveRequestId = request.Id;
                    row.CheckIn = null;
                    row.CheckOut = null;
                    row.MinutesLate = null;
                    row.Notes = note ?? row.Notes;
                    row.LastCorrectionReason = $"رخصتیِ تأییدشده #{request.Id}";
                    await db.SaveChangesAsync(ct);
                    await audit.LogAsync(nameof(DailyAttendance), row.Id, AuditAction.Update,
                        diff: AuditDiffFormatter.ForUpdate(
                            ("Status", before, row.Status),
                            ("LeaveRequestId", (int?)null, row.LeaveRequestId)));
                }
                else
                {
                    db.DailyAttendances.Add(new DailyAttendance
                    {
                        EmployeeId = request.EmployeeId,
                        Date = day,
                        Status = AttendanceStatus.Leave,
                        LeaveRequestId = request.Id,
                        Notes = note
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(LeaveRequest), request.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", LeaveRequestStatus.Pending, request.Status),
                    ("DecidedByUserId", (int?)null, request.DecidedByUserId)));
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task RejectAsync(int leaveRequestId, int? approverUserId, string reason, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
            throw new BusinessRuleException("HR_LEAVE_REJECT_REASON", "دلیلِ رد الزامی است.");

        var request = await db.LeaveRequests.FirstOrDefaultAsync(r => r.Id == leaveRequestId, ct)
            ?? throw new BusinessRuleException("HR_LEAVE_NOT_FOUND", "درخواستِ رخصتی پیدا نشد.");
        if (request.Status != LeaveRequestStatus.Pending)
            throw new BusinessRuleException("HR_LEAVE_NOT_PENDING", "فقط درخواستِ در انتظار رد می‌شود.");

        request.Status = LeaveRequestStatus.Rejected;
        request.DecidedByUserId = approverUserId;
        request.DecidedAtUtc = DateTime.UtcNow;
        request.RejectionReason = text.Length > 1000 ? text[..1000] : text;
        await db.SaveChangesAsync(ct);
        await audit.LogAndSaveAsync(nameof(LeaveRequest), request.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("Status", LeaveRequestStatus.Pending, request.Status),
                ("RejectionReason", (string?)null, request.RejectionReason)));
    }

    public async Task CancelAsync(int leaveRequestId, string reason, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
            throw new BusinessRuleException("HR_LEAVE_CANCEL_REASON", "دلیلِ لغو الزامی است.");

        var request = await db.LeaveRequests.Include(r => r.Employee).FirstOrDefaultAsync(r => r.Id == leaveRequestId, ct)
            ?? throw new BusinessRuleException("HR_LEAVE_NOT_FOUND", "درخواستِ رخصتی پیدا نشد.");
        if (request.Status is not (LeaveRequestStatus.Pending or LeaveRequestStatus.Approved))
            throw new BusinessRuleException("HR_LEAVE_NOT_CANCELLABLE", "این درخواست قابلِ لغو نیست.");

        var wasApproved = request.Status == LeaveRequestStatus.Approved;
        if (wasApproved)
        {
            await EnsureNotLockedAsync(request.Employee!, request.FromDate, ct);
        }

        await InTransactionAsync(async () =>
        {
            var before = request.Status;
            request.Status = LeaveRequestStatus.Cancelled;
            request.CancellationReason = text.Length > 1000 ? text[..1000] : text;

            if (wasApproved)
            {
                // روزهایی که فقط از همین رخصتی آمده بودند برداشته می‌شوند؛ حاضری دوباره ثبت‌شدنی است.
                var rows = await db.DailyAttendances.Where(a => a.LeaveRequestId == request.Id).ToListAsync(ct);
                foreach (var row in rows)
                {
                    await audit.LogAsync(nameof(DailyAttendance), row.Id, AuditAction.Delete,
                        diff: AuditDiffFormatter.ForDelete(
                            ("EmployeeId", row.EmployeeId),
                            ("Date", row.Date),
                            ("Status", row.Status),
                            ("LeaveRequestId", row.LeaveRequestId),
                            ("Reason", $"لغوِ رخصتی #{request.Id}: {text}")));
                }

                db.DailyAttendances.RemoveRange(rows);
            }

            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(LeaveRequest), request.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", before, request.Status),
                    ("CancellationReason", (string?)null, request.CancellationReason)));
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task<IReadOnlyList<LeaveBalance>> GetBalancesAsync(int employeeId, int year, CancellationToken ct = default)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync(ct);
        var requests = await db.LeaveRequests.AsNoTracking()
            .Where(r => r.EmployeeId == employeeId
                && r.FromDate >= start && r.FromDate <= end
                && (r.Status == LeaveRequestStatus.Approved || r.Status == LeaveRequestStatus.Pending))
            .Select(r => new { r.LeaveTypeId, r.Status, r.TotalDays })
            .ToListAsync(ct);

        return types.Select(t => new LeaveBalance(
                t.Id,
                t.Name,
                t.AnnualAllowanceDays,
                requests.Where(r => r.LeaveTypeId == t.Id && r.Status == LeaveRequestStatus.Approved).Sum(r => r.TotalDays),
                requests.Where(r => r.LeaveTypeId == t.Id && r.Status == LeaveRequestStatus.Pending).Sum(r => r.TotalDays)))
            .ToList();
    }

    private async Task EnsureNoOverlapAsync(int employeeId, DateTime from, DateTime to, int? excludeId, CancellationToken ct)
    {
        var overlap = await db.LeaveRequests.AsNoTracking().AnyAsync(r =>
            r.EmployeeId == employeeId
            && (excludeId == null || r.Id != excludeId)
            && (r.Status == LeaveRequestStatus.Pending || r.Status == LeaveRequestStatus.Approved)
            && r.FromDate <= to && r.ToDate >= from, ct);
        if (overlap)
            throw new BusinessRuleException("HR_LEAVE_OVERLAP", "این بازه با رخصتیِ دیگرِ همین کارمند (در انتظار یا تأییدشده) هم‌پوشانی دارد.");
    }

    /// <summary>سهمیه در سالِ تقویمیِ شروعِ رخصتی سنجیده می‌شود.</summary>
    private async Task EnsureBalanceAsync(
        int employeeId,
        LeaveType type,
        DateTime from,
        decimal requestedDays,
        int? excludeId,
        bool includePending,
        CancellationToken ct)
    {
        if (!type.AnnualAllowanceDays.HasValue)
            return;

        var start = new DateTime(from.Year, 1, 1);
        var end = new DateTime(from.Year, 12, 31);
        var used = await db.LeaveRequests.AsNoTracking()
            .Where(r => r.EmployeeId == employeeId
                && r.LeaveTypeId == type.Id
                && (excludeId == null || r.Id != excludeId)
                && r.FromDate >= start && r.FromDate <= end
                && (r.Status == LeaveRequestStatus.Approved || (includePending && r.Status == LeaveRequestStatus.Pending)))
            .SumAsync(r => (decimal?)r.TotalDays, ct) ?? 0m;

        if (used + requestedDays > type.AnnualAllowanceDays.Value)
            throw new BusinessRuleException("HR_LEAVE_BALANCE",
                $"ماندهٔ «{type.Name}» کافی نیست: سهمیه {type.AnnualAllowanceDays.Value:0.#} روز، استفاده/در انتظار {used:0.#} روز، درخواست {requestedDays:0.#} روز.");
    }

    private async Task EnsureNotLockedAsync(Employee employee, DateTime from, CancellationToken ct)
    {
        var lockedThrough = await PayrollLocks.LatestFinalizedMonthEndAsync(db, employee.Id, ct);
        if (lockedThrough.HasValue && from.Date <= lockedThrough.Value)
            throw new BusinessRuleException("HR_LEAVE_PAYROLL_LOCKED",
                $"معاشِ {employee.FullName} تا {lockedThrough.Value:yyyy-MM-dd} نهایی شده؛ رخصتیِ این بازه قفل است.");
    }

    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await work();
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }
}
