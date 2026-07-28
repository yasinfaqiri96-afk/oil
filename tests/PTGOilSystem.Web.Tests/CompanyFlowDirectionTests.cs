using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.CompanyFlow;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// نگاشت «رسید/برد» از دید شرکت — مرجع: فایل‌های کاری BNK - Solvex و Petrogas.
/// در آن فایل‌ها ستون Credit همان چیزی است که شرکت داده (پرداخت) و ستون Debit همان چیزی
/// که گرفته (بار/خدمت)، و بیلانس = Credit − Debit + قبلی. شیت فارسی «بیلانس» همین را
/// صریح می‌گوید: بیلانس = مصارف − عواید + قبلی.
/// </summary>
public sealed class CompanyFlowDirectionTests
{
    private static readonly CompanyFlowDirectionResolver Resolver = new();

    private static CompanyFlowDirection Resolve(
        string sourceType,
        LedgerSide? side = null,
        CompanyFlowPartyRole role = CompanyFlowPartyRole.Unknown,
        CompanyFlowLifecycle lifecycle = CompanyFlowLifecycle.Original,
        PaymentDirection? paymentDirection = null)
        => Resolver.Resolve(new CompanyFlowEvent(sourceType, side, role, lifecycle, paymentDirection));

    // ---------------------------------------------------------------- تأمین‌کننده

    [Fact]
    public void Supplier_LoadingIsReceipt_AndPaymentIsOutflow()
    {
        Assert.Equal(
            CompanyFlowDirection.Receipt,
            Resolve(CompanyFlowSourceTypes.Loading, LedgerSide.Credit, CompanyFlowPartyRole.Supplier));
        Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, CompanyFlowPartyRole.Supplier));
    }

    [Fact]
    public void Supplier_RefundFromSupplierIsReceipt()
        => Assert.Equal(
            CompanyFlowDirection.Receipt,
            Resolve(nameof(PaymentKind.SupplierReceipt), LedgerSide.Credit, CompanyFlowPartyRole.Supplier));

    [Fact]
    public void Supplier_PaymentThroughSarrafIsOutflowOnTheSupplierAccount()
        => Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(CompanyFlowSourceTypes.SupplierViaSarrafPayment, LedgerSide.Debit, CompanyFlowPartyRole.Supplier));

    // -------------------------------------------------------------------- مشتری

    [Fact]
    public void Customer_SaleIsOutflow_AndReceiptFromCustomerIsReceipt()
    {
        Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(CompanyFlowSourceTypes.Sale, LedgerSide.Credit, CompanyFlowPartyRole.Customer));
        Assert.Equal(
            CompanyFlowDirection.Receipt,
            Resolve(nameof(PaymentKind.CustomerReceipt), LedgerSide.Debit, CompanyFlowPartyRole.Customer));
    }

    [Fact]
    public void Customer_RefundToCustomerIsOutflow()
        => Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(nameof(PaymentKind.CustomerPayment), LedgerSide.Credit, CompanyFlowPartyRole.Customer));

    // --------------------------------------------------------- شرکت خدماتی و راننده

    /// <summary>
    /// ایجاد مصرف و پرداخت همان مصرف نباید یک‌جهت باشند. هر دو با
    /// <see cref="LedgerSide.Debit"/> ثبت می‌شوند، پس اگر جهت از سمت خوانده شود بیلانس
    /// دو برابر می‌شد. مصرف «دریافت خدمت» است و پرداختش «برد».
    /// </summary>
    [Fact]
    public void ServiceProvider_ExpenseIsReceipt_AndItsPaymentIsOutflow_SoTheyCancelOut()
    {
        var expense = Resolve(CompanyFlowSourceTypes.Expense, LedgerSide.Debit, CompanyFlowPartyRole.ServiceProvider);
        var payment = Resolve(nameof(PaymentKind.ServiceProviderPayment), LedgerSide.Debit, CompanyFlowPartyRole.ServiceProvider);

        Assert.Equal(CompanyFlowDirection.Receipt, expense);
        Assert.Equal(CompanyFlowDirection.Outflow, payment);

        var balance = new CompanyFlowBalanceService().Summarize(
            0m,
            [new CompanyFlowAmount(expense, 500m), new CompanyFlowAmount(payment, 500m)],
            CompanyFlowAccountKind.PartyAccount);
        Assert.Equal(0m, balance.ClosingBalance);
        Assert.Equal(500m, balance.TotalReceipt);
        Assert.Equal(500m, balance.TotalOutflow);
    }

    [Fact]
    public void Driver_FreightPaymentIsOutflow_AndShortageClaimIsOutflow()
    {
        Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(nameof(PaymentKind.TruckPayment), LedgerSide.Debit, CompanyFlowPartyRole.Driver));
        // ادعای کسری روی حمل‌کننده: او به شرکت بدهکار می‌شود، پس بیلانس باید بالا برود.
        Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(CompanyFlowSourceTypes.ShortageCharge, LedgerSide.Debit, CompanyFlowPartyRole.Driver));
    }

    // -------------------------------------------------------------------- صراف

    [Fact]
    public void Sarraf_PaymentOnBehalfOfCompanyIsReceipt_AndMoneySentToSarrafIsOutflow()
    {
        // صراف به‌جای شرکت پرداخت کرده ⇒ ارزش را او فراهم کرده ⇒ رسید در حساب صراف.
        Assert.Equal(
            CompanyFlowDirection.Receipt,
            Resolve(CompanyFlowSourceTypes.SupplierViaSarrafPayable, LedgerSide.Credit, CompanyFlowPartyRole.Sarraf));
        // ارسال پول شرکت به صراف ⇒ برد.
        Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(nameof(PaymentTransaction), role: CompanyFlowPartyRole.Sarraf, paymentDirection: PaymentDirection.Out));
        // برگشت وجه از صراف ⇒ رسید.
        Assert.Equal(
            CompanyFlowDirection.Receipt,
            Resolve(nameof(PaymentTransaction), role: CompanyFlowPartyRole.Sarraf, paymentDirection: PaymentDirection.In));
    }

    // ------------------------------------------------------------------ کارمند

    [Fact]
    public void Employee_AccrualAndBonusAreReceipt_PaymentsAndDeductionAreOutflow()
    {
        Assert.Equal(CompanyFlowDirection.Receipt, Resolve(CompanyFlowSourceTypes.SalaryAccrual));
        Assert.Equal(CompanyFlowDirection.Receipt, Resolve(CompanyFlowSourceTypes.Bonus));
        Assert.Equal(CompanyFlowDirection.Outflow, Resolve(CompanyFlowSourceTypes.SalaryPayment));
        Assert.Equal(CompanyFlowDirection.Outflow, Resolve(CompanyFlowSourceTypes.SalaryAdvance));
        Assert.Equal(CompanyFlowDirection.Outflow, Resolve(CompanyFlowSourceTypes.SalaryDeduction));
    }

    [Fact]
    public void Employee_ReturnOfMoneyIsReceipt()
        => Assert.Equal(
            CompanyFlowDirection.Receipt,
            Resolve(nameof(PaymentKind.EmployeeReturn), LedgerSide.Credit, CompanyFlowPartyRole.Employee));

    // ------------------------------------------------------------------- شریک

    [Fact]
    public void Partner_UsesTheSamePayableConventionAsSupplier()
    {
        Assert.Equal(
            CompanyFlowDirection.Receipt,
            Resolve(CompanyFlowSourceTypes.Loading, LedgerSide.Credit, CompanyFlowPartyRole.Partner));
        Assert.Equal(
            CompanyFlowDirection.Outflow,
            Resolve(nameof(PaymentKind.SupplierPayment), LedgerSide.Debit, CompanyFlowPartyRole.Partner));
    }

    // ----------------------------------------------- لغو، برگشت و ثبت مجدد

    [Fact]
    public void Reversal_InvertsTheOriginalDirection_SoTurnoverIsNotDoubled()
    {
        var original = Resolve(CompanyFlowSourceTypes.Loading, LedgerSide.Credit, CompanyFlowPartyRole.Supplier);
        var reversal = Resolve(
            CompanyFlowSourceTypes.Loading,
            LedgerSide.Credit,
            CompanyFlowPartyRole.Supplier,
            CompanyFlowLifecycle.Reversal);

        Assert.Equal(CompanyFlowDirection.Receipt, original);
        Assert.Equal(CompanyFlowDirection.Outflow, reversal);

        var balance = new CompanyFlowBalanceService().Summarize(
            0m,
            [new CompanyFlowAmount(original, 900m), new CompanyFlowAmount(reversal, 900m)],
            CompanyFlowAccountKind.PartyAccount);
        Assert.Equal(0m, balance.ClosingBalance);
    }

    [Theory]
    [InlineData(CompanyFlowSourceTypes.SarrafSettlementCancel)]
    [InlineData(CompanyFlowSourceTypes.SarrafSettlementEditReversal)]
    [InlineData(CompanyFlowSourceTypes.SupplierPaymentAllocationReversal)]
    [InlineData(CompanyFlowSourceTypes.ThreeWaySettlementCancellation)]
    public void KnownReversalSourceTypes_AreRecognised(string sourceType)
        => Assert.True(CompanyFlowSourceTypes.IsReversal(sourceType));

    [Theory]
    [InlineData(CompanyFlowSourceTypes.Loading)]
    [InlineData(CompanyFlowSourceTypes.Sale)]
    [InlineData(CompanyFlowSourceTypes.Expense)]
    [InlineData(CompanyFlowSourceTypes.SarrafSettlement)]
    public void OriginalSourceTypes_AreNotTreatedAsReversals(string sourceType)
        => Assert.False(CompanyFlowSourceTypes.IsReversal(sourceType));

    // --------------------------------------------- ثابت‌بودن جهت برای یک رویداد

    /// <summary>
    /// یک فعالیت باید در همهٔ صفحات دقیقاً یک جهت داشته باشد؛ نقش طرف‌حساب نباید
    /// جهت «بارگیری» یا «فروش» را عوض کند، چون معنی تجاری‌شان قطعی است.
    /// </summary>
    [Theory]
    [InlineData(CompanyFlowPartyRole.Supplier)]
    [InlineData(CompanyFlowPartyRole.Customer)]
    [InlineData(CompanyFlowPartyRole.Company)]
    [InlineData(CompanyFlowPartyRole.Partner)]
    [InlineData(CompanyFlowPartyRole.Unknown)]
    public void LoadingIsAlwaysReceipt_AndSaleIsAlwaysOutflow_ForEveryRole(CompanyFlowPartyRole role)
    {
        Assert.Equal(CompanyFlowDirection.Receipt, Resolve(CompanyFlowSourceTypes.Loading, LedgerSide.Credit, role));
        Assert.Equal(CompanyFlowDirection.Receipt, Resolve(CompanyFlowSourceTypes.Loading, LedgerSide.Debit, role));
        Assert.Equal(CompanyFlowDirection.Outflow, Resolve(CompanyFlowSourceTypes.Sale, LedgerSide.Credit, role));
        Assert.Equal(CompanyFlowDirection.Outflow, Resolve(CompanyFlowSourceTypes.Sale, LedgerSide.Debit, role));
    }
}
