using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.HumanResources;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>مدیریت بشری، فاز ۴ و ۵ — حاضری و رخصتی.</summary>
public class HrAttendanceAndLeaveTests
{
    // 2026-09-07 دوشنبه است؛ 2026-09-11 جمعه (رخصتیِ هفته).
    private static readonly DateTime Monday = new(2026, 9, 7);

    // ---------- حاضری ----------

    [Fact]
    public async Task Attendance_Records_Day_And_Derives_Lateness_From_Check_In()
    {
        await using var db = await SeedAsync();
        var result = await Attendance(db).SaveDayAsync(Monday,
        [
            new(1, AttendanceStatus.Present, new TimeSpan(8, 25, 0), new TimeSpan(16, 0, 0), null),
            new(2, AttendanceStatus.Present, new TimeSpan(8, 5, 0), null, null),
            new(3, null, null, null, null)
        ], null);

        Assert.Equal(2, result.Created);
        var rows = await db.DailyAttendances.OrderBy(a => a.EmployeeId).ToListAsync();
        Assert.Equal(25, rows[0].MinutesLate);
        Assert.Equal(0, rows[1].MinutesLate); // داخلِ مهلتِ ۱۰ دقیقه
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Changing_Recorded_Attendance_Requires_Reason_And_Is_Audited()
    {
        await using var db = await SeedAsync();
        var service = Attendance(db);
        await service.SaveDayAsync(Monday, [new(1, AttendanceStatus.Present, new TimeSpan(8, 0, 0), null, null)], null);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.SaveDayAsync(Monday, [new(1, AttendanceStatus.Absent, null, null, null)], null));
        Assert.Equal("HR_ATTENDANCE_CORRECTION_REASON", ex.Code);

        var result = await Attendance(db).SaveDayAsync(Monday, [new(1, AttendanceStatus.Absent, null, null, null)], "Was on field trip, not in office");
        Assert.Equal(1, result.Corrected);
        var row = await db.DailyAttendances.AsNoTracking().SingleAsync();
        Assert.Equal(AttendanceStatus.Absent, row.Status);
        Assert.Null(row.CheckIn);
        Assert.Equal("Was on field trip, not in office", row.LastCorrectionReason);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.EntityName == nameof(DailyAttendance) && a.Action == "Update");
    }

    [Fact]
    public async Task Manual_Leave_Status_Is_Rejected()
    {
        await using var db = await SeedAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Attendance(db).SaveDayAsync(Monday, [new(1, AttendanceStatus.Leave, null, null, null)], null));
        Assert.Equal("HR_ATTENDANCE_LEAVE_MANUAL", ex.Code);
    }

    [Fact]
    public async Task Attendance_In_Finalized_Salary_Month_Is_Locked()
    {
        await using var db = await SeedAsync();
        await HrSalaryIntegrityTests.BuildService(db, pilot: false).CreateAsync(new EmployeeSalaryTransactionCommand(
            1, new DateTime(2026, 9, 30), EmployeeSalaryTransactionType.SalaryAccrual, 1000m, "USD", null, null, null, null, 2026, 9));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Attendance(db).SaveDayAsync(Monday, [new(1, AttendanceStatus.Present, null, null, null)], null));
        Assert.Equal("HR_ATTENDANCE_PAYROLL_LOCKED", ex.Code);
    }

    // ---------- رخصتی ----------

    [Fact]
    public async Task Leave_Days_Skip_Weekly_Off_And_Holidays()
    {
        await using var db = await SeedAsync();
        db.HrHolidays.Add(new HrHoliday { Date = Monday.AddDays(1), Name = "Holiday" });
        await db.SaveChangesAsync();

        // دوشنبه تا یکشنبهٔ بعد = ۷ روز، منهای جمعه و رخصتیِ رسمی = ۵
        Assert.Equal(5m, await Leave(db).CountLeaveDaysAsync(Monday, Monday.AddDays(6), false));
        Assert.Equal(0.5m, await Leave(db).CountLeaveDaysAsync(Monday, Monday, true));
    }

    [Fact]
    public async Task Approving_Leave_Writes_Attendance_And_Cancel_Removes_It()
    {
        await using var db = await SeedAsync();
        await Attendance(db).SaveDayAsync(Monday, [new(1, AttendanceStatus.Present, new TimeSpan(8, 0, 0), null, null)], null);
        var request = await Leave(db).SubmitAsync(new(1, AnnualId, Monday, Monday.AddDays(4), false, "Family", null));
        Assert.Equal(4m, request.TotalDays); // دوشنبه تا جمعه، بدونِ جمعه

        await Leave(db).ApproveAsync(request.Id, approverUserId: 9);

        var rows = await db.DailyAttendances.AsNoTracking().Where(a => a.EmployeeId == 1).OrderBy(a => a.Date).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(AttendanceStatus.Leave, r.Status));
        Assert.All(rows, r => Assert.Equal(request.Id, r.LeaveRequestId));
        Assert.Null(rows[0].CheckIn); // سطرِ حاضرِ قبلی به رخصتی تبدیل شد

        // حاضریِ روزِ رخصتی دستی عوض نمی‌شود.
        var locked = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Attendance(db).SaveDayAsync(Monday, [new(1, AttendanceStatus.Present, null, null, null)], "x"));
        Assert.Equal("HR_ATTENDANCE_LEAVE_LOCKED", locked.Code);

        await Leave(db).CancelAsync(request.Id, "Trip cancelled");
        Assert.Empty(await db.DailyAttendances.Where(a => a.EmployeeId == 1).ToListAsync());
        Assert.Equal(LeaveRequestStatus.Cancelled, (await db.LeaveRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Overlapping_Leave_Is_Rejected()
    {
        await using var db = await SeedAsync();
        await Leave(db).SubmitAsync(new(1, AnnualId, Monday, Monday.AddDays(2), false, null, null));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Leave(db).SubmitAsync(new(1, SickId, Monday.AddDays(2), Monday.AddDays(3), false, null, null)));
        Assert.Equal("HR_LEAVE_OVERLAP", ex.Code);

        // کارمندِ دیگر آزاد است.
        await Leave(db).SubmitAsync(new(2, AnnualId, Monday, Monday.AddDays(2), false, null, null));
    }

    [Fact]
    public async Task Leave_Balance_Is_Enforced_When_Allowance_Is_Set()
    {
        await using var db = await SeedAsync();
        var annual = await db.LeaveTypes.SingleAsync(t => t.Id == AnnualId);
        annual.AnnualAllowanceDays = 3m;
        await db.SaveChangesAsync();

        await Leave(db).SubmitAsync(new(1, AnnualId, Monday, Monday.AddDays(1), false, null, null));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Leave(db).SubmitAsync(new(1, AnnualId, Monday.AddDays(7), Monday.AddDays(8), false, null, null)));
        Assert.Equal("HR_LEAVE_BALANCE", ex.Code);

        var balance = (await Leave(db).GetBalancesAsync(1, 2026)).Single(b => b.LeaveTypeId == AnnualId);
        Assert.Equal(2m, balance.PendingDays);
    }

    [Fact]
    public async Task Invalid_Leave_Requests_Are_Rejected()
    {
        await using var db = await SeedAsync();
        var service = Leave(db);

        Assert.Equal("HR_LEAVE_DATES", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.SubmitAsync(new(1, AnnualId, Monday, Monday.AddDays(-1), false, null, null)))).Code);
        Assert.Equal("HR_LEAVE_HALF_DAY", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.SubmitAsync(new(1, AnnualId, Monday, Monday.AddDays(1), true, null, null)))).Code);
        Assert.Equal("HR_LEAVE_NO_WORKING_DAYS", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.SubmitAsync(new(1, AnnualId, Monday.AddDays(4), Monday.AddDays(4), false, null, null)))).Code);
        Assert.Equal("EMPLOYEE_INACTIVE", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.SubmitAsync(new(4, AnnualId, Monday, Monday, false, null, null)))).Code);
    }

    [Fact]
    public async Task Reject_Requires_Reason()
    {
        await using var db = await SeedAsync();
        var request = await Leave(db).SubmitAsync(new(1, AnnualId, Monday, Monday, false, null, null));

        Assert.Equal("HR_LEAVE_REJECT_REASON", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => Leave(db).RejectAsync(request.Id, 9, " "))).Code);

        await Leave(db).RejectAsync(request.Id, 9, "Busy season");
        var saved = await db.LeaveRequests.SingleAsync();
        Assert.Equal(LeaveRequestStatus.Rejected, saved.Status);
        Assert.Equal(9, saved.DecidedByUserId);
        Assert.Empty(await db.DailyAttendances.ToListAsync());
    }

    // ---------- helpers ----------

    internal const int AnnualId = 1;
    internal const int SickId = 2;
    internal const int UnpaidId = 3;

    internal static async Task<ApplicationDbContext> SeedAsync()
    {
        var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        db.Employees.Add(new Employee { Id = 2, EmployeeCode = "EMP-2", FullName = "Second", SalaryCurrency = "USD", BaseSalaryAmount = 600m, HireDate = new DateTime(2026, 1, 1), IsActive = true });
        db.Employees.Add(new Employee { Id = 3, EmployeeCode = "EMP-3", FullName = "Third", SalaryCurrency = "USD", HireDate = new DateTime(2026, 1, 1), IsActive = true });
        db.Employees.Add(new Employee { Id = 4, EmployeeCode = "EMP-4", FullName = "Inactive", SalaryCurrency = "USD", HireDate = new DateTime(2026, 1, 1), IsActive = false });
        db.LeaveTypes.Add(new LeaveType { Id = AnnualId, Name = "Annual", IsPaid = true, IsActive = true });
        db.LeaveTypes.Add(new LeaveType { Id = SickId, Name = "Sick", IsPaid = true, IsActive = true });
        db.LeaveTypes.Add(new LeaveType { Id = UnpaidId, Name = "Unpaid", IsPaid = false, IsActive = true });
        await db.SaveChangesAsync();
        return db;
    }

    internal static AttendanceService Attendance(ApplicationDbContext db)
        => new(db, new HrCalendarService(db), new AuditService(db));

    internal static LeaveService Leave(ApplicationDbContext db)
        => new(db, new HrCalendarService(db), new HrFileStorage(db, new HrContractsAndCompensationTests.TestEnvironment()), new AuditService(db));
}
