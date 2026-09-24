using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// گزارش‌های مدیریت بشری — یک صفحه، یک جدول، یک خروجی CSV (همان الگوی خروجیِ موجود). گزارش‌هایی
/// که مبلغِ معاش دارند فقط با «دیدن معاش» باز می‌شوند. صورت‌حسابِ کارمند همان صورت‌حسابِ رسمیِ
/// طرف‌حساب است و اینجا فقط به آن پیوند داده می‌شود.
/// </summary>
[Authorize]
public class HrReportsController : Controller
{
    private readonly ApplicationDbContext _db;

    public HrReportsController(ApplicationDbContext db) => _db = db;

    public static readonly (string Key, string Label, bool NeedsSalary)[] Reports =
    [
        ("employees", "فهرست کارمندان", false),
        ("attendance", "حاضری ماه", false),
        ("leave", "رخصتی‌ها", false),
        ("contracts", "قراردادهای رو به انقضا", false),
        ("payroll", "معاش ماه", true),
        ("payments", "پرداخت‌های معاش", true),
        ("payable", "معاشِ پرداخت‌نشده", true),
        ("loans", "قرضه و مساعده", true),
        ("cost", "هزینهٔ کارمندان به تفکیک بخش", true)
    ];

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> Index(string? report = null, string? tab = null, int? year = null, int? month = null, DateTime? from = null, DateTime? to = null, string? format = null)
    {
        var canViewSalary = RoleAccessRules.CanViewEmployeeSalary(User);
        // نوارِ تبِ مشترک کلیدِ tab را می‌فرستد (و بر report ِ فیلترِ قبلی مقدم است)؛ پیوندِ خروجی report را.
        report = tab ?? report;
        var key = Reports.Any(r => r.Key == report) ? report! : "employees";
        if (Reports.First(r => r.Key == key).NeedsSalary && !canViewSalary)
        {
            key = "employees";
        }

        var today = AfghanistanBusinessClock.SystemToday;
        var y = year is >= 2000 and <= 2100 ? year.Value : today.Year;
        var m = month is >= 1 and <= 12 ? month.Value : today.Month;
        var start = (from ?? new DateTime(y, m, 1)).Date;
        var end = (to ?? new DateTime(y, m, DateTime.DaysInMonth(y, m))).Date;

        var table = key switch
        {
            "attendance" => await AttendanceAsync(y, m),
            "leave" => await LeaveAsync(start, end),
            "contracts" => await ContractsAsync(today, canViewSalary),
            "payroll" => await PayrollAsync(y, m),
            "payments" => await PaymentsAsync(start, end),
            "payable" => await PayableAsync(),
            "loans" => await LoansAsync(),
            "cost" => await CostAsync(y),
            _ => await EmployeesAsync(canViewSalary)
        };

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return CsvExportSupport.File(this, $"hr-{key}-{DateTime.UtcNow:yyyyMMdd}.csv", table.Headers, table.Rows);
        }

        return View(new HrReportViewModel
        {
            Report = key,
            Year = y,
            Month = m,
            From = start,
            To = end,
            Table = table,
            Reports = Reports.Where(r => !r.NeedsSalary || canViewSalary).Select(r => (r.Key, r.Label)).ToList()
        });
    }

    private async Task<HrReportTable> EmployeesAsync(bool canViewSalary)
    {
        var rows = await _db.Employees.AsNoTracking()
            .OrderByDescending(e => e.IsActive).ThenBy(e => e.FullName)
            .Select(e => new
            {
                e.EmployeeCode, e.FullName,
                Department = e.DepartmentRef != null ? e.DepartmentRef.Name : e.Department,
                Position = e.PositionRef != null ? e.PositionRef.Name : e.JobTitle,
                e.EmployeeType, e.HireDate, e.EndDate, e.IsActive, e.BaseSalaryAmount, e.SalaryCurrency
            })
            .ToListAsync();
        var headers = new List<string> { "کد", "نام", "بخش", "بست", "نوع", "شروع کار", "پایان کار", "وضعیت" };
        if (canViewSalary) headers.Add("معاش جاری");
        return new HrReportTable(
            headers.ToArray(),
            rows.Select(e =>
            {
                var cells = new List<string?>
                {
                    e.EmployeeCode, e.FullName, e.Department, e.Position, EmployeeTypeLabels.ToPersian(e.EmployeeType),
                    CsvExportSupport.Date(e.HireDate), CsvExportSupport.Date(e.EndDate), e.IsActive ? "فعال" : "غیرفعال"
                };
                if (canViewSalary) cells.Add($"{e.BaseSalaryAmount:0.##} {e.SalaryCurrency}");
                return cells.ToArray();
            }).ToList(),
            $"{rows.Count(e => e.IsActive):N0} فعال از {rows.Count:N0}");
    }

    private async Task<HrReportTable> AttendanceAsync(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var rows = await _db.DailyAttendances.AsNoTracking()
            .Where(a => a.Date >= start && a.Date <= end)
            .GroupBy(a => new { a.EmployeeId, a.Employee!.EmployeeCode, a.Employee.FullName })
            .Select(g => new
            {
                g.Key.EmployeeCode, g.Key.FullName,
                Present = g.Count(a => a.Status == AttendanceStatus.Present),
                Absent = g.Count(a => a.Status == AttendanceStatus.Absent),
                Leave = g.Count(a => a.Status == AttendanceStatus.Leave),
                LateDays = g.Count(a => a.MinutesLate > 0),
                LateMinutes = g.Sum(a => a.MinutesLate ?? 0)
            })
            .OrderBy(r => r.FullName)
            .ToListAsync();
        return new HrReportTable(
            ["کد", "نام", "حاضر", "غایب", "رخصتی", "روزهای تأخیر", "دقیقهٔ تأخیر"],
            rows.Select(r => new string?[] { r.EmployeeCode, r.FullName, r.Present.ToString(), r.Absent.ToString(), r.Leave.ToString(), r.LateDays.ToString(), r.LateMinutes.ToString() }).ToList(),
            $"{year:0000}/{month:00}");
    }

    private async Task<HrReportTable> LeaveAsync(DateTime from, DateTime to)
    {
        var rows = await _db.LeaveRequests.AsNoTracking()
            .Where(r => r.FromDate <= to && r.ToDate >= from)
            .OrderBy(r => r.FromDate)
            .Select(r => new { r.Employee!.EmployeeCode, r.Employee.FullName, Type = r.LeaveType!.Name, r.LeaveType.IsPaid, r.FromDate, r.ToDate, r.TotalDays, r.Status })
            .ToListAsync();
        return new HrReportTable(
            ["کد", "نام", "نوع", "با معاش", "از", "تا", "روز", "وضعیت"],
            rows.Select(r => new string?[] { r.EmployeeCode, r.FullName, r.Type, r.IsPaid ? "بلی" : "نخیر", CsvExportSupport.Date(r.FromDate), CsvExportSupport.Date(r.ToDate), CsvExportSupport.Decimal(r.TotalDays), LeaveLabels.Status(r.Status) }).ToList(),
            $"{CsvExportSupport.Date(from)} تا {CsvExportSupport.Date(to)}");
    }

    private async Task<HrReportTable> ContractsAsync(DateTime today, bool canViewSalary)
    {
        var soon = today.AddDays(60);
        var rows = await _db.EmploymentContracts.AsNoTracking()
            .Where(c => c.Status == EmploymentContractStatus.Active && c.EndDate != null && c.EndDate <= soon)
            .OrderBy(c => c.EndDate)
            .Select(c => new { c.Employee!.EmployeeCode, c.Employee.FullName, c.ContractNumber, c.ContractType, c.StartDate, c.EndDate, c.BaseSalary, c.Currency })
            .ToListAsync();
        var headers = new List<string> { "کد", "نام", "شمارهٔ قرارداد", "نوع", "شروع", "پایان", "روزِ مانده" };
        if (canViewSalary) headers.Add("معاش");
        return new HrReportTable(
            headers.ToArray(),
            rows.Select(r =>
            {
                var cells = new List<string?> { r.EmployeeCode, r.FullName, r.ContractNumber, EmploymentContractLabels.Type(r.ContractType), CsvExportSupport.Date(r.StartDate), CsvExportSupport.Date(r.EndDate), ((r.EndDate!.Value - today).Days).ToString() };
                if (canViewSalary) cells.Add($"{r.BaseSalary:0.##} {r.Currency}");
                return cells.ToArray();
            }).ToList(),
            "قراردادهای فعالی که تا ۶۰ روز دیگر پایان می‌یابند (منفی = گذشته)");
    }

    private async Task<HrReportTable> PayrollAsync(int year, int month)
    {
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Year == year && r.Month == month);
        var lines = run is null ? [] : await _db.PayrollRunLines.AsNoTracking()
            .Where(l => l.PayrollRunId == run.Id)
            .Include(l => l.Employee)
            .OrderBy(l => l.Employee!.FullName)
            .ToListAsync();
        return new HrReportTable(
            ["کد", "نام", "ارز", "معاش پایه", "درآمدِ دیگر", "ناخالص", "غیبت/تأخیر/رخصتی", "قرضه", "مساعده", "کسر دیگر", "خالص"],
            lines.Select(l => new string?[]
            {
                l.Employee!.EmployeeCode, l.Employee.FullName, l.Currency, CsvExportSupport.Decimal(l.BaseSalary),
                CsvExportSupport.Decimal(l.OvertimeAmount + l.BonusAmount + l.AllowanceAmount + l.OtherEarning), CsvExportSupport.Decimal(l.GrossSalary),
                CsvExportSupport.Decimal(l.AbsenceDeduction + l.LateDeduction + l.UnpaidLeaveDeduction), CsvExportSupport.Decimal(l.LoanDeduction),
                CsvExportSupport.Decimal(l.AdvanceDeduction), CsvExportSupport.Decimal(l.OtherDeduction), CsvExportSupport.Decimal(l.NetSalary)
            }).ToList(),
            run is null ? "معاشِ این ماه ساخته نشده است." : $"{year:0000}/{month:00} — {PayrollLabels.Status(run.Status)} — " + string.Join("، ", lines.GroupBy(l => l.Currency).Select(g => $"خالص {g.Sum(l => l.NetSalary):N2} {g.Key}")));
    }

    private async Task<HrReportTable> PaymentsAsync(DateTime from, DateTime to)
    {
        var rows = await _db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment && !t.IsCancelled && t.TransactionDate >= from && t.TransactionDate <= to)
            .OrderBy(t => t.TransactionDate)
            .Select(t => new { t.TransactionDate, t.Employee!.EmployeeCode, t.Employee.FullName, t.Amount, t.Currency, t.AmountUsd, Cash = t.CashAccount != null ? t.CashAccount.Name : null, t.Reference })
            .ToListAsync();
        return new HrReportTable(
            ["تاریخ", "کد", "نام", "مبلغ", "ارز", "معادل USD", "صندوق/بانک", "مرجع"],
            rows.Select(r => new string?[] { CsvExportSupport.Date(r.TransactionDate), r.EmployeeCode, r.FullName, CsvExportSupport.Decimal(r.Amount), r.Currency, CsvExportSupport.Decimal(r.AmountUsd), r.Cash, r.Reference }).ToList(),
            $"جمع {rows.Sum(r => r.AmountUsd):N2} USD");
    }

    private async Task<HrReportTable> PayableAsync()
    {
        var lines = await _db.PayrollRunLines.AsNoTracking()
            .Where(l => l.PayrollRun!.Status == PayrollRunStatus.Finalized)
            .Select(l => new { l.Id, l.EmployeeId, l.Employee!.EmployeeCode, l.Employee.FullName, l.PayrollRun!.Year, l.PayrollRun.Month, l.Currency, l.NetSalary })
            .ToListAsync();
        var ids = lines.Select(l => l.Id).ToList();
        var paid = await _db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.PayrollRunLineId != null && ids.Contains(t.PayrollRunLineId.Value) && t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment && !t.IsCancelled)
            .GroupBy(t => t.PayrollRunLineId!.Value)
            .Select(g => new { LineId = g.Key, Paid = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.LineId, x => x.Paid);
        var open = lines
            .Select(l => new { l, Remaining = l.NetSalary - paid.GetValueOrDefault(l.Id) })
            .Where(x => x.Remaining != 0m)
            .OrderBy(x => x.l.FullName).ThenBy(x => x.l.Year).ThenBy(x => x.l.Month)
            .ToList();
        return new HrReportTable(
            ["کد", "نام", "ماه", "ارز", "خالص", "پرداخت‌شده", "مانده"],
            open.Select(x => new string?[] { x.l.EmployeeCode, x.l.FullName, $"{x.l.Year:0000}/{x.l.Month:00}", x.l.Currency, CsvExportSupport.Decimal(x.l.NetSalary), CsvExportSupport.Decimal(x.l.NetSalary - x.Remaining), CsvExportSupport.Decimal(x.Remaining) }).ToList(),
            string.Join("، ", open.GroupBy(x => x.l.Currency).Select(g => $"{g.Sum(x => x.Remaining):N2} {g.Key}")));
    }

    private async Task<HrReportTable> LoansAsync()
    {
        var loans = await _db.EmployeeLoans.AsNoTracking()
            .Where(l => l.Status == EmployeeLoanStatus.Active)
            .Select(l => new { l.Id, l.Employee!.EmployeeCode, l.Employee.FullName, l.LoanDate, l.PrincipalAmount, l.Currency, l.InstallmentAmount })
            .ToListAsync();
        var rows = new List<string?[]>();
        foreach (var loan in loans)
        {
            var outstanding = await EmployeeSalaryService.GetLoanOutstandingAsync(_db, loan.Id, null, HttpContext?.RequestAborted ?? default);
            rows.Add(["قرضه", loan.EmployeeCode, loan.FullName, CsvExportSupport.Date(loan.LoanDate), loan.Currency, CsvExportSupport.Decimal(loan.PrincipalAmount), CsvExportSupport.Decimal(loan.InstallmentAmount), CsvExportSupport.Decimal(outstanding)]);
        }

        var advances = await _db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => !t.IsCancelled && (t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance || t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery))
            .GroupBy(t => new { t.Employee!.EmployeeCode, t.Employee.FullName, t.Currency })
            .Select(g => new
            {
                g.Key.EmployeeCode, g.Key.FullName, g.Key.Currency,
                Advanced = g.Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance).Sum(t => t.Amount),
                Recovered = g.Where(t => t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery).Sum(t => t.Amount)
            })
            .ToListAsync();
        rows.AddRange(advances.Where(a => a.Advanced - a.Recovered != 0m)
            .Select(a => new string?[] { "مساعده", a.EmployeeCode, a.FullName, null, a.Currency, CsvExportSupport.Decimal(a.Advanced), null, CsvExportSupport.Decimal(a.Advanced - a.Recovered) }));
        return new HrReportTable(["نوع", "کد", "نام", "تاریخ", "ارز", "مبلغ", "قسط", "مانده"], rows, "طلب‌های باز از کارمندان");
    }

    private async Task<HrReportTable> CostAsync(int year)
    {
        var rows = await _db.PayrollRunLines.AsNoTracking()
            .Where(l => l.PayrollRun!.Year == year && l.PayrollRun.Status == PayrollRunStatus.Finalized)
            .Select(l => new
            {
                Department = l.Employee!.DepartmentRef != null ? l.Employee.DepartmentRef.Name : (l.Employee.Department ?? "بدون بخش"),
                l.Currency,
                Earned = l.GrossSalary - l.AbsenceDeduction - l.LateDeduction - l.UnpaidLeaveDeduction - l.OtherDeduction,
                l.EmployeeId
            })
            .ToListAsync();
        var grouped = rows.GroupBy(r => new { r.Department, r.Currency })
            .OrderBy(g => g.Key.Department)
            .Select(g => new string?[] { g.Key.Department, g.Key.Currency, g.Select(r => r.EmployeeId).Distinct().Count().ToString(), CsvExportSupport.Decimal(g.Sum(r => r.Earned)) })
            .ToList();
        return new HrReportTable(["بخش", "ارز", "کارمند", "هزینهٔ معاش (کارکرد)"], grouped,
            $"سال {year:0000} — فقط معاش‌های نهایی‌شده؛ همان مبلغی که مصرفِ معاش ثبت شده است.");
    }
}
