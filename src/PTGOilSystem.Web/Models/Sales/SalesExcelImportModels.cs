using Microsoft.AspNetCore.Mvc.Rendering;

namespace PTGOilSystem.Web.Models.Sales;

public sealed record SalesExcelRow
{
    public int ExcelRow { get; init; }
    public string ImportKey { get; init; } = "";
    public string ContractNumber { get; init; } = "";
    public string SaleDateSolar { get; init; } = "";
    public DateTime SaleDate { get; init; }
    public string BuyerName { get; init; } = "";
    public string TicketSerial { get; init; } = "";
    public string TruckNumber { get; init; } = "";
    public string DriverName { get; init; } = "";
    public decimal QuantityMt { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal SourceAmountUsd { get; init; }
    public string Notes { get; init; } = "";
    public string ReviewReason { get; init; } = "";
    public bool RequiresReview => ReviewReason.Length > 0;
}

public sealed record SalesExcelMapping
{
    public string ImportKey { get; init; } = "";
    public string BuyerKey { get; init; } = "";
    public string SourceKey { get; init; } = "";
    public int? SaleTerminalId { get; init; }
    public bool ReviewConfirmed { get; init; }
    public string ReviewNote { get; init; } = "";
}

// Protected, user-bound manifest. Only the server may issue a Ready manifest;
// final posting accepts no editable row data or mappings.
public sealed record SalesExcelPlan
{
    public string UserId { get; init; } = "";
    public DateTime ExpiresUtc { get; init; }
    public bool Ready { get; init; }
    public List<SalesExcelRow> Rows { get; init; } = [];
    public List<SalesExcelMapping> Mappings { get; init; } = [];
}

public sealed class SalesExcelImportViewModel
{
    public string Manifest { get; set; } = "";
    public string? ReadyManifest { get; set; }
    public List<SalesExcelRow> Rows { get; set; } = [];
    public List<SalesExcelMapping> Mappings { get; set; } = [];
    public Dictionary<string, List<string>> Errors { get; set; } = [];
    public List<SelectListItem> Buyers { get; set; } = [];
    public List<SelectListItem> Sources { get; set; } = [];
    public List<SelectListItem> Terminals { get; set; } = [];
}
