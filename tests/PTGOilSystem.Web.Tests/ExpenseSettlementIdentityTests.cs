using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Expenses;
using PTGOilSystem.Web.Services.Ledger;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// PTG-P1-04 — «هر مصرف دقیقاً یک هویت تسویه دارد».
///
/// پیش از فاز ۱ یک مصرف می‌توانست نه پرداخت‌شده باشد و نه بدهی به کسی: سطرش در دفتر
/// می‌نشست، در هیچ حساب طرف‌حسابی دیده نمی‌شد و از «طلبات و بدهی‌ها» بیرون می‌افتاد.
/// این تست‌ها سه چیز را pin می‌کنند: هویت درست از روی طرف‌حساب، ردنشدنِ ردیف‌های
/// تاریخی، و اینکه سندِ ناقص اصلاً ذخیره نمی‌شود.
/// </summary>
public sealed class ExpenseSettlementIdentityTests
{
    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ExpenseTransaction Expense(Action<ExpenseTransaction>? configure = null)
    {
        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = 1,
            ExpenseDate = new DateTime(2026, 5, 20),
            Amount = 500m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 500m,
            Description = "کرایه"
        };
        configure?.Invoke(expense);
        return expense;
    }

    private static readonly IExpenseSettlementValidator Validator = new ExpenseSettlementValidator();

    // ------------------------------------------------- بدهی به طرف‌حساب

    /// <summary>
    /// بدهی به شرکت خدماتی: همان طرفی که سطرِ <c>Credit</c> دفتر روی حسابش می‌نشیند،
    /// حالا روی خودِ سند هم صریح است.
    /// </summary>
    [Fact]
    public void Service_Provider_Expense_Is_A_Payable_To_That_Service_Provider()
    {
        var expense = Expense(e => e.ServiceProviderId = 42);

        ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);

        Assert.Equal(ExpenseSettlementMode.Payable, expense.SettlementMode);
        Assert.Equal(AccountingPartyType.ServiceProvider, expense.CounterpartyType);
        Assert.Equal(42, expense.CounterpartyId);
        Assert.Null(expense.CashAccountId);
        Assert.True(Validator.Check(expense).IsValid);
    }

    /// <summary>
    /// ریشهٔ «کرایهٔ راننده در طلبات و بدهی‌ها دیده نمی‌شد»: بدهی به موتروانِ مستقل هم
    /// یک بدهیِ واقعی است، نه هزینهٔ بی‌طرف‌حساب.
    /// </summary>
    [Fact]
    public void Driver_Expense_Is_A_Payable_To_That_Driver()
    {
        var expense = Expense(e => e.DriverId = 7);

        ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);

        Assert.Equal(ExpenseSettlementMode.Payable, expense.SettlementMode);
        Assert.Equal(AccountingPartyType.Driver, expense.CounterpartyType);
        Assert.Equal(7, expense.CounterpartyId);
        Assert.True(Validator.Check(expense).IsValid);
    }

    /// <summary>
    /// ترتیب همان <see cref="ExpenseAccountingAdapter.ResolveParty"/> است و از همان‌جا
    /// خوانده می‌شود؛ اگر روزی آن ترتیب عوض شود، این تست باید بشکند.
    /// </summary>
    [Fact]
    public void Service_Provider_Wins_Over_Driver_Just_Like_The_Accounting_Party_Rule()
    {
        var expense = Expense(e =>
        {
            e.ServiceProviderId = 42;
            e.DriverId = 7;
        });

        ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);
        var (partyType, partyId) = ExpenseAccountingAdapter.ResolveParty(expense);

        Assert.Equal(partyType, expense.CounterpartyType);
        Assert.Equal(partyId, expense.CounterpartyId);
        Assert.Equal(AccountingPartyType.ServiceProvider, expense.CounterpartyType);
    }

    /// <summary>
    /// جهتِ سطرِ دفتر و هویتِ تسویه باید همیشه یک چیز بگویند: هر سندی که
    /// <c>Credit</c> می‌شود بدهی است، و هر <c>Debit</c> بدونِ طرف‌حساب.
    /// </summary>
    [Theory]
    [InlineData(42, null, LedgerSide.Credit, ExpenseSettlementMode.Payable)]
    [InlineData(null, 7, LedgerSide.Credit, ExpenseSettlementMode.Payable)]
    [InlineData(null, null, LedgerSide.Debit, ExpenseSettlementMode.NonCash)]
    public void Settlement_Identity_Never_Disagrees_With_The_Ledger_Side(
        int? serviceProviderId,
        int? driverId,
        LedgerSide expectedSide,
        ExpenseSettlementMode expectedMode)
    {
        using var db = CreateDb();
        var poster = new ExpenseLedgerPoster(new LedgerPostingService(db));
        var expense = Expense(e =>
        {
            e.ServiceProviderId = serviceProviderId;
            e.DriverId = driverId;
        });

        ExpenseLedgerPoster.ApplyCounterpartySettlement(expense);

        Assert.Equal(expectedSide, poster.ResolveSide(expense));
        Assert.Equal(expectedMode, expense.SettlementMode);
    }

    // ------------------------------------------------- پرداخت‌شده از صندوق

    /// <summary>مصرفی که همان‌جا پرداخت شده، حساب نقدی دارد و بدهی‌ای نمی‌گذارد.</summary>
    [Fact]
    public void Paid_Expense_Keeps_Its_Cash_Account_And_Leaves_No_Payable()
    {
        var expense = Expense(e =>
        {
            e.SettlementMode = ExpenseSettlementMode.PaidImmediately;
            e.CashAccountId = 5;
        });

        var result = Validator.Check(expense);

        Assert.True(result.IsValid);
        Assert.Null(expense.CounterpartyType);
        Assert.Equal(5, expense.CashAccountId);
    }

    [Fact]
    public void Paid_Expense_Without_A_Cash_Account_Cannot_Be_Saved()
    {
        var expense = Expense(e => e.SettlementMode = ExpenseSettlementMode.PaidImmediately);

        var result = Validator.Check(expense);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseSettlementValidator.CashAccountMissing, result.Code);
    }

    /// <summary>
    /// پرداخت‌شده و بدهکار هم‌زمان بی‌معنی است: یا پول رفته، یا تعهدی مانده.
    /// </summary>
    [Fact]
    public void Paid_Expense_Cannot_Also_Carry_A_Counterparty()
    {
        var expense = Expense(e =>
        {
            e.SettlementMode = ExpenseSettlementMode.PaidImmediately;
            e.CashAccountId = 5;
            e.CounterpartyType = AccountingPartyType.ServiceProvider;
            e.CounterpartyId = 42;
        });

        var result = Validator.Check(expense);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseSettlementValidator.CounterpartyNotAllowed, result.Code);
    }

    // ------------------------------------------------- سندِ ناقص

    /// <summary>
    /// همان حالتی که فاز ۱ برای بستنش آمده: بدهیِ نامرئی — سندی که می‌گوید بدهکارم،
    /// بی‌آنکه بگوید به کی.
    /// </summary>
    [Fact]
    public void Payable_Without_A_Counterparty_Cannot_Be_Saved()
    {
        var expense = Expense(e => e.SettlementMode = ExpenseSettlementMode.Payable);

        var result = Validator.Check(expense);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseSettlementValidator.CounterpartyMissing, result.Code);
        Assert.Throws<ExpenseSettlementValidationException>(() => Validator.Validate(expense));
    }

    [Fact]
    public void Unpaid_Expense_Cannot_Hold_A_Cash_Account()
    {
        var expense = Expense(e =>
        {
            e.SettlementMode = ExpenseSettlementMode.Payable;
            e.CounterpartyType = AccountingPartyType.Driver;
            e.CounterpartyId = 7;
            e.CashAccountId = 5;
        });

        var result = Validator.Check(expense);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseSettlementValidator.CashAccountNotAllowed, result.Code);
    }

    [Fact]
    public void A_New_Expense_Cannot_Be_Saved_Unclassified()
    {
        var expense = Expense();

        var result = Validator.Check(expense);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseSettlementValidator.ModeMissing, result.Code);
    }

    // ------------------------------------------------- ردیف‌های تاریخی

    /// <summary>
    /// ردیفِ پیش از فاز ۱ روی <c>Unknown</c> می‌ماند و حدس زده نمی‌شود، ولی خواندن و
    /// ویرایشِ غیرمالی‌اش نباید به‌خاطر قاعدهٔ تازه رد شود.
    /// </summary>
    [Fact]
    public void An_Existing_Unclassified_Row_Stays_Editable()
    {
        var expense = Expense(e => e.Id = 900);

        Assert.True(Validator.Check(expense).IsValid);
    }

    /// <summary>مصرفِ لغوشده دیگر تعهدی ندارد، پس قاعده جلوی لغو را نمی‌گیرد.</summary>
    [Fact]
    public void A_Cancelled_Row_Is_Never_Rejected()
    {
        var expense = Expense(e =>
        {
            e.SettlementMode = ExpenseSettlementMode.Payable;
            e.IsCancelled = true;
        });

        Assert.True(Validator.Check(expense).IsValid);
    }

    /// <summary>
    /// همان چیزی که backfill می‌نویسد: فقط دو رابطهٔ قطعی، و ردیفِ بی‌طرف‌حساب
    /// دست‌نخورده روی «طبقه‌بندی‌نشده» می‌ماند تا در گزارش‌ها جدا دیده شود.
    /// </summary>
    [Fact]
    public void Backfill_Rule_Leaves_A_Row_With_No_Deterministic_Party_Unclassified()
    {
        var expense = Expense(e => e.Id = 900);

        var (partyType, _) = ExpenseAccountingAdapter.ResolveParty(expense);

        Assert.Null(partyType);
        Assert.Equal(ExpenseSettlementMode.Unknown, expense.SettlementMode);
    }
}
