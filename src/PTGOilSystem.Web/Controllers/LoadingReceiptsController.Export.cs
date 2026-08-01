using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class LoadingReceiptsController
{
    [HttpGet, EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, string? q = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var view = (ViewResult)await Index(q, fromDate, toDate, page: 0);
        var document = TabularExportAuto.Build(view.Model!, "PTG_Loading_Receipts", "رسیدهای بارگیری", "Loading Receipts",
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 13, "ReceiptDate"),
            new("قرارداد", "Contract", TabularExportValueType.Text, 16, "ContractNumber"),
            new("جنس", "Product", TabularExportValueType.Text, 18, "ProductName"),
            new("نمبر وسیله", "Vehicle no.", TabularExportValueType.Text, 16, "VehicleNumber"),
            new("ترمینال", "Terminal", TabularExportValueType.Text, 18, "TerminalName"),
            new("مخزن", "Tank", TabularExportValueType.Text, 15, "StorageTankCode"),
            new("مقدار MT", "Quantity MT", TabularExportValueType.Number, 14, "ReceivedQuantityMt"),
            new("مرجع", "Reference", TabularExportValueType.Text, 20, "ReferenceDocument")
        ], TabularExportSupport.FiltersFromQuery(Request));
        return TabularExportSupport.File(this, format, document);
    }
}
