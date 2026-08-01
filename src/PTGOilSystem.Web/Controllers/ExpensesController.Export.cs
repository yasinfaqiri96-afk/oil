using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Expenses;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class ExpensesController
{
    [HttpGet, EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, [FromQuery] ExpenseIndexFilterViewModel? filter = null)
    {
        var view = (ViewResult)await Index(filter, page: 0);
        var document = TabularExportAuto.Build(view.Model!, "PTG_Expenses", "مصارف", "Expenses",
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 13, "ExpenseDate"),
            new("نوع مصرف", "Expense type", TabularExportValueType.Text, 19, "ExpenseTypeName"),
            new("قرارداد/محموله", "Contract/shipment", TabularExportValueType.Text, 19, "ContractNumber", "ShipmentCode"),
            new("مرجع حمل", "Transport ref.", TabularExportValueType.Text, 19, "TruckDispatchLabel", "TransportLegLabel"),
            new("نمبر وسیله", "Vehicle no.", TabularExportValueType.Text, 16, "VehicleNumber"),
            new("شرکت/دارایی", "Provider/asset", TabularExportValueType.Text, 20, "ServiceProviderName", "OperationalAssetName"),
            new("مبلغ", "Amount", TabularExportValueType.Number, 15, "Amount"),
            new("ارز", "Currency", TabularExportValueType.Text, 10, "Currency"),
            new("نرخ USD", "USD rate", TabularExportValueType.Number, 13, "AppliedFxRateToUsd"),
            new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 15, "AmountUsd"),
            new("شرح", "Description", TabularExportValueType.Text, 28, "Description")
        ], TabularExportSupport.FiltersFromQuery(Request), forceLandscape: true);
        return TabularExportSupport.File(this, format, document);
    }
}
