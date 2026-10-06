using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.LoadingReceipts;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

// Archive changes only list visibility. Accounting documents and reversals remain intact.
public sealed class OperationLifecycleService(
    ApplicationDbContext db,
    ILoadingReceiptCancellationService receipts,
    ISimplePurchaseWorkflowService simplePurchase,
    IPurchaseAccountingAdapter purchaseAccounting,
    IAuditService audit,
    IAfghanistanBusinessClock clock)
{
    public async Task CancelAsync(string kind, int id, string? reason, int? actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw Rule("ثبت دلیل لغو الزامی است.");
        reason = reason.Trim();
        if (reason.Length > 1000) throw Rule("دلیل لغو نباید بیشتر از ۱۰۰۰ حرف باشد.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (kind == "Contract")
        {
            var contract = await db.Contracts.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Rule("قرارداد پیدا نشد.");
            if (contract.IsArchived) throw Rule("قرارداد از فهرست حذف شده است.");
            if (contract.Status == ContractStatus.Closed) throw Rule("ابتدا قرارداد بسته‌شده را از مسیر مجاز باز کنید.");
            if (await db.PaymentTransactions.AnyAsync(x => x.ContractId == id, ct)
                || await db.SalesTransactions.AnyAsync(x => (x.ContractId == id || x.SourcePurchaseContractId == id) && !x.IsCancelled, ct)
                || await db.ContractBalanceTransfers.AnyAsync(x => (x.FromContractId == id || x.ToContractId == id) && !x.IsCancelled, ct)
                || await db.SupplierPaymentAllocations.AnyAsync(x => x.ContractId == id && x.Status == SupplierPaymentAllocationStatus.Active, ct))
                throw Rule("قرارداد پرداخت، فروش یا انتقال حساب فعال دارد؛ ابتدا آن عملیات را از مسیر استاندارد لغو کنید.");
            var hasSimplePosting = await simplePurchase.HasPostingAsync(id, ct);
            if (hasSimplePosting && await db.LoadingReceipts.AnyAsync(x => !x.IsCancelled
                && x.LoadingRegister != null && x.LoadingRegister.ContractId == id
                && x.LoadingRegister.ImportUniqueKey == $"SIMPLE-PURCHASE-CONTRACT:{id}", ct))
                await simplePurchase.ReverseAsync(id, reason, actorUserId: actorId, ct: ct);
            if (await db.LedgerEntries.AnyAsync(x => x.ContractId == id && x.SourceType != "Loading", ct)
                && !hasSimplePosting)
                throw Rule("قرارداد سند مالی مستقل دارد؛ ابتدا آن سند را از مسیر استاندارد برگردانید.");
            var loadings = await db.LoadingRegisters.Where(x => x.ContractId == id).OrderBy(x => x.Id).ToListAsync(ct);
            foreach (var loading in loadings) await CancelLoadingCoreAsync(loading, reason, actorId, ct);
            var previous = contract.Status;
            contract.Status = ContractStatus.Cancelled;
            await audit.LogAsync(nameof(Contract), id, AuditAction.Reverse,
                diff: AuditDiffFormatter.ForUpdate(("Status", previous, contract.Status), ("CancellationReason", null, reason)),
                actorUserId: actorId, ct: ct);
        }
        else if (kind == "LoadingRegister")
        {
            var loading = await db.LoadingRegisters.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Rule("بارگیری پیدا نشد.");
            if (await simplePurchase.HasPostingAsync(loading.ContractId, ct)
                && await db.Contracts.AnyAsync(x => x.Id == loading.ContractId && x.Status != ContractStatus.Cancelled, ct))
                throw Rule("این بارگیری متعلق به خرید ساده است؛ لغو را از دکمهٔ لغو قرارداد انجام دهید تا بدهی و موجودی باهم برگردند.");
            await CancelLoadingCoreAsync(loading, reason, actorId, ct);
        }
        else throw Rule("نوع رکورد معتبر نیست.");
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private async Task CancelLoadingCoreAsync(LoadingRegister loading, string reason, int? actorId, CancellationToken ct)
    {
        // Serialize with receipt creation, which locks this same loading row.
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            var locked = await db.LoadingRegisters
                .FromSqlInterpolated($@"SELECT * FROM ""LoadingRegisters"" WHERE ""Id"" = {loading.Id} FOR UPDATE")
                .AsNoTracking().SingleAsync(ct);
            db.Entry(loading).CurrentValues.SetValues(locked);
        }
        if (loading.IsCancelled) return;
        if (loading.IsArchived) throw Rule("بارگیری از فهرست حذف شده است.");
        if (await db.CustomsDeclarations.AnyAsync(x => x.LoadingRegisterId == loading.Id, ct)
            || await db.ShipmentLoadingAllocations.AnyAsync(x => x.LoadingRegisterId == loading.Id, ct)
            || await db.InventoryTransportLegAllocations.AnyAsync(x => x.SourceLoadingRegisterId == loading.Id
                && x.InventoryTransportLeg != null && x.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled, ct)
            || await db.QualityInspections.AnyAsync(x => x.LoadingRegisterId == loading.Id, ct)
            || await db.LossEvents.AnyAsync(x => x.LoadingRegisterId == loading.Id && !x.IsCancelled, ct)
            || await db.ExpenseTransactions.AnyAsync(x => x.LoadingRegisterId == loading.Id && !x.IsCancelled, ct)
            || await db.AssetRentTransactions.AnyAsync(x => x.LoadingRegisterId == loading.Id && !x.IsCancelled, ct))
            throw Rule("بارگیری عملیات گمرکی، کیفیت، کسری، مصرف یا کرایهٔ وابسته دارد؛ ابتدا آن عملیات را لغو کنید.");
        var ids = await db.LoadingReceipts.Where(x => x.LoadingRegisterId == loading.Id && !x.IsCancelled).Select(x => x.Id).ToListAsync(ct);
        if (ids.Count > 0)
        {
            var result = await receipts.CancelWithinCurrentTransactionAsync(ids, reason, actorId, ct);
            if (!result.Succeeded) throw Rule(string.Join(" ؛ ", result.Blockers.Select(x => x.Reason)));
        }
        await purchaseAccounting.TryPostPurchaseReversalAsync(loading, ct);
        var ledgers = await db.LedgerEntries.Where(x => x.SourceType == "Loading" && x.SourceId == loading.Id
            && (x.Reference == null || !x.Reference.EndsWith(LedgerReversalWriter.CancelReferenceSuffix))).ToListAsync(ct);
        foreach (var ledger in ledgers)
            await LedgerReversalWriter.ReverseAsync(db, ledger, clock.Today, reason, $"LOADING:{loading.Id}", ct);
        loading.IsCancelled = true;
        await audit.LogAsync(nameof(LoadingRegister), loading.Id, AuditAction.Reverse,
            diff: AuditDiffFormatter.ForUpdate(("IsCancelled", false, true), ("CancellationReason", null, reason)),
            actorUserId: actorId, ct: ct);
    }

    public async Task ArchiveAsync(string kind, int id, int? actorId, CancellationToken ct = default)
    {
        // No entity is removed: downstream FK references and financial history remain valid.
        if (kind == "Contract")
        {
            var item = await db.Contracts.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Rule("قرارداد پیدا نشد.");
            if (item.Status != ContractStatus.Cancelled) throw Rule("ابتدا قرارداد را لغو کنید.");
            if (await db.LoadingRegisters.AnyAsync(x => x.ContractId == id && !x.IsCancelled, ct))
                throw Rule("ابتدا بارگیری‌های فعال قرارداد را لغو کنید.");
            if (item.IsArchived) return;
            item.IsArchived = true;
        }
        else if (kind == "LoadingRegister")
        {
            var item = await db.LoadingRegisters.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Rule("بارگیری پیدا نشد.");
            if (!item.IsCancelled) throw Rule("ابتدا بارگیری را لغو کنید.");
            if (await db.LoadingReceipts.AnyAsync(x => x.LoadingRegisterId == id && !x.IsCancelled, ct))
                throw Rule("ابتدا رسیدهای فعال بارگیری را لغو کنید.");
            if (item.IsArchived) return;
            item.IsArchived = true;
        }
        else if (kind == "LoadingReceipt")
        {
            var item = await db.LoadingReceipts.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Rule("رسید پیدا نشد.");
            if (!item.IsCancelled) throw Rule("ابتدا رسید را لغو کنید.");
            if (item.IsArchived) return;
            item.IsArchived = true;
        }
        else if (kind == "InventoryTransportLeg")
        {
            var item = await db.InventoryTransportLegs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Rule("حمل پیدا نشد.");
            if (item.Status != InventoryTransportLegStatus.Cancelled) throw Rule("ابتدا حمل را لغو کنید.");
            if (item.IsArchived) return;
            item.IsArchived = true;
        }
        else throw Rule("نوع رکورد معتبر نیست.");
        await audit.LogAndSaveAsync(kind, id, AuditAction.Delete,
            diff: AuditDiffFormatter.ForUpdate(("IsArchived", false, true)), actorUserId: actorId, ct: ct);
    }

    private static BusinessRuleException Rule(string message) => new("LifecycleBlocked", message);
}
