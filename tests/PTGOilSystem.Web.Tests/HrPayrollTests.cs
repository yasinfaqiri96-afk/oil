using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.HumanResources;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>مدیریت بشری، فاز ۷ تا ۹ — معاشِ ماهانه، پرداخت، مساعده و قرضه.</summary>
public class HrPayrollTests
{
    // جون ۲۰۲۶: ۳۰ روز.
    private const int Year = 2026;
    private const int June = 6;

    // ---------- محاسبه ----------

    [Fact]
    public async Task Monthly_Salary_Is_Prorated_From_Salary_History_Segments()
    {
        await using var db = await SeedAsync();
        await Compensation(db).ChangeAsync(new(1, new DateTime(2026, 1, 1), 3000m, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null));
        await Compensation(db).ChangeAsync(new(1, new DateTime(2026, 6, 16), 6000m, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null));

        var run = (await Payroll(db).GenerateAsync(Year, June)).Run;

        var line = run.Lines.Single();
        // ۱۵ روز × ۳۰۰۰ + ۱۵ روز × ۶۰۰۰ از ۳۰ روز
        Assert.Equal(4500m, line.BaseSalary);
        Assert.Equal(6000m, line.MonthlySalary);
        Assert.Equal(30m, line.EmployedDays);
        Assert.Equal(4500m, line.NetSalary);
    }

    [Fact]
    public async Task Mid_Month_Hire_Is_Prorated()
    {
        await using var db = await SeedAsync(hireDate: new DateTime(2026, 6, 21));
        await Compensation(db).ChangeAsync(new(1, new DateTime(2026, 6, 21), 3000m, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null));

        var line = (await Payroll(db).GenerateAsync(Year, June)).Run.Lines.Single();

        Assert.Equal(10m, line.EmployedDays);
        Assert.Equal(1000m, line.BaseSalary);
    }

    [Fact]
    public async Task Unpaid_Leave_Is_Deducted_But_Absence_Only_When_Enabled()
    {
        await using var db = await SeedWithSalaryAsync(3000m);
        db.LeaveTypes.Add(new LeaveType { Id = 3, Name = "Unpaid", IsPaid = false, IsActive = true });
        await db.SaveChangesAsync();
        // ۱۵ و ۱۶ جون: دوشنبه و سه‌شنبه
        var leave = await HrAttendanceAndLeaveTests.Leave(db).SubmitAsync(new(1, 3, new DateTime(2026, 6, 15), new DateTime(2026, 6, 16), false, null, null));
        await HrAttendanceAndLeaveTests.Leave(db).ApproveAsync(leave.Id, 1);
        await HrAttendanceAndLeaveTests.Attendance(db).SaveDayAsync(new DateTime(2026, 6, 17), [new(1, AttendanceStatus.Absent, null, null, null)], null);

        var line = (await Payroll(db).GenerateAsync(Year, June)).Run.Lines.Single();
        Assert.Equal(2m, line.UnpaidLeaveDays);
        Assert.Equal(200m, line.UnpaidLeaveDeduction); // 3000 ÷ 30 × 2
        Assert.Equal(1m, line.AbsentDays);
        Assert.Equal(0m, line.AbsenceDeduction);       // «کسر غیبت» خاموش
        Assert.Equal(2800m, line.NetSalary);

        db.HrSettings.Add(new HrSettings { DeductAbsences = true, DeductLateMinutes = true });
        await db.SaveChangesAsync();
        var recalculated = (await Payroll(db).GenerateAsync(Year, June)).Run.Lines.Single();
        Assert.Equal(100m, recalculated.AbsenceDeduction);
        Assert.Equal(2700m, recalculated.NetSalary);
    }

    [Fact]
    public async Task Late_Minutes_Are_Deducted_When_Enabled()
    {
        await using var db = await SeedWithSalaryAsync(4800m);
        db.HrSettings.Add(new HrSettings { DeductLateMinutes = true, PayrollDaysPerMonth = 30, WorkingHoursPerDay = 8m });
        await db.SaveChangesAsync();
        await HrAttendanceAndLeaveTests.Attendance(db).SaveDayAsync(new DateTime(2026, 6, 15), [new(1, AttendanceStatus.Present, new TimeSpan(9, 0, 0), null, null)], null);

        var line = (await Payroll(db).GenerateAsync(Year, June)).Run.Lines.Single();

        Assert.Equal(60, line.LateMinutes);
        Assert.Equal(20m, line.LateDeduction); // 4800÷30 = 160/روز ÷ 480 دقیقه × 60
    }

    [Fact]
    public async Task Due_Advance_And_Loan_Installment_Are_Deducted_Up_To_Earned_Salary()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var salary = HrSalaryIntegrityTests.BuildService(db);
        // مساعدهٔ جون (خودکار از معاشِ همان ماه) و مساعدهٔ جولای (هنوز موعد نیست)
        await salary.CreateAsync(new(1, new DateTime(2026, 6, 5), EmployeeSalaryTransactionType.SalaryAdvance, 150m, "USD", null, 1, null, null, null, null));
        await salary.CreateAsync(new(1, new DateTime(2026, 6, 6), EmployeeSalaryTransactionType.SalaryAdvance, 80m, "USD", null, 1, null, null, null, null,
            RecoveryYear: 2026, RecoveryMonth: 7));
        await Loans(db, salary).CreateAsync(new(1, new DateTime(2026, 5, 20), 300m, "USD", 3, 2026, 6, 1, null));

        var line = (await Payroll(db, salary).GenerateAsync(Year, June)).Run.Lines.Single();

        Assert.Equal(150m, line.AdvanceDeduction);
        Assert.Equal(100m, line.LoanDeduction);
        Assert.Equal(750m, line.NetSalary);
        Assert.Equal(1000m, line.EarnedSalary);
    }

    // ---------- نهایی‌سازی و حسابداری ----------

    [Fact]
    public async Task Finalize_Posts_Expense_Once_And_Payable_Net_Of_Recoveries()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var settings = await db.AccountingSettings.SingleAsync();
        var salary = HrSalaryIntegrityTests.BuildService(db);
        await salary.CreateAsync(new(1, new DateTime(2026, 6, 5), EmployeeSalaryTransactionType.SalaryAdvance, 150m, "USD", null, 1, null, null, null, null));
        var payroll = Payroll(db, salary);
        var run = (await payroll.GenerateAsync(Year, June)).Run;

        await payroll.FinalizeAsync(run.Id, 1);

        Assert.Equal(PayrollRunStatus.Finalized, (await db.PayrollRuns.SingleAsync()).Status);
        var transactions = await db.EmployeeSalaryTransactions.Where(t => t.PayrollRunLineId != null).ToListAsync();
        Assert.Contains(transactions, t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual && t.Amount == 1000m && t.SalaryPeriodMonth == June);
        Assert.Contains(transactions, t => t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery && t.Amount == 150m);

        Assert.Equal(1000m, await Net(db, settings.SalaryExpenseAccountId!.Value));
        Assert.Equal(-850m, await Net(db, settings.EmployeePayableAccountId));     // بستانکار = بدهی ۸۵۰
        Assert.Equal(0m, await Net(db, settings.EmployeeAdvanceAccountId));         // مساعده وصول شد

        // سطرِ نهایی قفل است و ساختِ دوباره رد می‌شود.
        var lineId = run.Lines.Single().Id;
        Assert.Equal("HR_PAYROLL_FINALIZED", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => payroll.UpdateLineAsync(lineId, new(10m, 0, 0, 0, 0, null)))).Code);
        Assert.Equal("HR_PAYROLL_FINALIZED", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => payroll.GenerateAsync(Year, June))).Code);

        // معاشِ دستیِ همان ماه هم دیگر ثبت نمی‌شود (دوباره‌شماری).
        Assert.Equal("EMPLOYEE_SALARY_ACCRUAL_DUPLICATE", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => salary.CreateAsync(new(1, new DateTime(2026, 6, 30), EmployeeSalaryTransactionType.SalaryAccrual, 1000m, "USD", null, null, null, null, Year, June)))).Code);
    }

    [Fact]
    public async Task Finalize_Refuses_When_Inputs_Changed_After_Review()
    {
        await using var db = await SeedWithSalaryAsync(3000m);
        db.HrSettings.Add(new HrSettings { DeductAbsences = true });
        await db.SaveChangesAsync();
        var payroll = Payroll(db);
        var run = (await payroll.GenerateAsync(Year, June)).Run;
        await HrAttendanceAndLeaveTests.Attendance(db).SaveDayAsync(new DateTime(2026, 6, 17), [new(1, AttendanceStatus.Absent, null, null, null)], null);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => payroll.FinalizeAsync(run.Id, 1));

        Assert.Equal("HR_PAYROLL_STALE", ex.Code);
        Assert.Equal(PayrollRunStatus.Draft, (await db.PayrollRuns.SingleAsync()).Status);
        Assert.Equal(2900m, (await db.PayrollRunLines.SingleAsync()).NetSalary);
        Assert.Empty(await db.JournalEntries.Where(j => j.SourceModule == "EmployeeSalary").ToListAsync());
    }

    [Fact]
    public async Task Manual_Other_Deduction_Reduces_Expense_And_Negative_Net_Is_Rejected()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var payroll = Payroll(db);
        var run = (await payroll.GenerateAsync(Year, June)).Run;
        var lineId = run.Lines.Single().Id;

        await payroll.UpdateLineAsync(lineId, new(50m, 100m, 20m, 0m, 70m, "Penalty"));
        var line = await db.PayrollRunLines.SingleAsync();
        Assert.Equal(1170m, line.GrossSalary);
        Assert.Equal(1100m, line.NetSalary);
        Assert.Equal(1100m, line.EarnedSalary);

        Assert.Equal("HR_PAYROLL_NET_NEGATIVE", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => payroll.UpdateLineAsync(lineId, new(0, 0, 0, 0, 5000m, null)))).Code);
    }

    // ---------- پرداخت ----------

    [Fact]
    public async Task Payment_Is_Separate_From_Finalization_Allows_Partial_And_Blocks_Overpay()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var settings = await db.AccountingSettings.SingleAsync();
        var payroll = Payroll(db);
        var run = (await payroll.GenerateAsync(Year, June)).Run;
        await payroll.FinalizeAsync(run.Id, 1);
        var lineId = run.Lines.Single().Id;

        // نهایی‌شدن پول خارج نکرده است.
        Assert.Empty(await new CashPositionReader(db).ReadAccountTotalsAsync());

        await payroll.PayLineAsync(lineId, 400m, 1, new DateTime(2026, 7, 2), null);
        Assert.Equal(400m, (await payroll.GetPaidAmountsAsync(run.Id))[lineId]);
        Assert.Equal("HR_PAYROLL_OVERPAY", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => payroll.PayLineAsync(lineId, 600.01m, 1, new DateTime(2026, 7, 3), null))).Code);

        await payroll.PayLineAsync(lineId, 600m, 1, new DateTime(2026, 7, 3), null);
        Assert.Equal(1000m, (await payroll.GetPaidAmountsAsync(run.Id))[lineId]);
        Assert.Equal(-1000m, Assert.Single(await new CashPositionReader(db).ReadAccountTotalsAsync()).BalanceUsd);

        // مصرف فقط ۱۰۰۰؛ بدهی صفر؛ نقد −۱۰۰۰.
        Assert.Equal(1000m, await Net(db, settings.SalaryExpenseAccountId!.Value));
        Assert.Equal(0m, await Net(db, settings.EmployeePayableAccountId));
        Assert.Equal(-1000m, await Net(db, settings.CashBankControlAccountId));
    }

    [Fact]
    public async Task Draft_Payroll_Cannot_Be_Paid()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var payroll = Payroll(db);
        var run = (await payroll.GenerateAsync(Year, June)).Run;

        Assert.Equal("HR_PAYROLL_NOT_FINALIZED", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => payroll.PayLineAsync(run.Lines.Single().Id, 100m, 1, new DateTime(2026, 7, 1), null))).Code);
    }

    // ---------- بازگشایی ----------

    [Fact]
    public async Task Reopen_Requires_Reason_Blocks_Paid_Payroll_And_Reverses_Postings()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var settings = await db.AccountingSettings.SingleAsync();
        var salary = HrSalaryIntegrityTests.BuildService(db);
        var payroll = Payroll(db, salary);
        var run = (await payroll.GenerateAsync(Year, June)).Run;
        await payroll.FinalizeAsync(run.Id, 1);
        var payment = await payroll.PayLineAsync(run.Lines.Single().Id, 300m, 1, new DateTime(2026, 7, 2), null);

        Assert.Equal("HR_PAYROLL_REOPEN_REASON", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => payroll.ReopenAsync(run.Id, " "))).Code);
        Assert.Equal("HR_PAYROLL_HAS_PAYMENTS", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => payroll.ReopenAsync(run.Id, "Wrong bonus"))).Code);

        await salary.CancelAsync(payment.Id, "Paid by mistake");
        await payroll.ReopenAsync(run.Id, "Wrong bonus");

        var reopened = await db.PayrollRuns.SingleAsync();
        Assert.Equal(PayrollRunStatus.Draft, reopened.Status);
        Assert.Equal(1, reopened.Revision);
        Assert.Equal("Wrong bonus", reopened.LastReopenReason);
        Assert.All(await db.EmployeeSalaryTransactions.Where(t => t.PayrollRunLineId != null).ToListAsync(), t => Assert.True(t.IsCancelled));
        Assert.Equal(0m, await Net(db, settings.SalaryExpenseAccountId!.Value));
        Assert.Equal(0m, await Net(db, settings.EmployeePayableAccountId));
        Assert.Equal(0m, await Net(db, settings.CashBankControlAccountId));

        // بعد از اصلاح دوباره نهایی می‌شود و «ثبت معاش»ِ تازه جای قبلی را می‌گیرد.
        await payroll.UpdateLineAsync(run.Lines.Single().Id, new(0, 50m, 0, 0, 0, null));
        await payroll.FinalizeAsync(run.Id, 1);
        Assert.Equal(1050m, await Net(db, settings.SalaryExpenseAccountId!.Value));
    }

    [Fact]
    public async Task Finalized_Month_Locks_Attendance_And_Salary_Changes()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var payroll = Payroll(db);
        var run = (await payroll.GenerateAsync(Year, June)).Run;
        await payroll.FinalizeAsync(run.Id, 1);

        Assert.Equal("HR_ATTENDANCE_PAYROLL_LOCKED", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => HrAttendanceAndLeaveTests.Attendance(db).SaveDayAsync(new DateTime(2026, 6, 10), [new(1, AttendanceStatus.Present, null, null, null)], null))).Code);
        Assert.Equal("HR_SALARY_PERIOD_FINALIZED", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => Compensation(db).ChangeAsync(new(1, new DateTime(2026, 6, 20), 1200m, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null)))).Code);
    }

    [Fact]
    public async Task Employee_With_Manual_Accrual_For_Month_Is_Skipped()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        await HrSalaryIntegrityTests.BuildService(db).CreateAsync(new(1, new DateTime(2026, 6, 30), EmployeeSalaryTransactionType.SalaryAccrual, 1000m, "USD", null, null, null, null, Year, June));

        var result = await Payroll(db).GenerateAsync(Year, June);

        Assert.Empty(result.Run.Lines);
        Assert.Single(result.SkippedEmployees);
    }

    // ---------- قرضه ----------

    [Fact]
    public async Task Loan_Disbursement_Repayment_And_Status_Are_Tracked_In_Receivable()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var settings = await db.AccountingSettings.SingleAsync();
        var salary = HrSalaryIntegrityTests.BuildService(db);
        var loans = Loans(db, salary);

        var loan = await loans.CreateAsync(new(1, new DateTime(2026, 5, 20), 100m, "USD", 3, 2026, 6, 1, null));
        Assert.Equal(33.34m, loan.InstallmentAmount);
        Assert.Equal(100m, await Net(db, settings.EmployeeAdvanceAccountId));

        Assert.Equal("EMPLOYEE_LOAN_EXCEEDS_OUTSTANDING", (await Assert.ThrowsAsync<BusinessRuleException>(
            () => loans.RepayAsync(loan.Id, 100.01m, 1, new DateTime(2026, 5, 25), null))).Code);
        await loans.RepayAsync(loan.Id, 100m, 1, new DateTime(2026, 5, 25), null);

        Assert.Equal(EmployeeLoanStatus.Paid, (await db.EmployeeLoans.SingleAsync()).Status);
        Assert.Equal(0m, await Net(db, settings.EmployeeAdvanceAccountId));
        Assert.Equal(0m, await Net(db, settings.CashBankControlAccountId));

        // قرضهٔ تسویه‌شده در معاش کسر نمی‌شود.
        Assert.Equal(0m, (await Payroll(db, salary).GenerateAsync(Year, June)).Run.Lines.Single().LoanDeduction);
    }

    [Fact]
    public async Task Loan_Without_Installments_Can_Be_Cancelled_And_Payout_Reversed()
    {
        await using var db = await SeedWithSalaryAsync(1000m);
        var settings = await db.AccountingSettings.SingleAsync();
        var salary = HrSalaryIntegrityTests.BuildService(db);
        var loans = Loans(db, salary);
        var loan = await loans.CreateAsync(new(1, new DateTime(2026, 5, 20), 500m, "USD", 5, 2026, 6, 1, null));

        await loans.CancelAsync(loan.Id, "Entered twice");

        Assert.Equal(EmployeeLoanStatus.Cancelled, (await db.EmployeeLoans.SingleAsync()).Status);
        Assert.Equal(0m, await Net(db, settings.EmployeeAdvanceAccountId));
        Assert.Equal(0m, await Net(db, settings.CashBankControlAccountId));
        Assert.Equal(0m, Assert.Single(await new CashPositionReader(db).ReadAccountTotalsAsync()).BalanceUsd);
    }

    // ---------- helpers ----------

    private static async Task<decimal> Net(ApplicationDbContext db, int accountId)
        => await db.JournalEntryLines.Where(l => l.AccountId == accountId).SumAsync(l => l.Debit - l.Credit);

    private static async Task<ApplicationDbContext> SeedAsync(DateTime? hireDate = null)
    {
        var db = await HrSalaryIntegrityTests.NewAccountingDbAsync();
        var employee = await db.Employees.SingleAsync();
        employee.HireDate = hireDate ?? new DateTime(2026, 1, 1);
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<ApplicationDbContext> SeedWithSalaryAsync(decimal monthly)
    {
        var db = await SeedAsync();
        await Compensation(db).ChangeAsync(new(1, new DateTime(2026, 1, 1), monthly, "USD", EmployeeSalaryType.Monthly, CompensationSource.Manual, null, null));
        return db;
    }

    private static EmployeeCompensationService Compensation(ApplicationDbContext db) => new(db, new AuditService(db));

    private static PayrollService Payroll(ApplicationDbContext db, EmployeeSalaryService? salary = null)
        => new(db, salary ?? HrSalaryIntegrityTests.BuildService(db), new HrCalendarService(db), new AuditService(db));

    private static EmployeeLoanService Loans(ApplicationDbContext db, EmployeeSalaryService salary)
        => new(db, salary, new AuditService(db));
}
