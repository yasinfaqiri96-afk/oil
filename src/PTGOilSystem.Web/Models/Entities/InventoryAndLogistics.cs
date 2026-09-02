using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

public enum MovementDirection { In = 1, Out = 2, Transfer = 3, Adjustment = 4 }
public enum DispatchStatus { Loaded = 1, InTransit = 2, Delivered = 3, Cancelled = 4 }
public enum TruckDispatchMode
{
    FromInventory = 0,
    DirectFromReceipt = 1
}
public enum LoadingTransportType
{
    [Display(Name = "نامشخص")]
    Unspecified = 0,
    [Display(Name = "کشتی")]
    Vessel = 1,
    [Display(Name = "واگن")]
    Wagon = 2,
    [Display(Name = "موتر")]
    Truck = 3
}
public enum RubSettlementRateStatus
{
    NotRequired = 0,
    Pending = 1,
    Locked = 2
}

public enum LoadingReceiptDestination
{
    [Display(Name = "ورود به موجودی / تانک")]
    ToInventory = 0,
    [Display(Name = "تخلیه مستقیم")]
    DirectDispatch = 1,
    [Display(Name = "ترکیبی")]
    Mixed = 2
}
public enum LoadingReceiptAllocationDestination
{
    [Display(Name = "ورود به موجودی / تانک")]
    ToInventory = 0,
    [Display(Name = "بارگیری مستقیم در موتر")]
    DirectDispatchToTruck = 1,
    [Display(Name = "فروش مستقیم")]
    DirectSale = 2,
    [Display(Name = "انتقال به ترمینال / مخزن دیگر")]
    TransferToOtherTerminal = 3
}
public enum LoadingReceiptAllocationStatus
{
    [Display(Name = "فقط Trace")]
    TraceOnly = 0,
    [Display(Name = "تکمیل‌شده")]
    Completed = 1,
    [Display(Name = "در مسیر انتقال")]
    InTransit = 2,
    [Display(Name = "لغوشده")]
    Cancelled = 3
}
public enum InventoryTransportLegStatus
{
    Draft = 0,
    Loaded = 1,
    InTransit = 2,
    Received = 3,
    Cancelled = 4
}
public enum InventoryTransportBatchStatus
{
    Draft = 0,
    Loaded = 1,
    Cancelled = 2
}
public enum CarrierType
{
    ServiceProvider = 1,
    OperationalAsset = 2,
    // حمل‌کنندهٔ شخصی: نه شرکت خدماتی و نه دارایی عملیاتی؛ طرف حمل خودِ راننده است.
    PersonalCarrier = 3
}
public enum InventoryTransportReceiptDestination
{
    ToInventory = 0,
    DirectSale = 1,
    DirectDispatch = 2,
    Mixed = 3
}
public enum LossEventStage
{
    LoadingDifference = 1,
    TransitLoss = 2,
    ReceiptShortage = 3,
    TankNaturalLoss = 4,
    DispatchShortage = 5,
    CustomsLoss = 6,
    SalesDifference = 7,
    ManualAdjustment = 8,
    // ضایعات نهایی که هنگام تسویه کامل مخزن مشخص می‌شود (سناریوی Deferred).
    TankFinalSettlement = 9
}

// نحوهٔ ثبت ضایعات یک رسید ورود به موجودی.
public enum ReceiptLossMode
{
    // ضایعات همان لحظهٔ رسید معلوم است (رفتار قبلی سیستم).
    [Display(Name = "ضایعات حالا معلوم است")]
    ImmediateKnownLoss = 1,
    // ضایعات بعداً از «تسویه نهایی مخزن» مشخص می‌شود.
    [Display(Name = "ضایعات بعداً از تسویه مخزن")]
    DeferredTankSettlement = 2
}

// سطح قطعیت یک ضایعه: آیا مقدار آن واقعاً اندازه‌گیری شده یا فقط تخمین/تقسیم نسبتی است.
// برای سناریوی «چند واگن → یک مخزن مشترک» مهم است: وقتی سهم هر قرارداد دستی و
// اندازه‌گیری‌شده وارد شود = Measured؛ وقتی به نسبت سهم دفتری تقسیم شود = Estimated.
public enum LossCertaintyLevel
{
    [Display(Name = "اندازه‌گیری‌شده")]
    Measured = 1,
    [Display(Name = "تخمینی (تقسیم نسبتی)")]
    Estimated = 2
}

public class InventoryBatch : BaseEntity
{
    [Required, MaxLength(50)] public string BatchCode { get; set; } = "";
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    public DateTime ReceivedDate { get; set; }
    public decimal InitialQuantityMt { get; set; }
}

public class InventoryMovement : BaseEntity
{
    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    public int? StorageTankId { get; set; }
    public StorageTank? StorageTank { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int? InventoryBatchId { get; set; }
    public InventoryBatch? InventoryBatch { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? LoadingReceiptId { get; set; }
    public LoadingReceipt? LoadingReceipt { get; set; }
    public int? SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    // پیوند حسابرسیِ برگشت. nullable است تا داده‌های تاریخی بدون backfill اجباری معتبر بمانند.
    // برگشت، سند اصلی را حذف/ویرایش نمی‌کند و فقط یک حرکت معکوس به آن متصل می‌سازد.
    public int? ReversalOfInventoryMovementId { get; set; }
    public InventoryMovement? ReversalOfInventoryMovement { get; set; }
    public ICollection<InventoryMovement> Reversals { get; set; } = new List<InventoryMovement>();

    public MovementDirection Direction { get; set; }
    public DateTime MovementDate { get; set; }
    public decimal QuantityMt { get; set; }
    [MaxLength(500)] public string? ReferenceDocument { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class LoadingRegister : BaseEntity, IVersionedEntity, ICanonicalSearchable
{
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    public int ContractId { get; set; }       // required by system rule #2
    public Contract? Contract { get; set; }
    public int ProductId { get; set; }        // required by system rule #2
    public Product? Product { get; set; }
    public int? OriginLocationId { get; set; }
    public Location? OriginLocation { get; set; }
    public LoadingTransportType TransportType { get; set; }
    public int? VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public int? TruckId { get; set; }
    public Truck? Truck { get; set; }

    public DateTime LoadingDate { get; set; }
    public decimal LoadedQuantityMt { get; set; }
    [MaxLength(100)] public string? BillOfLadingNumber { get; set; }
    [MaxLength(100)] public string? RwbNo { get; set; }
    // کلید یکتای «همان بارگیری در همان قرارداد» — با LoadingImportKey ساخته می‌شود و
    // پشت یک Unique Index می‌نشیند تا ثبت دوبارهٔ یک ردیف اکسل در دیتابیس ممکن نباشد.
    [MaxLength(300)] public string? ImportUniqueKey { get; set; }
    [MaxLength(200)] public string? WagonNumber { get; set; }
    [MaxLength(200)] public string? RouteDescription { get; set; }
    public int? LogisticsServiceProviderId { get; set; }
    public ServiceProvider? LogisticsServiceProvider { get; set; }
    [MaxLength(200)] public string? LogisticsCompanyName { get; set; }
    [MaxLength(200)] public string? ConsigneeName { get; set; }
    [MaxLength(200)] public string? DestinationName { get; set; }
    public decimal? PlattsUsd { get; set; }
    public decimal? LoadingPriceUsd { get; set; }
    public decimal? FreightRateUsdPerMt { get; set; }
    public decimal? TransportExpenseUsd { get; set; }
    public decimal? WarehouseExpenseUsd { get; set; }
    public decimal? OtherExpenseUsd { get; set; }
    // Gap #2 — railway chargeable quantity rounding + railway expense per wagon
    public decimal? ChargeableQuantityMt { get; set; }
    public decimal? RailwayRateUsd { get; set; }
    public decimal? RailwayExpenseUsd { get; set; }
    [MaxLength(10)] public string SettlementCurrencyCode { get; set; } = "USD";
    public RubSettlementRateStatus RubRateStatus { get; set; } = RubSettlementRateStatus.NotRequired;
    public decimal? RubPerUsdRate { get; set; }
    public DateTime? RubRateDate { get; set; }
    [MaxLength(200)] public string? RubRateSource { get; set; }
    public decimal? AmountUsdAtRubLock { get; set; }
    public decimal? AmountRubAtRubLock { get; set; }
    // ارقام روبلی که مستقیم از فایل اکسل بارگیری خوانده می‌شوند (فقط ایمپورت/نمایش؛
    // مستقل از مسیر نرخ تبادله RubPerUsdRate و بدون اثر بر لِجر/پرداخت/موجودی).
    public decimal? SettlementUnitPriceRub { get; set; }
    public decimal? SettlementValueRub { get; set; }
    public DateTime? RubRateLockedAtUtc { get; set; }
    [MaxLength(100)] public string? RubRateLockedByUserName { get; set; }
    // Phase 1 — کرایه/مصرف این بارگیری بدوش کیست (فقط ثبت/نمایش، nullable). به منطق وابسته نیست.
    public CostResponsibility? FreightCostResponsibility { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public ICollection<LoadingReceipt> Receipts { get; set; } = new List<LoadingReceipt>();
    public ICollection<CustomsDeclaration> CustomsDeclarations { get; set; } = new List<CustomsDeclaration>();
    public ICollection<ExpenseTransaction> ExpenseTransactions { get; set; } = new List<ExpenseTransaction>();
    public ICollection<AssetRentTransaction> AssetRentTransactions { get; set; } = new List<AssetRentTransaction>();
    public ICollection<LoadingExpenseLine> ExpenseLines { get; set; } = new List<LoadingExpenseLine>();

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { BillOfLadingNumber, RwbNo, WagonNumber });
}

public class LoadingReceipt : BaseEntity
{
    public int LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }
    public LoadingReceiptDestination ReceiptDestination { get; set; } = LoadingReceiptDestination.ToInventory;
    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    public int? StorageTankId { get; set; }
    public StorageTank? StorageTank { get; set; }

    public DateTime ReceiptDate { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    // نحوهٔ ثبت ضایعات این رسید: حالا معلوم (پیش‌فرض، رفتار قبلی) یا معوق تا تسویهٔ مخزن.
    public ReceiptLossMode LossMode { get; set; } = ReceiptLossMode.ImmediateKnownLoss;
    // Gap #3 — arrival date and actual arrived quantity for per-wagon difference tracking
    public DateTime? ArrivalDate { get; set; }
    public DateTime? LeakDate { get; set; }
    public decimal? ActualArrivedQuantityMt { get; set; }
    [MaxLength(500)] public string? ReferenceDocument { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    // لغو نرم رسید (الگوی InventoryTransportReceipt.IsCancelled و LossEvent.IsCancelled).
    // رکورد هرگز حذف نمی‌شود؛ اثرهای موجودی/مالی با سند معکوس برگردانده می‌شوند و رسید
    // لغوشده از همهٔ محاسبات عملیاتی (دریافت‌شده، باقی‌مانده، کسری، گزارش‌ها) خارج می‌ماند.
    public bool IsCancelled { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    [MaxLength(500)] public string? CancellationReason { get; set; }
    // Concurrency token (xmin) تا لغو هم‌زمان دو کاربر روی یک رسید دوباره‌کاری نسازد.
    public uint RowVersion { get; set; }

    public InventoryMovement? InventoryMovement { get; set; }
    public ICollection<LoadingReceiptAllocation> Allocations { get; set; } = new List<LoadingReceiptAllocation>();
}

public class LoadingReceiptAllocation : BaseEntity
{
    public int LoadingReceiptId { get; set; }
    public LoadingReceipt? LoadingReceipt { get; set; }
    public LoadingReceiptAllocationDestination Destination { get; set; } = LoadingReceiptAllocationDestination.ToInventory;
    public LoadingReceiptAllocationStatus Status { get; set; } = LoadingReceiptAllocationStatus.TraceOnly;
    public decimal QuantityMt { get; set; }
    public int? SourcePurchaseContractId { get; set; }
    public Contract? SourcePurchaseContract { get; set; }
    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    public int? StorageTankId { get; set; }
    public StorageTank? StorageTank { get; set; }
    public int? DestinationTerminalId { get; set; }
    public Terminal? DestinationTerminal { get; set; }
    public int? DestinationStorageTankId { get; set; }
    public StorageTank? DestinationStorageTank { get; set; }
    public int? DestinationLocationId { get; set; }
    public Location? DestinationLocation { get; set; }
    [MaxLength(200)] public string? DestinationName { get; set; }
    [MaxLength(500)] public string? DestinationReference { get; set; }
    public int? InventoryMovementId { get; set; }
    public InventoryMovement? InventoryMovement { get; set; }
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }
    public ICollection<TruckDispatch> DirectTruckDispatches { get; set; } = new List<TruckDispatch>();
    public int? SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    [MaxLength(500)] public string? ReferenceDocument { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class InventoryTransportLeg : BaseEntity, IVersionedEntity
{
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    public int? InventoryTransportBatchId { get; set; }
    public InventoryTransportBatch? InventoryTransportBatch { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    // شناسهٔ مستقلِ گروه حمل: هر «ثبت حمل» یک گروه جدید می‌سازد، مستقل از قرارداد/شیپمنت/وسیله.
    // برای سازگاری با داده‌های قدیمی nullable است؛ legهای قدیمیِ بدون کلید با heuristic قبلی گروه می‌شوند.
    [MaxLength(64)] public string? TransportGroupKey { get; set; }
    public int SourcePurchaseContractId { get; set; }
    public Contract? SourcePurchaseContract { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int SourceTerminalId { get; set; }
    public Terminal? SourceTerminal { get; set; }
    public int? SourceStorageTankId { get; set; }
    public StorageTank? SourceStorageTank { get; set; }
    public int? DestinationTerminalId { get; set; }
    public Terminal? DestinationTerminal { get; set; }
    public int? DestinationStorageTankId { get; set; }
    public StorageTank? DestinationStorageTank { get; set; }
    public int? DestinationLocationId { get; set; }
    public Location? DestinationLocation { get; set; }
    public LoadingTransportType TransportType { get; set; }
    public int? TruckId { get; set; }
    public Truck? Truck { get; set; }
    public int? WagonId { get; set; }
    public Wagon? Wagon { get; set; }
    public int? VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    [MaxLength(200)] public string? WagonNumber { get; set; }
    [MaxLength(100)] public string? RwbNo { get; set; }
    [MaxLength(100)] public string? BillOfLadingNumber { get; set; }
    [MaxLength(200)] public string? RouteDescription { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    public int? OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AccountingPartyType? CarrierPartyType { get; set; }
    public int? CarrierPartyId { get; set; }
    public CarrierType? CarrierType { get; set; }
    // مسئول/مالک این حمل وقتی موتر شخصی است (موتروان). کرایه در مرحلهٔ رسید روی حساب همین موتروان می‌نشیند.
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public DateTime LoadedDate { get; set; }
    public DateTime? ExpectedArrivalDate { get; set; }
    public decimal QuantityMt { get; set; }
    public decimal? CapacityMt { get; set; }
    public decimal? FreightAmount { get; set; }
    public int? FreightCurrencyId { get; set; }
    public Currency? FreightCurrency { get; set; }
    public decimal? ChargeableQuantityMt { get; set; }
    public decimal? PurchaseUnitCostUsd { get; set; }
    public InventoryTransportLegStatus Status { get; set; } = InventoryTransportLegStatus.Draft;
    // کرایه در صفحهٔ «تسویهٔ کرایه موترها» تسویه شد (مستقل از تخلیهٔ موجودی که مرحلهٔ بعدی جداست).
    // با true شدن، حمل از لیست تسویه خارج و برچسب «کرایه تسویه‌شده» می‌گیرد؛ بار برای تخلیهٔ بعدی می‌ماند.
    public bool IsFreightSettled { get; set; }
    public DateTime? FreightSettledDate { get; set; }
    public int? OutboundInventoryMovementId { get; set; }
    public InventoryMovement? OutboundInventoryMovement { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public ICollection<InventoryTransportLegAllocation> Allocations { get; set; } = new List<InventoryTransportLegAllocation>();
}

public class InventoryTransportBatch : BaseEntity
{
    [Required, MaxLength(64)] public string BatchNumber { get; set; } = "";
    public int SourceTerminalId { get; set; }
    public Terminal? SourceTerminal { get; set; }
    // اختیاری شد: حملِ مستقیم از بار روی کشتی مخزنِ مبدأ ندارد (تخلیه بدون توقف در مخزن).
    public int? SourceStorageTankId { get; set; }
    public StorageTank? SourceStorageTank { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal TotalQuantityMt { get; set; }
    public DateTime TransportDate { get; set; }
    public InventoryTransportBatchStatus Status { get; set; } = InventoryTransportBatchStatus.Draft;
    [Required, MaxLength(64)] public string TransportGroupKey { get; set; } = "";
    [MaxLength(1000)] public string? Notes { get; set; }
    public ICollection<InventoryTransportLeg> Legs { get; set; } = new List<InventoryTransportLeg>();
}

// یک «سهم منبع» از بار یک مرحلهٔ حمل: چه مقدار، از کدام قرارداد خرید، و از کجا آمده.
//
// منبع دو نوع است و همیشه فقط یکی از آن‌ها پر است:
//   • منبع موجودی  → SourceInventoryMovementId (مخزن → وسیله؛ خروجی موجودی ساخته می‌شود)
//   • منبع وسیله   → SourceTransportLegId      (وسیله → وسیله؛ هیچ حرکت موجودی ساخته نمی‌شود)
//
// چون هر مرحله می‌تواند چند سهم داشته باشد، همین یک جدول هم Split و هم Merge را پوشش می‌دهد:
//   Split : یک مرحلهٔ والد در سهم‌های چند مرحلهٔ فرزند ظاهر می‌شود.
//   Merge : یک مرحلهٔ فرزند چند سهم با والدهای متفاوت دارد.
public class InventoryTransportLegAllocation : BaseEntity
{
    public int InventoryTransportLegId { get; set; }
    public InventoryTransportLeg? InventoryTransportLeg { get; set; }
    public int SourcePurchaseContractId { get; set; }
    public Contract? SourcePurchaseContract { get; set; }
    public int? SourceLoadingReceiptId { get; set; }
    public LoadingReceipt? SourceLoadingReceipt { get; set; }
    // برای سهم‌هایی که منبعشان وسیله است null می‌ماند؛ کالا مرز مخزن را رد نکرده تا سندی داشته باشد.
    public int? SourceInventoryMovementId { get; set; }
    public InventoryMovement? SourceInventoryMovement { get; set; }
    // مرحلهٔ والد در زنجیرهٔ حمل (وسیله → وسیله). برای سهم‌های مخزنی null است.
    public int? SourceTransportLegId { get; set; }
    public InventoryTransportLeg? SourceTransportLeg { get; set; }
    // رسیدی از والد که این انتقال را ثبت کرده (پیوند لغو/ردیابی).
    public int? SourceTransportReceiptId { get; set; }
    public InventoryTransportReceipt? SourceTransportReceipt { get; set; }
    public decimal QuantityMt { get; set; }
    public int? OutboundInventoryMovementId { get; set; }
    public InventoryMovement? OutboundInventoryMovement { get; set; }
}

public class InventoryTransportReceipt : BaseEntity
{
    public int InventoryTransportLegId { get; set; }
    public InventoryTransportLeg? InventoryTransportLeg { get; set; }
    public DateTime ReceiptDate { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    public decimal ShortageQuantityMt { get; set; }
    public decimal? AllowanceMt { get; set; }
    public decimal? ChargeableShortageMt { get; set; }
    public decimal? FreightRateUsdPerMt { get; set; }
    public decimal? FreightCostUsd { get; set; }
    public decimal? ShortageRateUsd { get; set; }
    public decimal? ShortageChargeUsd { get; set; }
    public decimal? FreightPayableUsd { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    public int? OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AccountingPartyType? CarrierPartyType { get; set; }
    public int? CarrierPartyId { get; set; }
    public InventoryTransportReceiptDestination ReceiptDestination { get; set; } = InventoryTransportReceiptDestination.ToInventory;
    public int? DestinationTerminalId { get; set; }
    public Terminal? DestinationTerminal { get; set; }
    public int? DestinationStorageTankId { get; set; }
    public StorageTank? DestinationStorageTank { get; set; }
    public int? InventoryMovementId { get; set; }
    public InventoryMovement? InventoryMovement { get; set; }
    public int? SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public bool IsCancelled { get; set; }
    public ICollection<TruckDispatch> DirectTruckDispatches { get; set; } = new List<TruckDispatch>();
}

public class TruckDispatch : BaseEntity, IVersionedEntity
{
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    public TruckDispatchMode DispatchMode { get; set; } = TruckDispatchMode.FromInventory;
    public int? LoadingReceiptAllocationId { get; set; }
    public LoadingReceiptAllocation? LoadingReceiptAllocation { get; set; }
    public int? InventoryTransportReceiptId { get; set; }
    public InventoryTransportReceipt? InventoryTransportReceipt { get; set; }
    public int? SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int TruckId { get; set; }
    public Truck? Truck { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? DestinationLocationId { get; set; }
    public Location? DestinationLocation { get; set; }
    public int? ServiceProviderId { get; set; }
    public ServiceProvider? ServiceProvider { get; set; }
    public int? OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AccountingPartyType? CarrierPartyType { get; set; }
    public int? CarrierPartyId { get; set; }

    public DateTime DispatchDate { get; set; }
    public DispatchStatus Status { get; set; } = DispatchStatus.Loaded;
    public decimal LoadedQuantityMt { get; set; }
    public decimal? DischargedQuantityMt { get; set; }
    public decimal? AllowanceMt { get; set; }
    public decimal? ShortageMt { get; set; }
    public decimal? FreightCostUsd { get; set; }
    public decimal? ShortageRateUsd { get; set; }
    public decimal? FreightPayableUsd { get; set; }
    public decimal? PayableUsd { get; set; }
    // Gap #4 — ticket serial number for dispatch
    [MaxLength(100)] public string? TicketSerialNumber { get; set; }
    // Gap #5 — truck freight tolerance and chargeable shortage
    public decimal? ToleranceMt { get; set; }
    public decimal? ChargeableShortageMt { get; set; }
    // کرایه در صفحهٔ «تسویهٔ کرایه موترها» تسویه شد (بدون تخلیهٔ موجودی؛ تخلیه مرحلهٔ بعدی جداست).
    // با true شدن، دیسپچ از لیست تسویه خارج و برچسب «کرایه تسویه‌شده» می‌گیرد؛ Status دست‌نخورده می‌ماند.
    public bool IsFreightSettled { get; set; }
    public DateTime? FreightSettledDate { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class Shipment : BaseEntity
{
    [Required, MaxLength(100)] public string ShipmentCode { get; set; } = "";
    public int? VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    // Primary contract (kept for backward compat); multi-contract via ShipmentContracts
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public DateTime? DepartureDate { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public int? OriginLocationId { get; set; }
    public Location? OriginLocation { get; set; }
    public int? DestinationLocationId { get; set; }
    public Location? DestinationLocation { get; set; }
    public decimal QuantityMt { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    // Gap #7 — multi-contract vessel shipment
    public ICollection<ShipmentContract> ShipmentContracts { get; set; } = new List<ShipmentContract>();
    public ICollection<ShipmentLoadingAllocation> LoadingAllocations { get; set; } = new List<ShipmentLoadingAllocation>();
    public ICollection<InventoryTransportLeg> InventoryTransportLegs { get; set; } = new List<InventoryTransportLeg>();
}

// Gap #7 — junction table: one shipment can reference many purchase contracts
public class ShipmentContract : BaseEntity
{
    public int ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public decimal? QuantityMt { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
}

// سهمِ دقیقِ یک بارگیریِ خرید از بار یک محموله.
//
// ShipmentContract فقط می‌گوید «این محموله چقدر از قرارداد A دارد»، اما یک قرارداد می‌تواند
// چند بارگیری با نرخ‌های قطعیِ متفاوت داشته باشد. این جدول همان یک سطح گمشده را پر می‌کند:
// «چه مقدار از کدام بارگیریِ مشخص». بهای خرید محموله از همین ردیف‌ها ساخته می‌شود:
//   Σ (QuantityMt × LoadingRegister.LoadingPriceUsd)
//
// اختیاری و additive است: محموله‌های قدیمی بدون این ردیف‌ها با همان fallback قبلی
// (میانگین وزنیِ قرارداد → نرخ نهایی هدر) محاسبه می‌شوند و هیچ backfill حدسی انجام نمی‌شود.
public class ShipmentLoadingAllocation : BaseEntity
{
    public int ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    // قرارداد خریدِ همین بارگیری. denormalized نگه داشته می‌شود تا رول‌آپ per-contract یک join کمتر
    // بخواهد؛ هنگام ثبت با LoadingRegister.ContractId اعتبارسنجی می‌شود.
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }
    public decimal QuantityMt { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
}

public class DeliveryReceipt : BaseEntity
{
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public DateTime ReceiptDate { get; set; }
    public decimal ReceivedQuantityMt { get; set; }
    [MaxLength(200)] public string? ReceivedBy { get; set; }
    [MaxLength(500)] public string? DocumentReference { get; set; }
}

public class LossEvent : BaseEntity, IVersionedEntity
{
    /// <summary>PTG-P1-05 — نشانهٔ هم‌زمانی. ببینید <see cref="IVersionedEntity"/>.</summary>
    public long Version { get; set; } = 1;

    public LossEventStage Stage { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public int? LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }
    public int? LoadingReceiptId { get; set; }
    public LoadingReceipt? LoadingReceipt { get; set; }
    public int? TransportLegId { get; set; }
    public InventoryTransportLeg? TransportLeg { get; set; }
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }
    public int? SalesTransactionId { get; set; }
    public SalesTransaction? SalesTransaction { get; set; }
    public int? TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    public int? StorageTankId { get; set; }
    public StorageTank? StorageTank { get; set; }
    public DateTime EventDate { get; set; }
    public decimal ExpectedQuantityMt { get; set; }
    public decimal ActualQuantityMt { get; set; }
    public decimal DifferenceQuantityMt { get; set; }
    public decimal ToleranceQuantityMt { get; set; }
    public decimal AllowableLossMt { get; set; }
    public decimal ChargeableLossMt { get; set; }
    [MaxLength(100)] public string? ResponsiblePartyType { get; set; }
    [MaxLength(200)] public string? ResponsiblePartyName { get; set; }
    [MaxLength(200)] public string? FinancialTreatment { get; set; }
    public bool AffectsInventory { get; set; }
    public int? InventoryMovementId { get; set; }
    public InventoryMovement? InventoryMovement { get; set; }
    // سطح قطعیت ضایعه (nullable تا رکوردهای قدیمی بدون تغییر بمانند).
    // فقط برای ضایعات تسویهٔ نهایی مخزن مقداردهی می‌شود: Measured (ورود دستی) یا Estimated (نسبتی).
    public LossCertaintyLevel? LossCertainty { get; set; }
    [MaxLength(200)] public string? Reference { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }

    public bool IsCancelled { get; set; }
    public ICollection<LossEventSourceAllocation> SourceAllocations { get; set; } = new List<LossEventSourceAllocation>();
}
