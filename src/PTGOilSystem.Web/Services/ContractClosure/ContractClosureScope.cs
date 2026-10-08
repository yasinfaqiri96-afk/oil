using Microsoft.EntityFrameworkCore.ChangeTracking;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.ContractClosure;

/// <summary>
/// «این قرارداد بسته است». عمداً استثنای اختصاصی است تا <c>BusinessRuleExceptionFilter</c>
/// آن را به پیام فارسی روی همان صفحه ترجمه کند، نه خطای ۵۰۰.
/// </summary>
public sealed class ContractClosedException(string message, int contractId) : InvalidOperationException(message)
{
    public int ContractId { get; } = contractId;
}

/// <summary>
/// دامنهٔ قفلِ قرارداد بسته: کدام موجودیت‌ها «عملیات قرارداد» هستند و قرارداد را با کدام ستون
/// نشان می‌دهند. <c>ApplicationDbContext</c> موقع ذخیره فقط از همین فهرست می‌پرسد.
/// </summary>
public static class ContractClosureScope
{
    /// <summary>ستون‌هایی که به قرارداد اشاره می‌کنند (فقط آن‌هایی که روی موجودیت وجود دارند خوانده می‌شوند).</summary>
    public static readonly string[] ContractKeyProperties =
    [
        "ContractId",
        "SourcePurchaseContractId",
        "FromContractId",
        "ToContractId",
        "CustomerSaleContractId",
        "SupplierPurchaseContractId",
        "ChargedToContractId"
    ];

    private static readonly HashSet<Type> ScopedTypes =
    [
        typeof(LoadingRegister),
        typeof(LoadingReceiptAllocation),
        typeof(InventoryTransportLeg),
        typeof(InventoryTransportLegAllocation),
        typeof(TruckDispatch),
        typeof(Shipment),
        typeof(ShipmentContract),
        typeof(ShipmentLoadingAllocation),
        typeof(LossEvent),
        typeof(LossEventSourceAllocation),
        typeof(SalesTransaction),
        typeof(SalesTransactionSourceAllocation),
        typeof(ExpenseTransaction),
        typeof(PaymentTransaction),
        typeof(SarrafSettlement),
        typeof(LedgerEntry),
        typeof(PartnerSettlement),
        typeof(ContractBalanceTransfer),
        typeof(SupplierPaymentAllocation),
        typeof(SupplierBalanceTransfer),
        typeof(ThreeWaySettlement),
        typeof(InventoryMovement),
        typeof(InventoryBatch),
        typeof(AssetCharge),
        typeof(AssetRentTransaction),
        typeof(QualityInspection),
        typeof(ContractAmendment),
        typeof(ContractPricingRule),
        typeof(ContractPartner)
    ];

    /// <summary>
    /// ستون‌های فنی که با backfill جستجو، شمارندهٔ هم‌زمانی یا مهر زمانی عوض می‌شوند.
    /// تغییرِ فقط همین‌ها «ویرایش عملیات» حساب نمی‌شود و قفل را فعال نمی‌کند.
    /// </summary>
    private static readonly HashSet<string> TechnicalProperties =
    [
        "SearchKey",
        "Version",
        nameof(BaseEntity.CreatedAtUtc),
        nameof(BaseEntity.UpdatedAtUtc),
        nameof(BaseEntity.CreatedByUserId),
        nameof(BaseEntity.UpdatedByUserId)
    ];

    public static bool Covers(object entity) => ScopedTypes.Contains(entity.GetType());

    public static bool HasBusinessChange(EntityEntry entry)
        => entry.Properties.Any(p => p.IsModified && !TechnicalProperties.Contains(p.Metadata.Name));

    public static string BuildLockedMessage(string contractLabel)
        => $"قرارداد «{contractLabel}» بسته شده است؛ ثبت، ویرایش یا حذف عملیات روی آن مجاز نیست. "
           + "برای ادامه، مدیر سیستم باید قرارداد را بازگشایی کند.";

    public const string DirectStatusChangeMessage =
        "بستن یا بازگشایی قرارداد فقط از گزینه‌های «بستن قرارداد» و «بازگشایی قرارداد» در صفحهٔ جزئیات ممکن است.";
}
