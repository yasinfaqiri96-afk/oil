using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Customs;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Customs;
using PTGOilSystem.Web.Services.Reporting;
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
        int? brokerId = null,
        int? serviceCashAccountId = null,
        CustomsDeclarationItem[]? items = null)
    {
        var db = CreateDb();
        db.CashAccounts.Add(new CashAccount { Id = 5, Name = "صندوق افغانی", Currency = "AFN" });
        db.CashAccounts.Add(new CashAccount { Id = 6, Name = "صندوق دالری", Currency = "USD" });
        db.CashAccounts.Add(new CashAccount { Id = 7, Name = "صندوق روبل", Currency = "RUB" });
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
            ServiceCashAccountId = serviceCashAccountId,
            ServiceProviderId = brokerId,
            Items = items?.ToList() ??
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

    // ------------------------------------------------- ارزِ صندوق

    /// <summary>
    /// «نقد پرداخت شد» از صندوق افغانی: همان مبلغ افغانیِ ردیف‌ها از صندوق کم می‌شود و معادل
    /// دالری عیناً همان معادلِ اظهارنامه می‌ماند. نرخ طوری است که Amount × نرخ = AmountUsd (شرطِ
    /// دفتر حسابداری دوطرفه).
    /// </summary>
    [Fact]
    public async Task Duty_Paid_From_Afn_Cash_Is_Posted_In_Afn_With_The_Declared_Usd()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Payable,
            dutyCashAccountId: 5,
            brokerId: 9);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var duty = await ExpenseAsync(db, declaration, CustomsComponentGroup.GovernmentDuty);
        Assert.Equal(42000m, duty.Amount);
        Assert.Equal("AFN", duty.Currency);
        Assert.Equal(600m, duty.AmountUsd);
        Assert.Equal(FxRateMath.RoundRate(600m / 42000m), duty.AppliedFxRateToUsd);
        Assert.Equal(duty.AmountUsd, decimal.Round(duty.Amount * duty.AppliedFxRateToUsd!.Value, 4, MidpointRounding.AwayFromZero));

        var ledger = await db.LedgerEntries.SingleAsync(l => l.SourceId == duty.Id);
        Assert.Equal(600m, ledger.AmountUsd);
        Assert.Equal("USD", ledger.Currency);
        Assert.Equal(42000m, ledger.SourceAmount);
        Assert.Equal("AFN", ledger.SourceCurrencyCode);
        Assert.Equal(duty.AppliedFxRateToUsd, ledger.AppliedFxRateToUsd);

        // کمیشنکارِ «بدهی» مثل قبل دالری است.
        var service = await ExpenseAsync(db, declaration, CustomsComponentGroup.ThirdPartyService);
        Assert.Equal(100m, service.Amount);
        Assert.Equal("USD", service.Currency);
        Assert.Equal(1m, service.AppliedFxRateToUsd);
    }

    [Fact]
    public async Task Duty_Paid_From_Usd_Cash_Stays_In_Usd()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Payable,
            dutyCashAccountId: 6,
            brokerId: 9);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var duty = await ExpenseAsync(db, declaration, CustomsComponentGroup.GovernmentDuty);
        Assert.Equal(600m, duty.Amount);
        Assert.Equal("USD", duty.Currency);
        Assert.Equal(1m, duty.AppliedFxRateToUsd);
        Assert.Equal(600m, duty.AmountUsd);
    }

    /// <summary>
    /// موجودی صندوق قبل و بعد از پرداخت: صندوق افغانی دقیقاً به‌اندازهٔ مبلغ افغانیِ حقوق دولتی و
    /// کمیشن کم می‌شود و چون همهٔ اسنادش افغانی‌اند، جمعش به افغانی می‌ماند (نه دالر).
    /// </summary>
    [Fact]
    public async Task Afn_Cash_Balance_Drops_By_The_Afn_Amount_Of_Duty_And_Commission()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.PaidImmediately,
            dutyCashAccountId: 5,
            serviceCashAccountId: 5);
        using var _ = db;
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 5, 1),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 5,
            FundingSource = PaymentFundingSource.Company,
            Amount = 100000m,
            Currency = "AFN",
            AmountUsd = 1428.5714m
        });
        await db.SaveChangesAsync();

        var before = await CashTotalsAsync(db, 5);
        Assert.Equal("AFN", before.TotalsCurrency);
        Assert.Equal(100000m, before.Balance);

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var after = await CashTotalsAsync(db, 5);
        Assert.False(after.UsesUsdTotals);
        Assert.Equal("AFN", after.TotalsCurrency);
        Assert.Equal(42000m + 7000m, after.NativeOut);
        Assert.Equal(100000m - 49000m, after.Balance);
        Assert.Equal(600m + 100m, after.UsdOut);

        var service = await ExpenseAsync(db, declaration, CustomsComponentGroup.ThirdPartyService);
        Assert.Equal(7000m, service.Amount);
        Assert.Equal("AFN", service.Currency);
        Assert.Equal(100m, service.AmountUsd);
    }

    [Fact]
    public async Task Usd_Cash_Balance_Drops_By_The_Usd_Amount()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.PaidImmediately,
            dutyCashAccountId: 6,
            serviceCashAccountId: 6);
        using var _ = db;
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            PaymentDate = new DateTime(2026, 5, 1),
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.ManualReceipt,
            CashAccountId = 6,
            FundingSource = PaymentFundingSource.Company,
            Amount = 1000m,
            Currency = "USD",
            AmountUsd = 1000m
        });
        await db.SaveChangesAsync();

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var after = await CashTotalsAsync(db, 6);
        Assert.Equal("USD", after.TotalsCurrency);
        Assert.Equal(700m, after.NativeOut);
        Assert.Equal(300m, after.Balance);
    }

    /// <summary>ردیف دالریِ بدون نرخ معادل افغانی ندارد؛ سندِ افغانیِ ناقص ساخته نمی‌شود.</summary>
    [Fact]
    public async Task Afn_Cash_Payment_Without_The_Afn_Amount_Of_Every_Row_Is_Rejected()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Unknown,
            dutyCashAccountId: 5,
            items:
            [
                Item(CustomsComponentType.Mahsooli, 600m),
                new CustomsDeclarationItem { ComponentType = CustomsComponentType.MahsooliDolari, AmountAfn = 0m, AmountUsd = 300m }
            ]);
        using var _ = db;

        var error = await Assert.ThrowsAsync<PTGOilSystem.Web.Services.Exceptions.BusinessRuleException>(
            () => CustomsDeclarationExpenseSync.SyncAsync(db, declaration));
        Assert.Equal(CustomsDeclarationExpenseSync.CashCurrencyMismatchCode, error.Code);
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Cash_Payment_From_An_Account_Neither_Afn_Nor_Usd_Is_Rejected()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Unknown,
            dutyCashAccountId: 7);
        using var _ = db;

        await Assert.ThrowsAsync<PTGOilSystem.Web.Services.Exceptions.BusinessRuleException>(
            () => CustomsDeclarationExpenseSync.SyncAsync(db, declaration));
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    /// <summary>
    /// تغییر صندوق یا حالتِ تسویه همان سند و همان سطر دفتر کل را درجا اصلاح می‌کند؛ سند تکراری
    /// ساخته نمی‌شود و معادل دالری ثابت می‌ماند.
    /// </summary>
    [Fact]
    public async Task Changing_The_Cash_Account_Updates_The_Same_Expense_And_Ledger_Row()
    {
        var (db, declaration) = await SeedAsync(
            ExpenseSettlementMode.PaidImmediately,
            ExpenseSettlementMode.Unknown,
            dutyCashAccountId: 6);
        using var _ = db;

        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);
        var original = await ExpenseAsync(db, declaration, CustomsComponentGroup.GovernmentDuty);
        Assert.Equal("USD", original.Currency);

        declaration.DutyCashAccountId = 5;
        await db.SaveChangesAsync();
        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var afn = Assert.Single(await db.ExpenseTransactions.Where(e => !e.IsCancelled).ToListAsync());
        Assert.Equal(original.Id, afn.Id);
        Assert.Equal(42000m, afn.Amount);
        Assert.Equal("AFN", afn.Currency);
        Assert.Equal(600m, afn.AmountUsd);
        var ledger = Assert.Single(await db.LedgerEntries.ToListAsync());
        Assert.Equal(afn.Id, ledger.SourceId);
        Assert.Equal("AFN", ledger.SourceCurrencyCode);
        Assert.Equal(42000m, ledger.SourceAmount);
        Assert.Equal(600m, ledger.AmountUsd);

        // حالت «بدون حرکت پول» ⇒ دوباره دالری، بدون صندوق.
        declaration.DutySettlementMode = ExpenseSettlementMode.NonCash;
        declaration.DutyCashAccountId = null;
        await db.SaveChangesAsync();
        await CustomsDeclarationExpenseSync.SyncAsync(db, declaration);

        var nonCash = Assert.Single(await db.ExpenseTransactions.Where(e => !e.IsCancelled).ToListAsync());
        Assert.Equal(original.Id, nonCash.Id);
        Assert.Equal(600m, nonCash.Amount);
        Assert.Equal("USD", nonCash.Currency);
        Assert.Equal(1m, nonCash.AppliedFxRateToUsd);
        Assert.Null(nonCash.CashAccountId);
        Assert.Single(await db.LedgerEntries.ToListAsync());
    }

    /// <summary>
    /// با نرخ ۱۲ رقمی، مبالغ بزرگ و نرخ‌های مختلطِ ردیف‌ها هم دقیقاً همان USD اظهارنامه را می‌سازند.
    /// </summary>
    [Theory]
    [InlineData("1500", "110000")]
    [InlineData("1000", "70000")]
    [InlineData("142857.14", "10000000")]
    [InlineData("3333.33", "250000")]
    [InlineData("0.01", "1")]
    public void Afn_Cash_Amount_Reproduces_The_Declared_Usd_Exactly(string usdText, string afnText)
    {
        var usd = decimal.Parse(usdText, System.Globalization.CultureInfo.InvariantCulture);
        var afn = decimal.Parse(afnText, System.Globalization.CultureInfo.InvariantCulture);

        var cash = CustomsDeclarationExpenseSync.ResolveCashAmount("AFN", usd, afn);

        Assert.NotNull(cash);
        Assert.Equal(afn, cash.Amount);
        Assert.Equal("AFN", cash.Currency);
        Assert.Equal(usd, decimal.Round(cash.Amount * cash.RateToUsd, 4, MidpointRounding.AwayFromZero));
        Assert.Equal(cash.RateToUsd, FxRateMath.RoundRate(cash.RateToUsd));
    }

    [Fact]
    public void Afn_Cash_Amount_Is_Unknown_Without_An_Afn_Amount_Or_For_Another_Currency()
    {
        Assert.Null(CustomsDeclarationExpenseSync.ResolveCashAmount("AFN", 600m, null));
        Assert.Null(CustomsDeclarationExpenseSync.ResolveCashAmount("AFN", 600m, 0m));
        Assert.Null(CustomsDeclarationExpenseSync.ResolveCashAmount("RUB", 600m, 42000m));
        Assert.Equal(new CustomsCashAmount(600m, "USD", 1m), CustomsDeclarationExpenseSync.ResolveCashAmount("usd", 600m, 42000m));
    }

    // ------------------------------------------------- فرم اظهارنامه

    [Fact]
    public async Task Form_Rejects_Afn_Cash_Payment_When_A_Usd_Row_Has_No_Rate()
    {
        var (db, _) = await SeedAsync(ExpenseSettlementMode.Unknown, ExpenseSettlementMode.Unknown);
        using var _db = db;

        var controller = NewCustomsController(db);
        var result = await controller.Create(FormModel(
            dutyCashAccountId: 5,
            Row(CustomsComponentType.Mahsooli, "AFN", 42000m, 70m),
            Row(CustomsComponentType.MahsooliDolari, "USD", 300m, null)));

        Assert.IsType<ViewResult>(result);
        Assert.Contains(
            controller.ModelState[nameof(CustomsDeclarationCreateViewModel.DutyCashAccountId)]!.Errors,
            e => e.ErrorMessage.Contains("معادل افغانیِ همهٔ ردیف‌های این گروه", StringComparison.Ordinal));
        Assert.Equal(1, await db.CustomsDeclarations.CountAsync());
        Assert.Empty(await db.ExpenseTransactions.ToListAsync());
    }

    [Fact]
    public async Task Form_Rejects_Cash_Payment_When_An_Afn_Row_Has_No_Rate()
    {
        var (db, _) = await SeedAsync(ExpenseSettlementMode.Unknown, ExpenseSettlementMode.Unknown);
        using var _db = db;

        var controller = NewCustomsController(db);
        await controller.Create(FormModel(
            dutyCashAccountId: 6,
            Row(CustomsComponentType.Mahsooli, "AFN", 42000m, null)));

        Assert.Contains(
            controller.ModelState[nameof(CustomsDeclarationCreateViewModel.DutyCashAccountId)]!.Errors,
            e => e.ErrorMessage.Contains("نرخ تبدیل را وارد کنید", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Form_Accepts_Afn_Cash_Payment_When_Every_Row_Has_A_Rate()
    {
        var (db, _) = await SeedAsync(ExpenseSettlementMode.Unknown, ExpenseSettlementMode.Unknown);
        using var _db = db;

        var controller = NewCustomsController(db);
        await controller.Create(FormModel(
            dutyCashAccountId: 5,
            Row(CustomsComponentType.Mahsooli, "AFN", 70000m, 70m),
            Row(CustomsComponentType.MahsooliDolari, "USD", 500m, 80m)));

        // تنها خطا نبودِ منبع (بارگیری/انتقال/موتر) است؛ صندوق افغانی پذیرفته شده.
        Assert.False(controller.ModelState.ContainsKey(nameof(CustomsDeclarationCreateViewModel.DutyCashAccountId)));
    }

    private static CustomsDeclarationsController NewCustomsController(ApplicationDbContext db)
        => new(db, NullLogger<CustomsDeclarationsController>.Instance, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static CustomsDeclarationItemRowViewModel Row(CustomsComponentType type, string currency, decimal amount, decimal? rate)
        => new() { ComponentType = type, Currency = currency, Amount = amount, Rate = rate };

    private static CustomsDeclarationCreateViewModel FormModel(int dutyCashAccountId, params CustomsDeclarationItemRowViewModel[] rows)
        => new()
        {
            DeclarationDate = new DateTime(2026, 5, 20),
            DutySettlementMode = ExpenseSettlementMode.PaidImmediately,
            DutyCashAccountId = dutyCashAccountId,
            ServiceSettlementMode = ExpenseSettlementMode.Unknown,
            Items = rows.ToList()
        };

    private static Task<ExpenseTransaction> ExpenseAsync(
        ApplicationDbContext db,
        CustomsDeclaration declaration,
        CustomsComponentGroup group)
        => db.ExpenseTransactions.SingleAsync(e => e.CustomsDeclarationId == declaration.Id
            && e.CustomsComponentGroup == group
            && !e.IsCancelled);

    private static async Task<CashAccountActivityTotals> CashTotalsAsync(ApplicationDbContext db, int cashAccountId)
        => (await new CashPositionReader(db).ReadAccountTotalsAsync([cashAccountId], null)).Single();
}
