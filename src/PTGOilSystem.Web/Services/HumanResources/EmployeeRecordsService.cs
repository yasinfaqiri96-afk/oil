using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.HumanResources;

public sealed record EmployeeDocumentInput(
    int EmployeeId,
    EmployeeDocumentType DocumentType,
    string? Title,
    DateTime? IssueDate,
    DateTime? ExpiryDate,
    string? Notes,
    IFormFile? File);

public sealed record CurrencyAmount(string Currency, decimal Amount);

public sealed record OffboardingChecklist(
    Employee Employee,
    IReadOnlyList<CurrencyAmount> UnpaidPayroll,
    decimal LegacyNetBalanceUsd,
    IReadOnlyList<CurrencyAmount> OutstandingLoans,
    IReadOnlyList<CurrencyAmount> OutstandingAdvances,
    IReadOnlyList<LeaveBalance> LeaveBalances,
    EmploymentContract? ActiveContract,
    User? LinkedUser)
{
    public bool HasOpenItems => UnpaidPayroll.Any(x => x.Amount != 0m)
        || OutstandingLoans.Any(x => x.Amount != 0m)
        || OutstandingAdvances.Any(x => x.Amount != 0m)
        || ActiveContract is not null;
}

public interface IEmployeeRecordsService
{
    Task<EmployeeDocument> UploadDocumentAsync(EmployeeDocumentInput input, int? replacesDocumentId, CancellationToken ct = default);
    Task DeleteDocumentAsync(int documentId, string reason, CancellationToken ct = default);
    Task<OffboardingChecklist> GetOffboardingChecklistAsync(int employeeId, CancellationToken ct = default);

    /// <param name="acknowledgeOpenItems">
    /// اگر معاش، قرضه، مساعده یا قراردادِ باز مانده باشد، پایانِ همکاری فقط با تأییدِ صریح انجام می‌شود.
    /// </param>
    Task TerminateAsync(int employeeId, DateTime lastWorkingDate, string reason, string? notes, bool acknowledgeOpenItems, CancellationToken ct = default);
}

/// <summary>
/// اسناد و پایانِ همکاری. سند حذف نمی‌شود (حذفِ نرم با دلیل) و فایل هرگز از دیسک پاک نمی‌شود.
/// پایانِ همکاری کارمند را حذف نمی‌کند: روزِ آخر، دلیل و یادداشت ثبت، قراردادِ فعال فسخ و — فقط اگر
/// کارمند به کاربر وصل باشد — ورودِ همان کاربر غیرفعال می‌شود. معاشِ ماهِ آخر از معاشِ ماهانه
/// (به نسبتِ روزهای کار) و تسویهٔ قرضه/مساعده از مسیرهای معمول انجام می‌شود.
/// </summary>
public sealed class EmployeeRecordsService(
    ApplicationDbContext db,
    IHrFileStorage files,
    ILeaveService leave,
    IEmploymentContractService contracts,
    IAuditService audit) : IEmployeeRecordsService
{
    public async Task<EmployeeDocument> UploadDocumentAsync(EmployeeDocumentInput input, int? replacesDocumentId, CancellationToken ct = default)
    {
        if (input.File is null || input.File.Length == 0)
            throw new BusinessRuleException("HR_FILE_EMPTY", "هیچ فایلی انتخاب نشده است.");
        if (input.IssueDate.HasValue && input.ExpiryDate.HasValue && input.ExpiryDate < input.IssueDate)
            throw new BusinessRuleException("HR_DOCUMENT_DATES", "تاریخِ انقضا پیش از تاریخِ صدور است.");

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == input.EmployeeId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");

        EmployeeDocument? replaced = null;
        if (replacesDocumentId.HasValue)
        {
            replaced = await db.EmployeeDocuments.FirstOrDefaultAsync(d => d.Id == replacesDocumentId.Value && d.EmployeeId == employee.Id && !d.IsDeleted, ct)
                ?? throw new BusinessRuleException("HR_DOCUMENT_NOT_FOUND", "سندی که جایگزین می‌شود پیدا نشد.");
        }

        var title = string.IsNullOrWhiteSpace(input.Title) ? null : input.Title.Trim();
        var document = new EmployeeDocument
        {
            EmployeeId = employee.Id,
            DocumentType = input.DocumentType,
            Title = title is null ? Path.GetFileNameWithoutExtension(input.File.FileName) : title.Length > 200 ? title[..200] : title,
            IssueDate = input.IssueDate?.Date,
            ExpiryDate = input.ExpiryDate?.Date,
            Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim()
        };
        document.Attachment = await files.SaveAsync(employee.Id, HrAttachmentOwner.EmployeeDocument, input.File, ct);
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync(ct);

        if (replaced is not null)
        {
            replaced.IsDeleted = true;
            replaced.DeletedAtUtc = DateTime.UtcNow;
            replaced.DeletedReason = "جایگزین شد با سندِ تازه";
            replaced.ReplacedByDocumentId = document.Id;
            await db.SaveChangesAsync(ct);
        }

        await audit.LogAndSaveAsync(nameof(EmployeeDocument), document.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("EmployeeId", document.EmployeeId),
                ("DocumentType", document.DocumentType),
                ("Title", document.Title),
                ("File", document.Attachment.OriginalFileName),
                ("ReplacesDocumentId", replaced?.Id)));
        return document;
    }

    public async Task DeleteDocumentAsync(int documentId, string reason, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
            throw new BusinessRuleException("HR_DOCUMENT_DELETE_REASON", "دلیلِ حذفِ سند الزامی است.");

        var document = await db.EmployeeDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new BusinessRuleException("HR_DOCUMENT_NOT_FOUND", "سند پیدا نشد.");
        if (document.IsDeleted)
            return;

        document.IsDeleted = true;
        document.DeletedAtUtc = DateTime.UtcNow;
        document.DeletedReason = text.Length > 500 ? text[..500] : text;
        await db.SaveChangesAsync(ct);
        await audit.LogAndSaveAsync(nameof(EmployeeDocument), document.Id, AuditAction.Delete,
            diff: AuditDiffFormatter.ForUpdate(("IsDeleted", false, true), ("DeletedReason", (string?)null, document.DeletedReason)));
    }

    public async Task<OffboardingChecklist> GetOffboardingChecklistAsync(int employeeId, CancellationToken ct = default)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");

        var lines = await db.PayrollRunLines.AsNoTracking()
            .Where(l => l.EmployeeId == employeeId && l.PayrollRun!.Status == PayrollRunStatus.Finalized)
            .Select(l => new { l.Id, l.Currency, l.NetSalary })
            .ToListAsync(ct);
        var lineIds = lines.Select(l => l.Id).ToList();
        var paid = await db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.PayrollRunLineId != null && lineIds.Contains(t.PayrollRunLineId.Value)
                && t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment && !t.IsCancelled)
            .Select(t => new { LineId = t.PayrollRunLineId!.Value, t.Amount })
            .ToListAsync(ct);
        var unpaid = lines
            .GroupBy(l => l.Currency)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(l => l.NetSalary - paid.Where(p => p.LineId == l.Id).Sum(p => p.Amount))))
            .Where(x => x.Amount != 0m)
            .ToList();

        var transactions = await db.EmployeeSalaryTransactions.AsNoTracking().Where(t => t.EmployeeId == employeeId).ToListAsync(ct);
        var legacyBalance = EmployeeSalarySummaryCalculator.FromTransactions(transactions).BalanceUsd;

        var loans = await db.EmployeeLoans.AsNoTracking()
            .Where(l => l.EmployeeId == employeeId && l.Status == EmployeeLoanStatus.Active)
            .Select(l => new { l.Id, l.Currency })
            .ToListAsync(ct);
        var loanOutstanding = new List<CurrencyAmount>();
        foreach (var loan in loans)
        {
            loanOutstanding.Add(new CurrencyAmount(loan.Currency, await EmployeeSalaryService.GetLoanOutstandingAsync(db, loan.Id, null, ct)));
        }

        var advanceCurrencies = transactions
            .Where(t => !t.IsCancelled && t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance)
            .Select(t => t.Currency)
            .Distinct()
            .ToList();
        var advances = new List<CurrencyAmount>();
        foreach (var currency in advanceCurrencies)
        {
            var amount = await EmployeeSalaryService.GetOutstandingAdvanceAsync(db, employeeId, currency, null, ct);
            if (amount != 0m) advances.Add(new CurrencyAmount(currency, amount));
        }

        var activeContract = await db.EmploymentContracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EmployeeId == employeeId && c.Status == EmploymentContractStatus.Active, ct);
        var user = employee.UserId.HasValue
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == employee.UserId.Value, ct)
            : null;

        return new OffboardingChecklist(
            employee,
            unpaid,
            legacyBalance,
            loanOutstanding.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.Amount))).Where(x => x.Amount != 0m).ToList(),
            advances,
            await leave.GetBalancesAsync(employeeId, AfghanistanBusinessClock.SystemToday.Year, ct),
            activeContract,
            user);
    }

    public async Task TerminateAsync(
        int employeeId,
        DateTime lastWorkingDate,
        string reason,
        string? notes,
        bool acknowledgeOpenItems,
        CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
            throw new BusinessRuleException("HR_TERMINATION_REASON", "دلیلِ پایانِ همکاری الزامی است.");

        var checklist = await GetOffboardingChecklistAsync(employeeId, ct);
        var employee = await db.Employees.FirstAsync(e => e.Id == employeeId, ct);
        if (!employee.IsActive || employee.TerminatedAtUtc.HasValue)
            throw new BusinessRuleException("HR_ALREADY_TERMINATED", "همکاریِ این کارمند قبلاً پایان یافته است.");
        if (lastWorkingDate.Date < employee.HireDate.Date)
            throw new BusinessRuleException("HR_TERMINATION_DATE", "روزِ آخرِ کار پیش از تاریخِ شروعِ کار است.");

        var hasOpenFinance = checklist.UnpaidPayroll.Any(x => x.Amount != 0m)
            || checklist.OutstandingLoans.Any(x => x.Amount != 0m)
            || checklist.OutstandingAdvances.Any(x => x.Amount != 0m);
        if (hasOpenFinance && !acknowledgeOpenItems)
            throw new BusinessRuleException("HR_TERMINATION_OPEN_ITEMS",
                "معاشِ پرداخت‌نشده، قرضه یا مساعدهٔ باز دارد. تسویه کنید یا باز ماندنِ آن‌ها را صریح تأیید کنید.");

        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (checklist.ActiveContract is not null)
            {
                await contracts.TerminateAsync(checklist.ActiveContract.Id, lastWorkingDate, $"پایانِ همکاری: {text}", ct);
            }

            var before = new { employee.IsActive, employee.EndDate };
            employee.IsActive = false;
            employee.EndDate = lastWorkingDate.Date;
            employee.TerminationReason = text.Length > 1000 ? text[..1000] : text;
            employee.TerminationNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            employee.TerminatedAtUtc = DateTime.UtcNow;

            User? disabledUser = null;
            if (employee.UserId.HasValue)
            {
                disabledUser = await db.Users.FirstOrDefaultAsync(u => u.Id == employee.UserId.Value, ct);
                if (disabledUser is { IsActive: true })
                {
                    disabledUser.IsActive = false;
                }
                else
                {
                    disabledUser = null;
                }
            }

            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(Employee), employee.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("IsActive", before.IsActive, employee.IsActive),
                    ("EndDate", before.EndDate, employee.EndDate),
                    ("TerminationReason", (string?)null, employee.TerminationReason),
                    ("OpenItemsAcknowledged", (object?)null, hasOpenFinance ? "true" : "false")));
            if (disabledUser is not null)
            {
                await audit.LogAsync(nameof(User), disabledUser.Id, AuditAction.Update,
                    diff: AuditDiffFormatter.ForUpdate(("IsActive", true, false), ("Reason", (string?)null, $"پایانِ همکاریِ کارمند #{employee.Id}")));
            }

            await db.SaveChangesAsync(ct);
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
