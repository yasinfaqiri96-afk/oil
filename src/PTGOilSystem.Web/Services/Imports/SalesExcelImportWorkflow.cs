using System.Data;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Operations;
using PTGOilSystem.Web.Services.OperationalPeriod;

namespace PTGOilSystem.Web.Services.Imports;

// Coordinates existing sale-line commands; owns no alternate inventory or ledger writes.
// No preview method calls SaveChanges. Commit holds one transaction for every row,
// including idempotency markers, receipts, movements, legacy ledger and journals.
public sealed class SalesExcelImportWorkflow(ApplicationDbContext db,
    CargoSourceQueryService cargo, IFormTokenGuard tokens)
{
    public async Task<Dictionary<string, List<string>>> ValidateAsync(SalesExcelPlan plan,
        IReadOnlyDictionary<string, GroupSaleSourceItem> sources, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, List<string>>();
        void Error(string key, string message)
        {
            if (!errors.ContainsKey(key)) errors[key] = [];
            errors[key].Add(message);
        }
        if (plan.Rows.Count is 0 or > SalesExcelWorkbookParser.MaxRows
            || plan.Rows.Select(r => SalesExcelWorkbookParser.Token(r.ImportKey)).Distinct().Count() != plan.Rows.Count)
        {
            Error("", "ردیف‌ها یا ImportKeyها معتبر نیستند."); return errors;
        }
        if (plan.Mappings.Count != plan.Rows.Count || plan.Mappings.Select(m => m.ImportKey).Distinct().Count() != plan.Rows.Count
            || plan.Mappings.Any(m => !plan.Rows.Any(r => r.ImportKey == m.ImportKey)))
        {
            Error("", "تمام ردیف‌ها باید دقیقاً یک تطبیق داشته باشند."); return errors;
        }
        var maps = plan.Mappings.ToDictionary(m => m.ImportKey);
        if (await db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive, ct)
            && !await db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive && c.Code == "USD", ct))
            Error("", "ارز دالر در سیستم فعال نیست.");
        var keys = plan.Rows.Select(r => SalesExcelWorkbookParser.Token(r.ImportKey)).ToArray();
        var consumed = (await db.ProcessedFormTokens.AsNoTracking().Where(t => keys.Contains(t.Token)).Select(t => t.Token).ToListAsync(ct)).ToHashSet();
        var customers = (await db.Customers.AsNoTracking().Where(c => c.IsActive).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var suppliers = (await db.Suppliers.AsNoTracking().Where(c => c.IsActive).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var terminals = (await db.Terminals.AsNoTracking().Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var planned = new Dictionary<string, decimal>();
        var period = await new OperationalPeriodGuard(db).GetStatusAsync(ct);
        var snapshots = new Dictionary<(string Key, DateTime Date), CargoSourceSnapshot?>();
        foreach (var row in plan.Rows.OrderBy(r => r.SaleDate).ThenBy(r => r.ExcelRow))
        {
            var m = maps[row.ImportKey];
            if (period.Covers(row.SaleDate)) Error(row.ImportKey, "تاریخ فروش در دورهٔ عملیاتی بسته است؛ ایمپورت از قفل دوره عبور نمی‌کند.");
            if (consumed.Contains(SalesExcelWorkbookParser.Token(row.ImportKey))) Error(row.ImportKey, "این ImportKey قبلاً ثبت شده است؛ هیچ ردیفی از این درخواست ثبت نمی‌شود.");
            var buyer = SaleBuyerKey.Parse(m.BuyerKey);
            if (!(buyer.CustomerId is > 0 && customers.Contains(buyer.CustomerId.Value)
                || buyer.SupplierId is > 0 && suppliers.Contains(buyer.SupplierId.Value)))
                Error(row.ImportKey, "خریدار فعال را صریحاً انتخاب کنید؛ تطبیق نام خودکار نیست.");
            if (row.RequiresReview && (!m.ReviewConfirmed || string.IsNullOrWhiteSpace(m.ReviewNote)))
                Error(row.ImportKey, "این ردیف نیازمند تأیید و توضیح بررسی است: " + row.ReviewReason);
            if (SalesExcelWorkbookParser.BuildNotes(row, m.ReviewNote).Length > 1000)
                Error(row.ImportKey, "یادداشت نهایی از ۱۰۰۰ حرف بیشتر است.");
            if (!sources.TryGetValue(m.SourceKey, out var source))
            { Error(row.ImportKey, "منبع واقعی موجودی را انتخاب کنید یا منبع تغییر کرده است."); continue; }
            if (source.MoveDate.Date > row.SaleDate.Date && source.Kind != GroupSaleSourceKind.TerminalStock)
                Error(row.ImportKey, "تاریخ فروش قبل از تاریخ ایجاد منبع است.");
            var used = planned.GetValueOrDefault(source.Key);
            if (source.Kind == GroupSaleSourceKind.TerminalStock)
            {
                var snapshotKey = (source.Key, row.SaleDate);
                if (!snapshots.TryGetValue(snapshotKey, out var snapshot))
                    snapshots[snapshotKey] = snapshot = (await cargo.LoadStockSourcesAsync(row.SaleDate, ct, source.ProductId,
                        source.TerminalId, source.StorageTankId, source.SourcePurchaseContractId)).SingleOrDefault();
                if (snapshot is null || !CargoOperationEligibility.Evaluate(snapshot, CargoAction.DirectSale).Allowed
                    || snapshot.SellableStockMt.GetValueOrDefault() < used + row.QuantityMt)
                    Error(row.ImportKey, "موجودی قابل فروش این قرارداد/مخزن در تاریخ فروش برای مجموع ردیف‌ها کافی نیست.");
            }
            else
            {
                if (source.AvailableMt < used + row.QuantityMt) Error(row.ImportKey, "مجموع وزن ردیف‌ها از ماندهٔ واقعی منبع بیشتر است.");
                if (source.IsFullVehicle && (used > 0 || source.AvailableMt != row.QuantityMt))
                    Error(row.ImportKey, "این منبع فقط فروش کامل را پشتیبانی می‌کند؛ برای فروش تقسیم‌شده منبع مناسب انتخاب کنید.");
                if (source.Kind == GroupSaleSourceKind.LoadingRegister && !(m.SaleTerminalId is > 0 && terminals.Contains(m.SaleTerminalId.Value)))
                    Error(row.ImportKey, "محل فروش مستقیم بارگیری را انتخاب کنید.");
            }
            planned[source.Key] = used + row.QuantityMt;
        }
        // Strict timeline for the exact contract/tank and the physical tank. Combine
        // ALL proposed rows, so two individually-valid rows cannot oversell together.
        var stockRows = plan.Rows.Where(r => sources.TryGetValue(maps[r.ImportKey].SourceKey, out var s)
            && s.Kind == GroupSaleSourceKind.TerminalStock).ToList();
        foreach (var group in stockRows.GroupBy(r => maps[r.ImportKey].SourceKey))
        {
            var s = sources[group.Key];
            var existing = await db.InventoryMovements.AsNoTracking()
                .Where(x => x.ProductId == s.ProductId && x.TerminalId == s.TerminalId && x.StorageTankId == s.StorageTankId
                    && (x.ContractId == s.SourcePurchaseContractId || x.ContractId == null && x.LoadingReceipt != null
                        && x.LoadingReceipt.LoadingRegister!.ContractId == s.SourcePurchaseContractId))
                .Select(x => new TimelinePoint(x.MovementDate, x.Id, x.Direction == MovementDirection.In || x.Direction == MovementDirection.Adjustment ? x.QuantityMt : -x.QuantityMt))
                .ToListAsync(ct);
            if (!TimelineValid(existing, group.ToList())) foreach (var row in group)
                Error(row.ImportKey, "این ایمپورت موجودی تاریخی قرارداد/مخزن را در تاریخ فروش یا پس از آن منفی می‌کند.");
        }
        foreach (var group in stockRows.GroupBy(r => { var s = sources[maps[r.ImportKey].SourceKey]; return (s.ProductId, s.TerminalId, s.StorageTankId); }))
        {
            var s = group.Key;
            var existing = await db.InventoryMovements.AsNoTracking()
                .Where(x => x.ProductId == s.ProductId && x.TerminalId == s.TerminalId && x.StorageTankId == s.StorageTankId)
                .Select(x => new TimelinePoint(x.MovementDate, x.Id, x.Direction == MovementDirection.In || x.Direction == MovementDirection.Adjustment ? x.QuantityMt : -x.QuantityMt))
                .ToListAsync(ct);
            if (!TimelineValid(existing, group.ToList())) foreach (var row in group)
                Error(row.ImportKey, "موجودی فیزیکی مخزن در خط زمانی کافی نیست.");
        }
        return errors;
    }

    internal sealed record TimelinePoint(DateTime Date, int Order, decimal Quantity);
    internal static bool TimelineValid(IEnumerable<TimelinePoint> existing, IReadOnlyCollection<SalesExcelRow> rows)
    {
        var start = rows.Min(r => r.SaleDate);
        var points = existing.Select(x => (x.Date, Proposed: false, Order: (long)x.Order, x.Quantity))
            .Concat(rows.Select(r => (Date: r.SaleDate, Proposed: true, Order: (long)r.ExcelRow, Quantity: -r.QuantityMt)))
            .OrderBy(x => x.Date).ThenBy(x => x.Proposed).ThenBy(x => x.Order);
        decimal balance = 0;
        foreach (var p in points)
        {
            balance += p.Quantity;
            if (p.Date >= start && balance < 0) return false;
        }
        return true;
    }

    public async Task<List<int>> CommitAsync(SalesExcelPlan plan,
        Func<Task<Dictionary<string, GroupSaleSourceItem>>> loadSources,
        Func<SalesExcelRow, SalesExcelMapping, GroupSaleSourceItem, Task<SalesTransaction>> createSale,
        CancellationToken ct = default)
    {
        if (!plan.Ready) throw new BusinessRuleException("IMPORT_NOT_REVIEWED", "ابتدا اعتبارسنجی و تأیید پیش‌نمایش را انجام دهید.");
        if (!db.Database.IsRelational()) throw new BusinessRuleException("IMPORT_TRANSACTION_REQUIRED", "ثبت نهایی به دیتابیس دارای تراکنش نیاز دارد.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var sources = await loadSources();
            var selected = plan.Mappings.Select(m => sources.GetValueOrDefault(m.SourceKey)).Where(s => s is not null).ToList();
            if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                var tankIds = selected.Where(s => s!.Kind == GroupSaleSourceKind.TerminalStock).Select(s => s!.StorageTankId).Distinct().Order().ToArray();
                var loadingIds = selected.Where(s => s!.Kind == GroupSaleSourceKind.LoadingRegister).Select(s => s!.Id).Distinct().Order().ToArray();
                if (tankIds.Length > 0) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"StorageTanks\" WHERE \"Id\" = ANY({tankIds}) ORDER BY \"Id\" FOR UPDATE", ct);
                if (loadingIds.Length > 0) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"LoadingRegisters\" WHERE \"Id\" = ANY({loadingIds}) ORDER BY \"Id\" FOR UPDATE", ct);
            }
            // Re-read after locks; serializable conflicts fail closed (no automatic retry).
            sources = await loadSources();
            var errors = await ValidateAsync(plan, sources, ct);
            if (errors.Count > 0) throw new BusinessRuleException("IMPORT_REVALIDATION_FAILED", string.Join("؛ ", errors.Select(e => e.Key + ": " + string.Join("؛ ", e.Value)).Take(8)));
            var maps = plan.Mappings.ToDictionary(m => m.ImportKey);
            var ids = new List<int>();
            foreach (var row in plan.Rows.OrderBy(r => r.SaleDate).ThenBy(r => r.ExcelRow))
            {
                var mapping = maps[row.ImportKey];
                var marker = new ProcessedFormToken { Token = SalesExcelWorkbookParser.Token(row.ImportKey),
                    Purpose = "Sales.ExcelImport", ReferenceType = nameof(SalesTransaction),
                    UserId = int.TryParse(plan.UserId, out var userId) ? userId : null, ConsumedAtUtc = DateTime.UtcNow };
                db.ProcessedFormTokens.Add(marker);
                await db.SaveChangesAsync(ct); // unique index is the concurrent/replay backstop
                var sale = await createSale(row, mapping, sources[mapping.SourceKey]);
                if (sale.QuantityMt != row.QuantityMt || sale.TotalUsd != row.SourceAmountUsd)
                    throw new BusinessRuleException("IMPORT_SALE_MISMATCH", "مقدار یا مبلغ سند با ردیف پیش‌نمایش مطابقت ندارد.");
                marker.ReferenceId = sale.Id;
                ids.Add(sale.Id);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return ids;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (tokens.IsDuplicate(ex)) throw new BusinessRuleException("IMPORT_DUPLICATE", "یکی از ImportKeyها قبلاً ثبت شده است؛ تمام این درخواست برگشت داده شد.");
            throw;
        }
    }
}
