using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.HumanResources;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.HumanResources;

namespace PTGOilSystem.Web.Controllers;

/// <summary>
/// «قرضه و مساعده» — یک صفحه با دو تب: قرضه‌ها (با قسط و مانده) و مساعده‌های باز. مساعده از
/// «تراکنش معاش» کارمند ثبت می‌شود و از معاشِ ماهانه وصول؛ قرضه اینجا ثبت و پرداخت می‌شود.
/// </summary>
[Authorize(Policy = AuthPolicies.HrViewSalary)]
public class EmployeeLoansController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IEmployeeLoanService _loans;

    public EmployeeLoansController(ApplicationDbContext db, IEmployeeLoanService loans)
    {
        _db = db;
        _loans = loans;
    }

    public async Task<IActionResult> Index(string? tab = null)
    {
        var loans = await _db.EmployeeLoans.AsNoTracking()
            .OrderBy(l => l.Status).ThenByDescending(l => l.LoanDate)
            .Select(l => new
            {
                l.Id, l.EmployeeId, l.Employee!.FullName, l.Employee.EmployeeCode, l.LoanDate, l.PrincipalAmount, l.Currency,
                l.InstallmentCount, l.InstallmentAmount, l.StartRecoveryYear, l.StartRecoveryMonth, l.Status
            })
            .ToListAsync();
        var loanIds = loans.Select(l => l.Id).ToList();
        var loanRows = await _db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.EmployeeLoanId != null && loanIds.Contains(t.EmployeeLoanId.Value) && !t.IsCancelled)
            .Select(t => new { LoanId = t.EmployeeLoanId!.Value, t.TransactionType, t.Amount })
            .ToListAsync();

        var advanceRows = await _db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => !t.IsCancelled
                && (t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                    || t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery))
            .Select(t => new
            {
                t.EmployeeId, t.Employee!.FullName, t.Employee.EmployeeCode, t.Currency, t.TransactionType, t.Amount,
                t.TransactionDate, t.RecoveryYear, t.RecoveryMonth
            })
            .ToListAsync();

        return View(new EmployeeLoansIndexViewModel
        {
            Tab = tab == "advances" ? "advances" : "loans",
            CanManage = RoleAccessRules.CanManageEmployeeSalary(User),
            Loans = loans.Select(l => new EmployeeLoanListItemViewModel
            {
                Id = l.Id,
                EmployeeId = l.EmployeeId,
                EmployeeName = l.FullName,
                EmployeeCode = l.EmployeeCode,
                LoanDate = l.LoanDate,
                PrincipalAmount = l.PrincipalAmount,
                Currency = l.Currency,
                InstallmentCount = l.InstallmentCount,
                InstallmentAmount = l.InstallmentAmount,
                StartRecovery = $"{l.StartRecoveryYear:0000}/{l.StartRecoveryMonth:00}",
                Status = l.Status,
                Outstanding = l.Status == EmployeeLoanStatus.Cancelled ? 0m
                    : loanRows.Where(r => r.LoanId == l.Id && r.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement).Sum(r => r.Amount)
                      - loanRows.Where(r => r.LoanId == l.Id && r.TransactionType != EmployeeSalaryTransactionType.LoanDisbursement).Sum(r => r.Amount)
            }).ToList(),
            Advances = advanceRows
                .GroupBy(r => new { r.EmployeeId, r.FullName, r.EmployeeCode, r.Currency })
                .Select(g => new OutstandingAdvanceViewModel
                {
                    EmployeeId = g.Key.EmployeeId,
                    EmployeeName = g.Key.FullName,
                    EmployeeCode = g.Key.EmployeeCode,
                    Currency = g.Key.Currency,
                    Advanced = g.Where(r => r.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance).Sum(r => r.Amount),
                    Recovered = g.Where(r => r.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery).Sum(r => r.Amount),
                    NextRecovery = g.Where(r => r.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance)
                        .Select(r => r.RecoveryYear.HasValue ? $"{r.RecoveryYear:0000}/{r.RecoveryMonth:00}" : $"{r.TransactionDate:yyyy/MM}")
                        .OrderBy(x => x).FirstOrDefault()
                })
                .Where(a => a.Outstanding > 0m)
                .OrderBy(a => a.EmployeeName)
                .ToList()
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var loan = await _db.EmployeeLoans.AsNoTracking().Include(l => l.Employee).FirstOrDefaultAsync(l => l.Id == id);
        if (loan is null) return NotFound();

        await PopulateCashAccountsAsync();
        return View(new EmployeeLoanDetailsViewModel
        {
            Loan = loan,
            Outstanding = loan.Status == EmployeeLoanStatus.Cancelled ? 0m : await EmployeeSalaryService.GetLoanOutstandingAsync(_db, id, null, HttpContext?.RequestAborted ?? default),
            Transactions = await _db.EmployeeSalaryTransactions.AsNoTracking()
                .Where(t => t.EmployeeLoanId == id)
                .OrderBy(t => t.TransactionDate).ThenBy(t => t.Id)
                .ToListAsync(),
            CanManage = RoleAccessRules.CanManageEmployeeSalary(User),
            CanPay = RoleAccessRules.CanPaySalary(User)
        });
    }

    [Authorize(Policy = AuthPolicies.HrManageSalary)]
    public async Task<IActionResult> Create(int? employeeId = null)
    {
        var model = new EmployeeLoanFormViewModel { EmployeeId = employeeId ?? 0 };
        await PopulateAsync(model);
        return View(model);
    }

    /// <summary>ثبتِ قرضه همان لحظه پول را از صندوق خارج می‌کند؛ پس «پرداخت معاش» هم لازم است.</summary>
    [Authorize(Policy = AuthPolicies.HrManageSalary)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(EmployeeLoanFormViewModel model)
    {
        if (!RoleAccessRules.CanPaySalary(User))
        {
            return Forbid();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var loan = await _loans.CreateAsync(new EmployeeLoanInput(
                    model.EmployeeId, model.LoanDate, model.PrincipalAmount, model.Currency, model.InstallmentCount,
                    model.StartRecoveryYear, model.StartRecoveryMonth, model.CashAccountId, model.Notes));
                TempData["ok"] = "قرضه ثبت و پرداخت شد.";
                return RedirectToAction(nameof(Details), new { id = loan.Id });
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        await PopulateAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.HrPaySalary)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Repay(int id, decimal amount, int cashAccountId, DateTime date, string? reference)
    {
        try
        {
            await _loans.RepayAsync(id, amount, cashAccountId, date, reference);
            TempData["ok"] = "بازپرداختِ قرضه ثبت شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = AuthPolicies.HrManageSalary)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        if (!RoleAccessRules.CanPaySalary(User))
        {
            return Forbid();
        }

        try
        {
            await _loans.CancelAsync(id, reason ?? "");
            TempData["ok"] = "قرضه لغو شد؛ پرداختش با سندِ برگشت به صندوق برگشت.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task PopulateAsync(EmployeeLoanFormViewModel model)
    {
        ViewBag.Employees = new SelectList(
            await _db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName)
                .Select(e => new { e.Id, Label = e.EmployeeCode + " - " + e.FullName }).ToListAsync(),
            "Id", "Label", model.EmployeeId);
        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
        await PopulateCashAccountsAsync(model.CashAccountId);
    }

    private async Task PopulateCashAccountsAsync(int? selected = null)
        => ViewBag.CashAccounts = new SelectList(
            await _db.CashAccounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Code)
                .Select(a => new { a.Id, Label = a.Code + " - " + a.Name + " (" + a.Currency + ")" }).ToListAsync(),
            "Id", "Label", selected);
}
