using System.Linq.Expressions;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// تعریفِ مرکزیِ «کدام LedgerEntry واقعاً متعلق به یک تأمین‌کننده است».
///
/// یک سند وقتی متعلق به تأمین‌کننده است که:
///  ۱) مستقیماً <see cref="LedgerEntry.SupplierId"/> آن همان تأمین‌کننده باشد؛ یا
///  ۲) legacy: هیچ طرف‌حسابِ مستقلِ دیگری روی سند ست نشده باشد
///     (ServiceProvider/Driver/Customer/Employee) و صرفاً از طریق
///     قرارداد خرید به تأمین‌کننده وصل باشد.
///
/// شرط (۲) از نشتِ اسنادی مثل کرایهٔ حمل (TRANSPORT-RECEIPT) جلوگیری می‌کند که
/// طرف واقعی‌شان ServiceProvider/Driver است ولی روی قرارداد خرید ثبت شده‌اند و
/// پیش‌تر اشتباهاً بدهی تأمین‌کننده را افزایش می‌دادند.
///
/// استثنای صریح: سطرِ <c>SupplierViaSarrafPayable</c> هرگز مالِ تأمین‌کننده نیست. این سطر
/// «بدهیِ شرکت به صراف» است و در مسیر تک‌نرخیِ legacy با ContractId ثبت می‌شود؛ چون هیچ FK
/// طرف‌حسابی ندارد، شرط (۲) آن را برمی‌داشت و در صورت‌حساب رسمیِ تأمین‌کننده روبه‌روی سطرِ
/// پرداخت می‌نشست و مانده را صفر می‌کرد. مالکیتِ صراف از SourceId (شناسهٔ صراف) خوانده
/// می‌شود و دست‌نخورده می‌ماند.
///
/// استثنای صریح (AUD-04): سطرِ <c>Expense</c> هرگز از راهِ قرارداد به تأمین‌کننده نمی‌چسبد.
/// هزینه‌های حمل/انتقال (مصرف از موجودی، ارسال موتر و …) روی قرارداد خرید ثبت می‌شوند ولی
/// هیچ FK طرف‌حسابی ندارند؛ شرط (۲) آن‌ها را «مالِ تأمین‌کنندهٔ قرارداد» می‌گرفت و بدهیِ او را
/// بی‌جهت بالا می‌برد. اگر هزینه‌ای واقعاً بدهی به تأمین‌کننده باشد، <see cref="LedgerEntry.SupplierId"/>
/// آن ست است و از مسیر (۱) شمرده می‌شود. سطر برگشتِ همان هزینه نیز SourceType یکسان دارد،
/// پس هر دو پا با هم کنار می‌مانند و خنثایی برگشت حفظ می‌شود.
///
/// هیچ چیزی در ثبت Ledger/هزینه/حساب حمل‌کننده تغییر نمی‌کند؛ این فقط «انتساب
/// خواندنیِ» صورت‌حساب/مانده تأمین‌کننده است و به‌صورت Expression نوشته شده تا در
/// همهٔ Queryهای EF یکسان ترجمه شود (شرطِ پراکنده ساخته نشود).
/// </summary>
public static class LedgerEntryOwnership
{
    /// <summary>بدهیِ شرکت به صراف؛ فقط در حساب صراف دیده می‌شود، هرگز در حساب تأمین‌کننده.</summary>
    public const string ViaSarrafPayableSourceType = "SupplierViaSarrafPayable";

    /// <summary>هزینه/مصرف؛ فقط با SupplierId صریح مالِ تأمین‌کننده است، هرگز از راهِ قرارداد.</summary>
    public const string ExpenseSourceType = "Expense";

    /// <summary>
    /// سندهای نقدی که «قرارداد» فقط برچسبِ ردیابیِ آن‌هاست، نه طرف‌حسابشان.
    ///
    /// هر پرداخت، طرفِ واقعی‌اش را با FK خودش حمل می‌کند (SupplierId، CustomerId، DriverId، …).
    /// وقتی هیچ FK ندارد یعنی طرفِ ثبت‌شده‌ای ندارد — نه اینکه مالِ تأمین‌کننده/مشتریِ قرارداد
    /// است. بدون این فهرست، «پرداخت کرایه موتر» یا «پرداخت هزینه» که فقط ContractId دارد،
    /// بدهیِ تأمین‌کنندهٔ همان قرارداد را کم می‌کرد و مانده او را غلط نشان می‌داد.
    ///
    /// <c>SupplierPayment</c> و <c>SupplierReceipt</c> عمداً در فهرست نیستند: آن‌ها واقعاً پولِ
    /// تأمین‌کننده‌اند و اگر سطرِ قدیمی‌ای بدون SupplierId مانده باشد، همچنان از راه قرارداد
    /// خوانده می‌شود و مانده‌های موجود تکان نمی‌خورند. برای مشتری هم <c>CustomerReceipt</c> و
    /// <c>CustomerPayment</c> به همین دلیل بیرون گذاشته شده‌اند.
    ///
    /// نام‌ها عیناً <c>PaymentKind.ToString()</c> هستند — همان چیزی که در
    /// <c>PaymentsController</c> در <see cref="LedgerEntry.SourceType"/> نوشته می‌شود.
    /// </summary>
    public static readonly string[] CashSourceTypesWithoutContractParty =
    [
        nameof(PaymentKind.ExpensePayment),
        nameof(PaymentKind.TruckPayment),
        nameof(PaymentKind.ManualPayment),
        nameof(PaymentKind.ManualReceipt),
        nameof(PaymentKind.CommissionPayment),
        nameof(PaymentKind.ServiceProviderPayment),
        nameof(PaymentKind.SarrafSettlement),
        nameof(PaymentKind.EmployeeSalaryPayment),
        nameof(PaymentKind.EmployeeSalaryAdvance),
        nameof(PaymentKind.EmployeeReturn)
    ];

    public static Expression<Func<LedgerEntry, bool>> SupplierOwned(int supplierId)
        => entry =>
            entry.SourceType != ViaSarrafPayableSourceType
            && (entry.SupplierId == supplierId
                || (entry.SupplierId == null
                    && entry.SourceType != ExpenseSourceType
                    && !CashSourceTypesWithoutContractParty.Contains(entry.SourceType)
                    && entry.ServiceProviderId == null
                    && entry.DriverId == null
                    && entry.CustomerId == null
                    && entry.EmployeeId == null
                    && entry.Contract != null
                    && entry.Contract.ContractType == ContractType.Purchase
                    && entry.Contract.SupplierId == supplierId));

    public static Expression<Func<LedgerEntry, bool>> SupplierOwnedAny(IReadOnlyCollection<int> supplierIds)
        => entry =>
            entry.SourceType != ViaSarrafPayableSourceType
            && ((entry.SupplierId != null && supplierIds.Contains(entry.SupplierId.Value))
                || (entry.SupplierId == null
                    && entry.SourceType != ExpenseSourceType
                    && !CashSourceTypesWithoutContractParty.Contains(entry.SourceType)
                    && entry.ServiceProviderId == null
                    && entry.DriverId == null
                    && entry.CustomerId == null
                    && entry.EmployeeId == null
                    && entry.Contract != null
                    && entry.Contract.ContractType == ContractType.Purchase
                    && entry.Contract.SupplierId != null
                    && supplierIds.Contains(entry.Contract.SupplierId.Value)));

    /// <summary>
    /// قرینهٔ <see cref="SupplierOwned"/> برای مشتری — تا قاعدهٔ انتساب در دو سمت یکی باشد.
    ///
    /// پیش از این سمت مشتری نه هزینه را کنار می‌گذاشت و نه نوع قرارداد را می‌دید، پس یک سطرِ
    /// «هزینه» که فقط روی قرارداد فروش ثبت شده بود، مطالبات همان مشتری را باد می‌کرد.
    /// فروش از راهِ SalesTransaction هم به مشتری می‌رسد و آن مسیر دست‌نخورده است.
    /// </summary>
    public static Expression<Func<LedgerEntry, bool>> CustomerOwnedByContract(int customerId)
        => entry =>
            entry.CustomerId == null
            && entry.SupplierId == null
            && entry.ServiceProviderId == null
            && entry.DriverId == null
            && entry.EmployeeId == null
            && entry.SourceType != ExpenseSourceType
            && !CashSourceTypesWithoutContractParty.Contains(entry.SourceType)
            && entry.Contract != null
            && entry.Contract.ContractType == ContractType.Sale
            && entry.Contract.CustomerId == customerId;
}
