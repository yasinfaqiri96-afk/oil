using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sarrafs;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.ThreeWaySettlement;

// D1 preview form. Only Supplier payee can be posted in this phase; Sarraf and Other stay preview-only.
public sealed class ThreeWaySettlementPreviewViewModel
{
    [Display(Name = "تاریخ حواله")]
    [DataType(DataType.Date)]
    public DateTime SettlementDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    public int? SourcePaymentTransactionId { get; set; }

    [Display(Name = "مشتری")]
    public int? CustomerId { get; set; }

    [Display(Name = "نوع گیرنده")]
    public ThreeWayPayeeType PayeeType { get; set; } = ThreeWayPayeeType.Supplier;

    [Display(Name = "تأمین‌کننده")]
    public int? SupplierId { get; set; }

    [Display(Name = "صراف")]
    public int? SarrafId { get; set; }

    [Display(Name = "نام/توضیح حساب دیگر")]
    [StringLength(200)]
    public string? OtherPayeeName { get; set; }

    [Display(Name = "مبلغ پرداختی مشتری")]
    public decimal CustomerPaidAmount { get; set; }

    [Display(Name = "مبلغ قبول‌شده گیرنده")]
    public decimal PayeeAcceptedAmount { get; set; }

    [Display(Name = "ارز")]
    [StringLength(10)]
    public string Currency { get; set; } = "USD";

    [Display(Name = "نرخ تبدیل به دالر")]
    public decimal FxRateToUsd { get; set; } = 1m;

    // فاز B1 — چندارزی per-leg (nullable؛ خالی → از Currency/FxRateToUsd پایه پر می‌شود).
    [Display(Name = "ارز پرداخت مشتری")]
    [StringLength(10)]
    public string? CustomerPaidCurrency { get; set; }

    [Display(Name = "نرخ پرداخت مشتری به دالر")]
    public decimal? CustomerPaidFxRateToUsd { get; set; }

    [Display(Name = "ارز قبول‌شده تأمین‌کننده")]
    [StringLength(10)]
    public string? SupplierAcceptedCurrency { get; set; }

    [Display(Name = "نرخ قبول‌شده تأمین‌کننده به دالر")]
    public decimal? SupplierAcceptedFxRateToUsd { get; set; }

    public string EffectiveCustomerCurrency
        => string.IsNullOrWhiteSpace(CustomerPaidCurrency) ? Currency : CustomerPaidCurrency!;

    public decimal EffectiveCustomerRate
        => CustomerPaidFxRateToUsd is > 0m ? CustomerPaidFxRateToUsd.Value : (FxRateToUsd > 0m ? FxRateToUsd : 1m);

    public string EffectiveSupplierCurrency
        => string.IsNullOrWhiteSpace(SupplierAcceptedCurrency) ? Currency : SupplierAcceptedCurrency!;

    public decimal EffectiveSupplierRate
        => SupplierAcceptedFxRateToUsd is > 0m ? SupplierAcceptedFxRateToUsd.Value : (FxRateToUsd > 0m ? FxRateToUsd : 1m);

    public bool IsMultiCurrency
        => !string.Equals(EffectiveCustomerCurrency, EffectiveSupplierCurrency, StringComparison.OrdinalIgnoreCase);

    [Display(Name = "قرارداد فروش مشتری")]
    public int? CustomerSaleContractId { get; set; }

    [Display(Name = "قرارداد خرید/تأمین‌کننده")]
    public int? SupplierPurchaseContractId { get; set; }

    [Display(Name = "دلیل تفاوت")]
    public DifferenceReason? DifferenceReason { get; set; }

    [Display(Name = "شماره حواله / مرجع")]
    [StringLength(200)]
    public string? ReferenceNumber { get; set; }

    [Display(Name = "توضیحات")]
    [StringLength(1000)]
    public string? Description { get; set; }

    public bool ShowPreview { get; set; }
    public decimal CustomerPaidUsd { get; set; }
    public decimal PayeeAcceptedUsd { get; set; }
    public decimal DifferenceUsd { get; set; }
    public string? CustomerName { get; set; }
    public string? PayeeName { get; set; }

    public bool HasDifference => Math.Abs(DifferenceUsd) > 0.0001m;
    public bool DifferenceReasonMissing => HasDifference && DifferenceReason is null;

    public bool PayeeSelected => PayeeType switch
    {
        ThreeWayPayeeType.Supplier => SupplierId.HasValue,
        ThreeWayPayeeType.Sarraf => SarrafId.HasValue,
        ThreeWayPayeeType.OtherAccount => !string.IsNullOrWhiteSpace(OtherPayeeName),
        _ => false
    };

    public bool SupplierInvolved => PayeeType == ThreeWayPayeeType.Supplier;
    public bool SarrafInvolved => PayeeType == ThreeWayPayeeType.Sarraf;
    public bool OtherAccountInvolved => PayeeType == ThreeWayPayeeType.OtherAccount;

    public decimal CompanyCashBankDeltaUsd => 0m;

    public string ReadinessText => CanPost
        ? "برای ثبت نهایی آماده است"
        : "برای ثبت نهایی آماده نیست";

    public string PayeeTypeLabel => PayeeType switch
    {
        ThreeWayPayeeType.Supplier => "تأمین‌کننده",
        ThreeWayPayeeType.Sarraf => "صراف",
        ThreeWayPayeeType.OtherAccount => "حساب دیگر",
        _ => "نامشخص"
    };

    public string CustomerImpactText => "طلب ما از مشتری کم می‌شود.";

    public string PayeeImpactText => PayeeType switch
    {
        ThreeWayPayeeType.Supplier => "بدهی ما به تأمین‌کننده کم می‌شود.",
        ThreeWayPayeeType.Sarraf => "صراف فقط واسطه حواله است؛ بدهی تأمین‌کننده با مبلغ قبول‌شده کم می‌شود.",
        ThreeWayPayeeType.OtherAccount => "حساب/طرف دیگر درگیر می‌شود.",
        _ => "گیرنده مشخص نیست."
    };

    public string CompanyCashBankImpactText => "بدون تغییر - پول وارد صندوق شرکت نمی‌شود.";

    public string DifferenceReasonText => HasDifference
        ? DifferenceReason.HasValue ? SarrafSettlementLabels.ToPersian(DifferenceReason) : "دلیل مشخص نشده"
        : "بدون تفاوت";

    // فاز C1 — سیاست trace-only تفاوت (برچسب/راهنما؛ بدون posting).
    public string DifferencePolicyText => HasDifference ? DifferenceReasonPolicy.PolicyText(DifferenceReason) : "";
    public bool DifferenceIsPotentialExpense => HasDifference && DifferenceReasonPolicy.IsPotentialExpense(DifferenceReason);
    public string DifferenceTraceOnlyNote => DifferenceReasonPolicy.TraceOnlyNote;
    public string DifferencePotentialExpenseNote => DifferenceReasonPolicy.PotentialExpenseNote;

    public List<string> PostBlockers
    {
        get
        {
            var issues = new List<string>();

            if (!CustomerId.HasValue)
            {
                issues.Add("مشتری باید مشخص باشد.");
            }

            if (!PayeeSelected)
            {
                issues.Add(PayeeType switch
                {
                    ThreeWayPayeeType.Supplier => "برای گیرنده تأمین‌کننده، SupplierId باید مشخص باشد.",
                    ThreeWayPayeeType.Sarraf => "برای گیرنده صراف، SarrafId باید مشخص باشد.",
                    ThreeWayPayeeType.OtherAccount => "برای حساب دیگر، نام/توضیح حساب دیگر باید مشخص باشد.",
                    _ => "گیرنده باید مشخص باشد."
                });
            }

            if (CustomerPaidAmount <= 0m)
            {
                issues.Add("مبلغ پرداختی مشتری باید مثبت باشد.");
            }

            if (PayeeAcceptedAmount <= 0m)
            {
                issues.Add("مبلغ قبول‌شده گیرنده باید مثبت باشد.");
            }

            if (string.IsNullOrWhiteSpace(Currency))
            {
                issues.Add("ارز باید مشخص باشد.");
            }

            if (EffectiveCustomerRate <= 0m)
            {
                issues.Add("نرخ تبدیل پرداخت مشتری به دالر باید مثبت باشد.");
            }

            if (EffectiveSupplierRate <= 0m)
            {
                issues.Add("نرخ تبدیل مبلغ قبول‌شده تأمین‌کننده به دالر باید مثبت باشد.");
            }

            if (DifferenceReasonMissing)
            {
                issues.Add("اگر مبلغ پرداختی مشتری با مبلغ قبول‌شده گیرنده فرق دارد، DifferenceReason الزامی است.");
            }

            // در حالت صراف، صراف فقط واسطه است؛ تأمین‌کننده گیرنده نهایی باید مشخص باشد.
            if (PayeeType == ThreeWayPayeeType.Sarraf && !SupplierId.HasValue)
            {
                issues.Add("برای حالت صراف، تأمین‌کننده گیرنده نهایی باید مشخص باشد.");
            }

            return issues;
        }
    }

    public bool CanPost => PostBlockers.Count == 0;
    public bool CanConfirmSupplier => CanPost && PayeeType == ThreeWayPayeeType.Supplier;
    // فاز A: صراف-به‌عنوان-واسطه هم قابل ثبت است (گیرنده نهایی تأمین‌کننده، صراف فقط حامل حواله).
    public bool CanConfirmSarraf => CanPost && PayeeType == ThreeWayPayeeType.Sarraf && SupplierId.HasValue;
    public bool CanConfirm => CanConfirmSupplier || CanConfirmSarraf;

    // Backward-compatible aliases for the interrupted draft.
    public string? OtherAccountName
    {
        get => OtherPayeeName;
        set => OtherPayeeName = value;
    }

    public decimal SupplierAcceptedAmount
    {
        get => PayeeAcceptedAmount;
        set => PayeeAcceptedAmount = value;
    }

    public decimal SupplierAcceptedUsd
    {
        get => PayeeAcceptedUsd;
        set => PayeeAcceptedUsd = value;
    }
}
