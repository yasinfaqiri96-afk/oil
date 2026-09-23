using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services.ContractClosure;

/// <summary>
/// «این قرارداد بسته است». عمداً استثنای اختصاصی است تا <c>BusinessRuleExceptionFilter</c>
/// آن را به پیام فارسی روی همان صفحه ترجمه کند، نه خطای ۵۰۰.
/// </summary>
public sealed class ContractClosedException(string message, int contractId) : InvalidOperationException(message)
{
    public int ContractId { get; } = contractId;
}

public enum ContractClosureBlockerKind
{
    Status = 0,
    Transport = 1,
    Delivery = 2,
    Stock = 3,
    Expense = 4,
    Payment = 5,
    Balance = 6,
    StagedDelivery = 7
}

/// <summary>یک مورد باز که مانع بستن قرارداد است؛ متن آماده برای نمایش به کاربر.</summary>
public sealed record ContractClosureBlocker(ContractClosureBlockerKind Kind, string Message);

public sealed record ContractClosureCheck(
    int ContractId,
    string ContractLabel,
    ContractStatus Status,
    IReadOnlyList<ContractClosureBlocker> Blockers)
{
    public bool CanClose => Blockers.Count == 0;
}

public sealed record ContractClosureResult(bool Succeeded, bool NotFound, string? Error, ContractClosureCheck? Check)
{
    public static ContractClosureResult Missing() => new(false, true, null, null);
    public static ContractClosureResult Fail(string error, ContractClosureCheck? check = null) => new(false, false, error, check);
    public static ContractClosureResult Ok() => new(true, false, null, null);
}

public interface IContractClosureService
{
    /// <summary>چه چیزی هنوز باز است؟ null یعنی قرارداد وجود ندارد.</summary>
    Task<ContractClosureCheck?> EvaluateAsync(int contractId, CancellationToken ct = default);

    /// <summary>فقط وقتی هیچ مورد بازی نمانده باشد؛ همراه با ثبت Audit.</summary>
    Task<ContractClosureResult> CloseAsync(int contractId, string? reason, int? actorUserId, CancellationToken ct = default);

    /// <summary>بازگشایی قرارداد بسته؛ دلیل اجباری است و Audit ثبت می‌شود. دسترسی مدیر در کنترلر بررسی می‌شود.</summary>
    Task<ContractClosureResult> ReopenAsync(int contractId, string? reason, int? actorUserId, CancellationToken ct = default);
}

/// <summary>
/// بستن و بازگشایی قرارداد. هیچ ستون یا جدول تازه‌ای ندارد: وضعیت همان
/// <see cref="Contract.Status"/> است و تاریخچه در <see cref="AuditLog"/> می‌نشیند.
///
/// این سرویس هیچ سند مالی، موجودی یا Ledger نمی‌سازد و هیچ محاسبه‌ای را تغییر نمی‌دهد؛
/// فقط می‌خواند و وضعیت را عوض می‌کند. قفلِ ثبت‌های بعدی در <c>ApplicationDbContext</c>
/// (با <see cref="ContractClosureScope"/>) اعمال می‌شود تا هیچ مسیری دور نزند.
/// </summary>
public sealed class ContractClosureService(
    ApplicationDbContext db,
    IAuditService audit,
    IStockService stock,
    IPartyBalanceReadService? partyBalances = null) : IContractClosureService
{
    private readonly IPartyBalanceReadService _partyBalances = partyBalances ?? PartyBalanceReadService.CreateDefault(db);

    public const string CloseAuditAction = "Close";
    public const string ReopenAuditAction = "Reopen";

    // ریزترین مقدارِ معنادار؛ باقی‌ماندهٔ گردکردن مانع بستن نمی‌شود.
    private const decimal QuantityToleranceMt = 0.001m;
    private const decimal AmountToleranceUsd = 0.01m;
    private const int ReasonMaxLength = 400;

    public async Task<ContractClosureCheck?> EvaluateAsync(int contractId, CancellationToken ct = default)
    {
        var contract = await db.Contracts
            .AsNoTracking()
            .Where(c => c.Id == contractId)
            .Select(c => new { c.Id, c.ContractName, c.ContractNumber, c.Status, c.ContractType })
            .FirstOrDefaultAsync(ct);
        if (contract is null)
        {
            return null;
        }

        var blockers = new List<ContractClosureBlocker>();
        if (contract.Status == ContractStatus.Closed)
        {
            blockers.Add(new(ContractClosureBlockerKind.Status, "این قرارداد قبلاً بسته شده است."));
        }
        else if (contract.Status == ContractStatus.Cancelled)
        {
            blockers.Add(new(ContractClosureBlockerKind.Status, "قرارداد لغوشده قابل بستن نیست."));
        }

        // ترتیب همان مسیر فیزیکی کالاست. هر مقدار دقیقاً در یک مرحله شمرده می‌شود:
        // بارگیری ← (رسید بارگیری | حمل مستقیم از بارگیری) ← (موجودی | فروش مستقیم | موتر | حمل بعدی | کسری).
        await AddLoadingBlockersAsync(contractId, blockers, ct);
        await AddReceiptAllocationBlockersAsync(contractId, blockers, ct);
        await AddTransportLegBlockersAsync(contractId, blockers, ct);
        await AddDispatchBlockersAsync(contractId, blockers, ct);

        if (contract.ContractType == ContractType.Purchase)
        {
            var stockSummary = await stock.GetStockSummaryAsync(contractId: contractId, ct: ct);
            var freeStockMt = stockSummary.Sum(s => s.FreeQuantityMt);
            if (freeStockMt > QuantityToleranceMt)
            {
                blockers.Add(new(
                    ContractClosureBlockerKind.Stock,
                    $"{Mt(freeStockMt)} تن موجودیِ فروخته‌نشده از این قرارداد در انبار باقی مانده است."));
            }
        }

        await AddStagedDeliveryBlockersAsync(contractId, blockers, ct);

        var draftSarrafCount = await db.SarrafSettlements
            .AsNoTracking()
            .CountAsync(s => s.ContractId == contractId && s.Status == SarrafSettlementStatus.Draft, ct);
        if (draftSarrafCount > 0)
        {
            blockers.Add(new(
                ContractClosureBlockerKind.Payment,
                $"{draftSarrafCount:N0} تسویهٔ صرافی هنوز در حالت پیش‌نویس است و ثبت نهایی نشده."));
        }

        await AddBalanceBlockersAsync(contractId, blockers, ct);

        return new ContractClosureCheck(
            contract.Id,
            Contract.BuildDisplayLabel(contract.ContractName, contract.ContractNumber),
            contract.Status,
            blockers);
    }

    public async Task<ContractClosureResult> CloseAsync(int contractId, string? reason, int? actorUserId, CancellationToken ct = default)
    {
        var check = await EvaluateAsync(contractId, ct);
        if (check is null)
        {
            return ContractClosureResult.Missing();
        }

        if (!check.CanClose)
        {
            return ContractClosureResult.Fail("قرارداد هنوز موارد باز دارد و بسته نشد.", check);
        }

        var normalizedReason = NormalizeReason(reason);
        return await ChangeStatusAsync(
            contractId,
            ContractStatus.Closed,
            CloseAuditAction,
            label => string.IsNullOrEmpty(normalizedReason)
                ? $"قرارداد {label} بسته شد."
                : $"قرارداد {label} بسته شد. دلیل: {normalizedReason}",
            normalizedReason,
            actorUserId,
            ct);
    }

    public async Task<ContractClosureResult> ReopenAsync(int contractId, string? reason, int? actorUserId, CancellationToken ct = default)
    {
        var status = await db.Contracts
            .AsNoTracking()
            .Where(c => c.Id == contractId)
            .Select(c => (ContractStatus?)c.Status)
            .FirstOrDefaultAsync(ct);
        if (status is null)
        {
            return ContractClosureResult.Missing();
        }

        if (status != ContractStatus.Closed)
        {
            return ContractClosureResult.Fail("فقط قرارداد بسته را می‌توان بازگشایی کرد.");
        }

        var normalizedReason = NormalizeReason(reason);
        if (string.IsNullOrEmpty(normalizedReason))
        {
            return ContractClosureResult.Fail("برای بازگشایی قرارداد باید دلیل نوشته شود.");
        }

        return await ChangeStatusAsync(
            contractId,
            ContractStatus.Active,
            ReopenAuditAction,
            label => $"قرارداد {label} بازگشایی شد. دلیل: {normalizedReason}",
            normalizedReason,
            actorUserId,
            ct);
    }

    private async Task<ContractClosureResult> ChangeStatusAsync(
        int contractId,
        ContractStatus nextStatus,
        string auditAction,
        Func<string, string> describe,
        string? reason,
        int? actorUserId,
        CancellationToken ct)
    {
        var contract = await db.Contracts.FirstOrDefaultAsync(c => c.Id == contractId, ct);
        if (contract is null)
        {
            return ContractClosureResult.Missing();
        }

        var previousStatus = contract.Status;
        contract.Status = nextStatus;

        await audit.LogActivityAsync(
            new AuditLogEntryInput
            {
                EntityName = nameof(Contract),
                EntityId = contract.Id,
                Action = auditAction,
                ActorUserId = actorUserId,
                Module = nameof(Contract),
                Description = Truncate(describe(contract.DisplayLabel), 500),
                Diff = AuditDiffFormatter.ForUpdate(
                    ("Status", previousStatus, nextStatus),
                    ("Reason", null, reason)),
                IsSuccess = true
            },
            ct);

        // تنها مسیری که قفل تغییر وضعیت در SaveChanges را باز می‌کند؛ دامنه‌اش همین یک ذخیره است.
        db.ContractStatusTransitionApproved = true;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            db.ContractStatusTransitionApproved = false;
        }

        return ContractClosureResult.Ok();
    }

    /// <summary>
    /// بارگیری فقط وقتی باز است که مقدارِ حل‌نشدهٔ واقعی داشته باشد. فرمول همان «باقیماندهٔ بارگیری»
    /// صفحهٔ بارگیری، ثبت حمل از بارگیری و گزارش کالای در راه است:
    /// بارگیری − رسیدهای لغونشده − کسری رسید بارگیری − تخصیص به حملِ لغونشده (حمل مستقیم از بارگیری).
    /// مقداری که به حمل رفته از این‌جا بیرون است و فقط در کنترل حمل شمرده می‌شود (بدون شمارش دوباره).
    /// </summary>
    private async Task AddLoadingBlockersAsync(int contractId, List<ContractClosureBlocker> blockers, CancellationToken ct)
    {
        var loadings = await db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            .Select(l => new
            {
                l.Id,
                l.LoadedQuantityMt,
                ReceivedMt = l.Receipts.Where(r => !r.IsCancelled).Sum(r => (decimal?)r.ReceivedQuantityMt) ?? 0m
            })
            .ToListAsync(ct);
        if (loadings.Count == 0)
        {
            return;
        }

        var loadingIds = loadings.Select(l => l.Id).ToList();

        // حملِ لغوشده حساب نمی‌شود؛ مقدارش یک‌بار (و فقط یک‌بار) به باقیماندهٔ بارگیری برمی‌گردد.
        var transportedByLoading = await db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => a.SourceLoadingRegisterId.HasValue
                && loadingIds.Contains(a.SourceLoadingRegisterId.Value)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(a => a.SourceLoadingRegisterId!.Value)
            .Select(g => new { LoadingId = g.Key, Mt = g.Sum(a => a.QuantityMt) })
            .ToDictionaryAsync(x => x.LoadingId, x => x.Mt, ct);

        // کسریِ رسیدِ خودِ بارگیری. کسریِ رسیدِ حمل (TransportLegId دارد) قبلاً در باقیماندهٔ همان حمل
        // مصرف شده و این‌جا دوباره کم نمی‌شود.
        var shortageRows = await db.LossEvents
            .AsNoTracking()
            .Where(e => !e.IsCancelled
                && e.Stage == LossEventStage.ReceiptShortage
                && e.TransportLegId == null
                && ((e.LoadingRegisterId.HasValue && loadingIds.Contains(e.LoadingRegisterId.Value))
                    || (e.LoadingReceiptId.HasValue
                        && e.LoadingReceipt != null
                        && loadingIds.Contains(e.LoadingReceipt.LoadingRegisterId))))
            .Select(e => new
            {
                LoadingId = e.LoadingRegisterId ?? (e.LoadingReceipt != null ? e.LoadingReceipt.LoadingRegisterId : (int?)null),
                e.DifferenceQuantityMt,
                e.ChargeableLossMt
            })
            .ToListAsync(ct);
        var shortageByLoading = shortageRows
            .Where(x => x.LoadingId.HasValue)
            .GroupBy(x => x.LoadingId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.DifferenceQuantityMt > 0m ? x.DifferenceQuantityMt : Math.Max(x.ChargeableLossMt, 0m)));

        var openMt = loadings
            .Select(l => l.LoadedQuantityMt
                - l.ReceivedMt
                - shortageByLoading.GetValueOrDefault(l.Id)
                - transportedByLoading.GetValueOrDefault(l.Id))
            .Where(remaining => remaining > QuantityToleranceMt)
            .ToList();
        if (openMt.Count > 0)
        {
            blockers.Add(new(
                ContractClosureBlockerKind.Delivery,
                $"{openMt.Count:N0} بارگیری هنوز مقدار حل‌نشده دارد؛ نه رسید شده و نه به حمل تخصیص یافته ({Mt(openMt.Sum())} تن)."));
        }
    }

    /// <summary>
    /// سرنوشتِ مقدارِ رسیدِ بارگیری به تفکیک مقصد تخصیص. «در مسیر» روی این جدول دو معنا دارد:
    /// برای انتقال به ترمینال دیگر یعنی کالا واقعاً در مسیر است؛ برای ارسال مستقیم با موتر یعنی بخشی از
    /// تخصیص هنوز با موتر ارسال نشده (بخش ارسال‌شده در کنترل موتر شمرده می‌شود).
    /// </summary>
    private async Task AddReceiptAllocationBlockersAsync(int contractId, List<ContractClosureBlocker> blockers, CancellationToken ct)
    {
        var allocations = await db.LoadingReceiptAllocations
            .AsNoTracking()
            .Where(a => a.LoadingReceipt != null
                && !a.LoadingReceipt.IsCancelled
                && (a.SourcePurchaseContractId == contractId
                    || (a.SourcePurchaseContractId == null
                        && a.LoadingReceipt.LoadingRegister != null
                        && a.LoadingReceipt.LoadingRegister.ContractId == contractId))
                && ((a.Destination == LoadingReceiptAllocationDestination.TransferToOtherTerminal
                        && a.Status == LoadingReceiptAllocationStatus.InTransit)
                    || (a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                        && (a.Status == LoadingReceiptAllocationStatus.TraceOnly
                            || a.Status == LoadingReceiptAllocationStatus.InTransit))))
            .Select(a => new
            {
                a.Destination,
                a.QuantityMt,
                DispatchedMt = db.TruckDispatches
                    .Where(d => d.LoadingReceiptAllocationId == a.Id && d.Status != DispatchStatus.Cancelled)
                    .Sum(d => (decimal?)d.LoadedQuantityMt) ?? 0m
            })
            .ToListAsync(ct);

        var inTransferMt = allocations
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.TransferToOtherTerminal && a.QuantityMt > QuantityToleranceMt)
            .Select(a => a.QuantityMt)
            .ToList();
        if (inTransferMt.Count > 0)
        {
            blockers.Add(new(
                ContractClosureBlockerKind.Transport,
                $"{inTransferMt.Count:N0} تخصیص رسید (انتقال به ترمینال دیگر) هنوز در مسیر است ({Mt(inTransferMt.Sum())} تن)."));
        }

        var undispatchedMt = allocations
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck)
            .Select(a => a.QuantityMt - a.DispatchedMt)
            .Where(remaining => remaining > QuantityToleranceMt)
            .ToList();
        if (undispatchedMt.Count > 0)
        {
            blockers.Add(new(
                ContractClosureBlockerKind.Delivery,
                $"{undispatchedMt.Count:N0} تخصیص رسید برای ارسال مستقیم با موتر هنوز کامل ارسال نشده است ({Mt(undispatchedMt.Sum())} تن)."));
        }
    }

    /// <summary>
    /// حملِ باز فقط به‌اندازهٔ باقیماندهٔ رسیدنشده‌اش (همان <see cref="TransportQuantityService"/>: هر رسیدِ
    /// لغونشده دریافت + کسری مصرف می‌کند؛ مقصد رسید — موجودی، فروش مستقیم یا حمل بعدی — آن مقدار را حل
    /// کرده است). در حملِ چندقراردادی فقط سهمِ این قرارداد (به نسبت تخصیص‌ها) شمرده می‌شود.
    /// فروش مستقیم از محموله (بدون رسید) هم آن مقدار را حل می‌کند؛ ببینید <see cref="GetStandaloneShipmentSalesMtAsync"/>.
    /// </summary>
    private async Task AddTransportLegBlockersAsync(int contractId, List<ContractClosureBlocker> blockers, CancellationToken ct)
    {
        var legs = await db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => (l.SourcePurchaseContractId == contractId
                    || l.Allocations.Any(a => a.SourcePurchaseContractId == contractId))
                && (l.Status == InventoryTransportLegStatus.Draft
                    || l.Status == InventoryTransportLegStatus.Loaded
                    || l.Status == InventoryTransportLegStatus.InTransit))
            .Select(l => new
            {
                l.Id,
                l.ShipmentId,
                l.SourcePurchaseContractId,
                ContractAllocatedMt = l.Allocations.Where(a => a.SourcePurchaseContractId == contractId).Sum(a => (decimal?)a.QuantityMt) ?? 0m,
                TotalAllocatedMt = l.Allocations.Sum(a => (decimal?)a.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);
        if (legs.Count == 0)
        {
            return;
        }

        var remainingByLeg = await new TransportQuantityService(db).GetRemainingMtAsync(legs.Select(l => l.Id).ToList(), ct);
        var legOpenMt = legs
            .OrderBy(l => l.Id)
            .Select(l =>
            {
                var remaining = Math.Max(remainingByLeg.GetValueOrDefault(l.Id), 0m);
                var share = l.TotalAllocatedMt > 0m
                    ? l.ContractAllocatedMt / l.TotalAllocatedMt
                    : l.SourcePurchaseContractId == contractId ? 1m : 0m;
                return (l.ShipmentId, OpenMt: remaining * share);
            })
            .ToList();

        // فروش مستقیم از محموله فقط سهمِ همین قرارداد در حمل‌های بازِ همان محموله را کم می‌کند؛ هرگز بیشتر.
        // ترتیب مصرف (قدیمی‌ترین حمل اول) فقط برای شمارش پیام است و جمع حل‌شده را تغییر نمی‌دهد.
        var shipmentIds = legs.Where(l => l.ShipmentId.HasValue).Select(l => l.ShipmentId!.Value).Distinct().ToList();
        if (shipmentIds.Count > 0)
        {
            var unconsumedSaleMt = await GetStandaloneShipmentSalesMtAsync(contractId, shipmentIds, ct);
            for (var i = 0; i < legOpenMt.Count; i++)
            {
                var (shipmentId, open) = legOpenMt[i];
                if (shipmentId is not int id || !unconsumedSaleMt.TryGetValue(id, out var saleMt) || saleMt <= 0m)
                {
                    continue;
                }

                var consumed = Math.Min(open, saleMt);
                unconsumedSaleMt[id] = saleMt - consumed;
                legOpenMt[i] = (shipmentId, open - consumed);
            }
        }

        var openMt = legOpenMt
            .Select(l => l.OpenMt)
            .Where(remaining => remaining > QuantityToleranceMt)
            .ToList();
        if (openMt.Count > 0)
        {
            blockers.Add(new(
                ContractClosureBlockerKind.Transport,
                $"{openMt.Count:N0} حمل موجودی هنوز در مسیر است و مقدار رسیدنشده دارد ({Mt(openMt.Sum())} تن)."));
        }
    }

    /// <summary>
    /// فروش مستقیم از محموله (کشتی) بدون رسید: فروشی که صراحتاً همان محموله (<c>ShipmentId</c>) و همین
    /// قرارداد را منبع (<c>SourcePurchaseContractId</c>) نام برده است. فروشِ بدون قرارداد منبع، لغوشده، پیش‌فروش
    /// یا از موجودی مخزن حساب نمی‌شود. فروشی که از مسیر دیگری ثبت شده (رسید حمل، موتر، تخصیص رسید بارگیری،
    /// تخصیص منبع حمل) کنار گذاشته می‌شود، چون مقدارش همان‌جا مصرف شده و نباید دو بار حل شود.
    /// </summary>
    private async Task<Dictionary<int, decimal>> GetStandaloneShipmentSalesMtAsync(
        int contractId,
        IReadOnlyCollection<int> shipmentIds,
        CancellationToken ct)
        => await db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && s.ShipmentId.HasValue
                && shipmentIds.Contains(s.ShipmentId.Value)
                && s.SourcePurchaseContractId == contractId
                && s.SaleStage != SaleStage.PreSale
                && s.SaleStage != SaleStage.TerminalStock
                && s.TruckDispatchId == null
                && !db.InventoryTransportReceipts.Any(r => r.SalesTransactionId == s.Id)
                && !db.TruckDispatches.Any(d => d.SalesTransactionId == s.Id)
                && !db.LoadingReceiptAllocations.Any(a => a.SalesTransactionId == s.Id)
                && !db.SalesTransactionSourceAllocations.Any(a => a.SalesTransactionId == s.Id))
            .GroupBy(s => s.ShipmentId!.Value)
            .Select(g => new { ShipmentId = g.Key, Mt = g.Sum(s => s.QuantityMt) })
            .ToDictionaryAsync(x => x.ShipmentId, x => x.Mt, ct);

    /// <summary>
    /// موترِ ارسال‌شده وقتی حل‌شده است که تحویل (DeliveryReceipt/Delivered)، لغو یا فروخته شده باشد.
    /// فروش جزئی فقط به‌اندازهٔ خودش کم می‌کند. دیسپچِ سازگاریِ «انتقال وسیله→وسیله» شمرده نمی‌شود،
    /// چون همان بار به‌صورت حملِ فرزند در کنترل حمل حساب شده است.
    /// </summary>
    private async Task AddDispatchBlockersAsync(int contractId, List<ContractClosureBlocker> blockers, CancellationToken ct)
    {
        var dispatches = await db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.ContractId == contractId
                && (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                && !db.DeliveryReceipts.Any(r => r.TruckDispatchId == d.Id)
                && !(d.InventoryTransportReceiptId != null
                    && db.InventoryTransportLegAllocations.Any(a => a.SourceTransportReceiptId == d.InventoryTransportReceiptId)))
            .Select(d => new
            {
                d.LoadedQuantityMt,
                SoldMt = db.SalesTransactions
                    .Where(s => !s.IsCancelled && (s.TruckDispatchId == d.Id || s.Id == d.SalesTransactionId))
                    .Sum(s => (decimal?)s.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);

        var openMt = dispatches
            .Select(d => d.LoadedQuantityMt - d.SoldMt)
            .Where(remaining => remaining > QuantityToleranceMt)
            .ToList();
        if (openMt.Count > 0)
        {
            blockers.Add(new(
                ContractClosureBlockerKind.Transport,
                $"{openMt.Count:N0} ارسال موتر هنوز تحویل یا فروخته نشده است ({Mt(openMt.Sum())} تن)."));
        }
    }

    /// <summary>
    /// فروش مرحله‌ای (پیش‌فروش): سفارشی که تحویلی از این قرارداد گرفته و هنوز باز است
    /// (پیش‌نویس، تأییدشده یا تحویل جزئی) مانع بستن است. تحویل کامل، بسته یا لغوشده حل‌شده است.
    /// </summary>
    private async Task AddStagedDeliveryBlockersAsync(int contractId, List<ContractClosureBlocker> blockers, CancellationToken ct)
    {
        var openOrders = await db.PreSaleOrders
            .AsNoTracking()
            .Where(o => (o.Status == PreSaleOrderStatus.Draft
                    || o.Status == PreSaleOrderStatus.Confirmed
                    || o.Status == PreSaleOrderStatus.PartiallyDelivered)
                && o.Deliveries.Any(s => !s.IsCancelled
                    && (s.ContractId == contractId || s.SourcePurchaseContractId == contractId)))
            .Select(o => new
            {
                o.QuantityMt,
                DeliveredMt = o.Deliveries.Where(s => !s.IsCancelled).Sum(s => (decimal?)s.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);
        if (openOrders.Count == 0)
        {
            return;
        }

        var remainingMt = openOrders.Sum(o => Math.Max(o.QuantityMt - o.DeliveredMt, 0m));
        blockers.Add(new(
            ContractClosureBlockerKind.StagedDelivery,
            $"{openOrders.Count:N0} فروش مرحله‌ای (پیش‌فروش) هنوز کامل تحویل یا بسته نشده است ({Mt(remainingMt)} تن باقی‌مانده)."));
    }

    private async Task AddBalanceBlockersAsync(int contractId, List<ContractClosureBlocker> blockers, CancellationToken ct)
    {
        // مانده به تفکیک طرف‌حساب، نه جمع کل: بدهیِ تأمین‌کننده نباید با طلب از شرکت خدماتی خنثی شود.
        // مانده از همان موتورِ رسمیِ صورت‌حساب خوانده می‌شود (جهت از نوعِ سند، انتساب از راهِ
        // قرارداد و فروش، فیلترِ تسویهٔ صراف)، پس قراردادی که صورت‌حساب رسمی برایش ماندهٔ باز
        // دارد هرگز «تسویه» دیده نمی‌شود و برعکس. علامت: مثبت = طلب شرکت، منفی = بدهی شرکت.
        var balances = await _partyBalances.GetContractBalancesAsync([contractId], ct: ct);
        if (!balances.TryGetValue(contractId, out var contract))
        {
            return;
        }

        foreach (var party in contract.Parties.Where(p => Math.Abs(p.ClosingBalanceUsd) > AmountToleranceUsd))
        {
            var name = string.IsNullOrWhiteSpace(party.PartyName) || party.PartyName == "-"
                ? $"#{party.PartyId}"
                : party.PartyName;
            var (kind, partyLabel) = party.PartyType switch
            {
                PartyStatementPartyType.Supplier => (ContractClosureBlockerKind.Balance, $"تأمین‌کننده «{name}»"),
                PartyStatementPartyType.Customer => (ContractClosureBlockerKind.Balance, $"مشتری «{name}»"),
                PartyStatementPartyType.Sarraf => (ContractClosureBlockerKind.Balance, $"صراف «{name}»"),
                PartyStatementPartyType.Partner => (ContractClosureBlockerKind.Balance, $"شریک «{name}»"),
                PartyStatementPartyType.ServiceProvider => (ContractClosureBlockerKind.Expense, $"شرکت خدماتی «{name}»"),
                PartyStatementPartyType.Driver => (ContractClosureBlockerKind.Expense, $"راننده «{name}»"),
                PartyStatementPartyType.Employee => (ContractClosureBlockerKind.Expense, $"کارمند «{name}»"),
                _ => (ContractClosureBlockerKind.Balance, $"«{name}»")
            };
            var direction = party.ClosingBalanceUsd > 0m ? "قابل دریافت از طرف‌حساب" : "قابل پرداخت به طرف‌حساب";
            blockers.Add(new(
                kind,
                $"ماندهٔ باز با {partyLabel}: {Math.Abs(party.ClosingBalanceUsd).ToString("#,0.00", CultureInfo.InvariantCulture)} USD ({direction})."));
        }
    }

    private static string Name(IReadOnlyDictionary<int, string> names, int id)
        => names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"#{id}";

    private static string Mt(decimal value) => value.ToString("#,0.###", CultureInfo.InvariantCulture);

    private static string? NormalizeReason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Truncate(trimmed, ReasonMaxLength);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// دامنهٔ قفلِ قرارداد بسته: کدام موجودیت‌ها «عملیات قرارداد» هستند و قرارداد را با کدام ستون
/// نشان می‌دهند. <c>ApplicationDbContext</c> موقع ذخیره فقط از همین فهرست می‌پرسد.
/// </summary>
public static class ContractClosureScope
{
    /// <summary>ستون‌هایی که به قرارداد اشاره می‌کنند (فقط آن‌هایی که روی موجودیت وجود دارند خوانده می‌شوند).</summary>
    public static readonly string[] ContractKeyProperties =
    [
        "ContractId",
        "SourcePurchaseContractId",
        "FromContractId",
        "ToContractId",
        "CustomerSaleContractId",
        "SupplierPurchaseContractId",
        "ChargedToContractId"
    ];

    private static readonly HashSet<Type> ScopedTypes =
    [
        typeof(LoadingRegister),
        typeof(LoadingReceiptAllocation),
        typeof(InventoryTransportLeg),
        typeof(InventoryTransportLegAllocation),
        typeof(TruckDispatch),
        typeof(Shipment),
        typeof(ShipmentContract),
        typeof(ShipmentLoadingAllocation),
        typeof(LossEvent),
        typeof(LossEventSourceAllocation),
        typeof(SalesTransaction),
        typeof(SalesTransactionSourceAllocation),
        typeof(ExpenseTransaction),
        typeof(PaymentTransaction),
        typeof(SarrafSettlement),
        typeof(LedgerEntry),
        typeof(PartnerSettlement),
        typeof(ContractBalanceTransfer),
        typeof(SupplierPaymentAllocation),
        typeof(SupplierBalanceTransfer),
        typeof(ThreeWaySettlement),
        typeof(InventoryMovement),
        typeof(InventoryBatch),
        typeof(AssetCharge),
        typeof(AssetRentTransaction),
        typeof(QualityInspection),
        typeof(ContractAmendment),
        typeof(ContractPricingRule),
        typeof(ContractPartner)
    ];

    /// <summary>
    /// ستون‌های فنی که با backfill جستجو، شمارندهٔ هم‌زمانی یا مهر زمانی عوض می‌شوند.
    /// تغییرِ فقط همین‌ها «ویرایش عملیات» حساب نمی‌شود و قفل را فعال نمی‌کند.
    /// </summary>
    private static readonly HashSet<string> TechnicalProperties =
    [
        "SearchKey",
        "Version",
        nameof(BaseEntity.CreatedAtUtc),
        nameof(BaseEntity.UpdatedAtUtc),
        nameof(BaseEntity.CreatedByUserId),
        nameof(BaseEntity.UpdatedByUserId)
    ];

    public static bool Covers(object entity) => ScopedTypes.Contains(entity.GetType());

    public static bool HasBusinessChange(EntityEntry entry)
        => entry.Properties.Any(p => p.IsModified && !TechnicalProperties.Contains(p.Metadata.Name));

    public static string BuildLockedMessage(string contractLabel)
        => $"قرارداد «{contractLabel}» بسته شده است؛ ثبت، ویرایش یا حذف عملیات روی آن مجاز نیست. "
           + "برای ادامه، مدیر سیستم باید قرارداد را بازگشایی کند.";

    public const string DirectStatusChangeMessage =
        "بستن یا بازگشایی قرارداد فقط از گزینه‌های «بستن قرارداد» و «بازگشایی قرارداد» در صفحهٔ جزئیات ممکن است.";
}
