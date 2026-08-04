using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace PTGOilSystem.Web.Helpers;

public static class UiText
{
    public static bool IsEn(HttpContext? ctx)
    {
        if (ctx is not null)
        {
            var lang = ctx.Request.Cookies["ptg-ui-lang"] ?? "fa";
            return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase);
    }

    public static string T(HttpContext? ctx, string fa, string en)
        => IsEn(ctx) ? en : fa;

    public static string T(string fa, string en)
        => IsEn(null) ? en : fa;

    /// <summary>
    /// عنوان حسابداریِ سمت سند. این «رسید/برد» نیست — واژگان CompanyFlow در
    /// <see cref="Services.CompanyFlow.CompanyFlowText"/> است. اینجا فقط متن سمت دفتر کل
    /// ترجمه می‌شود؛ خودِ LedgerSide و ساختار دیتابیس دست‌نخورده می‌ماند.
    /// </summary>
    public static string LedgerSideName(HttpContext? ctx, Models.Entities.LedgerSide side)
    {
        // نبودِ درخواست یعنی زبان پیش‌فرض سیستم (فارسی)؛ عنوان حسابداری نباید به
        // Culture محیط اجرا وابسته باشد.
        var isEnglish = ctx is not null && IsEn(ctx);
        return side == Models.Entities.LedgerSide.Debit
            ? (isEnglish ? "Debit" : "بدهکار")
            : (isEnglish ? "Credit" : "بستانکار");
    }

    public static string LedgerSource(HttpContext? ctx, string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return "-";
        }

        if (IsEn(ctx))
        {
            return sourceType;
        }

        return sourceType switch
        {
            "OpeningBalance" => "مانده افتتاحیه",
            "ManualAdjustment" => "تعدیل دستی",
            "Expense" => "مصرف",
            "Sale" => "فروش",
            "Loading" or "LoadingRegister" => "بارگیری",
            "LoadingReceipt" => "رسید بارگیری",
            "LoadingReceiptAllocation" => "تخصیص رسید بارگیری",
            "PaymentTransaction" => "تراکنش پرداخت",
            "CustomerReceipt" => "دریافت از مشتری",
            "SupplierPayment" => "پرداخت به تأمین‌کننده",
            "ExpensePayment" => "پرداخت مصرف",
            "TruckPayment" => "پرداخت کرایه موتر",
            "ManualPayment" => "پرداخت دستی",
            "ManualReceipt" => "دریافت دستی",
            "EmployeeSalaryPayment" => "پرداخت معاش کارمند",
            "EmployeeSalaryAdvance" => "پیش‌پرداخت معاش کارمند",
            "SupplierReceipt" => "دریافت از تأمین‌کننده",
            "CustomerPayment" => "پرداخت به مشتری",
            "EmployeeReturn" => "برگشت وجه کارمند",
            "ServiceProviderPayment" => "پرداخت به خدمات‌دهنده",
            "SarrafSettlement" => "تسویه صراف",
            "CommissionPayment" => "پرداخت کمیشن",
            "AssetRentTransaction" => "کرایه دارایی عملیاتی",
            "OperationalAsset" => "دارایی عملیاتی",
            "ShortageCharge" => "هزینه کسری",
            "ContractBalanceTransfer" => "انتقال مانده قرارداد",
            "SupplierPaymentAllocation" => "تخصیص پرداخت تأمین‌کننده",
            "SupplierPaymentAllocationReversal" => "برگشت تخصیص پرداخت تأمین‌کننده",
            "SupplierPaymentAllocationExchangeDifference" => "تفاوت نرخ تخصیص پیش‌پرداخت",
            "SupplierPaymentAllocationExchangeDifferenceReversal" => "برگشت تفاوت نرخ تخصیص پیش‌پرداخت",
            "SupplierBalanceTransfer" => "انتقال مانده به قرارداد",
            "SupplierBalanceTransferReversal" => "برگشت انتقال مانده",
            "SupplierBalanceTransferExchangeDifference" => "تفاوت نرخ انتقال مانده",
            "SupplierBalanceTransferExchangeDifferenceReversal" => "برگشت تفاوت نرخ انتقال مانده",
            "SupplierLoadingLedger" => "سند بارگیری تأمین‌کننده",
            "SupplierViaSarrafPayment" or "ViaSarrafSupplier" => "پرداخت تأمین‌کننده از طریق صراف",
            "SupplierViaSarrafPayable" => "بدهی تأمین‌کننده از طریق صراف",
            "ThreeWaySettlement" => "تسویه سه‌طرفه",
            "ThreeWaySettlementCancellation" => "برگشت تسویه سه‌طرفه",
            "SarrafSettlementCancel" => "لغو تسویه صراف",
            "SarrafSettlementEditReversal" => "برگشت ویرایش تسویه صراف",
            "SarrafSettlementExchangeDifference" => "تفاوت نرخ تسویه صراف",
            "SarrafFxGain" => "سود تفاوت نرخ ارز",
            _ => sourceType
        };
    }
}
