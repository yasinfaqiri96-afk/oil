using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.HumanResources;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class EmployeesController : Controller
{
    private const int IndexPageSize = 20;
    private const long MaxEmployeePhotoBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> AllowedEmployeePhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEmployeeSalaryService _salaryService;
    private readonly IWebHostEnvironment _environment;
    private readonly IPartyStatementReadService _partyStatements;
    private readonly IEmployeeCompensationService _compensation;
    private readonly ILeaveService _leave;
    private readonly IEmployeeRecordsService _records;
    private readonly IHrFileStorage _files;

    public EmployeesController(
        ApplicationDbContext db,
        IAuditService audit,
        IEmployeeSalaryService salaryService,
        IWebHostEnvironment environment,
        IPartyStatementReadService? partyStatements = null,
        IEmployeeCompensationService? compensation = null,
        ILeaveService? leave = null,
        IEmployeeRecordsService? records = null,
        IHrFileStorage? files = null)
    {
        _db = db;
        _audit = audit;
        _salaryService = salaryService;
        _environment = environment;
        _partyStatements = partyStatements ?? PartyStatementReadService.CreateDefault(db);
        _compensation = compensation ?? new EmployeeCompensationService(db, audit);
        _files = files ?? new HrFileStorage(db, environment);
        _leave = leave ?? new LeaveService(db, new HrCalendarService(db), _files, audit);
        _records = records ?? new EmployeeRecordsService(db, _files, _leave,
            new EmploymentContractService(db, _compensation, _files, audit), audit);
    }

    // کنترلرِ ساخته‌شده در تست HttpContext ندارد؛ بدون کاربر هیچ دسترسیِ معاشی فرض نمی‌شود.
    private bool CanViewSalary => User is not null && RoleAccessRules.CanViewEmployeeSalary(User);
    private bool CanManageSalary => User is not null && RoleAccessRules.CanManageEmployeeSalary(User);
    private bool CanPaySalary => User is not null && RoleAccessRules.CanPaySalary(User);

    private bool CanRecord(EmployeeSalaryTransactionType type)
        => EmployeeSalaryTransactionTypeLabels.IsCashType(type) ? CanPaySalary : CanManageSalary;

    public async Task<IActionResult> Index([FromQuery] EmployeeIndexFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var canViewSalary = CanViewSalary;
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        filter ??= new EmployeeIndexFilterViewModel();
        NormalizeFilter(filter);
        await PopulateLookupsAsync(filter: filter);

        var query = _db.Employees
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var keyword = filter.Query.Trim();
            query = query.Where(e =>
                e.EmployeeCode.Contains(keyword)
                || e.FullName.Contains(keyword)
                || (e.Phone != null && e.Phone.Contains(keyword))
                || (e.JobTitle != null && e.JobTitle.Contains(keyword))
                || (e.Department != null && e.Department.Contains(keyword)));
        }

        // چندانتخابی: OR بین مقادیرِ یک فیلتر، AND بین فیلترهای مختلف.
        if (filter.EmployeeType.Length > 0)
        {
            var employeeTypes = filter.EmployeeType;
            query = query.Where(e => employeeTypes.Contains(e.EmployeeType));
        }

        if (filter.SalaryType.Length > 0)
        {
            var salaryTypes = filter.SalaryType;
            query = query.Where(e => salaryTypes.Contains(e.SalaryType));
        }

        if (!string.IsNullOrWhiteSpace(filter.Department))
        {
            query = query.Where(e => e.Department != null && e.Department.Contains(filter.Department));
        }

        if (filter.DepartmentId.Length > 0)
        {
            var departmentIds = filter.DepartmentId;
            query = query.Where(e => e.DepartmentId != null && departmentIds.Contains(e.DepartmentId.Value));
        }

        if (filter.IsActive.HasValue)
        {
            query = query.Where(e => e.IsActive == filter.IsActive.Value);
        }

        if (canViewSalary && !string.IsNullOrWhiteSpace(filter.Currency))
        {
            query = query.Where(e => e.SalaryCurrency == filter.Currency);
        }

        var totalCount = await query.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var employees = await (page <= 0
                ? query.OrderByDescending(e => e.IsActive).ThenBy(e => e.EmployeeCode)
                : query
                    .OrderByDescending(e => e.IsActive)
                    .ThenBy(e => e.EmployeeCode)
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(e => new
            {
                e.Id,
                e.EmployeeCode,
                e.FullName,
                e.Phone,
                e.PhotoPath,
                e.JobTitle,
                e.Department,
                e.EmployeeType,
                e.SalaryType,
                e.BaseSalaryAmount,
                e.SalaryCurrency,
                e.IsActive,
                BalanceUsd = !canViewSalary ? 0m : e.SalaryTransactions
                    .Where(t => !t.IsCancelled)
                    .Sum(t => (decimal?)(t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual
                        || t.TransactionType == EmployeeSalaryTransactionType.Bonus
                        || t.TransactionType == EmployeeSalaryTransactionType.Adjustment
                        || t.TransactionType == EmployeeSalaryTransactionType.LoanRepayment
                            ? t.AmountUsd
                            : t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment
                              || t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                              || t.TransactionType == EmployeeSalaryTransactionType.SalaryDeduction
                              || t.TransactionType == EmployeeSalaryTransactionType.LoanDisbursement
                                ? -t.AmountUsd
                                : 0m)) ?? 0m
            })
            .ToListAsync();

        var items = employees
            .Select(e =>
            {
                return new EmployeeIndexItemViewModel
                {
                    Id = e.Id,
                    EmployeeCode = e.EmployeeCode,
                    FullName = e.FullName,
                    Phone = e.Phone,
                    PhotoPath = e.PhotoPath,
                    JobTitle = e.JobTitle,
                    Department = e.Department,
                    EmployeeType = e.EmployeeType,
                    EmployeeTypeName = EmployeeTypeLabels.ToPersian(e.EmployeeType),
                    SalaryType = e.SalaryType,
                    SalaryTypeName = EmployeeSalaryTypeLabels.ToPersian(e.SalaryType),
                    BaseSalaryAmount = canViewSalary ? e.BaseSalaryAmount : 0m,
                    SalaryCurrency = canViewSalary ? e.SalaryCurrency : "",
                    IsActive = e.IsActive,
                    BalanceUsd = canViewSalary ? e.BalanceUsd : 0m
                };
            })
            .ToList();

        return View(new EmployeeIndexViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount,
            CanViewSalary = canViewSalary
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new EmployeeFormViewModel
        {
            HireDate = AfghanistanBusinessClock.SystemToday,
            SalaryCurrency = SystemCurrency.BaseCurrencyCode,
            IsActive = true
        };
        await PopulateLookupsAsync(form: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] EmployeeFormViewModel model, string? returnUrl = null)
    {
        NormalizeForm(model);
        ValidateEmployeePhoto(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        if (await _db.Employees.AnyAsync(e => e.EmployeeCode == model.EmployeeCode))
        {
            ModelState.AddModelError(nameof(model.EmployeeCode), "این کد کارمند قبلاً ثبت شده است.");
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        var employee = new Employee();
        ApplyForm(employee, model);
        if (!await ApplyOrganizationAsync(employee, model))
        {
            await PopulateLookupsAsync(form: model);
            return View(model);
        }
        if (!CanManageSalary)
        {
            // معاش را فقط «مدیریت معاش» تعیین می‌کند؛ ثبت‌کنندهٔ مشخصات معاش را نمی‌بیند و نمی‌گذارد.
            employee.BaseSalaryAmount = 0m;
            employee.SalaryCurrency = SystemCurrency.BaseCurrencyCode;
        }
        employee.PhotoPath = await SaveEmployeePhotoAsync(model.PhotoFile);
        var initialSalary = employee.BaseSalaryAmount;
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();

        // اولین سطرِ تاریخچهٔ معاش از تاریخِ شروعِ کار؛ از این پس معاش فقط از «تغییر معاش» عوض می‌شود.
        if (CanManageSalary && initialSalary > 0m)
        {
            await _compensation.ChangeAsync(new CompensationChange(
                employee.Id,
                employee.HireDate,
                initialSalary,
                employee.SalaryCurrency,
                employee.SalaryType,
                CompensationSource.Manual,
                null,
                "معاشِ اولیه هنگام ثبت کارمند"));
        }

        await _audit.LogAsync(
            nameof(Employee),
            employee.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("EmployeeCode", employee.EmployeeCode),
                ("FullName", employee.FullName),
                ("PhotoPath", employee.PhotoPath),
                ("EmployeeType", employee.EmployeeType),
                ("SalaryType", employee.SalaryType),
                ("BaseSalaryAmount", employee.BaseSalaryAmount),
                ("SalaryCurrency", employee.SalaryCurrency),
                ("HireDate", employee.HireDate),
                ("DepartmentId", employee.DepartmentId),
                ("PositionId", employee.PositionId),
                ("IsActive", employee.IsActive)));
        await _db.SaveChangesAsync();

        TempData["ok"] = "کارمند با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Details), new { id = employee.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        var model = ToForm(employee);
        if (!CanManageSalary)
        {
            model.BaseSalaryAmount = 0m;
        }
        await PopulateLookupsAsync(form: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Employees.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Employee), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [FromForm] EmployeeFormViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        NormalizeForm(model);
        ValidateEmployeePhoto(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        if (await _db.Employees.AnyAsync(e => e.Id != id && e.EmployeeCode == model.EmployeeCode))
        {
            ModelState.AddModelError(nameof(model.EmployeeCode), "این کد کارمند قبلاً برای کارمند دیگری ثبت شده است.");
            await PopulateLookupsAsync(form: model);
            return View(model);
        }

        var previous = new
        {
            employee.EmployeeCode,
            employee.FullName,
            employee.FatherName,
            employee.Phone,
            employee.Email,
            employee.PhotoPath,
            employee.NationalId,
            employee.Address,
            employee.JobTitle,
            employee.Department,
            employee.DepartmentId,
            employee.PositionId,
            employee.UserId,
            employee.EmployeeType,
            employee.SalaryType,
            employee.BaseSalaryAmount,
            employee.SalaryCurrency,
            employee.HireDate,
            employee.EndDate,
            employee.IsActive,
            employee.Notes
        };

        ApplyForm(employee, model);
        if (!await ApplyOrganizationAsync(employee, model))
        {
            await PopulateLookupsAsync(form: model);
            return View(model);
        }
        // معاش از فرمِ ویرایش عوض نمی‌شود: تنها مسیرش «تغییر معاش» است که تاریخچه نگه می‌دارد.
        employee.BaseSalaryAmount = previous.BaseSalaryAmount;
        employee.SalaryCurrency = previous.SalaryCurrency;
        employee.SalaryType = previous.SalaryType;
        var previousPhotoPath = employee.PhotoPath;
        var uploadedPhotoPath = await SaveEmployeePhotoAsync(model.PhotoFile);
        if (!string.IsNullOrWhiteSpace(uploadedPhotoPath))
        {
            employee.PhotoPath = uploadedPhotoPath;
        }

        await _audit.LogAsync(
            nameof(Employee),
            employee.Id,
            AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("EmployeeCode", previous.EmployeeCode, employee.EmployeeCode),
                ("FullName", previous.FullName, employee.FullName),
                ("FatherName", previous.FatherName, employee.FatherName),
                ("Phone", previous.Phone, employee.Phone),
                ("Email", previous.Email, employee.Email),
                ("PhotoPath", previous.PhotoPath, employee.PhotoPath),
                ("NationalId", previous.NationalId, employee.NationalId),
                ("Address", previous.Address, employee.Address),
                ("JobTitle", previous.JobTitle, employee.JobTitle),
                ("Department", previous.Department, employee.Department),
                ("DepartmentId", previous.DepartmentId, employee.DepartmentId),
                ("PositionId", previous.PositionId, employee.PositionId),
                ("UserId", previous.UserId, employee.UserId),
                ("EmployeeType", previous.EmployeeType, employee.EmployeeType),
                ("SalaryType", previous.SalaryType, employee.SalaryType),
                ("BaseSalaryAmount", previous.BaseSalaryAmount, employee.BaseSalaryAmount),
                ("SalaryCurrency", previous.SalaryCurrency, employee.SalaryCurrency),
                ("HireDate", previous.HireDate, employee.HireDate),
                ("EndDate", previous.EndDate, employee.EndDate),
                ("IsActive", previous.IsActive, employee.IsActive),
                ("Notes", previous.Notes, employee.Notes)));

        await _db.SaveChangesAsync();
        if (!string.IsNullOrWhiteSpace(uploadedPhotoPath))
        {
            DeleteEmployeePhoto(previousPhotoPath);
        }

        TempData["ok"] = "اطلاعات کارمند ویرایش شد.";
        return RedirectToAction(nameof(Details), new { id = employee.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var canViewSalary = CanViewSalary;
        var employee = await _db.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        var transactions = !canViewSalary ? [] : await _db.EmployeeSalaryTransactions
            .AsNoTracking()
            .Include(t => t.CashAccount)
            .Include(t => t.PaymentTransaction)
            .Include(t => t.LedgerEntry)
            .Where(t => t.EmployeeId == id)
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.Id)
            .ToListAsync();

        var transactionIds = transactions.Select(t => t.Id).ToList();
        var roznamchaPayments = !canViewSalary ? [] : await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.CashAccount)
            .Include(p => p.Contract)
            .Include(p => p.Shipment)
            .Where(p => p.EmployeeId == id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Take(50)
            .Select(p => new PaymentListItemViewModel
            {
                Id = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                DirectionName = PaymentDirectionLabels.ToPersian(p.Direction),
                PaymentKind = p.PaymentKind,
                PaymentKindName = PaymentKindLabels.ToPersian(p.PaymentKind),
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : string.Empty,
                CashAccountCurrency = p.CashAccount != null ? p.CashAccount.Currency : p.Currency,
                CounterpartyTypeName = PaymentCounterpartyTypeLabels.ToPersian(PaymentCounterpartyType.Employee),
                CounterpartyName = employee.FullName,
                ContractNumber = p.Contract != null ? p.Contract.ContractNumber : null,
                ShipmentCode = p.Shipment != null ? p.Shipment.ShipmentCode : null,
                RelatedTo = p.Contract != null ? p.Contract.ContractNumber : p.Shipment != null ? p.Shipment.ShipmentCode : "—",
                Description = p.Description,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                LedgerEntryId = p.LedgerEntryId
            })
            .ToListAsync();

        // سندِ اصلی و سندِ معکوسِ لغو هر دو در جدول تراکنش‌های معاش دیده می‌شوند، نه اینجا.
        var salaryPaymentIds = transactions
            .SelectMany(t => new[] { t.PaymentTransactionId, t.ReversalPaymentTransactionId })
            .Where(paymentId => paymentId.HasValue)
            .Select(paymentId => paymentId!.Value)
            .ToHashSet();
        roznamchaPayments = roznamchaPayments
            .Where(p => !salaryPaymentIds.Contains(p.Id))
            .ToList();

        var auditItems = await _db.AuditLogs
            .AsNoTracking()
            .Where(a =>
                (a.EntityName == nameof(Employee) && a.EntityId == id)
                || (a.EntityName == nameof(EmployeeSalaryTransaction) && transactionIds.Contains(a.EntityId)))
            .OrderByDescending(a => a.ActionAtUtc)
            .Take(50)
            .Select(a => new EmployeeAuditItemViewModel
            {
                ActionAtUtc = a.ActionAtUtc,
                Action = a.Action,
                ActorUsername = a.ActorUsername,
                Description = a.Description,
                // جزئیاتِ تغییر ممکن است مبلغ معاش داشته باشد.
                Diff = canViewSalary ? a.Diff : null
            })
            .ToListAsync();

        // ---- پروفایلِ یکپارچه: قرارداد، حاضری، رخصتی، معاش، قرضه، اسناد ----
        var today = AfghanistanBusinessClock.SystemToday;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var departmentName = employee.DepartmentId.HasValue
            ? await _db.Departments.AsNoTracking().Where(d => d.Id == employee.DepartmentId).Select(d => d.Name).FirstOrDefaultAsync()
            : employee.Department;
        var positionName = employee.PositionId.HasValue
            ? await _db.Positions.AsNoTracking().Where(p => p.Id == employee.PositionId).Select(p => p.Name).FirstOrDefaultAsync()
            : employee.JobTitle;
        var contracts = await _db.EmploymentContracts.AsNoTracking()
            .Include(c => c.Department).Include(c => c.Position)
            .Where(c => c.EmployeeId == id)
            .OrderByDescending(c => c.StartDate)
            .ToListAsync();
        var monthAttendance = await _db.DailyAttendances.AsNoTracking()
            .Where(a => a.EmployeeId == id && a.Date >= monthStart && a.Date <= today)
            .Select(a => new { a.Status, a.MinutesLate })
            .ToListAsync();
        var recentAttendance = await _db.DailyAttendances.AsNoTracking()
            .Where(a => a.EmployeeId == id)
            .OrderByDescending(a => a.Date)
            .Take(15)
            .ToListAsync();
        var leaveRequests = await _db.LeaveRequests.AsNoTracking()
            .Include(r => r.LeaveType)
            .Where(r => r.EmployeeId == id)
            .OrderByDescending(r => r.FromDate)
            .Take(30)
            .ToListAsync();
        var leaveBalances = await _leave.GetBalancesAsync(id, today.Year);
        var documents = await _db.EmployeeDocuments.AsNoTracking()
            .Include(d => d.Attachment)
            .Where(d => d.EmployeeId == id)
            .OrderBy(d => d.IsDeleted).ThenBy(d => d.DocumentType).ThenByDescending(d => d.CreatedAtUtc)
            .ToListAsync();
        var linkedUsername = employee.UserId.HasValue
            ? await _db.Users.AsNoTracking().Where(u => u.Id == employee.UserId).Select(u => u.Username).FirstOrDefaultAsync()
            : null;

        var compensationHistory = new List<EmployeeCompensation>();
        var payrollLines = new List<EmployeePayrollLineItem>();
        var loans = new List<EmployeeLoanItem>();
        var outstandingAdvances = new List<CurrencyAmount>();
        if (canViewSalary)
        {
            compensationHistory = await _db.EmployeeCompensations.AsNoTracking()
                .Where(c => c.EmployeeId == id)
                .OrderByDescending(c => c.EffectiveFrom)
                .ToListAsync();
            var lines = await _db.PayrollRunLines.AsNoTracking()
                .Where(l => l.EmployeeId == id)
                .OrderByDescending(l => l.PayrollRun!.Year).ThenByDescending(l => l.PayrollRun!.Month)
                .Take(24)
                .Select(l => new { l.Id, l.PayrollRun!.Year, l.PayrollRun.Month, l.PayrollRun.Status, l.Currency, l.GrossSalary, l.TotalDeduction, l.NetSalary })
                .ToListAsync();
            var paidByLine = transactions
                .Where(t => t.PayrollRunLineId.HasValue && !t.IsCancelled && t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment)
                .GroupBy(t => t.PayrollRunLineId!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
            payrollLines = lines.Select(l => new EmployeePayrollLineItem
            {
                Year = l.Year,
                Month = l.Month,
                Status = l.Status,
                Currency = l.Currency,
                Gross = l.GrossSalary,
                Deductions = l.TotalDeduction,
                Net = l.NetSalary,
                Paid = paidByLine.GetValueOrDefault(l.Id)
            }).ToList();

            var loanRows = await _db.EmployeeLoans.AsNoTracking().Where(l => l.EmployeeId == id).OrderByDescending(l => l.LoanDate).ToListAsync();
            foreach (var loan in loanRows)
            {
                loans.Add(new EmployeeLoanItem
                {
                    Id = loan.Id,
                    LoanDate = loan.LoanDate,
                    Principal = loan.PrincipalAmount,
                    Currency = loan.Currency,
                    Installment = loan.InstallmentAmount,
                    Status = loan.Status,
                    Outstanding = loan.Status == EmployeeLoanStatus.Cancelled ? 0m : await EmployeeSalaryService.GetLoanOutstandingAsync(_db, loan.Id, null, default)
                });
            }

            foreach (var currency in transactions.Where(t => !t.IsCancelled && t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance).Select(t => t.Currency).Distinct())
            {
                var amount = await EmployeeSalaryService.GetOutstandingAdvanceAsync(_db, id, currency, null, default);
                if (amount != 0m) outstandingAdvances.Add(new CurrencyAmount(currency, amount));
            }
        }

        var model = new EmployeeDetailsViewModel
        {
            Id = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            FullName = employee.FullName,
            FatherName = employee.FatherName,
            Phone = employee.Phone,
            Email = employee.Email,
            PhotoPath = employee.PhotoPath,
            NationalId = employee.NationalId,
            Address = employee.Address,
            JobTitle = employee.JobTitle,
            Department = employee.Department,
            EmployeeType = employee.EmployeeType,
            EmployeeTypeName = EmployeeTypeLabels.ToPersian(employee.EmployeeType),
            SalaryType = employee.SalaryType,
            SalaryTypeName = EmployeeSalaryTypeLabels.ToPersian(employee.SalaryType),
            BaseSalaryAmount = canViewSalary ? employee.BaseSalaryAmount : 0m,
            SalaryCurrency = canViewSalary ? employee.SalaryCurrency : "",
            HireDate = employee.HireDate,
            EndDate = employee.EndDate,
            IsActive = employee.IsActive,
            Notes = employee.Notes,
            Summary = EmployeeSalarySummaryCalculator.FromTransactions(transactions),
            Transactions = transactions.Select(ToTransactionListItem).ToList(),
            RoznamchaPayments = roznamchaPayments,
            AuditItems = auditItems,
            CanViewSalary = canViewSalary,
            CanManageSalary = CanManageSalary,
            CanPaySalary = CanPaySalary,
            CanManageData = User is not null && RoleAccessRules.CanManageData(User),
            DepartmentName = departmentName,
            PositionName = positionName,
            TerminationReason = employee.TerminationReason,
            LinkedUsername = linkedUsername,
            CurrentCompensation = compensationHistory.FirstOrDefault(c => c.EffectiveFrom <= today && (c.EffectiveTo == null || c.EffectiveTo >= today)),
            CompensationHistory = compensationHistory,
            ActiveContract = contracts.FirstOrDefault(c => c.Status == EmploymentContractStatus.Active),
            Contracts = contracts,
            AttendanceThisMonth = new EmployeeAttendanceSummary
            {
                Present = monthAttendance.Count(a => a.Status == AttendanceStatus.Present),
                Absent = monthAttendance.Count(a => a.Status == AttendanceStatus.Absent),
                Leave = monthAttendance.Count(a => a.Status == AttendanceStatus.Leave),
                Late = monthAttendance.Count(a => a.MinutesLate > 0)
            },
            RecentAttendance = recentAttendance,
            LeaveBalances = leaveBalances,
            LeaveRequests = leaveRequests,
            PayrollLines = payrollLines,
            Loans = loans,
            OutstandingAdvances = outstandingAdvances,
            UnpaidPayroll = payrollLines
                .Where(l => l.Status == PayrollRunStatus.Finalized)
                .GroupBy(l => l.Currency)
                .Select(g => new CurrencyAmount(g.Key, g.Sum(l => l.Net - l.Paid)))
                .Where(x => x.Amount != 0m)
                .ToList(),
            Documents = documents
        };
        if (canViewSalary)
        {
            var statement = await _partyStatements.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Employee, id),
                new PartyStatementFilter { IncludeOperationalColumns = false },
                HttpContext?.RequestAborted ?? CancellationToken.None);
            ViewData["PartyStatementSummary"] = statement.Summary;
            ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        }
        return View(model);
    }

    // ---------------- اسناد ----------------

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadDocument(
        int id,
        EmployeeDocumentType documentType,
        string? title,
        DateTime? issueDate,
        DateTime? expiryDate,
        string? notes,
        IFormFile? file,
        int? replacesDocumentId = null)
    {
        try
        {
            await _records.UploadDocumentAsync(new EmployeeDocumentInput(id, documentType, title, issueDate, expiryDate, notes, file), replacesDocumentId);
            TempData["ok"] = replacesDocumentId.HasValue ? "سند جایگزین شد؛ نسخهٔ قبلی در سابقه ماند." : "سند ثبت شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id, tab = "documents" });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDocument(int id, int documentId, string? reason)
    {
        try
        {
            await _records.DeleteDocumentAsync(documentId, reason ?? "");
            TempData["ok"] = "سند حذف شد (سابقه و فایل باقی می‌ماند).";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id, tab = "documents" });
    }

    /// <summary>اسنادِ شخصی (تذکره، CV) فقط برای کسی که مشخصات کارمند را مدیریت می‌کند باز می‌شود.</summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Document(int id, int documentId)
    {
        var document = await _db.EmployeeDocuments.AsNoTracking()
            .Include(d => d.Attachment)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.EmployeeId == id);
        if (document?.Attachment is null) return NotFound();

        var path = _files.ResolvePath(document.Attachment);
        if (path is null) return NotFound();

        await _audit.LogAndSaveAsync(nameof(EmployeeDocument), document.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForCreate(("Downloaded", document.Attachment.OriginalFileName)));
        return PhysicalFile(path, document.Attachment.ContentType ?? "application/octet-stream", document.Attachment.OriginalFileName);
    }

    // ---------------- پایان همکاری ----------------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Offboard(int id)
    {
        try
        {
            ViewBag.Checklist = await _records.GetOffboardingChecklistAsync(id);
        }
        catch (BusinessRuleException)
        {
            return NotFound();
        }

        ViewBag.CanViewSalary = CanViewSalary;
        return View(new EmployeeTerminationViewModel { EmployeeId = id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Offboard(int id, EmployeeTerminationViewModel model)
    {
        if (id != model.EmployeeId) return BadRequest();

        if (ModelState.IsValid)
        {
            try
            {
                await _records.TerminateAsync(id, model.LastWorkingDate, model.Reason, model.Notes, model.AcknowledgeOpenItems);
                TempData["ok"] = "پایانِ همکاری ثبت شد؛ همهٔ سابقهٔ کارمند باقی می‌ماند.";
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        ViewBag.Checklist = await _records.GetOffboardingChecklistAsync(id);
        ViewBag.CanViewSalary = CanViewSalary;
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.HrManageSalary)]
    public async Task<IActionResult> ChangeSalary(int id)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        var current = await _compensation.GetEffectiveAsync(id, AfghanistanBusinessClock.SystemToday);
        var model = new EmployeeSalaryChangeViewModel
        {
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            EmployeeCode = employee.EmployeeCode,
            EffectiveFrom = AfghanistanBusinessClock.SystemToday,
            BaseSalary = current?.BaseSalary ?? employee.BaseSalaryAmount,
            Currency = current?.Currency ?? employee.SalaryCurrency,
            SalaryType = current?.SalaryType ?? employee.SalaryType,
            CurrentSalaryText = current is null ? null : $"{current.BaseSalary:N2} {current.Currency}"
        };
        await PopulateLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.HrManageSalary)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeSalary(int id, EmployeeSalaryChangeViewModel model)
    {
        if (id != model.EmployeeId)
        {
            return BadRequest();
        }

        if (ModelState.IsValid)
        {
            try
            {
                await _compensation.ChangeAsync(new CompensationChange(
                    model.EmployeeId,
                    model.EffectiveFrom,
                    model.BaseSalary,
                    model.Currency,
                    model.SalaryType,
                    CompensationSource.Manual,
                    null,
                    model.Notes));
                TempData["ok"] = "معاشِ تازه ثبت شد؛ معاشِ قبلی در تاریخچه باقی ماند.";
                return RedirectToAction(nameof(Details), new { id = model.EmployeeId, tab = "salary" });
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        model.EmployeeName = employee?.FullName ?? "";
        model.EmployeeCode = employee?.EmployeeCode ?? "";
        await PopulateLookupsAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.HrViewSalary)]
    public async Task<IActionResult> CreateSalaryTransaction(int id, string? returnUrl = null)
    {
        if (!CanManageSalary && !CanPaySalary)
        {
            return Forbid();
        }

        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null)
        {
            return NotFound();
        }

        var now = AfghanistanBusinessClock.SystemToday;
        var model = new EmployeeSalaryTransactionCreateViewModel
        {
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName,
            TransactionDate = now,
            Currency = employee.SalaryCurrency,
            SalaryPeriodYear = now.Year,
            SalaryPeriodMonth = now.Month,
            ReturnUrl = returnUrl,
            TransactionType = CanManageSalary
                ? EmployeeSalaryTransactionType.SalaryAccrual
                : EmployeeSalaryTransactionType.SalaryPayment
        };
        await PopulateSalaryTransactionLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.HrViewSalary)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSalaryTransaction(int id, EmployeeSalaryTransactionCreateViewModel model)
    {
        if (id != model.EmployeeId)
        {
            return BadRequest();
        }

        if (!EmployeeSalaryTransactionTypeLabels.IsManualEntryType(model.TransactionType))
        {
            return BadRequest();
        }

        if (!CanRecord(model.TransactionType))
        {
            return Forbid();
        }

        NormalizeSalaryTransactionModel(model);
        if (!ModelState.IsValid)
        {
            await HydrateSalaryTransactionEmployeeAsync(model);
            await PopulateSalaryTransactionLookupsAsync(model);
            return View(model);
        }

        try
        {
            var transaction = await _salaryService.CreateAsync(new EmployeeSalaryTransactionCommand(
                model.EmployeeId,
                model.TransactionDate,
                model.TransactionType,
                model.Amount,
                model.Currency,
                model.AppliedFxRateToUsd,
                model.CashAccountId,
                model.Reference,
                model.Description,
                model.SalaryPeriodYear,
                model.SalaryPeriodMonth,
                RecoveryYear: model.RecoveryYear,
                RecoveryMonth: model.RecoveryMonth));

            TempData["ok"] = "تراکنش معاش ثبت شد.";
            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id = transaction.EmployeeId });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await HydrateSalaryTransactionEmployeeAsync(model);
            await PopulateSalaryTransactionLookupsAsync(model);
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.HrViewSalary)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSalaryTransaction(EmployeeSalaryTransactionCancelViewModel model)
    {
        var transactionType = await _db.EmployeeSalaryTransactions
            .AsNoTracking()
            .Where(t => t.Id == model.TransactionId && t.EmployeeId == model.EmployeeId)
            .Select(t => (EmployeeSalaryTransactionType?)t.TransactionType)
            .FirstOrDefaultAsync();
        if (transactionType is null)
        {
            return NotFound();
        }

        if (!CanRecord(transactionType.Value))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            TempData["err"] = "برای لغو تراکنش، دلیل لغو را وارد کنید.";
            return RedirectToAction(nameof(Details), new { id = model.EmployeeId });
        }

        try
        {
            await _salaryService.CancelAsync(model.TransactionId, model.CancellationReason);
            TempData["ok"] = "تراکنش معاش لغو شد. رکورد حذف نشد و برای بررسی مالی باقی ماند.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id = model.EmployeeId });
    }

    private async Task PopulateLookupsAsync(
        EmployeeFormViewModel? form = null,
        EmployeeIndexFilterViewModel? filter = null)
    {
        ViewBag.EmployeeTypes = Enum.GetValues<EmployeeType>()
            .Select(t => new SelectListItem
            {
                Value = ((int)t).ToString(),
                Text = EmployeeTypeLabels.ToPersian(t),
                Selected = form?.EmployeeType == t || (filter?.EmployeeType.Contains(t) ?? false)
            })
            .ToList();

        ViewBag.SalaryTypes = Enum.GetValues<EmployeeSalaryType>()
            .Select(t => new SelectListItem
            {
                Value = ((int)t).ToString(),
                Text = EmployeeSalaryTypeLabels.ToPersian(t),
                Selected = form?.SalaryType == t || (filter?.SalaryType.Contains(t) ?? false)
            })
            .ToList();

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            form?.SalaryCurrency ?? filter?.Currency);

        ViewBag.CanManageSalary = CanManageSalary;
        ViewBag.CanViewSalary = CanViewSalary;
        ViewBag.CanLinkUser = User is not null && RoleAccessRules.CanManageUsers(User);
        if (ViewBag.CanLinkUser == true)
        {
            var currentEmployeeId = form?.Id ?? 0;
            var linkedUserId = form?.UserId;
            ViewBag.Users = await _db.Users.AsNoTracking()
                .Where(u => !_db.Employees.Any(e => e.UserId == u.Id && e.Id != currentEmployeeId))
                .OrderBy(u => u.Username)
                .Select(u => new SelectListItem { Value = u.Id.ToString(), Text = u.Username + " — " + u.FullName, Selected = u.Id == linkedUserId })
                .ToListAsync();
        }

        var selectedDepartmentIds = filter?.DepartmentId ?? [];
        var formDepartmentId = form?.DepartmentId;
        var formPositionId = form?.PositionId;
        var departments = await _db.Departments.AsNoTracking()
            .Where(d => d.IsActive || d.Id == formDepartmentId)
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();
        ViewBag.Departments = departments
            .Select(d => new SelectListItem
            {
                Value = d.Id.ToString(),
                Text = d.Name,
                Selected = d.Id == formDepartmentId || selectedDepartmentIds.Contains(d.Id)
            })
            .ToList();
        ViewBag.Positions = await _db.Positions.AsNoTracking()
            .Where(p => p.IsActive || p.Id == formPositionId)
            .OrderBy(p => p.Name)
            .Select(p => new SelectListItem
            {
                Value = p.Id.ToString(),
                Text = p.Department != null ? p.Name + " — " + p.Department.Name : p.Name,
                Selected = p.Id == formPositionId
            })
            .ToListAsync();

        ViewBag.Statuses = new List<SelectListItem>
        {
            new() { Value = "true", Text = "فعال", Selected = filter?.IsActive == true },
            new() { Value = "false", Text = "غیرفعال", Selected = filter?.IsActive == false }
        };
    }

    /// <summary>
    /// بخش و بست از جدول تعریف‌شده انتخاب می‌شوند و نامشان در ستونِ متنیِ قدیمی هم نوشته می‌شود.
    /// اگر چیزی انتخاب نشده باشد، متنِ قدیمی دست نمی‌خورد (فرم آن را نمی‌فرستد) مگر اینکه صریح
    /// فرستاده شده باشد؛ هیچ دادهٔ قدیمی بی‌صدا پاک نمی‌شود.
    /// </summary>
    private async Task<bool> ApplyOrganizationAsync(Employee employee, EmployeeFormViewModel model)
    {
        if (model.DepartmentId.HasValue)
        {
            var department = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == model.DepartmentId.Value);
            if (department is null || (!department.IsActive && employee.DepartmentId != department.Id))
            {
                ModelState.AddModelError(nameof(model.DepartmentId), "بخش انتخاب‌شده معتبر یا فعال نیست.");
                return false;
            }

            employee.DepartmentId = department.Id;
            employee.Department = department.Name;
        }
        else
        {
            employee.DepartmentId = null;
            if (model.Department is not null)
            {
                employee.Department = NormalizeText(model.Department);
            }
        }

        // پیوند به حساب کاربری فقط با «مدیریت کاربران»؛ کارمند بدونِ کاربر کاملاً معتبر است.
        if (User is not null && RoleAccessRules.CanManageUsers(User))
        {
            if (model.UserId.HasValue)
            {
                var userExists = await _db.Users.AsNoTracking().AnyAsync(u => u.Id == model.UserId.Value);
                var takenByOther = await _db.Employees.AsNoTracking().AnyAsync(e => e.UserId == model.UserId.Value && e.Id != employee.Id);
                if (!userExists || takenByOther)
                {
                    ModelState.AddModelError(nameof(model.UserId), "این حساب کاربری معتبر نیست یا به کارمندِ دیگری وصل است.");
                    return false;
                }
            }

            employee.UserId = model.UserId;
        }

        if (model.PositionId.HasValue)
        {
            var position = await _db.Positions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.PositionId.Value);
            if (position is null || (!position.IsActive && employee.PositionId != position.Id))
            {
                ModelState.AddModelError(nameof(model.PositionId), "بست انتخاب‌شده معتبر یا فعال نیست.");
                return false;
            }

            employee.PositionId = position.Id;
            employee.JobTitle = position.Name;
        }
        else
        {
            employee.PositionId = null;
            if (model.JobTitle is not null)
            {
                employee.JobTitle = NormalizeText(model.JobTitle);
            }
        }

        return true;
    }

    private async Task PopulateSalaryTransactionLookupsAsync(EmployeeSalaryTransactionCreateViewModel model)
    {
        ViewBag.TransactionTypes = Enum.GetValues<EmployeeSalaryTransactionType>()
            .Where(EmployeeSalaryTransactionTypeLabels.IsManualEntryType)
            .Where(CanRecord)
            .Select(t => new SelectListItem
            {
                Value = ((int)t).ToString(),
                Text = EmployeeSalaryTransactionTypeLabels.ToPersian(t),
                Selected = model.TransactionType == t
            })
            .ToList();

        ViewBag.CashAccounts = new SelectList(
            await _db.CashAccounts
                .AsNoTracking()
                .Where(a => a.IsActive)
                .OrderBy(a => a.Code)
                .Select(a => new { a.Id, Label = a.Code + " - " + a.Name + " (" + a.Currency + ")" })
                .ToListAsync(),
            "Id",
            "Label",
            model.CashAccountId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            model.Currency);
    }

    private async Task HydrateSalaryTransactionEmployeeAsync(EmployeeSalaryTransactionCreateViewModel model)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == model.EmployeeId);
        if (employee is null)
        {
            return;
        }

        model.EmployeeCode = employee.EmployeeCode;
        model.EmployeeName = employee.FullName;
    }

    private bool TryGetLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        {
            localReturnUrl = returnUrl;
            return true;
        }

        localReturnUrl = string.Empty;
        return false;
    }

    private static EmployeeFormViewModel ToForm(Employee employee) => new()
    {
        Id = employee.Id,
        EmployeeCode = employee.EmployeeCode,
        FullName = employee.FullName,
        FatherName = employee.FatherName,
        Phone = employee.Phone,
        Email = employee.Email,
        PhotoPath = employee.PhotoPath,
        NationalId = employee.NationalId,
        Address = employee.Address,
        JobTitle = employee.JobTitle,
        Department = employee.Department,
        DepartmentId = employee.DepartmentId,
        PositionId = employee.PositionId,
        UserId = employee.UserId,
        EmployeeType = employee.EmployeeType,
        SalaryType = employee.SalaryType,
        BaseSalaryAmount = employee.BaseSalaryAmount,
        SalaryCurrency = employee.SalaryCurrency,
        HireDate = employee.HireDate,
        EndDate = employee.EndDate,
        IsActive = employee.IsActive,
        Notes = employee.Notes
    };

    private static void ApplyForm(Employee employee, EmployeeFormViewModel model)
    {
        employee.EmployeeCode = model.EmployeeCode;
        employee.FullName = model.FullName;
        employee.FatherName = NormalizeText(model.FatherName);
        employee.Phone = NormalizeText(model.Phone);
        employee.Email = NormalizeText(model.Email);
        employee.NationalId = NormalizeText(model.NationalId);
        employee.Address = NormalizeText(model.Address);
        employee.EmployeeType = model.EmployeeType;
        employee.SalaryType = model.SalaryType;
        employee.BaseSalaryAmount = model.BaseSalaryAmount;
        employee.SalaryCurrency = SystemCurrency.Normalize(model.SalaryCurrency);
        employee.HireDate = model.HireDate.Date;
        employee.EndDate = model.EndDate?.Date;
        employee.IsActive = model.IsActive;
        employee.Notes = NormalizeText(model.Notes);
    }

    private static EmployeeSalaryTransactionListItemViewModel ToTransactionListItem(EmployeeSalaryTransaction transaction)
        => new()
        {
            Id = transaction.Id,
            EmployeeId = transaction.EmployeeId,
            EmployeeName = transaction.Employee?.FullName ?? "",
            EmployeeCode = transaction.Employee?.EmployeeCode ?? "",
            TransactionDate = transaction.TransactionDate,
            TransactionType = transaction.TransactionType,
            TransactionTypeName = EmployeeSalaryTransactionTypeLabels.ToPersian(transaction.TransactionType),
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            AmountUsd = transaction.AmountUsd,
            AppliedFxRateToUsd = transaction.AppliedFxRateToUsd,
            CashAccountName = transaction.CashAccount?.Name,
            PaymentTransactionId = transaction.PaymentTransactionId,
            LedgerEntryId = transaction.LedgerEntryId,
            Reference = transaction.Reference,
            Description = transaction.Description,
            SalaryPeriodYear = transaction.SalaryPeriodYear,
            SalaryPeriodMonth = transaction.SalaryPeriodMonth,
            IsCancelled = transaction.IsCancelled,
            CancellationReason = transaction.CancellationReason,
            ReversalPaymentTransactionId = transaction.ReversalPaymentTransactionId,
            CreatedAtUtc = transaction.CreatedAtUtc,
            CreatedByUserId = transaction.CreatedByUserId
        };

    private static void NormalizeFilter(EmployeeIndexFilterViewModel filter)
    {
        filter.Query = NormalizeText(filter.Query);
        filter.Department = NormalizeText(filter.Department);
        filter.Currency = string.IsNullOrWhiteSpace(filter.Currency) ? null : SystemCurrency.Normalize(filter.Currency);
    }

    private static void NormalizeForm(EmployeeFormViewModel model)
    {
        model.EmployeeCode = (model.EmployeeCode ?? string.Empty).Trim();
        model.FullName = (model.FullName ?? string.Empty).Trim();
        model.SalaryCurrency = string.IsNullOrWhiteSpace(model.SalaryCurrency)
            ? SystemCurrency.BaseCurrencyCode
            : SystemCurrency.Normalize(model.SalaryCurrency);
        model.HireDate = model.HireDate.Date;
        model.EndDate = model.EndDate?.Date;
    }

    private static void NormalizeSalaryTransactionModel(EmployeeSalaryTransactionCreateViewModel model)
    {
        model.TransactionDate = model.TransactionDate.Date;
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.Reference = NormalizeText(model.Reference);
        model.Description = NormalizeText(model.Description);
    }

    private void ValidateEmployeePhoto(EmployeeFormViewModel model)
    {
        if (model.PhotoFile is null || model.PhotoFile.Length == 0)
        {
            return;
        }

        if (model.PhotoFile.Length > MaxEmployeePhotoBytes)
        {
            ModelState.AddModelError(nameof(model.PhotoFile), "حجم عکس کارمند نباید بیشتر از 2MB باشد.");
            return;
        }

        var extension = Path.GetExtension(model.PhotoFile.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedEmployeePhotoExtensions.Contains(extension))
        {
            ModelState.AddModelError(nameof(model.PhotoFile), "فقط عکس با فرمت JPG، PNG یا WEBP قابل ثبت است.");
        }
    }

    private async Task<string?> SaveEmployeePhotoAsync(IFormFile? photoFile)
    {
        if (photoFile is null || photoFile.Length == 0)
        {
            return null;
        }

        var extension = Path.GetExtension(photoFile.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var webRootPath = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;
        var relativeDirectory = Path.Combine("uploads", "employees");
        var absoluteDirectory = Path.Combine(webRootPath, relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var absolutePath = Path.Combine(absoluteDirectory, fileName);
        await using var stream = System.IO.File.Create(absolutePath);
        await photoFile.CopyToAsync(stream);

        return "/" + relativeDirectory.Replace('\\', '/') + "/" + fileName;
    }

    private void DeleteEmployeePhoto(string? photoPath)
    {
        if (string.IsNullOrWhiteSpace(photoPath) || !photoPath.StartsWith("/uploads/employees/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = Path.GetFileName(photoPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var webRootPath = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;
        var absolutePath = Path.Combine(webRootPath, "uploads", "employees", fileName);
        if (System.IO.File.Exists(absolutePath))
        {
            System.IO.File.Delete(absolutePath);
        }
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
