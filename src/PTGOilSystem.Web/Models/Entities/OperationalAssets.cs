using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.Entities;

public enum OperationalAssetType
{
    Truck = 1,
    Trailer = 2,
    TankerTruck = 3,
    StorageTank = 4,
    Warehouse = 5,
    Terminal = 6,
    Wagon = 7,
    Other = 99
}

public enum OperationalAssetOwnershipMode
{
    FullyCompanyOwned = 1,
    PartnerOwned = 2,
    SharedOwnership = 3,
    LeasedButOperated = 4,
    Other = 5
}

public enum OperationalAssetStatus
{
    Planned = 0,
    Active = 1,
    UnderMaintenance = 2,
    OutOfService = 3,
    Disposed = 4
}

public enum AssetMaintenanceJobType
{
    Service = 1,
    Repair = 2,
    Inspection = 3,
    Other = 99
}

public enum AssetMaintenanceStatus
{
    Planned = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3
}

public enum AssetMeterType
{
    OdometerKm = 1,
    WorkHours = 2
}

public enum AssetDocumentType
{
    Insurance = 1,
    Registration = 2,
    Ownership = 3,
    Inspection = 4,
    Permit = 5,
    Other = 99
}

public enum AssetUsageDocumentType
{
    LoadingRegister = 1,
    InventoryTransportLeg = 2,
    InventoryTransportReceipt = 3,
    TruckDispatch = 4,
    AssetRentTransaction = 5
}

public enum AssetChargeKind
{
    InternalTransfer = 1,
    ExternalRental = 2
}

public enum AssetChargeRateBasis
{
    FixedAmount = 1,
    QuantityMt = 2,
    DistanceKm = 3,
    Days = 4
}

public enum AssetChargePostingStatus
{
    Pending = 0,
    Posted = 1,
    Skipped = 2,
    Cancelled = 3
}

public enum AssetOwnerType
{
    Company = 1,
    Partner = 2,
    ExternalOwner = 3,
    Other = 4
}

public enum AssetRentUsageType
{
    InternalCompanyUse = 1,
    ExternalCustomerRental = 2,
    PartnerUse = 3,
    Other = 4
}

public enum AssetRentChargedToType
{
    PurchaseContract = 1,
    SalesContract = 2,
    Customer = 3,
    CompanyInternal = 4,
    Partner = 5,
    Other = 6
}

public class OperationalAsset : BaseEntity
{
    [Required, MaxLength(50)] public string AssetCode { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public OperationalAssetType AssetType { get; set; } = OperationalAssetType.Other;
    public int? LinkedTruckId { get; set; }
    public Truck? LinkedTruck { get; set; }
    public int? LinkedStorageTankId { get; set; }
    public StorageTank? LinkedStorageTank { get; set; }
    public decimal? CapacityMt { get; set; }
    public int? LocationId { get; set; }
    public Location? Location { get; set; }
    public int? TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    public DateTime? AcquisitionDate { get; set; }
    public decimal? AcquisitionCostUsd { get; set; }
    public DateTime? InServiceDate { get; set; }
    public DateTime? DisposalDate { get; set; }
    public OperationalAssetStatus OperationalStatus { get; set; } = OperationalAssetStatus.Active;
    public OperationalAssetOwnershipMode OwnershipMode { get; set; } = OperationalAssetOwnershipMode.FullyCompanyOwned;
    public decimal MonthlyDepreciationUsd { get; set; }
    public decimal? DefaultInternalRateUsd { get; set; }
    public decimal? DefaultExternalRateUsd { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    public ICollection<AssetOwnershipShare> OwnershipShares { get; set; } = [];
    public ICollection<AssetAssignment> Assignments { get; set; } = [];
    public ICollection<AssetMaintenanceJob> MaintenanceJobs { get; set; } = [];
    public ICollection<AssetMeterReading> MeterReadings { get; set; } = [];
    public ICollection<AssetDocument> Documents { get; set; } = [];
    public ICollection<AssetUsage> Usages { get; set; } = [];
    public ICollection<AssetRentTransaction> RentTransactions { get; set; } = [];
    public ICollection<ExpenseTransaction> ExpenseTransactions { get; set; } = [];
}

public class AssetUsage : BaseEntity
{
    public int OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AssetUsageDocumentType DocumentType { get; set; }
    public int DocumentId { get; set; }
    public DateTime UsageDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public decimal? QuantityMt { get; set; }
    public decimal? DistanceKm { get; set; }
    public decimal? Days { get; set; }
    public int? FromLocationId { get; set; }
    public Location? FromLocation { get; set; }
    public int? ToLocationId { get; set; }
    public Location? ToLocation { get; set; }
    public bool IsReversed { get; set; }
    public ICollection<AssetCharge> Charges { get; set; } = [];
}

public class AssetCharge : BaseEntity
{
    public int AssetUsageId { get; set; }
    public AssetUsage? AssetUsage { get; set; }
    public AssetChargeKind ChargeKind { get; set; }
    public AssetChargeRateBasis RateBasis { get; set; }
    public decimal Rate { get; set; }
    public decimal? QuantityBasis { get; set; }
    [Required, MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal FxRateToUsd { get; set; } = 1m;
    public decimal AmountOriginal { get; set; }
    public decimal AmountUsd { get; set; }
    public AccountingPartyType? CounterpartyPartyType { get; set; }
    public int? CounterpartyPartyId { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public AssetChargePostingStatus PostingStatus { get; set; } = AssetChargePostingStatus.Pending;
    [MaxLength(500)] public string? SkipReason { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int? LedgerEntryId { get; set; }
    public LedgerEntry? LedgerEntry { get; set; }
    public int? LegacyAssetRentTransactionId { get; set; }
    public AssetRentTransaction? LegacyAssetRentTransaction { get; set; }
    public bool IsCancelled { get; set; }
}

public class AssetAssignment : BaseEntity
{
    public int OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AccountingPartyType ResponsiblePartyType { get; set; }
    public int ResponsiblePartyId { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? BaseTerminalId { get; set; }
    public Terminal? BaseTerminal { get; set; }
    [Required, MaxLength(100)] public string Role { get; set; } = "";
    public DateTime FromDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public DateTime? ToDate { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class AssetMaintenanceJob : BaseEntity
{
    public int OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AssetMaintenanceJobType JobType { get; set; } = AssetMaintenanceJobType.Service;
    public AssetMaintenanceStatus Status { get; set; } = AssetMaintenanceStatus.Planned;
    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public DateTime? ScheduledDate { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime? DowntimeFrom { get; set; }
    public DateTime? DowntimeTo { get; set; }
    public int? ExpenseTransactionId { get; set; }
    public ExpenseTransaction? ExpenseTransaction { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class AssetMeterReading : BaseEntity
{
    public int OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AssetMeterType MeterType { get; set; }
    public DateTime ReadingDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public decimal ReadingValue { get; set; }
    [MaxLength(200)] public string? Reference { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class AssetDocument : BaseEntity
{
    public int OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AssetDocumentType DocumentType { get; set; } = AssetDocumentType.Other;
    [MaxLength(200)] public string? DocumentNumber { get; set; }
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    [Required, MaxLength(260)] public string OriginalFileName { get; set; } = "";
    [Required, MaxLength(260)] public string StoredFileName { get; set; } = "";
    [Required, MaxLength(500)] public string FilePath { get; set; } = "";
    [MaxLength(200)] public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(200)] public string? UploadedByUserName { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class AssetOwnershipShare : BaseEntity
{
    public int OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public AssetOwnerType OwnerType { get; set; } = AssetOwnerType.Company;
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [MaxLength(200)] public string? OwnerName { get; set; }
    public decimal SharePercent { get; set; }
    public DateTime EffectiveFrom { get; set; } = AfghanistanBusinessClock.SystemToday;
    public DateTime? EffectiveTo { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class AssetRentTransaction : BaseEntity
{
    public int OperationalAssetId { get; set; }
    public OperationalAsset? OperationalAsset { get; set; }
    public int? LoadingRegisterId { get; set; }
    public LoadingRegister? LoadingRegister { get; set; }
    public int? TransportLegId { get; set; }
    public InventoryTransportLeg? TransportLeg { get; set; }
    public int? InventoryTransportReceiptId { get; set; }
    public InventoryTransportReceipt? InventoryTransportReceipt { get; set; }
    public int? TruckDispatchId { get; set; }
    public TruckDispatch? TruckDispatch { get; set; }
    public DateTime RentDate { get; set; } = AfghanistanBusinessClock.SystemToday;
    public AssetRentUsageType UsageType { get; set; } = AssetRentUsageType.InternalCompanyUse;
    public AssetRentChargedToType ChargedToType { get; set; } = AssetRentChargedToType.CompanyInternal;
    public int? ChargedToContractId { get; set; }
    public Contract? ChargedToContract { get; set; }
    public int? ChargedToCustomerId { get; set; }
    public Customer? ChargedToCustomer { get; set; }
    public int? ChargedToCompanyId { get; set; }
    public Company? ChargedToCompany { get; set; }
    public int? ChargedToPartnerId { get; set; }
    public Partner? ChargedToPartner { get; set; }
    public int? ChargedToServiceProviderId { get; set; }
    public ServiceProvider? ChargedToServiceProvider { get; set; }
    public decimal? QuantityMt { get; set; }
    public decimal? DistanceKm { get; set; }
    public decimal? Days { get; set; }
    public decimal Rate { get; set; }
    [MaxLength(10)] public string Currency { get; set; } = "USD";
    public decimal FxRateToUsd { get; set; } = 1m;
    public decimal AmountOriginal { get; set; }
    public decimal AmountUsd { get; set; }
    [MaxLength(200)] public string? ReferenceDocument { get; set; }
    [MaxLength(1000)] public string? Description { get; set; }
    public bool IsPostedToLedger { get; set; }
    public int? LedgerEntryId { get; set; }
    public LedgerEntry? LedgerEntry { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }
    [MaxLength(500)] public string? CancelReason { get; set; }

    public ICollection<AssetRentShare> RentShares { get; set; } = [];
}

public class AssetRentShare : BaseEntity
{
    public int AssetRentTransactionId { get; set; }
    public AssetRentTransaction? AssetRentTransaction { get; set; }
    public AssetOwnerType OwnerType { get; set; } = AssetOwnerType.Company;
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }
    [MaxLength(200)] public string? OwnerName { get; set; }
    public decimal SharePercent { get; set; }
    public decimal ShareAmountUsd { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}
