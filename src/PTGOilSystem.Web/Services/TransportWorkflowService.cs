using System.Data;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// درگاه واحد نوشتن Workflow حمل. این Facade منطق موتورهای تخصصی را دوباره پیاده نمی‌کند؛
/// فقط فرمان کاربر را به سرویس موجودی، زنجیره، رسید/فروش و کسری هدایت می‌کند.
/// </summary>
public interface ITransportWorkflowService
{
    Task<InventoryTransportBatch> StartFromInventoryAsync(
        InventoryTransportFromInventoryViewModel model,
        string? formToken,
        CancellationToken ct = default);

    Task<InventoryTransportLeg> StartFromReceiptAsync(
        StartTransportFromReceiptCommand command,
        CancellationToken ct = default);

    Task<ContinueToVehicleResult> ContinueToVehicleAsync(
        ContinueToVehicleCommand command,
        CancellationToken ct = default);

    Task<InventoryTransportReceipt> ReceiveToInventoryAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CancellationToken ct = default);

    Task<InventoryTransportReceipt> SellQuantityAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult conversion,
        CancellationToken ct = default);

    Task<LossEventWorkflowResult> RecordLossAsync(
        LossEventSubmission submission,
        CancellationToken ct = default);

    Task<InventoryTransportReceipt> SettleFreightAsync(
        SettleTransportFreightCommand command,
        CancellationToken ct = default);

    Task<IReadOnlyList<InventoryTransportLeg>> CancelOrReverseAsync(
        IReadOnlyCollection<int> sourceReceiptIds,
        CancellationToken ct = default);
}

public sealed record StartTransportFromReceiptCommand
{
    public required int LoadingReceiptId { get; init; }
    public required decimal QuantityMt { get; init; }
    public required LoadingTransportType TransportType { get; init; }
    public int? TruckId { get; init; }
    public int? WagonId { get; init; }
    public int? VesselId { get; init; }
    public int? DriverId { get; init; }
    public int? ServiceProviderId { get; init; }
    public required DateTime TransportDate { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
}

public sealed record SettleTransportFreightCommand
{
    public required int TransportLegId { get; init; }
    public required DateTime SettlementDate { get; init; }
    public decimal? FreightRateUsdPerMt { get; init; }
    public decimal? FreightCostUsd { get; init; }
    public string? Notes { get; init; }
}

public sealed class TransportWorkflowService : ITransportWorkflowService
{
    private const decimal Epsilon = 0.0001m;

    private readonly ApplicationDbContext _db;
    private readonly InventoryTransportBatchService _inventoryStarts;
    private readonly ITransportChainService _chain;
    private readonly InventoryTransportReceiptService _outcomes;
    private readonly ILossEventWorkflowService _losses;

    public TransportWorkflowService(
        ApplicationDbContext db,
        InventoryTransportBatchService inventoryStarts,
        ITransportChainService chain,
        InventoryTransportReceiptService outcomes,
        ILossEventWorkflowService losses)
    {
        _db = db;
        _inventoryStarts = inventoryStarts;
        _chain = chain;
        _outcomes = outcomes;
        _losses = losses;
    }

    public Task<InventoryTransportBatch> StartFromInventoryAsync(
        InventoryTransportFromInventoryViewModel model,
        string? formToken,
        CancellationToken ct = default)
        => _inventoryStarts.CreateAsync(model, formToken, ct);

    public async Task<InventoryTransportLeg> StartFromReceiptAsync(
        StartTransportFromReceiptCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.QuantityMt <= 0m)
        {
            throw Rule("TRANSPORT_RECEIPT_QTY_INVALID", "مقدار حمل باید بزرگ‌تر از صفر باشد.");
        }
        if (command.TransportDate == default)
        {
            throw Rule("TRANSPORT_RECEIPT_DATE_REQUIRED", "تاریخ حمل الزامی است.");
        }

        await ValidateVehicleAsync(command, ct);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            var receipt = await _db.LoadingReceipts
                .Include(r => r.LoadingRegister)
                .Include(r => r.Allocations)
                .FirstOrDefaultAsync(r => r.Id == command.LoadingReceiptId, ct)
                ?? throw Rule("TRANSPORT_RECEIPT_NOT_FOUND", "رسید/بارگیری انتخاب‌شده پیدا نشد.");

            if (receipt.IsCancelled
                || receipt.LoadingRegister is null
                || receipt.ReceiptDestination is not (LoadingReceiptDestination.DirectDispatch or LoadingReceiptDestination.Mixed))
            {
                throw Rule("TRANSPORT_RECEIPT_NOT_AVAILABLE", "این رسید منبع مستقیمِ قابل حمل نیست.");
            }

            var sourceRows = receipt.Allocations
                .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectDispatchToTruck
                    && a.Status != LoadingReceiptAllocationStatus.Cancelled
                    && a.SourcePurchaseContractId.HasValue
                    && a.QuantityMt > 0m)
                .OrderBy(a => a.Id)
                .ToList();
            if (sourceRows.Count == 0)
            {
                throw Rule("TRANSPORT_RECEIPT_SOURCE_MISSING", "برای این رسید سهم منبعِ قابل حمل ثبت نشده است.");
            }

            var used = await _db.InventoryTransportLegAllocations
                .AsNoTracking()
                .Where(a => a.SourceLoadingReceiptId == receipt.Id
                    && a.InventoryTransportLeg != null
                    && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                .GroupBy(a => a.SourcePurchaseContractId)
                .Select(g => new { ContractId = g.Key, QuantityMt = g.Sum(a => a.QuantityMt) })
                .ToDictionaryAsync(x => x.ContractId, x => x.QuantityMt, ct);

            var available = sourceRows
                .GroupBy(a => a.SourcePurchaseContractId!.Value)
                .Select(g => new ReceiptSource(g.Key, Math.Max(g.Sum(a => a.QuantityMt) - used.GetValueOrDefault(g.Key), 0m)))
                .Where(x => x.QuantityMt > Epsilon)
                .OrderBy(x => x.ContractId)
                .ToList();
            var availableTotal = available.Sum(x => x.QuantityMt);
            if (command.QuantityMt > availableTotal + Epsilon)
            {
                throw Rule(
                    "TRANSPORT_RECEIPT_INSUFFICIENT",
                    $"مقدار درخواستی از باقیماندهٔ مستقیم رسید ({availableTotal:N4} MT) بیشتر است.");
            }

            var groupKey = $"ITG:{Guid.NewGuid():N}";
            var batch = new InventoryTransportBatch
            {
                BatchNumber = $"ITB-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".ToUpperInvariant(),
                SourceTerminalId = receipt.TerminalId,
                SourceStorageTankId = null,
                ProductId = receipt.LoadingRegister.ProductId,
                TotalQuantityMt = command.QuantityMt,
                TransportDate = command.TransportDate.Date,
                Status = InventoryTransportBatchStatus.Loaded,
                TransportGroupKey = groupKey,
                Notes = Normalize(command.Notes)
            };

            var leg = new InventoryTransportLeg
            {
                InventoryTransportBatch = batch,
                TransportGroupKey = groupKey,
                SourcePurchaseContractId = available[0].ContractId,
                ProductId = receipt.LoadingRegister.ProductId,
                SourceTerminalId = receipt.TerminalId,
                SourceStorageTankId = null,
                TransportType = command.TransportType,
                TruckId = command.TransportType == LoadingTransportType.Truck ? command.TruckId : null,
                WagonId = command.TransportType == LoadingTransportType.Wagon ? command.WagonId : null,
                VesselId = command.TransportType == LoadingTransportType.Vessel ? command.VesselId : null,
                DriverId = command.TransportType == LoadingTransportType.Truck ? command.DriverId : null,
                ServiceProviderId = command.ServiceProviderId,
                CarrierType = CarrierType.ServiceProvider,
                LoadedDate = command.TransportDate.Date,
                QuantityMt = command.QuantityMt,
                Status = InventoryTransportLegStatus.Loaded,
                RwbNo = Normalize(command.Reference),
                Notes = Normalize(command.Notes)
            };
            var carrierParty = await new AssetUsageChargeService(_db).ResolveCarrierPartyAsync(
                leg.ServiceProviderId,
                leg.DriverId,
                leg.OperationalAssetId,
                leg.LoadedDate,
                ct);
            leg.CarrierPartyType = carrierParty?.PartyType;
            leg.CarrierPartyId = carrierParty?.PartyId;

            var remaining = command.QuantityMt;
            foreach (var source in available)
            {
                var quantity = Math.Min(source.QuantityMt, remaining);
                if (quantity <= Epsilon)
                {
                    continue;
                }
                leg.Allocations.Add(new InventoryTransportLegAllocation
                {
                    SourcePurchaseContractId = source.ContractId,
                    SourceLoadingReceiptId = receipt.Id,
                    SourceInventoryMovementId = null,
                    QuantityMt = decimal.Round(quantity, 4, MidpointRounding.AwayFromZero)
                });
                remaining -= quantity;
            }

            batch.Legs.Add(leg);
            _db.InventoryTransportBatches.Add(batch);
            await _db.SaveChangesAsync(ct);
            await new AssetUsageChargeService(_db).SyncOperationAsync(leg, ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return leg;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
    }

    public Task<ContinueToVehicleResult> ContinueToVehicleAsync(
        ContinueToVehicleCommand command,
        CancellationToken ct = default)
        => _chain.ContinueToVehicleAsync(command, ct);

    public Task<InventoryTransportReceipt> ReceiveToInventoryAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CancellationToken ct = default)
    {
        if (model.ReceiptDestination != InventoryTransportReceiptDestination.ToInventory)
        {
            throw Rule("TRANSPORT_OUTCOME_NOT_INVENTORY", "مقصد این فرمان باید مخزن باشد.");
        }
        return _outcomes.ApplyAsync(model, leg, saleConversion: null);
    }

    public Task<InventoryTransportReceipt> SellQuantityAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult conversion,
        CancellationToken ct = default)
    {
        if (model.ReceiptDestination != InventoryTransportReceiptDestination.DirectSale)
        {
            throw Rule("TRANSPORT_OUTCOME_NOT_SALE", "مقصد این فرمان باید فروش در مسیر باشد.");
        }
        return _outcomes.ApplyAsync(model, leg, conversion);
    }

    public Task<LossEventWorkflowResult> RecordLossAsync(
        LossEventSubmission submission,
        CancellationToken ct = default)
        => _losses.CreateAsync(submission, ct);

    public async Task<InventoryTransportReceipt> SettleFreightAsync(
        SettleTransportFreightCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SettlementDate == default)
        {
            throw Rule("TRANSPORT_FREIGHT_DATE_REQUIRED", "تاریخ تسویهٔ کرایه الزامی است.");
        }
        if (command.FreightRateUsdPerMt is < 0m || command.FreightCostUsd is < 0m)
        {
            throw Rule("TRANSPORT_FREIGHT_NEGATIVE", "نرخ یا مبلغ کرایه نمی‌تواند منفی باشد.");
        }
        if (!command.FreightRateUsdPerMt.HasValue && !command.FreightCostUsd.HasValue)
        {
            throw Rule("TRANSPORT_FREIGHT_AMOUNT_REQUIRED", "نرخ فی‌تن یا مبلغ کل کرایه را وارد کنید.");
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        try
        {
            var leg = await _outcomes.LoadLegAsync(command.TransportLegId, tracking: true)
                ?? throw Rule("TRANSPORT_FREIGHT_LEG_NOT_FOUND", "حمل موردنظر یافت نشد.");
            if (leg.Status is InventoryTransportLegStatus.Draft or InventoryTransportLegStatus.Cancelled)
            {
                throw Rule("TRANSPORT_FREIGHT_LEG_INVALID", "این حمل در وضعیت قابل تسویه نیست.");
            }
            if (leg.IsFreightSettled)
            {
                throw Rule("TRANSPORT_FREIGHT_ALREADY_SETTLED", "کرایهٔ این حمل قبلاً تسویه شده است.");
            }

            var model = new InventoryTransportReceiptCreateViewModel
            {
                InventoryTransportLegId = leg.Id,
                ReceiptDate = command.SettlementDate.Date,
                SettlementOnly = true,
                ShortageQuantityMt = 0m,
                AllowanceMt = 0m,
                FreightRateUsdPerMt = command.FreightRateUsdPerMt,
                FreightCostUsd = command.FreightCostUsd,
                ServiceProviderId = leg.ServiceProviderId,
                OperationalAssetId = leg.OperationalAssetId,
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                Notes = Normalize(command.Notes)
            };
            var modelState = new ModelStateDictionary();
            await _outcomes.ValidateAsync(model, leg, modelState, string.Empty);
            if (!modelState.IsValid)
            {
                var message = modelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
                    ?? "اطلاعات تسویهٔ کرایه معتبر نیست.";
                throw Rule("TRANSPORT_FREIGHT_INVALID", message);
            }

            var receipt = await _outcomes.ApplyAsync(model, leg, saleConversion: null);
            if (leg.OperationalAssetId.HasValue)
            {
                await _outcomes.RecordOperationalAssetFreightIncomeAsync(
                    leg.OperationalAssetId.Value,
                    receipt.FreightPayableUsd ?? receipt.FreightCostUsd ?? 0m,
                    command.SettlementDate,
                    leg.SourcePurchaseContractId,
                    leg.ShipmentId,
                    leg.Id,
                    truckDispatchId: null,
                    reference: $"TRANSPORT-RECEIPT:{receipt.Id}",
                    ct: ct);
            }
            leg.IsFreightSettled = true;
            leg.FreightSettledDate = command.SettlementDate.Date;
            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return receipt;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
    }

    public Task<IReadOnlyList<InventoryTransportLeg>> CancelOrReverseAsync(
        IReadOnlyCollection<int> sourceReceiptIds,
        CancellationToken ct = default)
        => _chain.CancelVehicleTransferAsync(sourceReceiptIds, ct);

    private async Task ValidateVehicleAsync(StartTransportFromReceiptCommand command, CancellationToken ct)
    {
        switch (command.TransportType)
        {
            case LoadingTransportType.Truck when command.TruckId.HasValue
                && await _db.Trucks.AsNoTracking().AnyAsync(t => t.Id == command.TruckId && t.IsActive, ct):
                break;
            case LoadingTransportType.Wagon when command.WagonId.HasValue
                && await _db.Wagons.AsNoTracking().AnyAsync(w => w.Id == command.WagonId && w.IsActive, ct):
                break;
            case LoadingTransportType.Vessel when command.VesselId.HasValue
                && await _db.Vessels.AsNoTracking().AnyAsync(v => v.Id == command.VesselId && v.IsActive, ct):
                break;
            default:
                throw Rule("TRANSPORT_RECEIPT_VEHICLE_INVALID", "وسیلهٔ مقصد معتبر و فعال نیست.");
        }

        if (command.DriverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == command.DriverId && d.IsActive, ct))
        {
            throw Rule("TRANSPORT_RECEIPT_DRIVER_INVALID", "راننده انتخاب‌شده معتبر و فعال نیست.");
        }
        if (command.ServiceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == command.ServiceProviderId && p.IsActive, ct))
        {
            throw Rule("TRANSPORT_RECEIPT_PROVIDER_INVALID", "شرکت خدماتی انتخاب‌شده معتبر و فعال نیست.");
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static BusinessRuleException Rule(string code, string message) => new(code, message);

    private sealed record ReceiptSource(int ContractId, decimal QuantityMt);
}
