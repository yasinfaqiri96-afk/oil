using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.HumanResources;

public sealed record PayrollManualInput(
    decimal OvertimeAmount,
    decimal BonusAmount,
    decimal AllowanceAmount,
    decimal OtherEarning,
    decimal OtherDeduction,
    string? Notes);

public sealed record PayrollGenerateResult(PayrollRun Run, IReadOnlyList<string> SkippedEmployees);

public interface IPayrollService
{
    /// <summary>پیش‌نویسِ ماه را می‌سازد یا دوباره محاسبه می‌کند؛ مقادیرِ دستیِ هر سطر حفظ می‌شود.</summary>
    Task<PayrollGenerateResult> GenerateAsync(int year, int month, CancellationToken ct = default);
    Task UpdateLineAsync(int lineId, PayrollManualInput input, CancellationToken ct = default);
    Task FinalizeAsync(int runId, int? userId, CancellationToken ct = default);
    Task ReopenAsync(int runId, string reason, CancellationToken ct = default);
    Task<EmployeeSalaryTransaction> PayLineAsync(int lineId, decimal amount, int cashAccountId, DateTime paymentDate, string? reference, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, decimal>> GetPaidAmountsAsync(int runId, CancellationToken ct = default);
}

/// <summary>
/// معاشِ ماهانه. قواعد صریح و خوانا در C# — بدونِ موتورِ فرمول:
///
///   معاشِ پایه      ماهانه/قراردادی: معاشِ هر دورهٔ اعتبار × روزهای استخدام در ماه ÷ روزهای ماه
///                   روزانه: نرخ × روزهای حاضر | ساعتی: نرخ × ساعت‌های کارکرده (ورود تا خروج)
///   نرخِ روزانه     ماهانه: معاشِ ماه ÷ «روزهای معاشِ ماه» (تنظیمات)
///   رخصتیِ بدون معاش  روزها × نرخِ روزانه (فقط معاشِ ماهانه — روزمزد در آن روز حاضر نبوده)
///   غیبت            اگر «کسر غیبت» روشن باشد: روزها × نرخِ روزانه (فقط ماهانه)
///   تأخیر           اگر «کسر تأخیر» روشن باشد: دقیقه × نرخِ روزانه ÷ (ساعتِ کاری × ۶۰)
///   ناخالص          پایه + اضافه‌کاری + بونس + امتیاز + درآمدِ دیگر
///   کارکرده         ناخالص − غیبت − تأخیر − رخصتیِ بدون معاش − کسرِ دیگر   ← مصرف و بدهیِ معاش
///   مساعده / قرضه   خودکار، همان ارز، حداکثر تا مبلغِ کارکرده (خالص منفی نمی‌شود)
///   خالص            کارکرده − مساعده − قرضه   ← پولی که باید پرداخت شود
///
/// نهایی‌سازی از مسیرِ موجودِ <see cref="IEmployeeSalaryService"/> ثبت می‌کند: «ثبت معاش» به مبلغِ
/// کارکرده (مصرف فقط همین‌جا)، و وصولِ مساعده/قسطِ قرضه (تهاترِ بدهی با طلب). پرداخت قدمِ جداگانه
/// است؛ نهایی‌شدن یعنی بدهی، نه پرداخت.
/// </summary>
public sealed class PayrollService(
    ApplicationDbContext db,
    IEmployeeSalaryService salaryService,
    IHrCalendarService calendar,
    IAuditService audit) : IPayrollService
{
    public async Task<PayrollGenerateResult> GenerateAsync(int year, int month, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            throw new BusinessRuleException("HR_PAYROLL_PERIOD_INVALID", "ماهِ معاش معتبر نیست.");

        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Year == year && r.Month == month, ct);
        if (run?.Status == PayrollRunStatus.Finalized)
            throw new BusinessRuleException("HR_PAYROLL_FINALIZED", "معاشِ این ماه نهایی شده است. برای تغییر، اول بازگشایی کنید.");

        var isNew = run is null;
        run ??= new PayrollRun { Year = year, Month = month, Status = PayrollRunStatus.Draft };
        if (isNew) db.PayrollRuns.Add(run);

        var (start, end) = MonthRange(year, month);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.HireDate <= end
                && (e.EndDate == null || e.EndDate >= start)
                && (e.IsActive || (e.EndDate != null && e.EndDate >= start)))
            .OrderBy(e => e.FullName)
            .ToListAsync(ct);

        // «ثبت معاش»ِ دستیِ همین ماه (خارج از معاشِ ماهانه) دوباره حساب نمی‌شود.
        var manualAccrualIds = await db.EmployeeSalaryTransactions.AsNoTracking()
            .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual
                && !t.IsCancelled
                && t.SalaryPeriodYear == year
                && t.SalaryPeriodMonth == month
                && t.PayrollRunLineId == null)
            .Select(t => t.EmployeeId)
            .ToListAsync(ct);
        var skipped = employees.Where(e => manualAccrualIds.Contains(e.Id))
            .Select(e => $"{e.FullName}: معاشِ این ماه قبلاً دستی ثبت شده است.")
            .ToList();
        employees = employees.Where(e => !manualAccrualIds.Contains(e.Id)).ToList();

        var inputs = await LoadInputsAsync(employees.Select(e => e.Id).ToList(), start, end, ct);
        var existing = run.Lines.ToDictionary(l => l.EmployeeId);
        var keep = new HashSet<int>();
        foreach (var employee in employees)
        {
            if (!existing.TryGetValue(employee.Id, out var line))
            {
                line = new PayrollRunLine { EmployeeId = employee.Id };
                run.Lines.Add(line);
            }

            Compute(line, employee, year, month, inputs);
            keep.Add(employee.Id);
        }

        // کارمندی که دیگر شاملِ این ماه نیست، از پیش‌نویس کنار می‌رود. سطری که (بعد از بازگشایی)
        // تراکنشِ لغوشده به آن پیوند دارد پاک نمی‌شود تا ردِ آن بماند؛ فقط صفر و علامت‌دار می‌شود.
        var staleLines = run.Lines.Where(l => !keep.Contains(l.EmployeeId)).ToList();
        var staleIds = staleLines.Where(l => l.Id > 0).Select(l => l.Id).ToList();
        var linkedStaleIds = staleIds.Count == 0
            ? new HashSet<int>()
            : (await db.EmployeeSalaryTransactions.AsNoTracking()
                .Where(t => t.PayrollRunLineId != null && staleIds.Contains(t.PayrollRunLineId.Value))
                .Select(t => t.PayrollRunLineId!.Value)
                .ToListAsync(ct)).ToHashSet();
        foreach (var stale in staleLines)
        {
            if (linkedStaleIds.Contains(stale.Id))
            {
                ZeroOut(stale, "این کارمند دیگر شاملِ معاشِ این ماه نیست.");
                continue;
            }

            run.Lines.Remove(stale);
            db.PayrollRunLines.Remove(stale);
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAndSaveAsync(nameof(PayrollRun), run.Id, isNew ? AuditAction.Insert : AuditAction.Update,
            diff: AuditDiffFormatter.ForCreate(
                ("Action", isNew ? "ساخت معاش ماه" : "محاسبهٔ دوبارهٔ معاش ماه"),
                ("Year", run.Year), ("Month", run.Month), ("Lines", run.Lines.Count),
                ("NetByCurrency", string.Join(", ", run.Lines.GroupBy(l => l.Currency).Select(g => $"{g.Key} {g.Sum(l => l.NetSalary):N2}")))));
        return new PayrollGenerateResult(run, skipped);
    }

    public async Task UpdateLineAsync(int lineId, PayrollManualInput input, CancellationToken ct = default)
    {
        if (input.OvertimeAmount < 0 || input.BonusAmount < 0 || input.AllowanceAmount < 0
            || input.OtherEarning < 0 || input.OtherDeduction < 0)
            throw new BusinessRuleException("HR_PAYROLL_NEGATIVE", "مبالغِ دستی نمی‌توانند منفی باشند.");

        var line = await db.PayrollRunLines.Include(l => l.PayrollRun).Include(l => l.Employee)
            .FirstOrDefaultAsync(l => l.Id == lineId, ct)
            ?? throw new BusinessRuleException("HR_PAYROLL_LINE_NOT_FOUND", "سطرِ معاش پیدا نشد.");
        if (line.PayrollRun!.Status != PayrollRunStatus.Draft)
            throw new BusinessRuleException("HR_PAYROLL_FINALIZED", "معاشِ نهایی‌شده ویرایش نمی‌شود.");

        var before = new { line.OvertimeAmount, line.BonusAmount, line.AllowanceAmount, line.OtherEarning, line.OtherDeduction, line.NetSalary };
        line.OvertimeAmount = Round(input.OvertimeAmount);
        line.BonusAmount = Round(input.BonusAmount);
        line.AllowanceAmount = Round(input.AllowanceAmount);
        line.OtherEarning = Round(input.OtherEarning);
        line.OtherDeduction = Round(input.OtherDeduction);
        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        line.Notes = notes?.Length > 1000 ? notes[..1000] : notes;

        var (start, end) = MonthRange(line.PayrollRun.Year, line.PayrollRun.Month);
        var inputs = await LoadInputsAsync([line.EmployeeId], start, end, ct);
        Compute(line, line.Employee!, line.PayrollRun.Year, line.PayrollRun.Month, inputs);
        if (line.EarnedSalary < 0m)
            throw new BusinessRuleException("HR_PAYROLL_NET_NEGATIVE", "کسرِ دیگر از معاشِ کارکرده بیشتر است.");

        await db.SaveChangesAsync(ct);
        await audit.LogAndSaveAsync(nameof(PayrollRunLine), line.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("OvertimeAmount", before.OvertimeAmount, line.OvertimeAmount),
                ("BonusAmount", before.BonusAmount, line.BonusAmount),
                ("AllowanceAmount", before.AllowanceAmount, line.AllowanceAmount),
                ("OtherEarning", before.OtherEarning, line.OtherEarning),
                ("OtherDeduction", before.OtherDeduction, line.OtherDeduction),
                ("NetSalary", before.NetSalary, line.NetSalary)));
    }

    public async Task FinalizeAsync(int runId, int? userId, CancellationToken ct = default)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).ThenInclude(l => l.Employee)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new BusinessRuleException("HR_PAYROLL_NOT_FOUND", "معاشِ ماه پیدا نشد.");
        if (run.Status != PayrollRunStatus.Draft)
            throw new BusinessRuleException("HR_PAYROLL_FINALIZED", "این معاش قبلاً نهایی شده است.");
        if (run.Lines.Count == 0)
            throw new BusinessRuleException("HR_PAYROLL_EMPTY", "معاشِ این ماه سطری ندارد.");

        // ورودی‌ها (حاضری، رخصتی، معاش، مساعده، قرضه) ممکن است بعد از بررسی عوض شده باشند؛ آنچه
        // نهایی می‌شود باید همان باشد که کاربر دیده است.
        var (start, end) = MonthRange(run.Year, run.Month);
        var inputs = await LoadInputsAsync(run.Lines.Select(l => l.EmployeeId).ToList(), start, end, ct);
        var changed = false;
        foreach (var line in run.Lines)
        {
            if (line.GrossSalary == 0m && line.Warning is not null && line.Warning.StartsWith("این کارمند دیگر شامل", StringComparison.Ordinal))
                continue;

            var snapshot = Snapshot(line);
            Compute(line, line.Employee!, run.Year, run.Month, inputs);
            changed |= snapshot != Snapshot(line);
            if (line.EarnedSalary < 0m || line.NetSalary < 0m)
                throw new BusinessRuleException("HR_PAYROLL_NET_NEGATIVE", $"معاشِ {line.Employee!.FullName} منفی می‌شود؛ کسرِ دیگر را اصلاح کنید.");
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            throw new BusinessRuleException("HR_PAYROLL_STALE",
                "بعد از آخرین محاسبه، حاضری/رخصتی/معاش/مساعده تغییر کرده بود. جدول به‌روز شد؛ دوباره بررسی و نهایی کنید.");
        }

        await InTransactionAsync(async () =>
        {
            var monthEnd = end;
            foreach (var line in run.Lines)
            {
                if (line.EarnedSalary > 0m)
                {
                    await salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
                        line.EmployeeId, monthEnd, EmployeeSalaryTransactionType.SalaryAccrual, line.EarnedSalary, line.Currency,
                        null, null, $"PAYROLL-{run.Year:0000}-{run.Month:00}", "معاشِ ماهانه", run.Year, run.Month,
                        PayrollRunLineId: line.Id), ct);
                }

                if (line.AdvanceDeduction > 0m)
                {
                    await salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
                        line.EmployeeId, monthEnd, EmployeeSalaryTransactionType.AdvanceRecovery, line.AdvanceDeduction, line.Currency,
                        null, null, $"PAYROLL-{run.Year:0000}-{run.Month:00}", "وصولِ مساعده از معاشِ ماهانه", null, null,
                        PayrollRunLineId: line.Id), ct);
                }

                if (line.LoanDeduction > 0m)
                {
                    foreach (var (loanId, amount) in AllocateLoanDeduction(line, inputs))
                    {
                        await salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
                            line.EmployeeId, monthEnd, EmployeeSalaryTransactionType.LoanRecovery, amount, line.Currency,
                            null, null, $"PAYROLL-{run.Year:0000}-{run.Month:00}", "قسطِ قرضه از معاشِ ماهانه", null, null,
                            EmployeeLoanId: loanId, PayrollRunLineId: line.Id), ct);
                    }
                }
            }

            run.Status = PayrollRunStatus.Finalized;
            run.FinalizedAtUtc = DateTime.UtcNow;
            run.FinalizedByUserId = userId;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(PayrollRun), run.Id, AuditAction.Approve,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", PayrollRunStatus.Draft, run.Status),
                    ("Revision", (object?)null, run.Revision),
                    ("Lines", (object?)null, run.Lines.Count),
                    ("EarnedByCurrency", (object?)null, string.Join(", ", run.Lines.GroupBy(l => l.Currency).Select(g => $"{g.Key} {g.Sum(l => l.EarnedSalary):N2}")))));
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task ReopenAsync(int runId, string reason, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
            throw new BusinessRuleException("HR_PAYROLL_REOPEN_REASON", "دلیلِ بازگشایی الزامی است.");

        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new BusinessRuleException("HR_PAYROLL_NOT_FOUND", "معاشِ ماه پیدا نشد.");
        if (run.Status != PayrollRunStatus.Finalized)
            throw new BusinessRuleException("HR_PAYROLL_NOT_FINALIZED", "فقط معاشِ نهایی‌شده بازگشایی می‌شود.");

        var lineIds = run.Lines.Select(l => l.Id).ToList();
        var linked = await db.EmployeeSalaryTransactions
            .Where(t => t.PayrollRunLineId != null && lineIds.Contains(t.PayrollRunLineId.Value) && !t.IsCancelled)
            .ToListAsync(ct);
        if (linked.Any(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment))
            throw new BusinessRuleException("HR_PAYROLL_HAS_PAYMENTS",
                "برای این معاش پرداخت ثبت شده است. اول پرداخت‌ها را (با دلیل) لغو کنید، بعد بازگشایی کنید.");

        await InTransactionAsync(async () =>
        {
            // وصول‌ها پیش از «ثبت معاش» برمی‌گردند؛ هیچ سندی پاک نمی‌شود.
            foreach (var transaction in linked.OrderBy(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual ? 1 : 0))
            {
                await salaryService.CancelAsync(transaction.Id, $"بازگشایی معاش {run.Year:0000}/{run.Month:00}: {text}", ct);
            }

            var before = run.Status;
            run.Status = PayrollRunStatus.Draft;
            run.Revision++;
            run.ReopenedAtUtc = DateTime.UtcNow;
            run.LastReopenReason = text.Length > 1000 ? text[..1000] : text;
            run.FinalizedAtUtc = null;
            run.FinalizedByUserId = null;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(nameof(PayrollRun), run.Id, AuditAction.Reverse,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", before, run.Status),
                    ("Revision", run.Revision - 1, run.Revision),
                    ("Reason", (object?)null, run.LastReopenReason),
                    ("ReversedTransactions", (object?)null, linked.Count)));
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task<EmployeeSalaryTransaction> PayLineAsync(
        int lineId,
        decimal amount,
        int cashAccountId,
        DateTime paymentDate,
        string? reference,
        CancellationToken ct = default)
    {
        var line = await db.PayrollRunLines.Include(l => l.PayrollRun).FirstOrDefaultAsync(l => l.Id == lineId, ct)
            ?? throw new BusinessRuleException("HR_PAYROLL_LINE_NOT_FOUND", "سطرِ معاش پیدا نشد.");
        if (line.PayrollRun!.Status != PayrollRunStatus.Finalized)
            throw new BusinessRuleException("HR_PAYROLL_NOT_FINALIZED", "فقط معاشِ نهایی‌شده پرداخت می‌شود.");

        amount = Round(amount);
        if (amount <= 0m)
            throw new BusinessRuleException("HR_PAYROLL_PAY_AMOUNT", "مبلغِ پرداخت باید بیشتر از صفر باشد.");

        var paid = await PaidForLinesAsync([line.Id], ct);
        var remaining = line.NetSalary - paid.GetValueOrDefault(line.Id);
        if (amount > remaining)
            throw new BusinessRuleException("HR_PAYROLL_OVERPAY",
                $"مبلغ ({amount:N2} {line.Currency}) از ماندهٔ پرداخت‌نشده ({remaining:N2} {line.Currency}) بیشتر است.");

        return await salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
            line.EmployeeId, paymentDate, EmployeeSalaryTransactionType.SalaryPayment, amount, line.Currency,
            null, cashAccountId,
            string.IsNullOrWhiteSpace(reference) ? $"PAYROLL-{line.PayrollRun.Year:0000}-{line.PayrollRun.Month:00}" : reference,
            $"پرداختِ معاشِ {line.PayrollRun.Year:0000}/{line.PayrollRun.Month:00}", null, null,
            PayrollRunLineId: line.Id), ct);
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetPaidAmountsAsync(int runId, CancellationToken ct = default)
    {
        var lineIds = await db.PayrollRunLines.AsNoTracking().Where(l => l.PayrollRunId == runId).Select(l => l.Id).ToListAsync(ct);
        return await PaidForLinesAsync(lineIds, ct);
    }

    private async Task<Dictionary<int, decimal>> PaidForLinesAsync(IReadOnlyCollection<int> lineIds, CancellationToken ct)
        => (await db.EmployeeSalaryTransactions.AsNoTracking()
                .Where(t => t.PayrollRunLineId != null
                    && lineIds.Contains(t.PayrollRunLineId.Value)
                    && t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment
                    && !t.IsCancelled)
                .Select(t => new { LineId = t.PayrollRunLineId!.Value, t.Amount })
                .ToListAsync(ct))
            .GroupBy(x => x.LineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

    // ---------------- محاسبه ----------------

    private sealed class PayrollInputs
    {
        public HrSettings Settings { get; init; } = new();
        public IReadOnlySet<DateTime> Holidays { get; init; } = new HashSet<DateTime>();
        public List<EmployeeCompensation> Compensations { get; init; } = [];
        public List<DailyAttendance> Attendance { get; init; } = [];
        public List<LeaveRequest> UnpaidLeave { get; init; } = [];
        public List<EmployeeSalaryTransaction> AdvanceRows { get; init; } = [];
        public List<EmployeeLoan> Loans { get; init; } = [];
        public List<EmployeeSalaryTransaction> LoanRows { get; init; } = [];
    }

    private async Task<PayrollInputs> LoadInputsAsync(IReadOnlyCollection<int> employeeIds, DateTime start, DateTime end, CancellationToken ct)
    {
        var settings = await calendar.GetSettingsAsync(ct);
        return new PayrollInputs
        {
            Settings = settings,
            Holidays = await calendar.HolidaysAsync(start, end, ct),
            Compensations = await db.EmployeeCompensations.AsNoTracking()
                .Where(c => employeeIds.Contains(c.EmployeeId) && c.EffectiveFrom <= end && (c.EffectiveTo == null || c.EffectiveTo >= start))
                .OrderBy(c => c.EffectiveFrom)
                .ToListAsync(ct),
            Attendance = await db.DailyAttendances.AsNoTracking()
                .Where(a => employeeIds.Contains(a.EmployeeId) && a.Date >= start && a.Date <= end)
                .ToListAsync(ct),
            UnpaidLeave = await db.LeaveRequests.AsNoTracking()
                .Where(r => employeeIds.Contains(r.EmployeeId)
                    && r.Status == LeaveRequestStatus.Approved
                    && !r.LeaveType!.IsPaid
                    && r.FromDate <= end && r.ToDate >= start)
                .ToListAsync(ct),
            AdvanceRows = await db.EmployeeSalaryTransactions.AsNoTracking()
                .Where(t => employeeIds.Contains(t.EmployeeId)
                    && !t.IsCancelled
                    && (t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                        || t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery))
                .ToListAsync(ct),
            Loans = await db.EmployeeLoans.AsNoTracking()
                .Where(l => employeeIds.Contains(l.EmployeeId) && l.Status != EmployeeLoanStatus.Cancelled)
                .OrderBy(l => l.LoanDate).ThenBy(l => l.Id)
                .ToListAsync(ct),
            LoanRows = await db.EmployeeSalaryTransactions.AsNoTracking()
                .Where(t => employeeIds.Contains(t.EmployeeId) && t.EmployeeLoanId != null && !t.IsCancelled)
                .ToListAsync(ct)
        };
    }

    private static void Compute(PayrollRunLine line, Employee employee, int year, int month, PayrollInputs inputs)
    {
        var (monthStart, monthEnd) = MonthRange(year, month);
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var windowStart = employee.HireDate.Date > monthStart ? employee.HireDate.Date : monthStart;
        var windowEnd = employee.EndDate.HasValue && employee.EndDate.Value.Date < monthEnd ? employee.EndDate.Value.Date : monthEnd;
        var settings = inputs.Settings;
        var warnings = new List<string>();

        // لغوِ سطرهایی که در این محاسبه حاضر نیستند (در مقابلِ مقادیرِ دستی).
        line.BaseSalary = line.AbsenceDeduction = line.LateDeduction = line.UnpaidLeaveDeduction = 0m;
        line.AdvanceDeduction = line.LoanDeduction = 0m;
        line.AbsentDays = line.UnpaidLeaveDays = line.EmployedDays = 0m;
        line.LateMinutes = 0;

        var segments = inputs.Compensations
            .Where(c => c.EmployeeId == employee.Id && c.EffectiveFrom <= windowEnd && (c.EffectiveTo == null || c.EffectiveTo >= windowStart))
            .OrderBy(c => c.EffectiveFrom)
            .ToList();
        var latest = segments.LastOrDefault();
        if (latest is null)
        {
            line.Currency = SystemCurrency.Normalize(employee.SalaryCurrency);
            line.MonthlySalary = 0m;
            line.DailyRate = 0m;
            warnings.Add("معاشی در تاریخچهٔ معاش برای این ماه ثبت نیست.");
        }
        else
        {
            line.Currency = SystemCurrency.Normalize(latest.Currency);
            line.MonthlySalary = latest.BaseSalary;
            if (segments.Any(s => !string.Equals(SystemCurrency.Normalize(s.Currency), line.Currency, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add("ارزِ معاش در میانهٔ ماه عوض شده؛ فقط دوره‌های همین ارز حساب شد. بررسی کنید.");
                segments = segments.Where(s => string.Equals(SystemCurrency.Normalize(s.Currency), line.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        var attendance = inputs.Attendance.Where(a => a.EmployeeId == employee.Id && a.Date >= windowStart && a.Date <= windowEnd).ToList();
        var salaryType = latest?.SalaryType ?? employee.SalaryType;
        var isMonthly = salaryType is EmployeeSalaryType.Monthly or EmployeeSalaryType.FixedContract;
        var baseSalary = 0m;
        foreach (var segment in segments)
        {
            var from = segment.EffectiveFrom > windowStart ? segment.EffectiveFrom : windowStart;
            var to = segment.EffectiveTo.HasValue && segment.EffectiveTo.Value < windowEnd ? segment.EffectiveTo.Value : windowEnd;
            if (to < from) continue;
            var days = (decimal)((to - from).TotalDays + 1);
            line.EmployedDays += days;
            baseSalary += salaryType switch
            {
                EmployeeSalaryType.Daily => segment.BaseSalary * attendance.Count(a => a.Status == AttendanceStatus.Present && a.Date >= from && a.Date <= to),
                EmployeeSalaryType.Hourly => segment.BaseSalary * (decimal)attendance
                    .Where(a => a.Status == AttendanceStatus.Present && a.Date >= from && a.Date <= to && a.CheckIn.HasValue && a.CheckOut.HasValue)
                    .Sum(a => (a.CheckOut!.Value - a.CheckIn!.Value).TotalHours),
                _ => segment.BaseSalary * days / daysInMonth
            };
        }

        line.BaseSalary = Round(baseSalary);
        var payrollDays = Math.Max(1, settings.PayrollDaysPerMonth);
        var hoursPerDay = settings.WorkingHoursPerDay > 0 ? settings.WorkingHoursPerDay : 8m;
        line.DailyRate = latest is null ? 0m : salaryType switch
        {
            EmployeeSalaryType.Daily => latest.BaseSalary,
            EmployeeSalaryType.Hourly => latest.BaseSalary * hoursPerDay,
            _ => decimal.Round(latest.BaseSalary / payrollDays, 6, MidpointRounding.AwayFromZero)
        };

        if (isMonthly)
        {
            line.UnpaidLeaveDays = UnpaidLeaveDays(employee.Id, windowStart, windowEnd, inputs);
            line.UnpaidLeaveDeduction = Round(line.UnpaidLeaveDays * line.DailyRate);

            line.AbsentDays = attendance.Count(a => a.Status == AttendanceStatus.Absent);
            line.AbsenceDeduction = settings.DeductAbsences ? Round(line.AbsentDays * line.DailyRate) : 0m;
        }

        line.LateMinutes = attendance.Where(a => a.Status == AttendanceStatus.Present).Sum(a => a.MinutesLate ?? 0);
        line.LateDeduction = settings.DeductLateMinutes && salaryType != EmployeeSalaryType.Hourly
            ? Round(line.LateMinutes * line.DailyRate / (hoursPerDay * 60m))
            : 0m;

        line.GrossSalary = line.BaseSalary + line.OvertimeAmount + line.BonusAmount + line.AllowanceAmount + line.OtherEarning;

        // کسرِ کارکرد نمی‌تواند از خودِ کارکرد بیشتر شود.
        var workDeductions = line.AbsenceDeduction + line.LateDeduction + line.UnpaidLeaveDeduction;
        if (workDeductions > line.BaseSalary)
        {
            var scale = line.BaseSalary <= 0m ? 0m : line.BaseSalary / workDeductions;
            line.UnpaidLeaveDeduction = Round(line.UnpaidLeaveDeduction * scale);
            line.AbsenceDeduction = Round(line.AbsenceDeduction * scale);
            line.LateDeduction = Math.Max(0m, line.BaseSalary - line.UnpaidLeaveDeduction - line.AbsenceDeduction);
        }

        var earned = line.EarnedSalary;
        var available = Math.Max(0m, earned);

        // مساعده: فقط همان ارز، فقط آنچه موعدِ وصولش رسیده، و حداکثر تا کارکرد.
        var advanceRows = inputs.AdvanceRows.Where(t => t.EmployeeId == employee.Id).ToList();
        var sameCurrencyAdvances = advanceRows.Where(t => string.Equals(t.Currency, line.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
        var totalOutstanding = sameCurrencyAdvances.Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance).Sum(t => t.Amount)
            - sameCurrencyAdvances.Where(t => t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery && t.PayrollRunLineId != line.Id).Sum(t => t.Amount);
        var dueAdvances = sameCurrencyAdvances
            .Where(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance && IsDue(t, year, month))
            .Sum(t => t.Amount);
        var recovered = sameCurrencyAdvances
            .Where(t => t.TransactionType == EmployeeSalaryTransactionType.AdvanceRecovery && t.PayrollRunLineId != line.Id)
            .Sum(t => t.Amount);
        var dueOutstanding = Math.Max(0m, Math.Min(totalOutstanding, dueAdvances - recovered));
        line.AdvanceDeduction = Math.Min(dueOutstanding, available);
        available -= line.AdvanceDeduction;
        if (advanceRows.Any(t => t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                && !string.Equals(t.Currency, line.Currency, StringComparison.OrdinalIgnoreCase)))
            warnings.Add("مساعده به ارزِ دیگر دارد؛ خودکار کسر نشد.");

        // قرضه: قسطِ هر قرضهٔ فعالِ همین ارز که موعدش رسیده، به ترتیبِ تاریخ.
        foreach (var loan in inputs.Loans.Where(l => l.EmployeeId == employee.Id))
        {
            if (!string.Equals(loan.Currency, line.Currency, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("قرضه به ارزِ دیگر دارد؛ خودکار کسر نشد.");
                continue;
            }

            if (new DateTime(loan.StartRecoveryYear, loan.StartRecoveryMonth, 1) > monthStart)
                continue;

            var outstanding = LoanOutstanding(loan.Id, line.Id, inputs);
            var installment = Math.Min(loan.InstallmentAmount, outstanding);
            var take = Math.Min(installment, available);
            line.LoanDeduction += take;
            available -= take;
        }

        line.TotalDeduction = line.AbsenceDeduction + line.LateDeduction + line.UnpaidLeaveDeduction
            + line.OtherDeduction + line.AdvanceDeduction + line.LoanDeduction;
        line.NetSalary = line.GrossSalary - line.TotalDeduction;
        line.Warning = warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct());
        if (line.Warning?.Length > 1000) line.Warning = line.Warning[..1000];
    }

    private static IEnumerable<(int LoanId, decimal Amount)> AllocateLoanDeduction(PayrollRunLine line, PayrollInputs inputs)
    {
        var (monthStart, _) = MonthRange(line.PayrollRun!.Year, line.PayrollRun.Month);
        var remaining = line.LoanDeduction;
        foreach (var loan in inputs.Loans.Where(l => l.EmployeeId == line.EmployeeId
                     && string.Equals(l.Currency, line.Currency, StringComparison.OrdinalIgnoreCase)
                     && new DateTime(l.StartRecoveryYear, l.StartRecoveryMonth, 1) <= monthStart))
        {
            if (remaining <= 0m) yield break;
            var outstanding = LoanOutstanding(loan.Id, line.Id, inputs);
            var take = Math.Min(Math.Min(loan.InstallmentAmount, outstanding), remaining);
            if (take <= 0m) continue;
            remaining -= take;
            yield return (loan.Id, take);
        }
    }

    private static decimal LoanOutstanding(int loanId, int currentLineId, PayrollInputs inputs)
    {
        var rows = inputs.LoanRows
            .Where(t => t.EmployeeLoanId == loanId && (t.PayrollRunLineId == null || t.PayrollRunLineId != currentLineId))
            .ToList();
        return Math.Max(0m,
            rows.Where(t => t.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement).Sum(t => t.Amount)
            - rows.Where(t => t.TransactionType is EmployeeSalaryTransactionType.LoanRecovery or EmployeeSalaryTransactionType.LoanRepayment).Sum(t => t.Amount));
    }

    private static bool IsDue(EmployeeSalaryTransaction advance, int year, int month)
    {
        var runMonth = new DateTime(year, month, 1);
        if (advance.RecoveryYear.HasValue && advance.RecoveryMonth.HasValue)
            return new DateTime(advance.RecoveryYear.Value, advance.RecoveryMonth.Value, 1) <= runMonth;

        // بدونِ ماهِ وصول: از اولین معاشی که ماهش به تاریخِ مساعده رسیده.
        return new DateTime(advance.TransactionDate.Year, advance.TransactionDate.Month, 1) <= runMonth;
    }

    private static decimal UnpaidLeaveDays(int employeeId, DateTime from, DateTime to, PayrollInputs inputs)
    {
        var offDays = inputs.Settings.OffDays();
        var total = 0m;
        foreach (var request in inputs.UnpaidLeave.Where(r => r.EmployeeId == employeeId))
        {
            var start = request.FromDate > from ? request.FromDate : from;
            var end = request.ToDate < to ? request.ToDate : to;
            for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
            {
                if (offDays.Contains(day.DayOfWeek) || inputs.Holidays.Contains(day))
                    continue;
                total += request.IsHalfDay ? 0.5m : 1m;
            }
        }

        return total;
    }

    private static void ZeroOut(PayrollRunLine line, string warning)
    {
        line.MonthlySalary = line.DailyRate = line.EmployedDays = line.AbsentDays = line.UnpaidLeaveDays = 0m;
        line.LateMinutes = 0;
        line.BaseSalary = line.OvertimeAmount = line.BonusAmount = line.AllowanceAmount = line.OtherEarning = 0m;
        line.AbsenceDeduction = line.LateDeduction = line.UnpaidLeaveDeduction = 0m;
        line.LoanDeduction = line.AdvanceDeduction = line.OtherDeduction = 0m;
        line.GrossSalary = line.TotalDeduction = line.NetSalary = 0m;
        line.Warning = warning;
    }

    private static string Snapshot(PayrollRunLine line)
        => string.Join("|", line.Currency, line.BaseSalary, line.AbsenceDeduction, line.LateDeduction,
            line.UnpaidLeaveDeduction, line.AdvanceDeduction, line.LoanDeduction, line.GrossSalary, line.NetSalary);

    private static (DateTime Start, DateTime End) MonthRange(int year, int month)
        => (new DateTime(year, month, 1), new DateTime(year, month, DateTime.DaysInMonth(year, month)));

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await work();
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
