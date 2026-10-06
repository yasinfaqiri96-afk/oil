using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PTG.ContractReportingRepair;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Reporting;

namespace PTG.ContractReportingRepair.Tests;

internal sealed class DirectCogsTestController(ApplicationDbContext db, ISalesAccountingAdapter adapter,
    IProfitAndLossService pnl) : ControllerBase
{
    public async Task<IActionResult> Run()
    {
        if (!db.Database.GetConnectionString()!.Contains("Database=zuri_p002_repair_test;", StringComparison.Ordinal))
            return NotFound();
        if (adapter is not DirectTransportSalesAccountingAdapter)
            throw new Exception("Production DI did not select direct COGS decorator: " + adapter.GetType().FullName);
        var checks = new List<string>();
        void Check(bool value, string name) {
            if (!value) throw new Exception(name);
            checks.Add(name);
        }
        var sale = await db.SalesTransactions.SingleAsync(x => x.Id == 3);
        var movements = await db.InventoryMovements.CountAsync();
        var pools = await db.InventoryAverageCosts.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var poolsBefore = System.Text.Json.JsonSerializer.Serialize(pools);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var before = await pnl.BuildForSalesAsync([3,4]);
            Check(before.UncostedSaleCount == 1 && before.CostOfGoodsSoldUsd == 99500m, "Existing warning reproduced");
            var posted = await adapter.TryPostCogsAsync(sale);
            Check(posted.Status == PaymentPostingStatus.Posted, "Direct COGS posted");
            Check(posted.Journal!.Lines.Sum(x => x.Debit) == 98000m
                && posted.Journal.Lines.Sum(x => x.Credit) == 98000m, "Balanced historical cost 98MT = USD98000");
            Check(posted.Journal.Lines.Any(x => x.AccountId == 4 && x.Credit == 98000m)
                && posted.Journal.Lines.All(x => x.AccountId != 3), "Transit credited; inventory untouched");
            var duplicate = await adapter.TryPostCogsAsync(sale);
            Check(duplicate.Status == PaymentPostingStatus.Duplicate, "Replay is idempotent");
            Check(await db.SalesCostConsumptions.CountAsync(x => x.SalesTransactionId == 3) == 1, "Single cost snapshot");
            var after = await pnl.BuildForSalesAsync([3,4]);
            Check(after.UncostedSaleCount == 0 && after.CostOfGoodsSoldUsd == 197500m
                && after.GrossProfitUsd == 98750m, "Canonical P&L verified; no missing COGS");
            var inventory = await adapter.TryPostCogsAsync(await db.SalesTransactions.SingleAsync(x => x.Id == 4));
            Check(inventory.Status == PaymentPostingStatus.Duplicate, "Existing tank sale retains installed path");
            var reversal = await adapter.TryReverseCogsAsync(sale, sale.SaleDate.Date);
            Check(reversal.Status == PaymentPostingStatus.Posted && reversal.Journal!.Lines.Sum(x => x.Debit) == 98000m,
                "Exact balanced direct COGS reversal");
            Check((await adapter.TryReverseCogsAsync(sale, sale.SaleDate.Date)).Status == PaymentPostingStatus.Duplicate,
                "Reversal is idempotent");
            Check(!await db.SalesCostConsumptions.AnyAsync(x => x.SalesTransactionId == 3 && x.Status == SalesCostConsumptionStatus.Active),
                "Reversed cost excluded from active P&L");
            Check(await db.InventoryMovements.CountAsync() == movements, "No artificial stock movements");
            Check(System.Text.Json.JsonSerializer.Serialize(await db.InventoryAverageCosts.AsNoTracking().OrderBy(x => x.Id).ToListAsync())
                == poolsBefore, "No artificial valuation pool on sale or reversal");
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Check(await db.SalesCostConsumptions.CountAsync(x => x.SalesTransactionId == 3) == 0, "Test rollback leaves no COGS repair");
        return Ok(checks);
    }

    public async Task<IActionResult> PostPreview()
    {
        if (!db.Database.GetConnectionString()!.Contains("Database=zuri_p002_repair_test;", StringComparison.Ordinal))
            return NotFound();
        var result = await adapter.TryPostCogsAsync(await db.SalesTransactions.SingleAsync(x => x.Id == 3));
        return Ok(new { result.Status, result.Reason, Cost = result.Journal?.Lines.Sum(x => x.Debit) });
    }
}
