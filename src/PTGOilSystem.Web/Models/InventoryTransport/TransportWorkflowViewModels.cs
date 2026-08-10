using System.ComponentModel.DataAnnotations;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Models.InventoryTransport;

public enum TransportStartSourceKind
{
    [Display(Name = "موجودی مخزن")]
    Inventory = 1,
    [Display(Name = "رسید/بارگیری مستقیم")]
    LoadingReceipt = 2,
    [Display(Name = "حمل در جریان")]
    ActiveTransport = 3
}

public sealed class TransportStartViewModel
{
    public TransportStartSourceKind SourceKind { get; set; } = TransportStartSourceKind.Inventory;
    public int? LoadingReceiptId { get; set; }
    public int? TransportLegId { get; set; }
    public IReadOnlyList<TransportLookupItem> LoadingReceipts { get; set; } = [];
    public IReadOnlyList<TransportLookupItem> ActiveTransports { get; set; } = [];
}

public sealed record TransportLookupItem(int Id, string Label, decimal AvailableQuantityMt = 0m);

public sealed class TransportStartFromReceiptViewModel
{
    [Range(1, int.MaxValue)]
    public int LoadingReceiptId { get; set; }
    public string ReceiptLabel { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal AvailableQuantityMt { get; set; }

    [Range(typeof(decimal), "0.0001", "79228162514264337593543950335")]
    public decimal QuantityMt { get; set; }

    public LoadingTransportType TransportType { get; set; } = LoadingTransportType.Truck;
    public int? TruckId { get; set; }
    public int? WagonId { get; set; }
    public int? VesselId { get; set; }
    public int? DriverId { get; set; }
    public int? ServiceProviderId { get; set; }

    [DataType(DataType.Date)]
    public DateTime TransportDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [StringLength(100)]
    public string? Reference { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class TransportContinueViewModel
{
    public List<TransportContinueSourceInput> Sources { get; set; } = [];
    public LoadingTransportType TargetTransportType { get; set; } = LoadingTransportType.Truck;
    public int? TargetTruckId { get; set; }
    public int? TargetWagonId { get; set; }
    public int? TargetVesselId { get; set; }
    public int? DriverId { get; set; }

    [DataType(DataType.Date)]
    public DateTime TransferDate { get; set; } = AfghanistanBusinessClock.SystemToday;

    [StringLength(100)]
    public string? TicketSerialNumber { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class TransportFreightSettlementViewModel
{
    public int TransportLegId { get; set; }

    [DataType(DataType.Date)]
    public DateTime SettlementDate { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? FreightRateUsdPerMt { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? FreightCostUsd { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}

public sealed class TransportContinueSourceInput
{
    public int LegId { get; set; }
    public bool Selected { get; set; }
    public string Label { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal RemainingQuantityMt { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal QuantityMt { get; set; }
}
