using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Employees;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Employees;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// مدیریت بشری، فاز ۰ — درستیِ مالیِ معاش: دفتر کل دوطرفه، لغوِ واقعی، جلوگیری از ثبتِ تکراری
/// و محرمانگیِ معاش.
/// </summary>
public class HrSalaryIntegrityTests
{
    private static readonly DateTime May31 = new(2026, 5, 31);

    // ---------- دفتر کل ----------

    [Fact]
    public async Task Accrual_Posts_Salary_Expense_Against_Employee_Payable()
    {
        await using var db = await NewAccountingDbAsync();
        var settings = await db.AccountingSettings.SingleAsync();

        var accrual = await BuildService(db).CreateAsync(Accrual(1000m));

        var journal = await db.JournalEntries.Include(j => j.Lines)
            .SingleAsync(j => j.SourceModule == EmployeeSalaryAccountingAdapter.SourceModule);
        Assert.Equal(EmployeeSalaryAccountingAdapter.BuildCreatedSourceEventId(accrual.Id), journal.SourceEventId);
        var debit = Assert.Single(journal.Lines, l => l.Debit > 0m);
        var credit = Assert.Single(journal.Lines, l => l.Credit > 0m);
        Assert.Equal(settings.SalaryExpenseAccountId, debit.AccountId);
        Assert.Equal(1000m, debit.Debit);
        Assert.Equal(settings.EmployeePayableAccountId, credit.AccountId);
        Assert.Equal(1000m, credit.Credit);
        Assert.Equal(AccountingPartyType.Employee, credit.PartyType);
        Assert.Equal(1, credit.PartyId);
    }

    [Fact]
    public async Task Payment_Settles_Payable_And_Never_Expenses_Salary_Twice()
    {
        await using var db = await NewAccountingDbAsync();
        var settings = await db.AccountingSettings.SingleAsync();
        var service = BuildService(db);

        await service.CreateAsync(Accrual(1000m));
        var payment = await service.CreateAsync(Cash(EmployeeSalaryTransactionType.SalaryPayment, 600m));

        var paymentJournal = await db.JournalEntries.Include(j => j.Lines)
            .SingleAsync(j => j.SourceModule == PaymentAccountingAdapter.SourceModule
                && j.SourceEntityId == payment.PaymentTransactionId);
        var debit = Assert.Single(paymentJournal.Lines, l => l.Debit > 0m);
        var credit = Assert.Single(paymentJournal.Lines, l => l.Credit > 0m);
        Assert.Equal(settings.EmployeePayableAccountId, debit.AccountId);
        Assert.Equal(AccountingPartyType.Employee, debit.PartyType);
        Assert.Equal(settings.CashBankControlAccountId, credit.AccountId);
        Assert.Equal(600m, credit.Credit);

        // مصرف معاش فقط یک بار — هنگام ثبت معاش — شناسایی شده است.
        var expenseDebits = await db.JournalEntryLines
            .Where(l => l.AccountId == settings.SalaryExpenseAccountId)
            .SumAsync(l => l.Debit - l.Credit);
        Assert.Equal(1000m, expenseDebits);

        var payableBalance = await db.JournalEntryLines
            .Where(l => l.AccountId == settings.EmployeePayableAccountId)
            .SumAsync(l => l.Credit - l.Debit);
        Assert.Equal(400m, payableBalance);
    }

    [Fact]
    public async Task Advance_Posts_Employee_Advance_Against_Cash()
    {
        await using var db = await NewAccountingDbAsync();
        var settings = await db.AccountingSettings.SingleAsync();

        var advance = await BuildService(db).CreateAsync(Cash(EmployeeSalaryTransactionType.SalaryAdvance, 150m));

        var journal = await db.JournalEntries.Include(j => j.Lines)
            .SingleAsync(j => j.SourceEntityId == advance.PaymentTransactionId);
        var debit = Assert.Single(journal.Lines, l => l.Debit > 0m);
        Assert.Equal(settings.EmployeeAdvanceAccountId, debit.AccountId);
        Assert.Equal(150m, debit.Debit);
        Assert.Contains(journal.Lines, l => l.AccountId == settings.CashBankControlAccountId && l.Credit == 150m);
    }

    [Fact]
    public async Task Advance_Recovery_Moves_Payable_To_Advance_And_Cannot_Exceed_Outstanding()
    {
        await using var db = await NewAccountingDbAsync();
        var settings = await db.AccountingSettings.SingleAsync();
        var service = BuildService(db);
        await service.CreateAsync(Cash(EmployeeSalaryTransactionType.SalaryAdvance, 150m));

        var tooMuch = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.CreateAsync(NonCash(EmployeeSalaryTransactionType.AdvanceRecovery, 200m)));
        Assert.Equal("EMPLOYEE_ADVANCE_RECOVERY_EXCEEDS_OUTSTANDING", tooMuch.Code);

        var recovery = await service.CreateAsync(NonCash(EmployeeSalaryTransactionType.AdvanceRecovery, 100m));
        var journal = await db.JournalEntries.Include(j => j.Lines)
            .SingleAsync(j => j.SourceEventId == EmployeeSalaryAccountingAdapter.BuildCreatedSourceEventId(recovery.Id));
        Assert.Contains(journal.Lines, l => l.AccountId == settings.EmployeePayableAccountId && l.Debit == 100m);
        Assert.Contains(journal.Lines, l => l.AccountId == settings.EmployeeAdvanceAccountId && l.Credit == 100m);
        Assert.Equal(50m, await EmployeeSalaryService.GetOutstandingAdvanceUsdAsync(db, 1, null, default));

        // وصول ماندهٔ خالصِ کارمند را تغییر نمی‌دهد.
        var summary = EmployeeSalarySummaryCalculator.FromTransactions(await db.EmployeeSalaryTransactions.ToListAsync());
        Assert.Equal(-150m, summary.BalanceUsd);
        Assert.Equal(50m, summary.OutstandingAdvanceUsd);
    }

    [Fact]
    public async Task Recovered_Advance_Cannot_Be_Cancelled_Before_Its_Recovery()
    {
        await using var db = await NewAccountingDbAsync();
        var service = BuildService(db);
        var advance = await service.CreateAsync(Cash(EmployeeSalaryTransactionType.SalaryAdvance, 150m));
        await service.CreateAsync(NonCash(EmployeeSalaryTransactionType.AdvanceRecovery, 100m));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CancelAsync(advance.Id, "wrong"));
        Assert.Equal("EMPLOYEE_ADVANCE_ALREADY_RECOVERED", ex.Code);
        Assert.False((await db.EmployeeSalaryTransactions.SingleAsync(t => t.Id == advance.Id)).IsCancelled);
    }

    [Fact]
    public async Task Pilot_Off_Keeps_Legacy_Flow_Without_Journals()
    {
        await using var db = await NewAccountingDbAsync();
        var service = BuildService(db, pilot: false);

        await service.CreateAsync(Accrual(1000m));
        var payment = await service.CreateAsync(Cash(EmployeeSalaryTransactionType.SalaryPayment, 400m));

        Assert.Empty(await db.JournalEntries.ToListAsync());
        Assert.NotNull(payment.PaymentTransactionId);
        Assert.NotNull(payment.LedgerEntryId);
    }

    // ---------- لغو ----------

    [Fact]
    public async Task Cancel_Payment_Returns_Cash_Keeps_Original_And_Reverses_Journal()
    {
        await using var db = await NewAccountingDbAsync();
        var settings = await db.AccountingSettings.SingleAsync();
        var service = BuildService(db);
        await service.CreateAsync(Accrual(1000m));
        var payment = await service.CreateAsync(Cash(EmployeeSalaryTransactionType.SalaryPayment, 250m));

        await service.CancelAsync(payment.Id, "Paid to the wrong employee");

        var saved = await db.EmployeeSalaryTransactions.SingleAsync(t => t.Id == payment.Id);
        Assert.True(saved.IsCancelled);
        Assert.NotNull(saved.ReversalPaymentTransactionId);
        Assert.NotNull(saved.ReversalLedgerEntryId);

        // سند اصلی حذف نشده؛ سند معکوس همان مبلغ را به همان صندوق برمی‌گرداند.
        var original = await db.PaymentTransactions.SingleAsync(p => p.Id == payment.PaymentTransactionId);
        var reversal = await db.PaymentTransactions.SingleAsync(p => p.Id == saved.ReversalPaymentTransactionId);
        Assert.Equal(PaymentDirection.Out, original.Direction);
        Assert.Equal(PaymentDirection.In, reversal.Direction);
        Assert.Equal(PaymentKind.EmployeeReturn, reversal.PaymentKind);
        Assert.Equal(original.CashAccountId, reversal.CashAccountId);
        Assert.Equal(original.AmountUsd, reversal.AmountUsd);
        Assert.True(CompanyFlowSourceTypes.IsReversalReference(reversal.Reference));

        var reversalLedger = await db.LedgerEntries.SingleAsync(l => l.Id == saved.ReversalLedgerEntryId);
        Assert.Equal(LedgerSide.Credit, reversalLedger.Side);
        Assert.Equal(nameof(PaymentKind.EmployeeReturn), reversalLedger.SourceType);
        Assert.Equal(reversal.Id, reversalLedger.SourceId);
        Assert.Equal(reversal.LedgerEntryId, reversalLedger.Id);

        var cash = await new CashPositionReader(db).ReadAccountTotalsAsync();
        Assert.Equal(0m, Assert.Single(cash).BalanceUsd);

        // دفتر کل: پرداخت و برگشتش خنثی؛ بدهیِ معاش دوباره کامل است.
        var cashNet = await db.JournalEntryLines
            .Where(l => l.AccountId == settings.CashBankControlAccountId)
            .SumAsync(l => l.Debit - l.Credit);
        Assert.Equal(0m, cashNet);
        var payable = await db.JournalEntryLines
            .Where(l => l.AccountId == settings.EmployeePayableAccountId)
            .SumAsync(l => l.Credit - l.Debit);
        Assert.Equal(1000m, payable);

        // صورت‌حساب کارمند: پرداختِ لغوشده بیرون است، مانده همان معاش ثبت‌شده است.
        var summary = EmployeeSalarySummaryCalculator.FromTransactions(await db.EmployeeSalaryTransactions.ToListAsync());
        Assert.Equal(1000m, summary.BalanceUsd);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.EntityName == nameof(EmployeeSalaryTransaction) && a.Action == "Reverse");
    }

    [Fact]
    public async Task Cancel_Accrual_Reverses_Its_Journal()
    {
        await using var db = await NewAccountingDbAsync();
        var settings = await db.AccountingSettings.SingleAsync();
        var service = BuildService(db);
        var accrual = await service.CreateAsync(Accrual(1000m));

        await service.CancelAsync(accrual.Id, "Wrong month");

        Assert.NotNull(await db.JournalEntries.SingleOrDefaultAsync(
            j => j.SourceEventId == EmployeeSalaryAccountingAdapter.BuildReversedSourceEventId(accrual.Id)));
        var expenseNet = await db.JournalEntryLines
            .Where(l => l.AccountId == settings.SalaryExpenseAccountId)
            .SumAsync(l => l.Debit - l.Credit);
        Assert.Equal(0m, expenseNet);

        // لغوِ دوباره بی‌اثر است.
        await service.CancelAsync(accrual.Id, "again");
        Assert.Equal(2, await db.JournalEntries.CountAsync());
    }

    [Fact]
    public async Task Cancelled_Salary_Payment_Is_Never_Posted_Later_By_Backfill()
    {
        await using var db = await NewAccountingDbAsync();
        var payment = await BuildService(db, pilot: false)
            .CreateAsync(Cash(EmployeeSalaryTransactionType.SalaryPayment, 250m));
        await BuildService(db, pilot: false).CancelAsync(payment.Id, "wrong");

        var original = await db.PaymentTransactions.SingleAsync(p => p.Id == payment.PaymentTransactionId);
        var result = await BuildPaymentAdapter(db, pilot: true).TryPostPaymentAsync(original);

        Assert.Equal(PaymentPostingStatus.Skipped, result.Status);
        Assert.Equal("EMPLOYEE_SALARY_CANCELLED", result.Reason);
    }

    // ---------- ثبت تکراری ----------

    [Fact]
    public async Task Duplicate_Accrual_For_Same_Employee_And_Month_Is_Rejected_Until_Cancelled()
    {
        await using var db = await NewAccountingDbAsync();
        var service = BuildService(db);
        var first = await service.CreateAsync(Accrual(1000m));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(Accrual(1000m)));
        Assert.Equal("EMPLOYEE_SALARY_ACCRUAL_DUPLICATE", ex.Code);
        Assert.Equal(1, await db.EmployeeSalaryTransactions.CountAsync());

        // ماهِ دیگر آزاد است.
        await service.CreateAsync(Accrual(1000m) with { SalaryPeriodMonth = 6 });

        // لغوِ ثبتِ قبلی جا را آزاد می‌کند.
        await service.CancelAsync(first.Id, "Recalculated");
        await service.CreateAsync(Accrual(1100m));
        Assert.Equal(2, await db.EmployeeSalaryTransactions.CountAsync(t => !t.IsCancelled));
    }

    // ---------- دسترسی ----------

    [Fact]
    public async Task Employee_List_Hides_Salary_And_Balance_Without_Permission()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        await db.SaveChangesAsync();

        var result = await EmployeeModuleTests.BuildEmployeesController(db, User(AuthRoles.Operator)).Index();

        var model = Assert.IsType<EmployeeIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.False(model.CanViewSalary);
        var item = Assert.Single(model.Items);
        Assert.Equal(0m, item.BaseSalaryAmount);
        Assert.Equal(0m, item.BalanceUsd);
    }

    [Fact]
    public async Task Employee_Details_Hides_Salary_History_Without_Permission()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        await db.SaveChangesAsync();
        await EmployeeModuleTests.BuildSalaryService(db).CreateAsync(Accrual(1000m));

        var result = await EmployeeModuleTests.BuildEmployeesController(db, User(AuthRoles.Operator)).Details(1);

        var model = Assert.IsType<EmployeeDetailsViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.False(model.CanViewSalary);
        Assert.Empty(model.Transactions);
        Assert.Equal(0m, model.BaseSalaryAmount);
        Assert.Equal(0m, model.Summary.BalanceUsd);
        Assert.All(model.AuditItems, a => Assert.Null(a.Diff));
    }

    [Fact]
    public async Task Pay_Permission_Allows_Payment_But_Not_Accrual()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        await db.SaveChangesAsync();
        var controller = EmployeeModuleTests.BuildEmployeesController(
            db, User(AuthRoles.Operator, AppPermissions.PaySalary));

        var accrual = await controller.CreateSalaryTransaction(1, new EmployeeSalaryTransactionCreateViewModel
        {
            EmployeeId = 1,
            TransactionType = EmployeeSalaryTransactionType.SalaryAccrual,
            TransactionDate = May31,
            Amount = 1000m,
            Currency = "USD",
            SalaryPeriodYear = 2026,
            SalaryPeriodMonth = 5
        });
        Assert.IsType<ForbidResult>(accrual);

        var payment = await controller.CreateSalaryTransaction(1, new EmployeeSalaryTransactionCreateViewModel
        {
            EmployeeId = 1,
            TransactionType = EmployeeSalaryTransactionType.SalaryPayment,
            TransactionDate = May31,
            Amount = 100m,
            Currency = "USD",
            CashAccountId = 1
        });
        Assert.IsType<RedirectToActionResult>(payment);
        Assert.Equal(EmployeeSalaryTransactionType.SalaryPayment, (await db.EmployeeSalaryTransactions.SingleAsync()).TransactionType);
    }

    [Fact]
    public async Task Salary_Form_Actions_Are_Forbidden_Without_Any_Salary_Permission()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        await db.SaveChangesAsync();
        var controller = EmployeeModuleTests.BuildEmployeesController(db, User(AuthRoles.Operator, AppPermissions.ViewEmployeeSalary));

        Assert.IsType<ForbidResult>(await controller.CreateSalaryTransaction(1));
    }

    [Fact]
    public async Task Editing_Without_Salary_Permission_Preserves_Salary()
    {
        await using var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        await db.SaveChangesAsync();
        var controller = EmployeeModuleTests.BuildEmployeesController(db, User(AuthRoles.Operator));

        var result = await controller.Edit(1, new EmployeeFormViewModel
        {
            Id = 1,
            EmployeeCode = "EMP-BASE",
            FullName = "Renamed Employee",
            BaseSalaryAmount = 0m,
            SalaryCurrency = "USD",
            HireDate = new DateTime(2026, 5, 1),
            IsActive = true
        });

        Assert.IsType<RedirectToActionResult>(result);
        var employee = await db.Employees.SingleAsync();
        Assert.Equal("Renamed Employee", employee.FullName);
        Assert.Equal(1000m, employee.BaseSalaryAmount);
    }

    [Fact]
    public void Role_Grants_Explicit_Hr_Permissions_And_Admin_Gets_All()
    {
        var operatorClaims = UserClaimsFactory.Build(new User
        {
            Id = 7,
            Username = "clerk",
            FullName = "Clerk",
            Role = new Role { Name = AuthRoles.Operator, GrantedPermissions = "HR.PaySalary,Unknown,HR.PaySalary" }
        });
        var permissions = operatorClaims.Where(c => c.Type == AppClaimTypes.Permission).Select(c => c.Value).ToList();
        Assert.Contains(AppPermissions.PaySalary, permissions);
        Assert.DoesNotContain("Unknown", permissions);
        Assert.DoesNotContain(AppPermissions.ManageEmployeeSalary, permissions);
        Assert.Single(permissions, AppPermissions.PaySalary);

        var clerk = new ClaimsPrincipal(new ClaimsIdentity(operatorClaims, "Test"));
        Assert.True(RoleAccessRules.CanViewEmployeeSalary(clerk));
        Assert.True(RoleAccessRules.CanPaySalary(clerk));
        Assert.False(RoleAccessRules.CanManageEmployeeSalary(clerk));
        Assert.False(RoleAccessRules.CanRunPayroll(clerk));

        Assert.Equal(4, RoleAccessRules.ResolveGrantedPermissions(new Role { Name = AuthRoles.Admin }).Length);
        Assert.False(RoleAccessRules.CanViewEmployeeSalary(User(AuthRoles.Manager)));
    }

    [Fact]
    public void Employees_Moved_To_Human_Resources_Navigation()
    {
        Assert.Equal(RoleNavigationKeys.HumanResources, RoleAccessRules.NavigationKeyForController("Employees"));
        Assert.Contains(RoleNavigationKeys.HumanResources, RoleAccessRules.DefaultNavigationForRole(AuthRoles.Operator));
    }

    // ---------- helpers ----------

    private static ClaimsPrincipal User(string role, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        claims.AddRange(permissions.Select(p => new Claim(AppClaimTypes.Permission, p)));
        if (role != AuthRoles.Viewer)
        {
            claims.Add(new Claim(AppClaimTypes.Permission, AppPermissions.ManageData));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static EmployeeSalaryTransactionCommand Accrual(decimal amount)
        => new(1, May31, EmployeeSalaryTransactionType.SalaryAccrual, amount, "USD", null, null, "SAL-2026-05", null, 2026, 5);

    private static EmployeeSalaryTransactionCommand Cash(EmployeeSalaryTransactionType type, decimal amount)
        => new(1, May31, type, amount, "USD", null, 1, $"{type}-{amount}", null, null, null);

    private static EmployeeSalaryTransactionCommand NonCash(EmployeeSalaryTransactionType type, decimal amount)
        => new(1, May31, type, amount, "USD", null, null, $"{type}-{amount}", null, null, null);

    internal static async Task<ApplicationDbContext> NewAccountingDbAsync()
    {
        var db = new ApplicationDbContext(EmployeeModuleTests.NewDbOptions());
        EmployeeModuleTests.SeedReferenceData(db);
        db.Companies.Add(new Company { Id = 1, Code = "PTG", Name = "PTG", Country = "AF", IsActive = true, IsSystemOwner = true });
        db.FiscalYears.Add(new FiscalYear
        {
            Id = 1, CompanyId = 1, Name = "FY-2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31),
            Status = FiscalYearStatus.Open, IsCurrent = true
        });
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            Id = 1, CompanyId = 1, FiscalYearId = 1, PeriodNumber = 1, Name = "FY26",
            StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31), Status = FiscalPeriodStatus.Open
        });
        await db.SaveChangesAsync();
        await new AccountingChartSeeder(db, Microsoft.Extensions.Options.Options.Create(new AccountingOptions { DefaultFunctionalCurrencyCode = "USD" })).SeedAsync();
        return db;
    }

    private static IOptions<AccountingOptions> PilotOptions(bool pilot)
        => Microsoft.Extensions.Options.Options.Create(new AccountingOptions
        {
            Enabled = true,
            Pilots = new AccountingPilotOptions { EmployeeSalary = pilot }
        });

    private static AccountingPostingService Posting(ApplicationDbContext db, IOptions<AccountingOptions> options)
        => new(db, new PeriodGuard(db, new FiscalCalendarService(db)), options, new SystemCompanyProvider(db));

    private static PaymentAccountingAdapter BuildPaymentAdapter(ApplicationDbContext db, bool pilot)
    {
        var options = PilotOptions(pilot);
        var posting = Posting(db, options);
        return new PaymentAccountingAdapter(
            db,
            posting,
            new AccountingJournalNumberGenerator(),
            new PaymentCompanyResolver(db),
            new ExpenseAccountingAdapter(db, posting, new AccountingJournalNumberGenerator(), options, NullLogger<ExpenseAccountingAdapter>.Instance),
            options,
            NullLogger<PaymentAccountingAdapter>.Instance,
            new SystemCompanyProvider(db));
    }

    internal static EmployeeSalaryService BuildService(ApplicationDbContext db, bool pilot = true)
    {
        var options = PilotOptions(pilot);
        var salaryAccounting = new EmployeeSalaryAccountingAdapter(
            db,
            Posting(db, options),
            new AccountingJournalNumberGenerator(),
            new SystemCompanyProvider(db),
            options,
            NullLogger<EmployeeSalaryAccountingAdapter>.Instance);
        return new EmployeeSalaryService(
            db,
            new CurrencyConversionService(new PricingService(db)),
            new AuditService(db),
            NullLogger<EmployeeSalaryService>.Instance,
            BuildPaymentAdapter(db, pilot),
            salaryAccounting);
    }
}
