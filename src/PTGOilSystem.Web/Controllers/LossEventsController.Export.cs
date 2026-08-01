using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

public partial class LossEventsController
{
    [HttpGet, EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, [FromQuery] LossEventIndexFilterViewModel? filter = null)
    {
        var view = (ViewResult)await Index(filter, page: 0);
        var document = TabularExportAuto.Build(view.Model!, "PTG_Loss_Events", "رویدادهای کسری", "Loss Events",
        [
            new("تاریخ", "Date", TabularExportValueType.Date, 13, "EventDate"),
            new("مرحله", "Stage", TabularExportValueType.Text, 15, "Stage"),
            new("جنس", "Product", TabularExportValueType.Text, 18, "ProductName"),
            new("قرارداد/محموله", "Contract/shipment", TabularExportValueType.Text, 19, "ContractNumber", "ShipmentCode"),
            new("نمبر وسیله", "Vehicle no.", TabularExportValueType.Text, 16, "VehicleNumber"),
            new("مسئول", "Responsible", TabularExportValueType.Text, 19, "ResponsiblePartyName"),
            new("تفاوت MT", "Difference MT", TabularExportValueType.Number, 14, "DifferenceQuantityMt"),
            new("مجاز MT", "Allowance MT", TabularExportValueType.Number, 14, "AllowableLossMt"),
            new("قابل‌حساب MT", "Chargeable MT", TabularExportValueType.Number, 15, "ChargeableLossMt"),
            new("اثر موجودی", "Affects inventory", TabularExportValueType.Boolean, 13, "AffectsInventory")
        ], TabularExportSupport.FiltersFromQuery(Request), forceLandscape: true);
        return TabularExportSupport.File(this, format, document);
    }
}
