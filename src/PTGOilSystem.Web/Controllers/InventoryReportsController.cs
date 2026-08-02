using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryReports;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class InventoryReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;

    public InventoryReportsController(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    public async Task<IActionResult> IlinkaStock([Bind(Prefix = "Filter")] IlinkaStockReportFilterViewModel? filter = null)
    {
        filter ??= new IlinkaStockReportFilterViewModel();

        if (filter.FromDate.HasValue && filter.ToDate.HasValue && filter.FromDate > filter.ToDate)
        {
            ModelState.AddModelError(string.Empty, "بازه تاریخ معتبر نیست.");
        }

        await PopulateLookupsAsync(filter);

        if (!ModelState.IsValid)
        {
            return View(new IlinkaStockReportViewModel { Filter = filter });
        }

        var stockRows = await _stock.GetStockCardAsync(
            productId: filter.ProductId,
            contractId: filter.ContractId,
            terminalId: filter.TerminalId,
            storageTankId: filter.StorageTankId,
            fromUtc: filter.FromDate,
            toUtc: filter.ToDate);
        var openingRows = filter.FromDate.HasValue
            ? await _stock.GetStockCardAsync(
                productId: filter.ProductId,
                contractId: filter.ContractId,
                terminalId: filter.TerminalId,
                storageTankId: filter.StorageTankId,
                toUtc: filter.FromDate.Value.Date.AddTicks(-1))
            : [];
        var closingRows = await _stock.GetStockCardAsync(
            productId: filter.ProductId,
            contractId: filter.ContractId,
            terminalId: filter.TerminalId,
            storageTankId: filter.StorageTankId,
            toUtc: filter.ToDate);

        var tankIds = stockRows
            .Where(r => r.StorageTankId.HasValue)
            .Select(r => r.StorageTankId!.Value)
            .Distinct()
            .ToList();
        var tankNames = await StorageTankDisplay.LoadNamesAsync(
            _db.StorageTanks.AsNoTracking().Where(t => tankIds.Contains(t.Id)));

        var rows = stockRows.Select(r => new IlinkaStockReportRowViewModel
        {
            MovementId = r.MovementId,
            Date = r.MovementDate,
            Reference = r.ReferenceDocument,
            ProductCode = r.ProductCode,
            ProductName = r.ProductName,
            ContractNumber = r.ContractNumber,
            TerminalCode = r.TerminalCode,
            TerminalName = r.TerminalName,
            StorageTankCode = StorageTankDisplay.Resolve(tankNames, r.StorageTankId, r.StorageTankCode),
            Direction = r.Direction,
            InQuantityMt = r.Direction == MovementDirection.In ? r.QuantityMt : 0m,
            OutQuantityMt = r.Direction == MovementDirection.Out ? r.QuantityMt : 0m,
            AdjustmentQuantityMt = r.Direction == MovementDirection.Adjustment ? r.SignedQuantityMt : 0m,
            TransferQuantityMt = r.Direction == MovementDirection.Transfer ? r.SignedQuantityMt : 0m,
            RunningBalanceMt = r.RunningBalanceMt,
            SourceType = BuildSourceType(r),
            SourceReference = r.ReferenceDocument,
            Notes = r.Notes
        }).ToList();

        return View(new IlinkaStockReportViewModel
        {
            Filter = filter,
            OpeningBalanceMt = SumLatestRunningBalanceByScope(openingRows),
            ClosingBalanceMt = SumLatestRunningBalanceByScope(closingRows),
            Rows = rows
        });
    }

    private async Task PopulateLookupsAsync(IlinkaStockReportFilterViewModel filter)
    {
        ViewBag.Products = new SelectList(
            await _db.Products
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.ProductId);

        var contractLookupRows = await _db.Contracts
            .AsNoTracking()
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Select(c => new
            {
                c.Id,
                c.ContractName,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            contractLookupRows
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractName,
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            filter.ContractId);

        ViewBag.Terminals = new SelectList(
            await _db.Terminals
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Code)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.TerminalId);

        ViewBag.StorageTanks = new SelectList(
            await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks
                .AsNoTracking()
                .OrderBy(t => t.DisplayName ?? t.TankCode)),
            "Id",
            "Display",
            filter.StorageTankId);
    }

    private static decimal SumLatestRunningBalanceByScope(IReadOnlyList<StockCardItem> rows)
        => rows
            .GroupBy(r => new { r.ProductId, r.TerminalId, r.ContractId })
            .Sum(g => g
                .OrderBy(r => r.MovementDate)
                .ThenBy(r => r.MovementId)
                .LastOrDefault()?.RunningBalanceMt ?? 0m);

    private static string BuildSourceType(StockCardItem row)
    {
        if (!string.IsNullOrWhiteSpace(row.ReferenceDocument))
        {
            return row.Direction switch
            {
                MovementDirection.In => "InventoryMovement / Receipt",
                MovementDirection.Out => "InventoryMovement / Dispatch or Sale",
                MovementDirection.Adjustment => "InventoryMovement / Adjustment",
                MovementDirection.Transfer => "InventoryMovement / Transfer",
                _ => "InventoryMovement"
            };
        }

        return "InventoryMovement";
    }
}
