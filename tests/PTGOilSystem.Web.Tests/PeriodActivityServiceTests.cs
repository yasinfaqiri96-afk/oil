using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// گاردهای صفحهٔ «مشاهده فعالیت‌ها»: فیلترِ بازهٔ تاریخِ دوره، فیلترِ شرکت روی موجودیت‌های دارای
/// CompanyId، و پنهان‌ماندنِ اسناد حسابداری وقتی Accounting.Enabled=false. فقط خواندن.
/// </summary>
public class PeriodActivityServiceTests
{
    private static readonly DateTime InRange = new(2026, 3, 15);
    private static readonly DateTime OutOfRange = new(2026, 4, 15);

    [Fact]
    public async Task Build_Only_Counts_Records_In_The_Period_Range_And_For_The_Company()
    {
        using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var model = await new PeriodActivityService(db).BuildAsync(
            fiscalPeriodId: 10, companyId: 1, accountingEnabled: false);

        Assert.NotNull(model);
        // فروش: فقط یک ردیفِ داخل بازه و شرکتِ ۱ (ردیفِ خارج بازه و ردیفِ شرکتِ ۲ حذف می‌شوند).
        Assert.Equal(1, model!.Kpis.SalesCount);
        Assert.Equal(500m, model.Kpis.SalesUsd);
        Assert.Single(model.Sales);
        // خرید: فقط قرارداد خریدِ داخل بازهٔ شرکتِ ۱ (قراردادِ فروش و قراردادِ شرکتِ ۲ حذف می‌شوند).
        Assert.Equal(1, model.Kpis.PurchaseCount);
        // دریافت/پرداخت: هرکدام یک ردیفِ داخل بازهٔ شرکتِ ۱.
        Assert.Equal(200m, model.Kpis.ReceiptsUsd);
        Assert.Equal(80m, model.Kpis.PaymentsUsd);
        // مصارف و موجودی فیلدِ شرکت ندارند؛ فقط با تاریخ فیلتر می‌شوند.
        Assert.Equal(30m, model.Kpis.ExpensesUsd);
        Assert.Equal(10m, model.Kpis.InventoryInMt);
        Assert.Equal(4m, model.Kpis.InventoryOutMt);
        Assert.Equal(6m, model.Kpis.InventoryNetMt);
    }

    [Fact]
    public async Task Build_Hides_Accounting_Journals_When_Accounting_Is_Disabled()
    {
        using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var disabled = await new PeriodActivityService(db).BuildAsync(10, 1, accountingEnabled: false);
        Assert.Empty(disabled!.Journals);
        Assert.Equal(0, disabled.Kpis.JournalCount);
        Assert.Equal(0m, disabled.Kpis.JournalDebitUsd);

        var enabled = await new PeriodActivityService(db).BuildAsync(10, 1, accountingEnabled: true);
        Assert.Single(enabled!.Journals);
        Assert.Equal(1, enabled.Kpis.JournalCount);
        Assert.Equal(500m, enabled.Kpis.JournalDebitUsd);
    }

    [Fact]
    public async Task Build_Returns_Null_For_A_Period_Of_Another_Company()
    {
        using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        // دورهٔ ۱۰ متعلق به شرکتِ ۱ است؛ درخواست با شرکتِ ۲ نباید آن را ببیند.
        var model = await new PeriodActivityService(db).BuildAsync(10, companyId: 2, accountingEnabled: true);
        Assert.Null(model);
    }

    // ── داربست ────────────────────────────────────────────────────────────────────

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Companies.Add(new Company { Id = 1, Code = "A", Name = "Owner", Country = "AF", IsActive = true, IsSystemOwner = true });
        db.Companies.Add(new Company { Id = 2, Code = "B", Name = "Other", Country = "AF", IsActive = true });

        // موجودیت‌های مرجع تا nav در projection حل شود (در داده واقعی همیشه حل می‌شود).
        db.Products.Add(new Product { Id = 1, Code = "PRD", Name = "Product" });
        db.ExpenseTypes.Add(new ExpenseType { Id = 1, Code = "EXP", Name = "Expense" });

        db.FiscalYears.Add(new FiscalYear
        {
            Id = 100, CompanyId = 1, Name = "FY-2026",
            StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31),
            Status = FiscalYearStatus.Open
        });
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            Id = 10, CompanyId = 1, FiscalYearId = 100, PeriodNumber = 3, Name = "P3",
            StartDate = new DateTime(2026, 3, 1), EndDate = new DateTime(2026, 3, 31),
            Status = FiscalPeriodStatus.Open
        });

        // فروش: داخل بازه/شرکت۱ (شمرده)، خارج بازه (حذف)، داخل بازه ولی شرکت۲ (حذف).
        db.SalesTransactions.Add(new SalesTransaction { Id = 1, CompanyId = 1, ProductId = 1, SaleDate = InRange, QuantityMt = 5m, TotalUsd = 500m, IsCancelled = false });
        db.SalesTransactions.Add(new SalesTransaction { Id = 2, CompanyId = 1, ProductId = 1, SaleDate = OutOfRange, QuantityMt = 9m, TotalUsd = 900m, IsCancelled = false });
        db.SalesTransactions.Add(new SalesTransaction { Id = 3, CompanyId = 2, ProductId = 1, SaleDate = InRange, QuantityMt = 7m, TotalUsd = 700m, IsCancelled = false });

        // خرید: قرارداد خریدِ داخل بازه/شرکت۱ (شمرده)، قرارداد فروش (حذف)، خریدِ شرکت۲ (حذف).
        db.Contracts.Add(new Contract { Id = 1, CompanyId = 1, ProductId = 1, ContractNumber = "P-1", ContractType = ContractType.Purchase, ContractDate = InRange, QuantityMt = 100m, Status = ContractStatus.Active });
        db.Contracts.Add(new Contract { Id = 2, CompanyId = 1, ProductId = 1, ContractNumber = "S-1", ContractType = ContractType.Sale, ContractDate = InRange, QuantityMt = 50m, Status = ContractStatus.Active });
        db.Contracts.Add(new Contract { Id = 3, CompanyId = 2, ProductId = 1, ContractNumber = "P-2", ContractType = ContractType.Purchase, ContractDate = InRange, QuantityMt = 20m, Status = ContractStatus.Active });

        // دریافت/پرداخت: داخل بازه/شرکت۱ شمرده، شرکت۲ حذف.
        db.PaymentTransactions.Add(new PaymentTransaction { Id = 1, CompanyId = 1, Direction = PaymentDirection.In, PaymentKind = PaymentKind.CustomerReceipt, PaymentDate = InRange, AmountUsd = 200m, CashAccountId = 1 });
        db.PaymentTransactions.Add(new PaymentTransaction { Id = 2, CompanyId = 1, Direction = PaymentDirection.Out, PaymentKind = PaymentKind.SupplierPayment, PaymentDate = InRange, AmountUsd = 80m, CashAccountId = 1 });
        db.PaymentTransactions.Add(new PaymentTransaction { Id = 3, CompanyId = 2, Direction = PaymentDirection.In, PaymentKind = PaymentKind.CustomerReceipt, PaymentDate = InRange, AmountUsd = 999m, CashAccountId = 1 });

        // مصارف: بدون شرکت — داخل بازه شمرده، خارج بازه حذف.
        db.ExpenseTransactions.Add(new ExpenseTransaction { Id = 1, ExpenseTypeId = 1, ExpenseDate = InRange, AmountUsd = 30m, IsCancelled = false });
        db.ExpenseTransactions.Add(new ExpenseTransaction { Id = 2, ExpenseTypeId = 1, ExpenseDate = OutOfRange, AmountUsd = 70m, IsCancelled = false });

        // بارگیری: بدون شرکت — داخل بازه.
        db.LoadingRegisters.Add(new LoadingRegister { Id = 1, ContractId = 1, ProductId = 1, LoadingDate = InRange, LoadedQuantityMt = 12m });

        // موجودی: ورود ۱۰، خروج ۴ (داخل بازه)، و یک ورودِ خارج بازه که نباید شمرده شود.
        db.InventoryMovements.Add(new InventoryMovement { Id = 1, TerminalId = 1, ProductId = 1, Direction = MovementDirection.In, MovementDate = InRange, QuantityMt = 10m });
        db.InventoryMovements.Add(new InventoryMovement { Id = 2, TerminalId = 1, ProductId = 1, Direction = MovementDirection.Out, MovementDate = InRange, QuantityMt = 4m });
        db.InventoryMovements.Add(new InventoryMovement { Id = 3, TerminalId = 1, ProductId = 1, Direction = MovementDirection.In, MovementDate = OutOfRange, QuantityMt = 99m });

        // اسناد حسابداری: یک سندِ Posted داخل بازهٔ شرکت۱ با بدهکار ۵۰۰.
        db.JournalEntries.Add(new JournalEntry
        {
            Id = 1, CompanyId = 1, Status = JournalEntryStatus.Posted, AccountingDate = InRange,
            JournalNumber = "JV-1", FiscalYearId = 100, FiscalPeriodId = 10,
            Lines = new List<JournalEntryLine>
            {
                new() { Id = 1, AccountId = 1, Debit = 500m, Credit = 0m },
                new() { Id = 2, AccountId = 2, Debit = 0m, Credit = 500m }
            }
        });
    }
}
