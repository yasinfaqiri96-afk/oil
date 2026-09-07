using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Customs;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-04 — گمرک از انبارِ مالیِ موازی به همان خط لولهٔ مصرف/دفتر کل.
///
/// پیش از این، هزینهٔ گمرک فقط در <c>CustomsDeclarations.TotalUsd</c> بود و گزارش سود و
/// زیان آن را مستقیم می‌خواند: نه سطرِ دفتری داشت، نه در حساب طرف‌حسابی می‌نشست، و اگر
/// همان هزینه از فرم «مصارف گمرکی» هم ثبت می‌شد در P&amp;L دو بار شمرده می‌شد.
///
/// یک اظهارنامه دو جنسِ پول دارد و این تست‌ها همان تفکیک را pin می‌کنند: حقوق دولتی که
/// در گمرک نقد می‌شود، و کمیشنکار که بدهیِ یک طرف‌حسابِ واقعی است.
/// </summary>
public sealed class CustomsSettlementTests
{
    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CustomsDeclarationItem Item(CustomsComponentType type, decimal usd)
        => new() { ComponentType = type, AmountAfn = usd * 70m, AmountUsd = usd };

    // ------------------------------------------------- تفکیکِ اجزا

    /// <summary>
    /// محصولی و فواید عامه در گمرک و به‌نامِ دولت پرداخت می‌شوند؛ کمیشنکار صورت‌حسابِ یک
    /// شخصِ مشخص است. جمعِ دو گروه باید با کلِ اظهارنامه یکی بماند.
    /// </summary>
    [Fact]
    public void Government_Duty_And_Third_Party_Service_Are_Split_Without_Losing_A_Cent()
    {
        var items = new[]
        {
            Item(CustomsComponentType.Mahsooli, 500m),
            Item(CustomsComponentType.FawaidAama, 100m),
            Item(CustomsComponentType.Komisionkar, 60m),
            Item(CustomsComponentType.KomisionBank, 40m)
        };

        var (dutyUsd, serviceUsd) = CustomsDeclarationExpenseSync.SplitUsd(items);

        Assert.Equal(600m, dutyUsd);
        Assert.Equal(100m, serviceUsd);
        Assert.Equal(items.Sum(i => i.AmountUsd ?? 0m), dutyUsd + serviceUsd);
    }

    [Theory]
    [InlineData(CustomsComponentType.Mahsooli, CustomsComponentGroup.GovernmentDuty)]
    [InlineData(CustomsComponentType.MahsooliDolari, CustomsComponentGroup.GovernmentDuty)]
    [InlineData(CustomsComponentType.FawaidAama, CustomsComponentGroup.GovernmentDuty)]
    [InlineData(CustomsComponentType.GomrokSarhadi, CustomsComponentGroup.GovernmentDuty)]
    [InlineData(CustomsComponentType.Komisionkar, CustomsComponentGroup.ThirdPartyService)]
    [InlineData(CustomsComponentType.KomisionBank, CustomsComponentGroup.ThirdPartyService)]
    [InlineData(CustomsComponentType.KomisionBarchalani, CustomsComponentGroup.ThirdPartyService)]
    [InlineData(CustomsComponentType.TarazuMotor, CustomsComponentGroup.ThirdPartyService)]
    public void Each_Component_Has_One_Documented_Group(
        CustomsComponentType componentType,
        CustomsComponentGroup expected)
        => Assert.Equal(expected, CustomsComponentGroupMap.Resolve(componentType));

    /// <summary>
    /// جزءِ تازه‌ای که در نقشه تصمیم‌گیری نشده باشد باید همان لحظه سر و صدا کند، نه اینکه
    /// خاموش در گروهِ غلط پول جابه‌جا کند.
    /// </summary>
    [Fact]
    public void An_Unclassified_Component_Throws_Instead_Of_Falling_Into_A_Default_Group()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => CustomsComponentGroupMap.Resolve((CustomsComponentType)12345));

    // ------------------------------------------------- خط لولهٔ مالی

    private static async Task<(ApplicationDbContext Db, CustomsDeclaration Declaration)> SeedAsync(
        ExpenseSettlementMode dutyMode,
        ExpenseSettlementMode serviceMode,
        int? dutyCashAccountId = null,
        int? brokerId = null)
    {
        var db = CreateDb();
        db.CashAccounts.Add(new CashAccount { Id = 5, Name = "صندوق افغانی", Currency = "AFN" });
        db.ServiceProviders.Add(new ServiceProvider
        {
            Id = 9,
            Name = "کمیشنکار حیرتان",
            ProviderType = ServiceProviderType.CustomsBroker
        });

        var declaration = new CustomsDeclaration
        {
            DeclarationDate = new DateTime(2026, 5, 20),
            WagonOrTruckNumber = "WGN-1",
            DutySettlementMode = dutyMode,
            DutyCashAccountId = dutyCashAccountId,
            ServiceSettlementMode = serviceMode,
            ServiceProviderId = brokerId,
            Items =
            [
                Item(CustomsComponentType.Mahsooli, 600m),
                Item(CustomsComponentType.Komisionkar, 100m)
            ]
        };
        db.CustomsDeclarations.Add(declaration);
        await db.SaveChangesAsync();
        return (db, declaration);
    }

    /// <summary>
    /// حالتِ رایجِ افغانستان: حقوق دولتی همان‌جا نقد می‌شود و کمیشنکار بدهی می‌ماند. یک
    /// اظهارنامه، دو مصرف، دو هویتِ متفاوت — چیزی که با یک انتخابِ مشترک قابل ثبت نبود.
    /// </summary>
    [Fact]
    public async Task Duty_Paid_In_Cash_And_Broker_Left_As_A_Payable_Become_Two_Separate_Expenses()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Payable,
            dutyCashAccountId: 5,
            brokerId: 9);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var expenses = await db.ExpenseTransactions
            .Where(e => e.CustomsDeclarationId == declaration.Id && !e.IsCancelled)
            .ToListAsync();
        Assert.Equal(2, expenses.Count);

        var duty = expenses.Single(e => e.CustomsComponentGroup == CustomsComponentGroup.GovernmentDuty);
        Assert.Equal(600m, duty.AmountUsd);
        Assert.Equal(ExpenseSettlementMode.PaidImmediately, duty.SettlementMode);
        Assert.Equal(5, duty.CashAccountId);
        Assert.Null(duty.CounterpartyType);

        var service = expenses.Single(e => e.CustomsComponentGroup == CustomsComponentGroup.ThirdPartyService);
        Assert.Equal(100m, service.AmountUsd);
        Assert.Equal(ExpenseSettlementMode.Payable, service.SettlementMode);
        Assert.Equal(AccountingPartyType.ServiceProvider, service.CounterpartyType);
        Assert.Equal(9, service.CounterpartyId);
        Assert.Null(service.CashAccountId);
    }

    /// <summary>هزینهٔ گمرک باید در دفتر کل سطر داشته باشد — چیزی که قبلاً نداشت.</summary>
    [Fact]
    public async Task Customs_Now_Reaches_The_Ledger()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Payable,
            dutyCashAccountId: 5,
            brokerId: 9);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var expenseIds = await db.ExpenseTransactions
            .Where(e => e.CustomsDeclarationId == declaration.Id)
            .Select(e => e.Id)
            .ToListAsync();
        var ledgers = await db.LedgerEntries
            .Where(l => l.SourceType == ExpenseLedgerPoster.ExpenseSourceType
                && expenseIds.Contains(l.SourceId))
            .ToListAsync();

        Assert.Equal(2, ledgers.Count);
        Assert.Equal(700m, ledgers.Sum(l => l.AmountUsd));

        // بدهی به کمیشنکار سطرِ بستانکار روی حساب همان طرف است؛ حقوق دولتیِ نقدشده طرف‌حساب ندارد.
        var brokerLedger = ledgers.Single(l => l.ServiceProviderId == 9);
        Assert.Equal(LedgerSide.Credit, brokerLedger.Side);
        Assert.Equal(100m, brokerLedger.AmountUsd);
    }

    /// <summary>
    /// ذخیرهٔ دوباره نباید مصرفِ دوم بسازد — وگرنه هر ویرایشِ اظهارنامه هزینه را در P&amp;L
    /// تکرار می‌کرد.
    /// </summary>
    [Fact]
    public async Task Saving_The_Same_Declaration_Again_Creates_No_Duplicate_Expense()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Payable,
            dutyCashAccountId: 5,
            brokerId: 9);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);
        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);
        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var active = await db.ExpenseTransactions
            .Where(e => e.CustomsDeclarationId == declaration.Id && !e.IsCancelled)
            .ToListAsync();
        var ledgerCount = await db.LedgerEntries
            .CountAsync(l => l.SourceType == ExpenseLedgerPoster.ExpenseSourceType);

        Assert.Equal(2, active.Count);
        Assert.Equal(2, ledgerCount);
        Assert.Equal(700m, active.Sum(e => e.AmountUsd));
    }

    /// <summary>
    /// تا وقتی کاربر هویتِ تسویه را نگفته، هیچ سطرِ مالی ساخته نمی‌شود: حدس‌زدن یعنی
    /// ساختنِ بدهی یا پرداختی که وجود نداشته. گزارش‌ها همان اظهارنامه را جدا نشان می‌دهند.
    /// </summary>
    [Fact]
    public async Task An_Unclassified_Declaration_Raises_No_Expense_And_No_Ledger_Row()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.Unknown,
            ExpenseSettlementMode.Unknown);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    /// <summary>گروهی که مبلغش صفر شده باید مصرفش لغو شود، نه اینکه هزینهٔ مرده بماند.</summary>
    [Fact]
    public async Task Clearing_A_Group_Amount_Cancels_Its_Expense()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Payable,
            dutyCashAccountId: 5,
            brokerId: 9);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        // کمیشنکار از اظهارنامه برداشته می‌شود.
        var brokerItem = declaration.Items.Single(i => i.ComponentType == CustomsComponentType.Komisionkar);
        declaration.Items.Remove(brokerItem);
        db.CustomsDeclarationItems.Remove(brokerItem);
        await db.SaveChangesAsync();

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var active = await db.ExpenseTransactions
            .Where(e => e.CustomsDeclarationId == declaration.Id && !e.IsCancelled)
            .ToListAsync();
        Assert.Single(active);
        Assert.Equal(CustomsComponentGroup.GovernmentDuty, active[0].CustomsComponentGroup);
        Assert.Equal(600m, active[0].AmountUsd);
    }

    /// <summary>
    /// بدهیِ کمیشنکار بدونِ طرف‌حساب نباید ذخیره شود — همان «بدهیِ نامرئی» که فاز ۱ برای
    /// بستنش آمده.
    /// </summary>
    [Fact]
    public async Task A_Payable_Group_Without_A_Broker_Is_Rejected()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.NonCash,
            ExpenseSettlementMode.Payable,
            brokerId: null);
        using var _ = db;

        await Assert.ThrowsAsync<ExpenseSettlementValidationException>(
            () => CustomsDeclarationExpenseSync.SyncAsync(db, declaration));
    }

    /// <summary>پرداختِ نقدی بدون حساب نقدی هم همان‌طور رد می‌شود.</summary>
    [Fact]
    public async Task A_Paid_Group_Without_A_Cash_Account_Is_Rejected()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.NonCash,
            dutyCashAccountId: null);
        using var _ = db;

        await Assert.ThrowsAsync<ExpenseSettlementValidationException>(
            () => CustomsDeclarationExpenseSync.SyncAsync(db, declaration));
    }

    /// <summary>
    /// حذفِ اظهارنامه باید مصرف و سطرِ دفترش را ببرد، وگرنه هزینه‌ای بی‌سند در P&amp;L
    /// می‌ماند.
    /// </summary>
    [Fact]
    public async Task Cancelling_A_Declaration_Removes_Its_Ledger_Rows()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Payable,
            dutyCashAccountId: 5,
            brokerId: 9);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);
        await CustomsDeclarationExpenseSync.CancelByDeclarationIdAsync(db, declaration.Id);

        Assert.Empty(await db.ExpenseTransactions.Where(e => !e.IsCancelled).ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }
}
