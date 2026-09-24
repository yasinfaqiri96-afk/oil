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

/// <summary>
/// معاش: انتخاب ماه ← «ساخت معاش ماه» ← بررسی و ویرایشِ مقادیرِ دستی در همان جدول ← نهایی‌سازی ←
/// پرداخت (یکی، چندتا، همه، ناقص). همهٔ اعداد فقط با «دیدن معاش» نمایش داده می‌شوند.
/// </summary>
[Authorize(Policy = AuthPolicies.HrViewSalary)]
public class PayrollController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPayrollService _payroll;

    public PayrollController(ApplicationDbContext db, IPayrollService payroll)
    {
        _db = db;
        _payroll = payroll;
    }

    public async Task<IActionResult> Index(int? year = null, int? month = null)
    {
        var (y, m) = ResolveMonth(year, month);
        var skipped = TempData["payrollSkipped"] as string;
        return View(await BuildAsync(y, m, skipped is null ? [] : skipped.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Authorize(Policy = AuthPolicies.HrRunPayroll)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(int year, int month)
    {
        try
        {
            var result = await _payroll.GenerateAsync(year, month);
            TempData["ok"] = $"معاشِ {year:0000}/{month:00} برای {result.Run.Lines.Count:N0} کارمند محاسبه شد.";
            if (result.SkippedEmployees.Count > 0)
                TempData["payrollSkipped"] = string.Join("\n", result.SkippedEmployees);
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { year, month });
    }

    [Authorize(Policy = AuthPolicies.HrRunPayroll)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveLines(int year, int month, List<PayrollLineInput> lines)
    {
        try
        {
            var ids = (lines ?? []).Select(l => l.LineId).ToList();
            var current = await _db.PayrollRunLines.AsNoTracking().Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
            var updated = 0;
            foreach (var input in lines ?? [])
            {
                if (!current.TryGetValue(input.LineId, out var line)) continue;
                var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
                if (line.OvertimeAmount == input.OvertimeAmount && line.BonusAmount == input.BonusAmount
                    && line.AllowanceAmount == input.AllowanceAmount && line.OtherEarning == input.OtherEarning
                    && line.OtherDeduction == input.OtherDeduction && line.Notes == notes)
                    continue;

                await _payroll.UpdateLineAsync(input.LineId, new PayrollManualInput(
                    input.OvertimeAmount, input.BonusAmount, input.AllowanceAmount, input.OtherEarning, input.OtherDeduction, input.Notes));
                updated++;
            }

            TempData["ok"] = updated == 0 ? "تغییری نبود." : $"{updated:N0} سطر ذخیره و دوباره محاسبه شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { year, month });
    }

    [Authorize(Policy = AuthPolicies.HrRunPayroll)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Finalize(int runId, int year, int month)
    {
        try
        {
            await _payroll.FinalizeAsync(runId, CurrentUserId());
            TempData["ok"] = "معاش نهایی شد: مصرف و بدهیِ معاش ثبت شد. پرداخت قدمِ جداگانه است.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { year, month });
    }

    [Authorize(Policy = AuthPolicies.HrRunPayroll)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(int runId, int year, int month, string? reason)
    {
        try
        {
            await _payroll.ReopenAsync(runId, reason ?? "");
            TempData["ok"] = "معاش بازگشایی شد؛ ثبت‌های قبلی با سندِ برگشت خنثی شدند.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { year, month });
    }

    [Authorize(Policy = AuthPolicies.HrPaySalary)]
    public async Task<IActionResult> Pay(int year, int month)
    {
        var model = await BuildAsync(year, month, []);
        if (!model.IsFinalized)
        {
            TempData["err"] = "فقط معاشِ نهایی‌شده پرداخت می‌شود.";
            return RedirectToAction(nameof(Index), new { year, month });
        }

        ViewBag.CashAccounts = new SelectList(
            await _db.CashAccounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Code)
                .Select(a => new { a.Id, Label = a.Code + " - " + a.Name + " (" + a.Currency + ")" }).ToListAsync(),
            "Id", "Label");
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.HrPaySalary)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PayLines(int year, int month, int cashAccountId, DateTime paymentDate, string? reference, List<PayrollPayRowInput> rows)
    {
        var selected = (rows ?? []).Where(r => r.Selected && r.Amount > 0m).ToList();
        if (selected.Count == 0)
        {
            TempData["err"] = "هیچ ردیفی برای پرداخت انتخاب نشده است.";
            return RedirectToAction(nameof(Pay), new { year, month });
        }

        var paid = 0;
        var errors = new List<string>();
        foreach (var row in selected)
        {
            try
            {
                await _payroll.PayLineAsync(row.LineId, row.Amount, cashAccountId, paymentDate, reference);
                paid++;
            }
            catch (BusinessRuleException ex)
            {
                // هر پرداخت جداگانه ثبت می‌شود؛ خطای یکی بقیه را برنمی‌گرداند و گزارش می‌شود.
                errors.Add(ex.Message);
            }
        }

        if (paid > 0) TempData["ok"] = $"{paid:N0} پرداخت ثبت شد.";
        if (errors.Count > 0) TempData["err"] = string.Join(" | ", errors.Distinct());
        return RedirectToAction(nameof(Pay), new { year, month });
    }

    private async Task<PayrollIndexViewModel> BuildAsync(int year, int month, IReadOnlyList<string> skipped)
    {
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Year == year && r.Month == month);
        var lines = new List<PayrollLineViewModel>();
        if (run is not null)
        {
            var paid = await _payroll.GetPaidAmountsAsync(run.Id);
            lines = await _db.PayrollRunLines.AsNoTracking()
                .Where(l => l.PayrollRunId == run.Id)
                .OrderBy(l => l.Employee!.FullName)
                .Select(l => new PayrollLineViewModel
                {
                    Line = l,
                    EmployeeName = l.Employee!.FullName,
                    EmployeeCode = l.Employee.EmployeeCode,
                    DepartmentName = l.Employee.DepartmentRef != null ? l.Employee.DepartmentRef.Name : l.Employee.Department
                })
                .ToListAsync();
            lines = lines.Select(l => new PayrollLineViewModel
            {
                Line = l.Line,
                EmployeeName = l.EmployeeName,
                EmployeeCode = l.EmployeeCode,
                DepartmentName = l.DepartmentName,
                Paid = paid.GetValueOrDefault(l.Line.Id)
            }).ToList();
        }

        return new PayrollIndexViewModel
        {
            Year = year,
            Month = month,
            Run = run,
            Lines = lines,
            Totals = lines.GroupBy(l => l.Line.Currency).Select(g => new PayrollCurrencyTotal
            {
                Currency = g.Key,
                Base = g.Sum(l => l.Line.BaseSalary),
                Earnings = g.Sum(l => l.Line.GrossSalary),
                Deductions = g.Sum(l => l.Line.TotalDeduction),
                Net = g.Sum(l => l.Line.NetSalary),
                Paid = g.Sum(l => l.Paid)
            }).ToList(),
            Skipped = skipped,
            CanRun = RoleAccessRules.CanRunPayroll(User),
            CanPay = RoleAccessRules.CanPaySalary(User)
        };
    }

    private static (int Year, int Month) ResolveMonth(int? year, int? month)
    {
        var today = AfghanistanBusinessClock.SystemToday;
        return (year is >= 2000 and <= 2100 ? year.Value : today.Year, month is >= 1 and <= 12 ? month.Value : today.Month);
    }

    private int? CurrentUserId()
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
