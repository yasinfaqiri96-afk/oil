namespace PTGOilSystem.Web.Models.Entities;

/// <summary>
/// سهم منبع خرید در یک فروش واحد. خودِ فاکتور/فروش، مشتری، مبلغ و دفترکل همچنان یک سند است؛
/// این جدول فقط نسب‌نامهٔ فیزیکی و بهای تمام‌شده را برای فروش چندقراردادی نگه می‌دارد.
/// </summary>
public sealed class SalesTransactionSourceAllocation : BaseEntity
{
    public int SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    public int? TransportLegId { get; set; }
    public InventoryTransportLeg? TransportLeg { get; set; }
    public int SourcePurchaseContractId { get; set; }
    public Contract? SourcePurchaseContract { get; set; }
    public int? SourceLoadingReceiptId { get; set; }
    public LoadingReceipt? SourceLoadingReceipt { get; set; }
    public int? SourceInventoryMovementId { get; set; }
    public InventoryMovement? SourceInventoryMovement { get; set; }
    public int? SourceTransportLegId { get; set; }
    public InventoryTransportLeg? SourceTransportLeg { get; set; }
    public int? SourceTransportReceiptId { get; set; }
    public InventoryTransportReceipt? SourceTransportReceipt { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal AmountUsd { get; set; }
}

/// <summary>
/// سهم منبع خرید در یک رویداد کسری/ضایعات. رویداد یکی می‌ماند و فقط مقدار آن میان منابع
/// واقعی تقسیم می‌شود؛ بنابراین راپور تاریخی، مسئولیت و ثبت مالی موازی ساخته نمی‌شود.
/// </summary>
public sealed class LossEventSourceAllocation : BaseEntity
{
    public int LossEventId { get; set; }
    public LossEvent? LossEvent { get; set; }
    public int? TransportLegId { get; set; }
    public InventoryTransportLeg? TransportLeg { get; set; }
    public int SourcePurchaseContractId { get; set; }
    public Contract? SourcePurchaseContract { get; set; }
    public int? SourceLoadingReceiptId { get; set; }
    public LoadingReceipt? SourceLoadingReceipt { get; set; }
    public int? SourceInventoryMovementId { get; set; }
    public InventoryMovement? SourceInventoryMovement { get; set; }
    public int? SourceTransportLegId { get; set; }
    public InventoryTransportLeg? SourceTransportLeg { get; set; }
    public int? SourceTransportReceiptId { get; set; }
    public InventoryTransportReceipt? SourceTransportReceipt { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal? ValueUsd { get; set; }
}
