using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Security;

namespace PTGOilSystem.Web.Controllers;

public partial class ReportsController
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> InventorySales(
        [FromQuery] ManagementReportFilterViewModel? filter = null,
        bool showDetails = false, int page = 1, CancellationToken ct = default)
    {
        filter ??= new ManagementReportFilterViewModel();
        // Only the dimensions shared by the stock and reservation readers belong
        // to this report. Contract/date-range drill-down remains on the stock card.
        filter = new ManagementReportFilterViewModel
        {
            ProductId = filter.ProductId, TerminalId = filter.TerminalId,
            StorageTankId = filter.StorageTankId, ToDate = filter.ToDate
        };
        await PopulateLookupsAsync(filter, includeInventory: true);
        var stock = await BuildSellableStockAsync(filter, ct);
        var canViewMovements = RoleAccessRules.CanAccessController(User, "Inventory",
            HttpContext.RequestServices.GetService<ClientModuleProfile>());
        InventoryOperationsReportViewModel? operations = null;
        IReadOnlyList<StockCardItem> movements = [];
        if (showDetails)
        {
            operations = await BuildInventoryOperationsReportAsync(filter);
            // Avoid loading the entire stock history until a product is selected.
            if (filter.ProductId.HasValue && canViewMovements)
            {
                movements = await _stock.GetStockCardAsync(
                    productId: filter.ProductId, terminalId: filter.TerminalId,
                    storageTankId: filter.StorageTankId,
                    toUtc: filter.ToDate?.Date.AddDays(1).AddTicks(-1), ct: ct);
            }
        }
        var pageCount = Math.Max(1, (int)Math.Ceiling(movements.Count / (double)InventorySalesReportViewModel.PageSize));
        page = Math.Clamp(page, 1, pageCount);
        return View(new InventorySalesReportViewModel
        {
            Stock = stock, Operations = operations, ShowDetails = showDetails,
            CanViewMovements = canViewMovements,
            TotalMovements = movements.Count, Page = page,
            Movements = movements.OrderByDescending(r => r.MovementDate)
                .ThenByDescending(r => r.MovementId)
                .Skip((page - 1) * InventorySalesReportViewModel.PageSize)
                .Take(InventorySalesReportViewModel.PageSize).ToList()
        });
    }
}
