using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.HumanResources;

public sealed record EmployeeLoanInput(
    int EmployeeId,
    DateTime LoanDate,
    decimal PrincipalAmount,
    string Currency,
    int InstallmentCount,
    int StartRecoveryYear,
    int StartRecoveryMonth,
    int CashAccountId,
    string? Notes);

public interface IEmployeeLoanService
{
    /// <summary>قرضه را ثبت و همان لحظه از صندوق/بانک پرداخت می‌کند.</summary>
    Task<EmployeeLoan> CreateAsync(EmployeeLoanInput input, CancellationToken ct = default);

    /// <summary>بازپرداختِ نقدیِ بخشی یا همهٔ قرضه (پیش از موعد هم مجاز).</summary>
    Task<EmployeeSalaryTransaction> RepayAsync(int loanId, decimal amount, int cashAccountId, DateTime date, string? reference, CancellationToken ct = default);

    /// <summary>فقط قرضه‌ای که هیچ قسط/بازپرداختی ندارد لغو می‌شود؛ پرداختش با سندِ معکوس برمی‌گردد.</summary>
    Task CancelAsync(int loanId, string reason, CancellationToken ct = default);
}

/// <summary>
/// قرضهٔ کارمند، بدونِ سود. قسط = اصل ÷ تعدادِ اقساط (گرد به بالا؛ قسطِ آخر هرچه مانده). هر
/// حرکتِ پولی (پرداخت، قسطِ معاش، بازپرداخت) یک تراکنشِ معاشِ پیوسته به قرضه است و از مسیرِ
/// موجودِ <see cref="IEmployeeSalaryService"/> ثبت می‌شود — روزنامچه، دفتر، صورت‌حساب و دفتر کل.
/// </summary>
public sealed class EmployeeLoanService(
    ApplicationDbContext db,
    IEmployeeSalaryService salaryService,
    IAuditService audit) : IEmployeeLoanService
{
    public async Task<EmployeeLoan> CreateAsync(EmployeeLoanInput input, CancellationToken ct = default)
    {
        if (input.PrincipalAmount <= 0m)
            throw new BusinessRuleException("EMPLOYEE_LOAN_AMOUNT", "مبلغِ قرضه باید بیشتر از صفر باشد.");
        if (input.InstallmentCount is < 1 or > 120)
            throw new BusinessRuleException("EMPLOYEE_LOAN_INSTALLMENTS", "تعدادِ اقساط باید بین ۱ و ۱۲۰ باشد.");
        if (input.StartRecoveryYear is < 2000 or > 2100 || input.StartRecoveryMonth is < 1 or > 12)
            throw new BusinessRuleException("EMPLOYEE_LOAN_START_MONTH", "ماهِ شروعِ کسر معتبر نیست.");
        if (new DateTime(input.StartRecoveryYear, input.StartRecoveryMonth, 1) < new DateTime(input.LoanDate.Year, input.LoanDate.Month, 1))
            throw new BusinessRuleException("EMPLOYEE_LOAN_START_MONTH", "شروعِ کسر نمی‌تواند پیش از ماهِ قرضه باشد.");

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == input.EmployeeId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");
        if (!employee.IsActive)
            throw new BusinessRuleException("EMPLOYEE_INACTIVE", "به کارمندِ غیرفعال قرضه داده نمی‌شود.");

        var principal = decimal.Round(input.PrincipalAmount, 2, MidpointRounding.AwayFromZero);
        // گرد به بالا تا اقساط در همان تعداد تمام شود؛ قسطِ آخر فقط هرچه مانده کسر می‌شود.
        var installment = Math.Ceiling(principal * 100m / input.InstallmentCount) / 100m;

        return await InTransactionAsync(async () =>
        {
            var loan = new EmployeeLoan
            {
                EmployeeId = employee.Id,
                LoanDate = input.LoanDate.Date,
                PrincipalAmount = principal,
                Currency = SystemCurrency.Normalize(input.Currency),
                InstallmentCount = input.InstallmentCount,
                InstallmentAmount = installment,
                StartRecoveryYear = input.StartRecoveryYear,
                StartRecoveryMonth = input.StartRecoveryMonth,
                Status = EmployeeLoanStatus.Active,
                Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim()
            };
            db.EmployeeLoans.Add(loan);
            await db.SaveChangesAsync(ct);

            var disbursement = await salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
                employee.Id, loan.LoanDate, EmployeeSalaryTransactionType.LoanDisbursement, principal, loan.Currency,
                null, input.CashAccountId, $"LOAN-{loan.Id}", "پرداختِ قرضهٔ کارمند", null, null,
                EmployeeLoanId: loan.Id), ct);

            loan.DisbursementTransactionId = disbursement.Id;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(EmployeeLoan), loan.Id, AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("EmployeeId", loan.EmployeeId),
                    ("LoanDate", loan.LoanDate),
                    ("PrincipalAmount", loan.PrincipalAmount),
                    ("Currency", loan.Currency),
                    ("InstallmentCount", loan.InstallmentCount),
                    ("InstallmentAmount", loan.InstallmentAmount),
                    ("StartRecovery", $"{loan.StartRecoveryYear:0000}/{loan.StartRecoveryMonth:00}"),
                    ("DisbursementTransactionId", loan.DisbursementTransactionId)));
            await db.SaveChangesAsync(ct);
            return loan;
        }, ct);
    }

    public async Task<EmployeeSalaryTransaction> RepayAsync(int loanId, decimal amount, int cashAccountId, DateTime date, string? reference, CancellationToken ct = default)
    {
        var loan = await db.EmployeeLoans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == loanId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_LOAN_NOT_FOUND", "قرضه پیدا نشد.");
        if (loan.Status != EmployeeLoanStatus.Active)
            throw new BusinessRuleException("EMPLOYEE_LOAN_NOT_ACTIVE", "فقط قرضهٔ فعال بازپرداخت می‌شود.");

        return await salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
            loan.EmployeeId, date, EmployeeSalaryTransactionType.LoanRepayment, decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            loan.Currency, null, cashAccountId, string.IsNullOrWhiteSpace(reference) ? $"LOAN-{loan.Id}-REPAY" : reference,
            "بازپرداختِ نقدیِ قرضه", null, null, EmployeeLoanId: loan.Id), ct);
    }

    public async Task CancelAsync(int loanId, string reason, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
            throw new BusinessRuleException("EMPLOYEE_LOAN_CANCEL_REASON", "دلیلِ لغوِ قرضه الزامی است.");

        var loan = await db.EmployeeLoans.FirstOrDefaultAsync(l => l.Id == loanId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_LOAN_NOT_FOUND", "قرضه پیدا نشد.");
        if (loan.Status == EmployeeLoanStatus.Cancelled)
            return;

        if (await db.EmployeeSalaryTransactions.AnyAsync(t => t.EmployeeLoanId == loan.Id
                && !t.IsCancelled
                && t.TransactionType != EmployeeSalaryTransactionType.LoanDisbursement, ct))
            throw new BusinessRuleException("EMPLOYEE_LOAN_HAS_REPAYMENTS", "این قرضه قسط یا بازپرداخت دارد؛ اول آن‌ها را لغو کنید.");

        await InTransactionAsync(async () =>
        {
            if (loan.DisbursementTransactionId.HasValue)
            {
                await salaryService.CancelAsync(loan.DisbursementTransactionId.Value, $"لغوِ قرضه: {text}", ct);
            }

            var before = loan.Status;
            loan.Status = EmployeeLoanStatus.Cancelled;
            loan.CancellationReason = text.Length > 1000 ? text[..1000] : text;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(EmployeeLoan), loan.Id, AuditAction.Reverse,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", before, loan.Status),
                    ("CancellationReason", (string?)null, loan.CancellationReason)));
            await db.SaveChangesAsync(ct);
            return loan;
        }, ct);
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await work();
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return result;
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
