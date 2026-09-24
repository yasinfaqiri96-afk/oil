using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// داشبوردِ ساده: شش عدد و چهار هشدار، بدونِ نمودار. اعدادِ مالی فقط با «دیدن معاش».
/// </summary>
[Authorize]
public class HrDashboardController : Controller
{
    private readonly ApplicationDbContext _db;

    public HrDashboardController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var today = AfghanistanBusinessClock.SystemToday;
        var canViewSalary = RoleAccessRules.CanViewEmployeeSalary(User);
        var soon = today.AddDays(EmploymentContractLabels.ExpiringSoonDays);

        var model = new HrDashboardViewModel
        {
            Today = today,
            CanViewSalary = canViewSalary,
            TotalEmployees = await _db.Employees.CountAsync(),
            ActiveEmployees = await _db.Employees.CountAsync(e => e.IsActive),
            PresentToday = await _db.DailyAttendances.CountAsync(a => a.Date == today && a.Status == AttendanceStatus.Present),
            AbsentToday = await _db.DailyAttendances.CountAsync(a => a.Date == today && a.Status == AttendanceStatus.Absent),
            OnLeaveToday = await _db.LeaveRequests.CountAsync(r => r.Status == LeaveRequestStatus.Approved && r.FromDate <= today && r.ToDate >= today),
            NotRecordedToday = await _db.Employees.CountAsync(e => e.IsActive && !_db.DailyAttendances.Any(a => a.EmployeeId == e.Id && a.Date == today)),
            PendingLeaveRequests = await _db.LeaveRequests.CountAsync(r => r.Status == LeaveRequestStatus.Pending),
            ExpiringContracts = await _db.EmploymentContracts.AsNoTracking()
                .Where(c => c.Status == EmploymentContractStatus.Active && c.EndDate != null && c.EndDate >= today && c.EndDate <= soon)
                .OrderBy(c => c.EndDate)
                .Select(c => new HrDashboardAlertRow(c.Employee!.FullName, c.ContractNumber, c.EndDate, c.Id))
                .Take(10)
                .ToListAsync(),
            ExpiringDocuments = await _db.EmployeeDocuments.CountAsync(d => !d.IsDeleted && d.ExpiryDate != null && d.ExpiryDate <= soon)
        };

        if (canViewSalary)
        {
            var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Year == today.Year && r.Month == today.Month);
            model.CurrentPayrollStatus = run?.Status;
            model.CurrentPayrollNet = run is null ? [] : await _db.PayrollRunLines.AsNoTracking()
                .Where(l => l.PayrollRunId == run.Id)
                .GroupBy(l => l.Currency)
                .Select(g => new HrCurrencyTotal(g.Key, g.Sum(l => l.NetSalary)))
                .ToListAsync();

            var finalizedLines = await _db.PayrollRunLines.AsNoTracking()
                .Where(l => l.PayrollRun!.Status == PayrollRunStatus.Finalized)
                .Select(l => new { l.Id, l.Currency, l.NetSalary })
                .ToListAsync();
            var ids = finalizedLines.Select(l => l.Id).ToList();
            var paid = await _db.EmployeeSalaryTransactions.AsNoTracking()
                .Where(t => t.PayrollRunLineId != null && ids.Contains(t.PayrollRunLineId.Value) && t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment && !t.IsCancelled)
                .GroupBy(t => t.PayrollRunLineId!.Value)
                .Select(g => new { g.Key, Amount = g.Sum(t => t.Amount) })
                .ToDictionaryAsync(x => x.Key, x => x.Amount);
            model.UnpaidSalaries = finalizedLines
                .GroupBy(l => l.Currency)
                .Select(g => new HrCurrencyTotal(g.Key, g.Sum(l => l.NetSalary - paid.GetValueOrDefault(l.Id))))
                .Where(x => x.Amount != 0m)
                .ToList();

            var loanRows = await _db.EmployeeSalaryTransactions.AsNoTracking()
                .Where(t => !t.IsCancelled && (t.EmployeeLoanId != null
                    || t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                    || t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery))
                .Select(t => new { t.TransactionType, t.Currency, t.Amount })
                .ToListAsync();
            model.OutstandingLoansAndAdvances = loanRows
                .GroupBy(r => r.Currency)
                .Select(g => new HrCurrencyTotal(g.Key,
                    g.Where(r => r.TransactionType is EmployeeSalaryTransactionType.LoanDisbursement or EmployeeSalaryTransactionType.SalaryAdvance).Sum(r => r.Amount)
                    - g.Where(r => r.TransactionType is EmployeeSalaryTransactionType.LoanRecovery or EmployeeSalaryTransactionType.LoanRepayment or EmployeeSalaryTransactionType.AdvanceRecovery).Sum(r => r.Amount)))
                .Where(x => x.Amount != 0m)
                .ToList();
        }

        return View(model);
    }
}
