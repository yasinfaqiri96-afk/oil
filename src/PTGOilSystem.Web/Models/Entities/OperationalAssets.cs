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
    public OperationalAssetOwnershipMode OwnershipMode { get; set; } = OperationalAssetOwnershipMode.FullyCompanyOwned;
    public decimal MonthlyDepreciationUsd { get; set; }
    public decimal? DefaultInternalRateUsd { get; set; }
    public decimal? DefaultExternalRateUsd { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    public ICollection<AssetOwnershipShare> OwnershipShares { get; set; } = [];
    public ICollection<AssetRentTransaction> RentTransactions { get; set; } = [];
    public ICollection<ExpenseTransaction> ExpenseTransactions { get; set; } = [];
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
