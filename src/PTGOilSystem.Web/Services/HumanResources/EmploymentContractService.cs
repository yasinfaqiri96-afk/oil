using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.HumanResources;

public sealed record ContractTerms(
    string? ContractNumber,
    EmploymentContractType ContractType,
    DateTime StartDate,
    DateTime? EndDate,
    int? DepartmentId,
    int? PositionId,
    decimal? BaseSalary,
    string? Currency,
    decimal? WorkingDaysPerWeek,
    decimal? WorkingHoursPerDay,
    string? Notes,
    IFormFile? Attachment);

public interface IEmploymentContractService
{
    /// <param name="canSetSalary">
    /// بدونِ «مدیریت معاش» مبلغ از فرم خوانده نمی‌شود و قرارداد معاشِ جاریِ کارمند را می‌گیرد.
    /// </param>
    Task<EmploymentContract> CreateAsync(int employeeId, ContractTerms terms, bool canSetSalary, CancellationToken ct = default);
    Task<EmploymentContract> RenewAsync(int contractId, ContractTerms terms, bool canSetSalary, CancellationToken ct = default);
    Task TerminateAsync(int contractId, DateTime terminatedOn, string reason, CancellationToken ct = default);
    Task UpdateNotesAsync(int contractId, string? notes, IFormFile? attachment, CancellationToken ct = default);
}

/// <summary>
/// قرارداد کاری. شرایطِ ثبت‌شده (تاریخ، معاش، بخش، بست) هرگز ویرایش نمی‌شود؛ «تمدید» قراردادِ
/// تازه‌ای می‌سازد و قبلی را «منقضی» می‌کند، پس تاریخچهٔ کاری کامل می‌ماند. معاشِ قرارداد اگر با
/// معاشِ جاری فرق کند، از طریقِ <see cref="IEmployeeCompensationService"/> — تنها مسیرِ تغییرِ
/// معاش — در تاریخچهٔ معاش ثبت می‌شود. بخش/بستِ قراردادِ جاری روی پروفایلِ کارمند هم می‌نشیند.
/// </summary>
public sealed class EmploymentContractService(
    ApplicationDbContext db,
    IEmployeeCompensationService compensation,
    IHrFileStorage files,
    IAuditService audit) : IEmploymentContractService
{
    public async Task<EmploymentContract> CreateAsync(int employeeId, ContractTerms terms, bool canSetSalary, CancellationToken ct = default)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");
        if (!employee.IsActive)
            throw new BusinessRuleException("EMPLOYEE_INACTIVE", "برای کارمند غیرفعال قرارداد ثبت نمی‌شود.");
        if (await db.EmploymentContracts.AnyAsync(c => c.EmployeeId == employeeId && c.Status == EmploymentContractStatus.Active, ct))
            throw new BusinessRuleException("HR_CONTRACT_ACTIVE_EXISTS", "این کارمند قراردادِ فعال دارد. برای تغییرِ شرایط از «تمدید» استفاده کنید.");

        return await InTransactionAsync(async () =>
        {
            var contract = await BuildAsync(employee, terms, canSetSalary, renewedFrom: null, ct);
            await AuditCreatedAsync(contract, "Create");
            return contract;
        }, ct);
    }

    public async Task<EmploymentContract> RenewAsync(int contractId, ContractTerms terms, bool canSetSalary, CancellationToken ct = default)
    {
        var previous = await db.EmploymentContracts.Include(c => c.Employee).FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new BusinessRuleException("HR_CONTRACT_NOT_FOUND", "قرارداد پیدا نشد.");
        if (previous.Status != EmploymentContractStatus.Active)
            throw new BusinessRuleException("HR_CONTRACT_NOT_ACTIVE", "فقط قراردادِ فعال تمدید می‌شود.");
        if (!previous.Employee!.IsActive)
            throw new BusinessRuleException("EMPLOYEE_INACTIVE", "برای کارمند غیرفعال قرارداد ثبت نمی‌شود.");
        if (terms.StartDate.Date <= previous.StartDate.Date)
            throw new BusinessRuleException("HR_CONTRACT_RENEW_DATE", "قراردادِ تازه باید بعد از شروعِ قراردادِ فعلی شروع شود.");

        return await InTransactionAsync(async () =>
        {
            var before = new { previous.Status, previous.EndDate };
            previous.Status = EmploymentContractStatus.Expired;
            if (!previous.EndDate.HasValue || previous.EndDate.Value.Date >= terms.StartDate.Date)
            {
                previous.EndDate = terms.StartDate.Date.AddDays(-1);
            }
            // قراردادِ قبلی پیش از ساختنِ تازه بسته می‌شود تا قیدِ «یک قراردادِ فعال» نقض نشود.
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(EmploymentContract), previous.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", before.Status, previous.Status),
                    ("EndDate", before.EndDate, previous.EndDate)));

            var terms2 = terms with
            {
                // شمارهٔ خالی در تمدید از قرارداد قبلی ساخته می‌شود.
                ContractNumber = string.IsNullOrWhiteSpace(terms.ContractNumber) ? null : terms.ContractNumber
            };
            var contract = await BuildAsync(previous.Employee, terms2, canSetSalary, previous, ct);
            await AuditCreatedAsync(contract, "Renew");
            return contract;
        }, ct);
    }

    public async Task TerminateAsync(int contractId, DateTime terminatedOn, string reason, CancellationToken ct = default)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalizedReason is null)
            throw new BusinessRuleException("HR_CONTRACT_TERMINATE_REASON", "دلیلِ فسخ قرارداد الزامی است.");

        var contract = await db.EmploymentContracts.FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new BusinessRuleException("HR_CONTRACT_NOT_FOUND", "قرارداد پیدا نشد.");
        if (contract.Status != EmploymentContractStatus.Active)
            throw new BusinessRuleException("HR_CONTRACT_NOT_ACTIVE", "فقط قراردادِ فعال فسخ می‌شود.");
        if (terminatedOn.Date < contract.StartDate.Date)
            throw new BusinessRuleException("HR_CONTRACT_TERMINATE_DATE", "تاریخِ فسخ پیش از شروعِ قرارداد است.");

        contract.Status = EmploymentContractStatus.Terminated;
        contract.TerminatedOn = terminatedOn.Date;
        contract.TerminationReason = normalizedReason.Length > 1000 ? normalizedReason[..1000] : normalizedReason;
        await db.SaveChangesAsync(ct);
        await audit.LogAndSaveAsync(nameof(EmploymentContract), contract.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("Status", EmploymentContractStatus.Active, contract.Status),
                ("TerminatedOn", (DateTime?)null, contract.TerminatedOn),
                ("TerminationReason", (string?)null, contract.TerminationReason)));
    }

    public async Task UpdateNotesAsync(int contractId, string? notes, IFormFile? attachment, CancellationToken ct = default)
    {
        var contract = await db.EmploymentContracts.FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new BusinessRuleException("HR_CONTRACT_NOT_FOUND", "قرارداد پیدا نشد.");

        var before = new { contract.Notes, contract.AttachmentId };
        contract.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (attachment is { Length: > 0 })
        {
            // فایلِ قبلی پاک نمی‌شود؛ پیوست فقط به فایلِ تازه اشاره می‌کند.
            contract.Attachment = await files.SaveAsync(contract.EmployeeId, HrAttachmentOwner.EmploymentContract, attachment, ct);
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAndSaveAsync(nameof(EmploymentContract), contract.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("Notes", before.Notes, contract.Notes),
                ("AttachmentId", before.AttachmentId, contract.AttachmentId)));
    }

    private async Task<EmploymentContract> BuildAsync(
        Employee employee,
        ContractTerms terms,
        bool canSetSalary,
        EmploymentContract? renewedFrom,
        CancellationToken ct)
    {
        var start = terms.StartDate.Date;
        var end = terms.EndDate?.Date;
        if (end.HasValue && end.Value < start)
            throw new BusinessRuleException("HR_CONTRACT_DATES", "تاریخِ پایانِ قرارداد پیش از شروع است.");
        if (terms.WorkingDaysPerWeek is < 0 or > 7)
            throw new BusinessRuleException("HR_CONTRACT_DAYS", "روزهای کاری در هفته باید بین ۰ و ۷ باشد.");
        if (terms.WorkingHoursPerDay is < 0 or > 24)
            throw new BusinessRuleException("HR_CONTRACT_HOURS", "ساعت کاری در روز باید بین ۰ و ۲۴ باشد.");

        Department? department = null;
        if (terms.DepartmentId.HasValue)
        {
            department = await db.Departments.FirstOrDefaultAsync(d => d.Id == terms.DepartmentId.Value && d.IsActive, ct)
                ?? throw new BusinessRuleException("HR_DEPARTMENT_INVALID", "بخش انتخاب‌شده معتبر یا فعال نیست.");
        }

        Position? position = null;
        if (terms.PositionId.HasValue)
        {
            position = await db.Positions.FirstOrDefaultAsync(p => p.Id == terms.PositionId.Value && p.IsActive, ct)
                ?? throw new BusinessRuleException("HR_POSITION_INVALID", "بست انتخاب‌شده معتبر یا فعال نیست.");
        }

        var current = await compensation.GetEffectiveAsync(employee.Id, start, ct);
        decimal salary;
        string currency;
        if (canSetSalary && terms.BaseSalary.HasValue)
        {
            salary = terms.BaseSalary.Value;
            currency = SystemCurrency.Normalize(string.IsNullOrWhiteSpace(terms.Currency) ? employee.SalaryCurrency : terms.Currency);
        }
        else
        {
            salary = current?.BaseSalary ?? renewedFrom?.BaseSalary ?? 0m;
            currency = current?.Currency ?? renewedFrom?.Currency ?? SystemCurrency.BaseCurrencyCode;
        }

        if (salary < 0m)
            throw new BusinessRuleException("HR_SALARY_NEGATIVE", "معاش نمی‌تواند منفی باشد.");

        var number = string.IsNullOrWhiteSpace(terms.ContractNumber)
            ? await NextNumberAsync(employee, start, ct)
            : terms.ContractNumber.Trim();
        if (await db.EmploymentContracts.AnyAsync(c => c.ContractNumber == number, ct))
            throw new BusinessRuleException("HR_CONTRACT_NUMBER_DUPLICATE", "این شمارهٔ قرارداد قبلاً ثبت شده است.");

        var contract = new EmploymentContract
        {
            EmployeeId = employee.Id,
            ContractNumber = number,
            ContractType = terms.ContractType,
            StartDate = start,
            EndDate = end,
            DepartmentId = department?.Id,
            PositionId = position?.Id,
            BaseSalary = salary,
            Currency = currency,
            WorkingDaysPerWeek = terms.WorkingDaysPerWeek,
            WorkingHoursPerDay = terms.WorkingHoursPerDay,
            Notes = string.IsNullOrWhiteSpace(terms.Notes) ? null : terms.Notes.Trim(),
            Status = EmploymentContractStatus.Active,
            RenewedFromContractId = renewedFrom?.Id
        };

        if (terms.Attachment is { Length: > 0 })
        {
            contract.Attachment = await files.SaveAsync(employee.Id, HrAttachmentOwner.EmploymentContract, terms.Attachment, ct);
        }

        db.EmploymentContracts.Add(contract);
        await db.SaveChangesAsync(ct);

        // معاش فقط وقتی تاریخچه می‌گیرد که واقعاً تغییر کرده باشد.
        var salaryChanged = current is null
            || current.BaseSalary != salary
            || !string.Equals(current.Currency, currency, StringComparison.OrdinalIgnoreCase);
        if (canSetSalary && salaryChanged)
        {
            await compensation.ChangeAsync(new CompensationChange(
                employee.Id,
                start,
                salary,
                currency,
                current?.SalaryType ?? employee.SalaryType,
                CompensationSource.Contract,
                contract.Id,
                $"قرارداد {contract.ContractNumber}"), ct);
        }

        // بخش/بستِ قراردادِ جاری روی پروفایل می‌نشیند (همراهِ متنِ قدیمی برای سازگاری).
        if (start <= AfghanistanBusinessClock.SystemToday)
        {
            if (department is not null)
            {
                employee.DepartmentId = department.Id;
                employee.Department = department.Name;
            }

            if (position is not null)
            {
                employee.PositionId = position.Id;
                employee.JobTitle = position.Name;
            }

            await db.SaveChangesAsync(ct);
        }

        return contract;
    }

    private async Task<string> NextNumberAsync(Employee employee, DateTime start, CancellationToken ct)
    {
        var baseNumber = $"HR-{employee.EmployeeCode}-{start:yyyyMMdd}";
        var candidate = baseNumber;
        for (var i = 2; await db.EmploymentContracts.AnyAsync(c => c.ContractNumber == candidate, ct); i++)
        {
            candidate = $"{baseNumber}-{i}";
        }

        return candidate.Length > 50 ? candidate[..50] : candidate;
    }

    private Task AuditCreatedAsync(EmploymentContract contract, string kind)
        => audit.LogAndSaveAsync(nameof(EmploymentContract), contract.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Kind", kind),
                ("EmployeeId", contract.EmployeeId),
                ("ContractNumber", contract.ContractNumber),
                ("ContractType", contract.ContractType),
                ("StartDate", contract.StartDate),
                ("EndDate", contract.EndDate),
                ("DepartmentId", contract.DepartmentId),
                ("PositionId", contract.PositionId),
                ("BaseSalary", contract.BaseSalary),
                ("Currency", contract.Currency),
                ("RenewedFromContractId", contract.RenewedFromContractId)));

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
