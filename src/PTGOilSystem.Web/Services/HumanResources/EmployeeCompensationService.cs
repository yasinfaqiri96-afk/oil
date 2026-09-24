using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.HumanResources;

public sealed record CompensationChange(
    int EmployeeId,
    DateTime EffectiveFrom,
    decimal BaseSalary,
    string Currency,
    EmployeeSalaryType SalaryType,
    CompensationSource Source,
    int? EmploymentContractId,
    string? Notes);

public interface IEmployeeCompensationService
{
    /// <summary>معاشی که در این روز اعتبار دارد؛ اگر ثبت نشده باشد null.</summary>
    Task<EmployeeCompensation?> GetEffectiveAsync(int employeeId, DateTime date, CancellationToken ct = default);

    /// <summary>
    /// دورهٔ معاشِ قبلی را می‌بندد و سطرِ تازه می‌سازد. SaveChanges را خودش انجام می‌دهد ولی
    /// Transaction باز نمی‌کند؛ صداکننده اگر بیش از این یک کار دارد Transaction را باز کند.
    /// </summary>
    Task<EmployeeCompensation> ChangeAsync(CompensationChange change, CancellationToken ct = default);
}

/// <summary>
/// تاریخچهٔ معاش — تنها مسیرِ تغییرِ معاش. معاشِ قدیمی بازنویسی نمی‌شود. تغییر فقط بعد از آخرین
/// سطرِ ثبت‌شده مجاز است (تاریخِ عقب‌تر یعنی بازنویسیِ گذشته) و در ماهی که معاشش نهایی شده
/// هم مجاز نیست. <see cref="Employee.BaseSalaryAmount"/> آینهٔ معاشِ جاری است و فقط وقتی به‌روز
/// می‌شود که تغییر از امروز یا گذشته اعتبار داشته باشد.
/// </summary>
public sealed class EmployeeCompensationService(ApplicationDbContext db, IAuditService audit) : IEmployeeCompensationService
{
    public Task<EmployeeCompensation?> GetEffectiveAsync(int employeeId, DateTime date, CancellationToken ct = default)
    {
        var day = date.Date;
        return db.EmployeeCompensations.AsNoTracking()
            .Where(c => c.EmployeeId == employeeId
                && c.EffectiveFrom <= day
                && (c.EffectiveTo == null || c.EffectiveTo >= day))
            .OrderByDescending(c => c.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<EmployeeCompensation> ChangeAsync(CompensationChange change, CancellationToken ct = default)
    {
        if (change.BaseSalary < 0m)
            throw new BusinessRuleException("HR_SALARY_NEGATIVE", "معاش نمی‌تواند منفی باشد.");

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == change.EmployeeId, ct)
            ?? throw new BusinessRuleException("EMPLOYEE_NOT_FOUND", "کارمند انتخاب‌شده معتبر نیست.");

        var currency = SystemCurrency.Normalize(change.Currency);
        var hasCurrencies = await db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive, ct);
        if (hasCurrencies && !await db.Currencies.AsNoTracking().AnyAsync(c => c.Code == currency && c.IsActive, ct))
            throw new BusinessRuleException("HR_SALARY_CURRENCY_INVALID", "ارز معاش معتبر نیست.");

        var effectiveFrom = change.EffectiveFrom.Date;
        var latest = await db.EmployeeCompensations
            .Where(c => c.EmployeeId == change.EmployeeId)
            .OrderByDescending(c => c.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        if (latest is not null && effectiveFrom <= latest.EffectiveFrom)
            throw new BusinessRuleException(
                "HR_SALARY_BACKDATED",
                $"تاریخ اعتبارِ معاشِ تازه باید بعد از آخرین تغییر ({latest.EffectiveFrom:yyyy-MM-dd}) باشد. تاریخچهٔ معاش بازنویسی نمی‌شود.");

        await EnsurePeriodNotFinalizedAsync(change.EmployeeId, effectiveFrom, ct);

        var previousTo = latest?.EffectiveTo;
        if (latest is not null)
        {
            latest.EffectiveTo = effectiveFrom.AddDays(-1);
        }

        var record = new EmployeeCompensation
        {
            EmployeeId = change.EmployeeId,
            EffectiveFrom = effectiveFrom,
            BaseSalary = change.BaseSalary,
            Currency = currency,
            SalaryType = change.SalaryType,
            Source = change.Source,
            EmploymentContractId = change.EmploymentContractId,
            Notes = string.IsNullOrWhiteSpace(change.Notes) ? null : change.Notes.Trim()
        };
        db.EmployeeCompensations.Add(record);

        var previousMirror = new { employee.BaseSalaryAmount, employee.SalaryCurrency, employee.SalaryType };
        if (effectiveFrom <= AfghanistanBusinessClock.SystemToday)
        {
            employee.BaseSalaryAmount = record.BaseSalary;
            employee.SalaryCurrency = record.Currency;
            employee.SalaryType = record.SalaryType;
        }

        await db.SaveChangesAsync(ct);

        if (latest is not null)
        {
            await audit.LogAsync(nameof(EmployeeCompensation), latest.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("EffectiveTo", previousTo, latest.EffectiveTo)));
        }

        await audit.LogAsync(nameof(EmployeeCompensation), record.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("EmployeeId", record.EmployeeId),
                ("EffectiveFrom", record.EffectiveFrom),
                ("BaseSalary", record.BaseSalary),
                ("Currency", record.Currency),
                ("SalaryType", record.SalaryType),
                ("Source", record.Source),
                ("EmploymentContractId", record.EmploymentContractId)));

        if (previousMirror.BaseSalaryAmount != employee.BaseSalaryAmount
            || previousMirror.SalaryCurrency != employee.SalaryCurrency
            || previousMirror.SalaryType != employee.SalaryType)
        {
            await audit.LogAsync(nameof(Employee), employee.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("BaseSalaryAmount", previousMirror.BaseSalaryAmount, employee.BaseSalaryAmount),
                    ("SalaryCurrency", previousMirror.SalaryCurrency, employee.SalaryCurrency),
                    ("SalaryType", previousMirror.SalaryType, employee.SalaryType)));
        }

        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>
    /// معاشِ ماهی که نهایی شده تغییر نمی‌کند؛ تغییر باید از ماهِ بعد اعتبار بگیرد یا اول معاشِ
    /// آن ماه بازگشایی شود.
    /// </summary>
    private async Task EnsurePeriodNotFinalizedAsync(int employeeId, DateTime effectiveFrom, CancellationToken ct)
    {
        var finalizedThrough = await PayrollLocks.LatestFinalizedMonthEndAsync(db, employeeId, ct);
        if (finalizedThrough.HasValue && effectiveFrom <= finalizedThrough.Value)
            throw new BusinessRuleException(
                "HR_SALARY_PERIOD_FINALIZED",
                $"معاشِ ماه‌های تا {finalizedThrough.Value:yyyy-MM-dd} نهایی شده است. تغییرِ معاش باید بعد از این تاریخ اعتبار بگیرد.");
    }
}

/// <summary>
/// جایی که «آیا معاشِ این ماه نهایی شده؟» پاسخ داده می‌شود. تا پیش از ماژولِ معاشِ ماهانه فقط
/// «ثبت معاش»های فعال را می‌شناسد (ماهی که معاشش ثبت شده، بسته حساب می‌شود).
/// </summary>
public static class PayrollLocks
{
    public static async Task<DateTime?> LatestFinalizedMonthEndAsync(ApplicationDbContext db, int employeeId, CancellationToken ct)
    {
        var latest = await db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.EmployeeId == employeeId
                && t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual
                && !t.IsCancelled
                && t.SalaryPeriodYear != null
                && t.SalaryPeriodMonth != null)
            .OrderByDescending(t => t.SalaryPeriodYear).ThenByDescending(t => t.SalaryPeriodMonth)
            .Select(t => new { Year = t.SalaryPeriodYear!.Value, Month = t.SalaryPeriodMonth!.Value })
            .FirstOrDefaultAsync(ct);

        // معاشِ ماهانهٔ نهایی‌شده هم قفل است، حتی اگر مبلغِ کارکردِ آن ماه صفر بوده و «ثبت معاش» نساخته.
        var finalized = await db.PayrollRunLines.AsNoTracking()
            .Where(l => l.EmployeeId == employeeId && l.PayrollRun!.Status == PayrollRunStatus.Finalized)
            .OrderByDescending(l => l.PayrollRun!.Year).ThenByDescending(l => l.PayrollRun!.Month)
            .Select(l => new { l.PayrollRun!.Year, l.PayrollRun.Month })
            .FirstOrDefaultAsync(ct);

        DateTime? fromAccrual = latest is null
            ? null
            : new DateTime(latest.Year, latest.Month, DateTime.DaysInMonth(latest.Year, latest.Month));
        DateTime? fromPayroll = finalized is null
            ? null
            : new DateTime(finalized.Year, finalized.Month, DateTime.DaysInMonth(finalized.Year, finalized.Month));

        if (fromAccrual is null) return fromPayroll;
        if (fromPayroll is null) return fromAccrual;
        return fromAccrual > fromPayroll ? fromAccrual : fromPayroll;
    }
}
