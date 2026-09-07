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

    Task<InventoryTransportLeg> StartFromLoadingAsync(
        StartTransportFromLoadingCommand command,
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

public sealed record StartTransportFromLoadingCommand
{
    public required int LoadingRegisterId { get; init; }
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

    /// <summary>
    /// رانندهٔ طرفِ کرایه، وقتی حمل شرکت خدماتی/دارایی ملکی ندارد. روی خودِ حمل ثبت
    /// می‌شود تا سرویس رسید همان قاعدهٔ همیشگیِ «شرکت خدماتی، وگرنه راننده» را ببیند و
    /// بدهیِ کرایه روی حساب همان راننده بنشیند.
    /// </summary>
    public int? DriverId { get; init; }

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

    public async Task<InventoryTransportLeg> StartFromLoadingAsync(
        StartTransportFromLoadingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.QuantityMt <= 0m)
        {
            throw Rule("TRANSPORT_LOADING_QTY_INVALID", "مقدار حمل باید بزرگ‌تر از صفر باشد.");
        }
        if (command.TransportDate == default)
        {
            throw Rule("TRANSPORT_LOADING_DATE_REQUIRED", "تاریخ حمل الزامی است.");
        }

        await ValidateVehicleAsync(
            command.TransportType,
            command.TruckId,
            command.WagonId,
            command.VesselId,
            command.DriverId,
            command.ServiceProviderId,
            ct);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        try
        {
            LoadingRegister? loading;
            if (_db.Database.IsRelational()
                && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                loading = await _db.LoadingRegisters
                    .FromSqlInterpolated($@"SELECT * FROM ""LoadingRegisters"" WHERE ""Id"" = {command.LoadingRegisterId} FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                loading = await _db.LoadingRegisters.SingleOrDefaultAsync(l => l.Id == command.LoadingRegisterId, ct);
            }

            if (loading is null)
            {
                throw Rule("TRANSPORT_LOADING_NOT_FOUND", "بارگیری انتخاب‌شده پیدا نشد.");
            }

            var receivedMt = await _db.LoadingReceipts
                .AsNoTracking()
                .Where(r => r.LoadingRegisterId == loading.Id && !r.IsCancelled)
                .SumAsync(r => (decimal?)r.ReceivedQuantityMt, ct) ?? 0m;
            var shortageMt = await _db.LossEvents
                .AsNoTracking()
                .Where(e => (e.LoadingRegisterId == loading.Id
                        || e.LoadingReceiptId.HasValue
                            && e.LoadingReceipt != null
                            && e.LoadingReceipt.LoadingRegisterId == loading.Id)
                    && e.Stage == LossEventStage.ReceiptShortage
                    && !e.IsCancelled)
                .SumAsync(e => (decimal?)(e.DifferenceQuantityMt > 0m
                    ? e.DifferenceQuantityMt
                    : e.ChargeableLossMt > 0m ? e.ChargeableLossMt : 0m), ct) ?? 0m;
            var transportedMt = await _db.InventoryTransportLegAllocations
                .AsNoTracking()
                .Where(a => a.SourceLoadingRegisterId == loading.Id
                    && a.InventoryTransportLeg != null
                    && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
                .SumAsync(a => (decimal?)a.QuantityMt, ct) ?? 0m;
            var availableMt = Math.Max(loading.LoadedQuantityMt - receivedMt - shortageMt - transportedMt, 0m);
            if (command.QuantityMt > availableMt + Epsilon)
            {
                throw Rule(
                    "TRANSPORT_LOADING_INSUFFICIENT",
                    $"مقدار درخواستی از باقیماندهٔ بارگیری ({availableMt:N4} MT) بیشتر است.");
            }

            var groupKey = $"ITG:{Guid.NewGuid():N}";
            var batch = new InventoryTransportBatch
            {
                BatchNumber = $"ITB-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".ToUpperInvariant(),
                SourceTerminalId = null,
                SourceStorageTankId = null,
                ProductId = loading.ProductId,
                TotalQuantityMt = command.QuantityMt,
                TransportDate = command.TransportDate.Date,
                Status = InventoryTransportBatchStatus.Loaded,
                TransportGroupKey = groupKey,
                Notes = Normalize(command.Notes)
            };
            var reference = Normalize(command.Reference) ?? loading.RwbNo ?? loading.BillOfLadingNumber;
            var leg = new InventoryTransportLeg
            {
                InventoryTransportBatch = batch,
                TransportGroupKey = groupKey,
                SourcePurchaseContractId = loading.ContractId,
                ProductId = loading.ProductId,
                SourceTerminalId = null,
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
                RwbNo = reference,
                BillOfLadingNumber = loading.BillOfLadingNumber,
                RouteDescription = loading.RouteDescription,
                PurchaseUnitCostUsd = loading.LoadingPriceUsd,
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
            leg.Allocations.Add(new InventoryTransportLegAllocation
            {
                SourceLoadingRegisterId = loading.Id,
                SourcePurchaseContractId = loading.ContractId,
                QuantityMt = decimal.Round(command.QuantityMt, 4, MidpointRounding.AwayFromZero)
            });
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

            // طرفِ کرایه: اگر کاربر راننده انتخاب کرده و حمل شرکت خدماتی/دارایی ملکی ندارد،
            // همان راننده روی حمل ثبت می‌شود — دقیقاً همان کاری که مسیر تسویهٔ تفصیلی
            // (TruckSettlementsController) می‌کند، تا دو قاعدهٔ موازی ساخته نشود.
            if (command.DriverId is > 0
                && !leg.ServiceProviderId.HasValue
                && !leg.OperationalAssetId.HasValue)
            {
                if (!await _db.Drivers.AnyAsync(d => d.Id == command.DriverId.Value && d.IsActive, ct))
                {
                    throw Rule("TRANSPORT_FREIGHT_DRIVER_INVALID", "راننده معتبر یا فعال نیست.");
                }

                leg.DriverId = command.DriverId;
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
        => await ValidateVehicleAsync(
            command.TransportType,
            command.TruckId,
            command.WagonId,
            command.VesselId,
            command.DriverId,
            command.ServiceProviderId,
            ct);

    private async Task ValidateVehicleAsync(
        LoadingTransportType transportType,
        int? truckId,
        int? wagonId,
        int? vesselId,
        int? driverId,
        int? serviceProviderId,
        CancellationToken ct)
    {
        switch (transportType)
        {
            case LoadingTransportType.Truck when truckId.HasValue
                && await _db.Trucks.AsNoTracking().AnyAsync(t => t.Id == truckId && t.IsActive, ct):
                break;
            case LoadingTransportType.Wagon when wagonId.HasValue
                && await _db.Wagons.AsNoTracking().AnyAsync(w => w.Id == wagonId && w.IsActive, ct):
                break;
            case LoadingTransportType.Vessel when vesselId.HasValue
                && await _db.Vessels.AsNoTracking().AnyAsync(v => v.Id == vesselId && v.IsActive, ct):
                break;
            default:
                throw Rule("TRANSPORT_RECEIPT_VEHICLE_INVALID", "وسیلهٔ مقصد معتبر و فعال نیست.");
        }

        if (driverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == driverId && d.IsActive, ct))
        {
            throw Rule("TRANSPORT_RECEIPT_DRIVER_INVALID", "راننده انتخاب‌شده معتبر و فعال نیست.");
        }
        if (serviceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == serviceProviderId && p.IsActive, ct))
        {
            throw Rule("TRANSPORT_RECEIPT_PROVIDER_INVALID", "شرکت خدماتی انتخاب‌شده معتبر و فعال نیست.");
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static BusinessRuleException Rule(string code, string message) => new(code, message);

    private sealed record ReceiptSource(int ContractId, decimal QuantityMt);
}
