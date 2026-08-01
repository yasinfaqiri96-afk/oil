using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public sealed class InventoryTransportBatchService
{
    private const decimal Tolerance = 0.0001m;
    private const string FormPurpose = "InventoryTransport.CreateFromInventory";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IFormTokenGuard _formTokens;

    public InventoryTransportBatchService(
        ApplicationDbContext db,
        IStockService stock,
        IFormTokenGuard? formTokens = null)
    {
        _db = db;
        _stock = stock;
        _formTokens = formTokens ?? new FormTokenGuard(db);
    }

    public async Task<IReadOnlyList<InventoryTransportSourceAvailabilityViewModel>> GetAvailableSourcesAsync(
        int terminalId,
        int storageTankId,
        int productId,
        int? shipmentId = null,
        CancellationToken ct = default)
    {
        if (productId <= 0)
        {
            return [];
        }

        // ورود از پروندهٔ محموله: مبدأ = خود محموله، پس ترمینال/مخزن در فرم پنهان‌اند و اینجا از خودِ
        // محموله (مخزنِ تخلیهٔ قبلی یا اولین مخزنِ مناسب) به‌صورت خودکار استنتاج می‌شوند — کاربر مخزن انتخاب نمی‌کند.
        if ((terminalId <= 0 || storageTankId <= 0) && shipmentId is > 0)
        {
            var (resolvedTerminalId, resolvedStorageTankId) = await ResolveShipmentSourceLocationAsync(shipmentId.Value, productId, ct);
            if (terminalId <= 0) terminalId = resolvedTerminalId;
            if (storageTankId <= 0) storageTankId = resolvedStorageTankId;
        }

        // ترمینال همیشه لازم است. مخزن فقط در حالت عادی (بدون محموله) اجباری است؛ در حالت محموله،
        // «بار روی کشتی» بدون مخزن نمایش داده می‌شود (ردیف‌های مخزن فقط اگر مخزنی استنتاج شده باشد).
        if (terminalId <= 0 || (storageTankId <= 0 && shipmentId is not > 0))
        {
            return [];
        }

        // حالت محموله: مبدأ = خودِ محموله. فقط «موجودی واقعی داخل محموله» نمایش داده می‌شود،
        // نه موجودیِ ترمینال/مخزن. ردیف‌های مخزن اینجا ساخته نمی‌شوند.
        if (shipmentId is > 0)
        {
            return await GetVesselSourceRowsAsync(shipmentId.Value, productId, terminalId, ct);
        }

        var inbound = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.TerminalId == terminalId
                && m.StorageTankId == storageTankId
                && m.ProductId == productId
                && (m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment)
                && m.QuantityMt > 0m)
            .Select(m => new
            {
                m.Id,
                m.QuantityMt,
                m.MovementDate,
                m.LoadingReceiptId,
                ContractId = m.ContractId
                    ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                        : null),
                ContractNumber = m.Contract != null
                    ? m.Contract.ContractNumber
                    : m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null && m.LoadingReceipt.LoadingRegister.Contract != null
                        ? m.LoadingReceipt.LoadingRegister.Contract.ContractNumber
                        : null,
                ContractType = m.Contract != null
                    ? (ContractType?)m.Contract.ContractType
                    : m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null && m.LoadingReceipt.LoadingRegister.Contract != null
                        ? (ContractType?)m.LoadingReceipt.LoadingRegister.Contract.ContractType
                        : null,
                ContractProductId = m.Contract != null
                    ? (int?)m.Contract.ProductId
                    : m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null && m.LoadingReceipt.LoadingRegister.Contract != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.Contract.ProductId
                        : null,
                ReceiptReference = m.LoadingReceipt != null
                    ? m.LoadingReceipt.ReferenceDocument
                    : m.ReferenceDocument,
                TransportType = m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                    ? (LoadingTransportType?)m.LoadingReceipt.LoadingRegister.TransportType
                    : null,
                HasVessel = m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                    && m.LoadingReceipt.LoadingRegister.VesselId != null
            })
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var valid = inbound
            .Where(m => m.ContractId.HasValue
                && m.ContractType == ContractType.Purchase
                && m.ContractProductId == productId)
            .ToList();
        if (shipmentId.HasValue)
        {
            var shipmentContractIds = await _db.ShipmentContracts.AsNoTracking()
                .Where(sc => sc.ShipmentId == shipmentId.Value)
                .Select(sc => sc.ContractId)
                .ToListAsync(ct);
            valid = valid.Where(m => shipmentContractIds.Contains(m.ContractId!.Value)).ToList();
        }

        var sourceMovementIds = valid.Select(m => m.Id).ToArray();
        var usedRows = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .Where(a => sourceMovementIds.Contains(a.SourceInventoryMovementId)
                && a.OutboundInventoryMovementId != null)
            .Select(a => new { a.SourceInventoryMovementId, a.QuantityMt })
            .ToListAsync(ct);
        var usedBySource = usedRows
            .GroupBy(a => a.SourceInventoryMovementId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.QuantityMt));

        var remainingByContract = new Dictionary<int, decimal>();
        foreach (var contractId in valid.Select(m => m.ContractId!.Value).Distinct())
        {
            remainingByContract[contractId] = Math.Max(0m, await _stock.GetFreeQuantityMtAsync(
                productId,
                terminalId: terminalId,
                contractId: contractId,
                storageTankId: storageTankId,
                ct: ct));
        }

        var rows = new List<InventoryTransportSourceAvailabilityViewModel>();
        foreach (var source in valid)
        {
            var contractId = source.ContractId!.Value;
            var sourceAvailable = Math.Max(0m, source.QuantityMt - usedBySource.GetValueOrDefault(source.Id));
            var available = Math.Min(sourceAvailable, remainingByContract.GetValueOrDefault(contractId));
            available = decimal.Round(available, 4, MidpointRounding.AwayFromZero);
            remainingByContract[contractId] = Math.Max(0m, remainingByContract.GetValueOrDefault(contractId) - available);
            if (available <= 0m)
            {
                continue;
            }

            rows.Add(new InventoryTransportSourceAvailabilityViewModel
            {
                SourceInventoryMovementId = source.Id,
                SourcePurchaseContractId = contractId,
                ContractNumber = source.ContractNumber ?? $"#{contractId}",
                SourceLoadingReceiptId = source.LoadingReceiptId,
                ReceiptReference = string.IsNullOrWhiteSpace(source.ReceiptReference)
                    ? source.LoadingReceiptId.HasValue ? $"رسید #{source.LoadingReceiptId}" : $"ورودی #{source.Id}"
                    : source.ReceiptReference,
                SourceKind = source.TransportType switch
                {
                    LoadingTransportType.Vessel => "تخلیه کشتی",
                    LoadingTransportType.Wagon => "واگن",
                    LoadingTransportType.Truck => "موتر",
                    _ => source.HasVessel
                        ? "تخلیه کشتی"
                        : source.LoadingReceiptId.HasValue ? "رسید بارگیری" : "ورودی مستقیم"
                },
                SourceDate = source.MovementDate,
                ProductId = productId,
                TerminalId = terminalId,
                StorageTankId = storageTankId,
                AvailableQuantityMt = available
            });
        }

        return rows;
    }

    // نشانهٔ منبعِ «بار روی کشتی» (تخلیه‌نشده). ردیف‌های این نوع تا لحظهٔ بارگیری هیچ InventoryMovementای
    // ندارند؛ به‌جای شناسهٔ حرکت، یک سنتینلِ منفی (-contractId) حمل می‌کنند که در commit ماتریالایز می‌شود.
    private const string VesselSourceKind = "بار روی کشتی";

    internal static bool IsVesselSentinel(int sourceInventoryMovementId) => sourceInventoryMovementId < 0;

    // موجودی واقعیِ باقی‌مانده «داخل محموله» برای هر قرارداد — دقیقاً همان فرمول کارت «باقی‌مانده»
    // در پروندهٔ محموله (ShipmentPnl/Details): بارگیری‌شده − تخلیه‌شده (رسیدهای کشتی) − فروش‌شدهٔ محموله.
    //   • تخلیه‌شده = رسیدهای ToInventory غیرلغو با تگ «Group receipt: SHIP:{id}» یا روی legهای نوع Vessel
    //     (شاملِ رسیدِ حملِ مستقیم از بار کشتی که MaterializeVesselSourcesAsync می‌سازد).
    //   • فروش‌شده = SalesTransactionهای غیرلغوِ وصل به همین محموله (بدون پیش‌فروش).
    // خواندنی محض؛ هیچ داده‌ای اینجا تغییر نمی‌کند.
    private async Task<IReadOnlyList<InventoryTransportSourceAvailabilityViewModel>> GetVesselSourceRowsAsync(
        int shipmentId,
        int productId,
        int terminalId,
        CancellationToken ct)
    {
        var allocations = await _db.ShipmentContracts.AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId
                && sc.Contract != null
                && sc.Contract.ContractType == ContractType.Purchase
                && sc.Contract.ProductId == productId)
            .Select(sc => new VesselContractAllocation(
                sc.ContractId,
                sc.Contract!.ContractNumber,
                sc.QuantityMt ?? 0m))
            .ToListAsync(ct);

        // سازگاری با محموله‌های تک‌قراردادِ قدیمی که ردیف ShipmentContracts ندارند.
        if (allocations.Count == 0)
        {
            allocations = await _db.Shipments.AsNoTracking()
                .Where(s => s.Id == shipmentId
                    && s.ContractId != null
                    && s.Contract!.ContractType == ContractType.Purchase
                    && s.Contract.ProductId == productId
                    && s.QuantityMt > 0m)
                .Select(s => new VesselContractAllocation(
                    s.ContractId!.Value,
                    s.Contract!.ContractNumber,
                    s.QuantityMt))
                .ToListAsync(ct);
        }

        if (allocations.Count == 0)
        {
            return [];
        }

        // اگر ردیف‌های ShipmentContracts مقدار تفکیکی ندارند (QuantityMt خالی)، مثل پرونده از
        // مقدار کل خود محموله استفاده می‌کنیم — در حالت تک‌قرارداد تمام مقدار به همان قرارداد می‌رسد.
        if (allocations.Sum(a => a.AllocatedMt) <= 0m && allocations.Count == 1)
        {
            var shipmentQuantityMt = await _db.Shipments.AsNoTracking()
                .Where(s => s.Id == shipmentId)
                .Select(s => s.QuantityMt)
                .FirstOrDefaultAsync(ct);
            if (shipmentQuantityMt > 0m)
            {
                allocations[0] = allocations[0] with { AllocatedMt = shipmentQuantityMt };
            }
        }

        var contractIds = allocations.Select(a => a.ContractId).ToList();

        // تخلیه‌شده از کشتی به تفکیک قرارداد — همان تعریف «تخلیه‌شده» در پروندهٔ محموله.
        var shipmentGroupReceiptTag = $"Group receipt: SHIP:{shipmentId} |";
        var unloadedByContract = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
                && r.InventoryMovementId != null
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipmentId
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Draft
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled
                && (r.InventoryTransportLeg.TransportType == LoadingTransportType.Vessel
                    || (r.Notes != null && r.Notes.Contains(shipmentGroupReceiptTag)))
                && contractIds.Contains(r.InventoryTransportLeg.SourcePurchaseContractId))
            .GroupBy(r => r.InventoryTransportLeg!.SourcePurchaseContractId)
            .Select(g => new { ContractId = g.Key, UnloadedMt = g.Sum(x => x.ReceivedQuantityMt) })
            .ToDictionaryAsync(g => g.ContractId, g => g.UnloadedMt, ct);

        // فروش‌شدهٔ وصل به همین محموله به تفکیک قرارداد؛ فروش‌های بدون قرارداد در یک استخر مشترک
        // نگه داشته می‌شوند و به ترتیب از باقی‌ماندهٔ ردیف‌ها کم می‌شوند.
        var shipmentSales = await _db.SalesTransactions.AsNoTracking()
            .Where(s => s.ShipmentId == shipmentId
                && !s.IsCancelled
                && s.SaleStage != SaleStage.PreSale
                && s.ProductId == productId)
            .Select(s => new { s.ContractId, s.QuantityMt })
            .ToListAsync(ct);
        var soldByContract = shipmentSales
            .Where(s => s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value))
            .GroupBy(s => s.ContractId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));
        var unassignedSoldMt = shipmentSales
            .Where(s => !s.ContractId.HasValue || !contractIds.Contains(s.ContractId.Value))
            .Sum(s => s.QuantityMt);

        var rows = new List<InventoryTransportSourceAvailabilityViewModel>();
        foreach (var alloc in allocations)
        {
            var remaining = Math.Max(
                alloc.AllocatedMt
                - unloadedByContract.GetValueOrDefault(alloc.ContractId)
                - soldByContract.GetValueOrDefault(alloc.ContractId),
                0m);
            if (unassignedSoldMt > 0m && remaining > 0m)
            {
                var deducted = Math.Min(remaining, unassignedSoldMt);
                remaining -= deducted;
                unassignedSoldMt -= deducted;
            }
            remaining = decimal.Round(remaining, 4, MidpointRounding.AwayFromZero);
            if (remaining <= 0m)
            {
                continue;
            }

            rows.Add(new InventoryTransportSourceAvailabilityViewModel
            {
                SourceInventoryMovementId = -alloc.ContractId,
                SourcePurchaseContractId = alloc.ContractId,
                ContractNumber = string.IsNullOrWhiteSpace(alloc.ContractNumber) ? $"#{alloc.ContractId}" : alloc.ContractNumber,
                SourceLoadingReceiptId = null,
                ReceiptReference = VesselSourceKind,
                SourceKind = VesselSourceKind,
                SourceDate = AfghanistanBusinessClock.SystemToday,
                ProductId = productId,
                TerminalId = terminalId,
                StorageTankId = 0,
                AvailableQuantityMt = remaining
            });
        }

        return rows;
    }

    // ترمینال/مخزنِ «عبور» را برای حملِ مستقیم از محموله استنتاج می‌کند تا کاربر مجبور به انتخاب مخزن نباشد.
    // اولویت: مخزنِ همان محموله که قبلاً موجودیِ همین محصول در آن تخلیه/رسید شده؛ در نبودِ آن، اولین مخزنِ
    // فعالِ مناسبِ همان محصول. اگر هیچ مخزنی نبود (0,0) برمی‌گردد. خواندنی محض.
    public async Task<(int TerminalId, int StorageTankId)> ResolveShipmentSourceLocationAsync(
        int shipmentId,
        int productId,
        CancellationToken ct = default)
    {
        if (shipmentId <= 0 || productId <= 0)
        {
            return (0, 0);
        }

        var existing = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
                && r.DestinationTerminalId != null
                && r.DestinationStorageTankId != null
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipmentId
                && r.InventoryTransportLeg.ProductId == productId)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new { TerminalId = r.DestinationTerminalId!.Value, StorageTankId = r.DestinationStorageTankId!.Value })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return (existing.TerminalId, existing.StorageTankId);
        }

        var fallbackTank = await _db.StorageTanks.AsNoTracking()
            .Where(t => t.IsActive && (t.ProductId == null || t.ProductId == productId))
            .OrderBy(t => t.TankCode)
            .Select(t => new { t.TerminalId, t.Id })
            .FirstOrDefaultAsync(ct);
        if (fallbackTank is not null)
        {
            return (fallbackTank.TerminalId, fallbackTank.Id);
        }

        // محموله‌ای که هنوز هیچ تخلیه‌ای ندارد و مخزنی هم برای محصول نیست: فقط یک ترمینالِ فعال
        // لازم است (حرکت‌های بار روی کشتی در سطح ترمینال با مخزنِ null ثبت می‌شوند). مخزن = 0 (بدون مخزن).
        var fallbackTerminalId = await _db.Terminals.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        return (fallbackTerminalId, 0);
    }

    // استنتاج کشتی برای «مبدأِ» یک مخزن مقصد (برای پیش‌پرکردن در GET): رسیدهای «به مخزن»
    // که به این ترمینال/مخزن/محصول تخلیه شده‌اند را می‌گیریم و کشتیِ legِ آن‌ها را می‌خوانیم.
    // خواندنی محض؛ هیچ داده‌ای تغییر نمی‌کند.
    public async Task<ShipmentLinkInference> InferShipmentForTankAsync(
        int terminalId,
        int storageTankId,
        int productId,
        CancellationToken ct = default)
    {
        if (terminalId <= 0 || storageTankId <= 0 || productId <= 0)
        {
            return new ShipmentLinkInference(null, false);
        }

        var shipmentIds = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
                && r.DestinationTerminalId == terminalId
                && r.DestinationStorageTankId == storageTankId
                && r.InventoryTransportLeg!.ProductId == productId
                && r.InventoryTransportLeg.ShipmentId != null)
            .Select(r => r.InventoryTransportLeg!.ShipmentId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return shipmentIds.Count == 1
            ? new ShipmentLinkInference(shipmentIds[0], false)
            : new ShipmentLinkInference(null, shipmentIds.Count > 1);
    }

    // استنتاج کشتی از حرکت‌های موجودیِ انتخاب‌شده در POST: هر حرکتِ ورودیِ منبع، همان
    // InventoryMovementِ رسیدِ مرحلهٔ قبل است؛ از رسید به leg و از leg به کشتی می‌رسیم.
    private async Task<ShipmentLinkInference> InferShipmentFromSourceMovementsAsync(
        IEnumerable<int> sourceMovementIds,
        CancellationToken ct)
    {
        var ids = sourceMovementIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new ShipmentLinkInference(null, false);
        }

        var shipmentIds = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.InventoryMovementId != null
                && ids.Contains(r.InventoryMovementId.Value)
                && r.InventoryTransportLeg!.ShipmentId != null)
            .Select(r => r.InventoryTransportLeg!.ShipmentId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return shipmentIds.Count == 1
            ? new ShipmentLinkInference(shipmentIds[0], false)
            : new ShipmentLinkInference(null, shipmentIds.Count > 1);
    }

    public async Task<InventoryTransportBatch> CreateAsync(
        InventoryTransportFromInventoryViewModel model,
        string? formToken,
        CancellationToken ct = default)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            }

            await ResolveTypedVehiclesAsync(model, ct);
            var prepared = await ValidateAndPrepareAsync(model, ct);

            // انتشار خودکار کشتی: اگر کاربر کشتی را صریح نداده باشد، از خودِ حرکت‌های موجودیِ انتخاب‌شده
            // (هرکدام حرکتِ ورودیِ رسیدِ مرحلهٔ قبلِ همین بار است) کشتی را استنتاج می‌کنیم تا legِ مرحلهٔ
            // بعدی بدون کشتی نماند. فقط خواندنی است؛ موجودی/لجر/فروش را تغییر نمی‌دهد.
            if (model.ShipmentId is null or <= 0)
            {
                var inference = await InferShipmentFromSourceMovementsAsync(prepared.Sources.Keys, ct);
                if (inference.IsAmbiguous)
                {
                    throw new BusinessRuleException(
                        "TRANSPORT_LEG_SHIPMENT_AMBIGUOUS",
                        "منبع انتخاب‌شده به بیش از یک کشتی تعلق دارد و کشتی مشخص نیست. برای ثبت مرحلهٔ بعدی، از دکمهٔ «حمل بعدی» در پروندهٔ همان کشتی استفاده کنید.");
                }
                model.ShipmentId = inference.ShipmentId;
            }

            var groupKey = $"ITG:{Guid.NewGuid():N}";
            var batch = new InventoryTransportBatch
            {
                BatchNumber = $"ITB-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".ToUpperInvariant(),
                SourceTerminalId = model.SourceTerminalId,
                SourceStorageTankId = model.SourceStorageTankId > 0 ? model.SourceStorageTankId : null,
                ProductId = model.ProductId,
                TotalQuantityMt = prepared.TotalQuantityMt,
                TransportDate = model.TransportDate.Date,
                Status = model.SubmissionMode == InventoryTransportSubmissionMode.Loaded
                    ? InventoryTransportBatchStatus.Loaded
                    : InventoryTransportBatchStatus.Draft,
                TransportGroupKey = groupKey,
                Notes = Normalize(model.Notes)
            };

            // «بار روی کشتی»: هر منبعِ کشتیِ انتخاب‌شده را به یک رسیدِ استانداردِ کشتی→مخزن مبدأ تبدیل کن
            // و نگاشتِ سنتینلِ منفی → شناسهٔ حرکتِ In واقعی را بگیر (در حالت پیش‌نویس این نگاشت خالی است).
            var vesselSentinelRemap = await MaterializeVesselSourcesAsync(model, prepared, groupKey, ct);

            foreach (var vehicle in prepared.Vehicles)
            {
                var firstSource = prepared.Sources[vehicle.Allocations[0].SourceInventoryMovementId];
                var leg = new InventoryTransportLeg
                {
                    InventoryTransportBatch = batch,
                    ShipmentId = model.ShipmentId,
                    TransportGroupKey = groupKey,
                    SourcePurchaseContractId = firstSource.SourcePurchaseContractId,
                    ProductId = model.ProductId,
                    SourceTerminalId = model.SourceTerminalId,
                    SourceStorageTankId = model.SourceStorageTankId > 0 ? model.SourceStorageTankId : null,
                    TransportType = vehicle.Input.TransportType,
                    TruckId = vehicle.Input.TruckId,
                    WagonId = vehicle.Input.WagonId,
                    WagonNumber = vehicle.WagonNumber,
                    DriverId = vehicle.Input.DriverId,
                    CarrierType = vehicle.Input.CarrierType,
                    ServiceProviderId = vehicle.Input.CarrierType == CarrierType.ServiceProvider
                        ? vehicle.Input.ServiceProviderId
                        : null,
                    OperationalAssetId = vehicle.Input.CarrierType == CarrierType.OperationalAsset
                        ? vehicle.Input.OperationalAssetId
                        : null,
                    LoadedDate = model.TransportDate.Date,
                    QuantityMt = vehicle.Input.QuantityMt,
                    CapacityMt = vehicle.CapacityMt,
                    FreightAmount = vehicle.Input.FreightAmount.GetValueOrDefault() > 0m
                        ? vehicle.Input.FreightAmount
                        : null,
                    FreightCurrencyId = vehicle.Input.FreightAmount.GetValueOrDefault() > 0m
                        ? vehicle.Input.FreightCurrencyId
                        : null,
                    RwbNo = Normalize(vehicle.Input.RwbNo),
                    BillOfLadingNumber = Normalize(vehicle.Input.BillOfLadingNumber),
                    Status = model.SubmissionMode == InventoryTransportSubmissionMode.Loaded
                        ? InventoryTransportLegStatus.Loaded
                        : InventoryTransportLegStatus.Draft,
                    Notes = Normalize(model.Notes)
                };

                foreach (var allocationInput in vehicle.Allocations)
                {
                    var source = prepared.Sources[allocationInput.SourceInventoryMovementId];
                    // منابع کشتی سنتینلِ منفی دارند؛ به شناسهٔ حرکتِ In واقعیِ ماتریالایزشده نگاشت می‌شوند.
                    var sourceMovementId = vesselSentinelRemap.TryGetValue(allocationInput.SourceInventoryMovementId, out var realMovementId)
                        ? realMovementId
                        : allocationInput.SourceInventoryMovementId;
                    leg.Allocations.Add(new InventoryTransportLegAllocation
                    {
                        SourcePurchaseContractId = source.SourcePurchaseContractId,
                        SourceLoadingReceiptId = source.SourceLoadingReceiptId,
                        SourceInventoryMovementId = sourceMovementId,
                        QuantityMt = allocationInput.QuantityMt
                    });
                }

                batch.Legs.Add(leg);
            }

            _db.InventoryTransportBatches.Add(batch);
            _formTokens.Stamp(formToken, FormPurpose, nameof(InventoryTransportBatch));
            await _db.SaveChangesAsync(ct);

            if (model.SubmissionMode == InventoryTransportSubmissionMode.Loaded)
            {
                await CreateOutboundMovementsAsync(batch, ct);
            }

            await _db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return batch;
        }
        catch (Exception ex) when (_formTokens.IsDuplicate(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw new BusinessRuleException(
                "INVENTORY_TRANSPORT_DUPLICATE_SUBMIT",
                "این فورم قبلاً ثبت شده است. صفحه را دوباره تازه کنید.");
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<InventoryTransportBatch> LoadDraftAsync(int batchId, CancellationToken ct = default)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            }

            var batch = await _db.InventoryTransportBatches
                .Include(b => b.Legs)
                    .ThenInclude(l => l.Allocations)
                .FirstOrDefaultAsync(b => b.Id == batchId, ct)
                ?? throw Rule("INVENTORY_TRANSPORT_BATCH_MISSING", "سند حمل پیدا نشد.");
            if (batch.Status != InventoryTransportBatchStatus.Draft
                || batch.Legs.Any(l => l.Status != InventoryTransportLegStatus.Draft
                    || l.Allocations.Any(a => a.OutboundInventoryMovementId.HasValue)))
            {
                throw Rule("INVENTORY_TRANSPORT_BATCH_ALREADY_LOADED", "این سند قبلاً بارگیری شده یا قابل بارگیری نیست.");
            }

            var validationModel = new InventoryTransportFromInventoryViewModel
            {
                ShipmentId = batch.Legs.Select(l => l.ShipmentId).Distinct().Count() == 1
                    ? batch.Legs.First().ShipmentId
                    : null,
                SourceTerminalId = batch.SourceTerminalId,
                SourceStorageTankId = batch.SourceStorageTankId ?? 0,
                ProductId = batch.ProductId,
                TransportDate = batch.TransportDate,
                SubmissionMode = InventoryTransportSubmissionMode.Loaded,
                Sources = batch.Legs
                    .SelectMany(l => l.Allocations)
                    .GroupBy(a => a.SourceInventoryMovementId)
                    .Select(g => new InventoryTransportSourceSelectionInput
                    {
                        SourceInventoryMovementId = g.Key,
                        QuantityMt = g.Sum(a => a.QuantityMt)
                    })
                    .ToList(),
                Vehicles = batch.Legs.Select(l => new InventoryTransportVehicleInput
                {
                    TransportType = l.TransportType,
                    TruckId = l.TruckId,
                    WagonId = l.WagonId,
                    DriverId = l.DriverId,
                    QuantityMt = l.QuantityMt,
                    CapacityMt = l.CapacityMt,
                    CarrierType = l.CarrierType ?? CarrierType.ServiceProvider,
                    ServiceProviderId = l.ServiceProviderId,
                    OperationalAssetId = l.OperationalAssetId,
                    FreightAmount = l.FreightAmount,
                    FreightCurrencyId = l.FreightCurrencyId,
                    RwbNo = l.RwbNo,
                    BillOfLadingNumber = l.BillOfLadingNumber,
                    Allocations = l.Allocations.Select(a => new InventoryTransportVehicleAllocationInput
                    {
                        SourceInventoryMovementId = a.SourceInventoryMovementId,
                        QuantityMt = a.QuantityMt
                    }).ToList()
                }).ToList()
            };
            await ValidateAndPrepareAsync(validationModel, ct);
            await CreateOutboundMovementsAsync(batch, ct);
            batch.Status = InventoryTransportBatchStatus.Loaded;
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return batch;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task CreateOutboundMovementsAsync(InventoryTransportBatch batch, CancellationToken ct)
    {
        // خروجی از همان نقطه‌ای که منبع در آن است کسر می‌شود: ترمینال/مخزنِ حرکتِ منبعِ هر سهم.
        // برای منابع مخزن = همان مخزن؛ برای «بار روی کشتی» = ترمینالِ همان با مخزنِ null (بدون توقف در مخزن).
        var sourceMovementIds = batch.Legs
            .SelectMany(l => l.Allocations)
            .Select(a => a.SourceInventoryMovementId)
            .Distinct()
            .ToList();
        var sourceLocations = await _db.InventoryMovements.AsNoTracking()
            .Where(m => sourceMovementIds.Contains(m.Id))
            .Select(m => new { m.Id, m.TerminalId, m.StorageTankId })
            .ToDictionaryAsync(m => m.Id, m => (m.TerminalId, m.StorageTankId), ct);

        foreach (var leg in batch.Legs)
        {
            foreach (var allocation in leg.Allocations)
            {
                var location = sourceLocations.TryGetValue(allocation.SourceInventoryMovementId, out var loc)
                    ? loc
                    : (batch.SourceTerminalId, batch.SourceStorageTankId);
                var movement = new InventoryMovement
                {
                    ProductId = batch.ProductId,
                    ContractId = allocation.SourcePurchaseContractId,
                    TerminalId = location.Item1,
                    StorageTankId = location.Item2,
                    Direction = MovementDirection.Out,
                    MovementDate = batch.TransportDate,
                    QuantityMt = allocation.QuantityMt,
                    ReferenceDocument = $"TRANSPORT-ALLOCATION:{allocation.Id}",
                    Notes = $"Inventory transport batch {batch.BatchNumber}, leg {leg.Id}"
                };
                // قفل هم‌زمانی روی مخزن/کالا پیش از چک موجودی (داخل تراکنشِ caller).
                await _stock.AcquireStockMutationLockAsync(movement, ct);
                await _stock.EnsureSufficientStockForMovementAsync(movement, ct);
                _db.InventoryMovements.Add(movement);
                await _db.SaveChangesAsync(ct);
                allocation.OutboundInventoryMovementId = movement.Id;
            }

            if (leg.Allocations.Count == 1)
            {
                leg.OutboundInventoryMovementId = leg.Allocations.Single().OutboundInventoryMovementId;
            }
            leg.Status = InventoryTransportLegStatus.Loaded;
            leg.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    // «بار روی کشتی» (سنتینلِ منفی) را به رسیدِ استانداردِ کشتی→مخزن مبدأ تبدیل می‌کند — بدون Entity/Migration جدید:
    //   • legِ رسید: نوع Unspecified، Status=Received، بدون OutboundInventoryMovement → خارج از داشبورد فعال و
    //     خارج از محاسبهٔ خروجی/در راه؛ در تشخیصِ ریشهٔ قرارداد هم دخالت نمی‌کند (چون Vessel نیست و خروجی ندارد).
    //   • یک InventoryMovement ورودی در مخزن مبدأ (contract, qty) تا مخزن برای خروجیِ وسایط موجودی داشته باشد.
    //   • یک InventoryTransportReceipt (ToInventory) با تگ گروهِ SHIP تا در «رسیدهای کشتی» پروندهٔ محموله دیده شود
    //     و «تخلیه‌شدهٔ» محموله بالا رود؛ یعنی از باقی‌ماندهٔ واقعیِ کشتی کم شود.
    // خروجی: نگاشتِ سنتینلِ منفی → شناسهٔ حرکتِ In واقعی. در نبودِ منبع کشتی، نگاشتِ خالی برمی‌گردد.
    private async Task<IReadOnlyDictionary<int, int>> MaterializeVesselSourcesAsync(
        InventoryTransportFromInventoryViewModel model,
        PreparedBatch prepared,
        string groupKey,
        CancellationToken ct)
    {
        var totalsBySentinel = new Dictionary<int, decimal>();
        foreach (var vehicle in prepared.Vehicles)
        {
            foreach (var allocation in vehicle.Allocations)
            {
                if (!IsVesselSentinel(allocation.SourceInventoryMovementId))
                {
                    continue;
                }
                totalsBySentinel[allocation.SourceInventoryMovementId] =
                    totalsBySentinel.GetValueOrDefault(allocation.SourceInventoryMovementId) + allocation.QuantityMt;
            }
        }

        if (totalsBySentinel.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var remap = new Dictionary<int, int>();
        foreach (var (sentinelId, rawQuantity) in totalsBySentinel)
        {
            var source = prepared.Sources[sentinelId];
            var quantity = decimal.Round(rawQuantity, 4, MidpointRounding.AwayFromZero);
            if (quantity <= 0m)
            {
                continue;
            }

            // بدون مخزن: بارِ کشتی مستقیم تخلیه می‌شود؛ حرکت‌ها در سطح ترمینال با StorageTankId = null
            // ثبت می‌شوند (ورود و خروجِ هم‌زمان = خالص صفر؛ در هیچ مخزنی نمی‌ماند).
            var vesselReceiptLeg = new InventoryTransportLeg
            {
                ShipmentId = model.ShipmentId,
                TransportGroupKey = groupKey,
                SourcePurchaseContractId = source.SourcePurchaseContractId,
                ProductId = model.ProductId,
                SourceTerminalId = model.SourceTerminalId,
                SourceStorageTankId = null,
                DestinationTerminalId = model.SourceTerminalId,
                DestinationStorageTankId = null,
                TransportType = LoadingTransportType.Unspecified,
                LoadedDate = model.TransportDate.Date,
                QuantityMt = quantity,
                Status = InventoryTransportLegStatus.Received,
                Notes = "رسید بار کشتی برای حمل مستقیم از موجودی محموله"
            };
            _db.InventoryTransportLegs.Add(vesselReceiptLeg);
            await _db.SaveChangesAsync(ct);

            var inboundMovement = new InventoryMovement
            {
                ProductId = model.ProductId,
                ContractId = source.SourcePurchaseContractId,
                TerminalId = model.SourceTerminalId,
                StorageTankId = null,
                Direction = MovementDirection.In,
                MovementDate = model.TransportDate.Date,
                QuantityMt = quantity,
                ReferenceDocument = $"VESSEL-DIRECT-LEG:{vesselReceiptLeg.Id}",
                Notes = "Direct-from-vessel discharge (no tank)"
            };
            _db.InventoryMovements.Add(inboundMovement);
            await _db.SaveChangesAsync(ct);

            var receipt = new InventoryTransportReceipt
            {
                InventoryTransportLegId = vesselReceiptLeg.Id,
                ReceiptDate = model.TransportDate.Date,
                ReceivedQuantityMt = quantity,
                ShortageQuantityMt = 0m,
                ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                DestinationTerminalId = model.SourceTerminalId,
                DestinationStorageTankId = null,
                InventoryMovementId = inboundMovement.Id,
                Notes = $"Group receipt: SHIP:{model.ShipmentId!.Value} | حمل مستقیم از بار کشتی | Total received: {quantity:N4} MT"
            };
            _db.InventoryTransportReceipts.Add(receipt);
            await _db.SaveChangesAsync(ct);

            remap[sentinelId] = inboundMovement.Id;
        }

        return remap;
    }

    private static string? NormalizePlate(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    // Turns a typed truck/wagon number into a base-data record: reuses an existing active row
    // by number, otherwise creates a new profile in Trucks/Wagons, then binds it to the vehicle.
    private async Task ResolveTypedVehiclesAsync(
        InventoryTransportFromInventoryViewModel model,
        CancellationToken ct)
    {
        var vehicles = (model.Vehicles ?? []).Where(v => v.QuantityMt > 0m).ToList();
        var createdTrucks = new List<(InventoryTransportVehicleInput Vehicle, Truck Truck)>();
        var createdWagons = new List<(InventoryTransportVehicleInput Vehicle, Wagon Wagon)>();
        foreach (var vehicle in vehicles)
        {
            if (vehicle.CarrierType != CarrierType.ServiceProvider)
            {
                continue;
            }

            if (vehicle.TransportType == LoadingTransportType.Truck)
            {
                var plate = NormalizePlate(vehicle.TruckPlateNumberInput);
                if (plate is null)
                {
                    continue;
                }
                var pendingTruck = createdTrucks.FirstOrDefault(c => c.Truck.PlateNumber == plate).Truck;
                if (pendingTruck is not null)
                {
                    createdTrucks.Add((vehicle, pendingTruck));
                    vehicle.WagonId = null;
                    continue;
                }
                var existing = await _db.Trucks.FirstOrDefaultAsync(t => t.PlateNumber == plate, ct);
                if (existing is not null)
                {
                    if (!existing.IsActive)
                    {
                        throw Rule("INVENTORY_TRANSPORT_TRUCK_INACTIVE", $"موتر با نمبر پلیت «{plate}» قبلاً غیرفعال ثبت شده است؛ ابتدا آن را در داده‌های پایه فعال کنید.");
                    }
                    vehicle.TruckId = existing.Id;
                }
                else
                {
                    var truck = new Truck { PlateNumber = plate, MaxLoadMt = PositiveCapacity(vehicle.CapacityMt), IsActive = true };
                    _db.Trucks.Add(truck);
                    createdTrucks.Add((vehicle, truck));
                }
                vehicle.WagonId = null;
            }
            else if (vehicle.TransportType == LoadingTransportType.Wagon)
            {
                var number = NormalizePlate(vehicle.WagonNumberInput);
                if (number is null)
                {
                    continue;
                }
                var existing = await _db.Wagons.FirstOrDefaultAsync(w => w.WagonNumber == number, ct);
                if (existing is not null)
                {
                    if (!existing.IsActive)
                    {
                        throw Rule("INVENTORY_TRANSPORT_WAGON_INACTIVE", $"واگن با نمبر «{number}» قبلاً غیرفعال ثبت شده است؛ ابتدا آن را در داده‌های پایه فعال کنید.");
                    }
                    vehicle.WagonId = existing.Id;
                }
                else
                {
                    var wagon = new Wagon { WagonNumber = number, CapacityMt = PositiveCapacity(vehicle.CapacityMt), IsActive = true };
                    _db.Wagons.Add(wagon);
                    createdWagons.Add((vehicle, wagon));
                }
                vehicle.TruckId = null;
            }
        }

        if (createdTrucks.Count > 0 || createdWagons.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            foreach (var (vehicle, truck) in createdTrucks)
            {
                vehicle.TruckId = truck.Id;
            }
            foreach (var (vehicle, wagon) in createdWagons)
            {
                vehicle.WagonId = wagon.Id;
            }
        }
    }

    private async Task<PreparedBatch> ValidateAndPrepareAsync(
        InventoryTransportFromInventoryViewModel model,
        CancellationToken ct)
    {
        if (model.ShipmentId.HasValue
            && !await _db.Shipments.AsNoTracking().AnyAsync(s => s.Id == model.ShipmentId.Value, ct))
        {
            throw Rule("INVENTORY_TRANSPORT_SHIPMENT_INVALID", "محموله انتخاب‌شده پیدا نشد.");
        }

        // ورود از پروندهٔ محموله: ترمینالِ عبور از خودِ محموله استنتاج می‌شود؛ مخزن اجباری نیست
        // (حملِ مستقیم از بار روی کشتی بدون توقف در مخزن). فقط یک ترمینالِ معتبر لازم است.
        if (model.ShipmentId is > 0 && model.ProductId > 0
            && (model.SourceTerminalId <= 0 || model.SourceStorageTankId <= 0))
        {
            var (resolvedTerminalId, resolvedStorageTankId) = await ResolveShipmentSourceLocationAsync(model.ShipmentId.Value, model.ProductId, ct);
            if (model.SourceTerminalId <= 0) model.SourceTerminalId = resolvedTerminalId;
            if (model.SourceStorageTankId <= 0) model.SourceStorageTankId = resolvedStorageTankId;
            if (model.SourceTerminalId <= 0)
            {
                throw Rule("INVENTORY_TRANSPORT_SHIPMENT_NO_TERMINAL", "ترمینالی برای ثبت تخلیهٔ این محموله پیدا نشد. حداقل یک ترمینال فعال باید تعریف شده باشد.");
            }
        }

        if (model.TransportDate == default)
        {
            throw Rule("INVENTORY_TRANSPORT_DATE_REQUIRED", "تاریخ حمل الزامی است.");
        }

        // مخزن اختیاری است: فقط وقتی مخزن انتخاب/استنتاج شده باشد اعتبارسنجی می‌شود.
        if (model.SourceStorageTankId > 0)
        {
            var tank = await _db.StorageTanks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.SourceStorageTankId, ct);
            if (tank is null || !tank.IsActive || tank.TerminalId != model.SourceTerminalId)
            {
                throw Rule("INVENTORY_TRANSPORT_TANK_INVALID", "مخزن مبدأ فعال و مربوط به ترمینال انتخاب‌شده نیست.");
            }
            if (tank.ProductId.HasValue && tank.ProductId != model.ProductId)
            {
                throw Rule("INVENTORY_TRANSPORT_TANK_PRODUCT", "محصول مخزن با محصول انتخاب‌شده یکسان نیست.");
            }
        }
        if (!await _db.Terminals.AsNoTracking().AnyAsync(t => t.Id == model.SourceTerminalId && t.IsActive, ct)
            || !await _db.Products.AsNoTracking().AnyAsync(p => p.Id == model.ProductId && p.IsActive, ct))
        {
            throw Rule("INVENTORY_TRANSPORT_SOURCE_INVALID", "ترمینال یا محصول مبدأ فعال نیست.");
        }

        var availableSources = await GetAvailableSourcesAsync(
            model.SourceTerminalId,
            model.SourceStorageTankId,
            model.ProductId,
            model.ShipmentId,
            ct);
        var availableById = availableSources.ToDictionary(s => s.SourceInventoryMovementId);
        var selected = (model.Sources ?? [])
            .Where(s => s.QuantityMt.GetValueOrDefault() > 0m)
            .ToList();
        if (selected.Count == 0)
        {
            throw Rule("INVENTORY_TRANSPORT_SOURCE_REQUIRED", "حداقل یک منبع موجودی را انتخاب کنید.");
        }
        if (selected.Select(s => s.SourceInventoryMovementId).Distinct().Count() != selected.Count)
        {
            throw Rule("INVENTORY_TRANSPORT_SOURCE_DUPLICATE", "یک منبع موجودی بیشتر از یک بار انتخاب شده است.");
        }
        if (selected.Any(s => IsVesselSentinel(s.SourceInventoryMovementId)))
        {
            if (model.ShipmentId is null or <= 0)
            {
                throw Rule("INVENTORY_TRANSPORT_VESSEL_NO_SHIPMENT", "حمل مستقیم از بار کشتی فقط از داخل پروندهٔ محموله ممکن است.");
            }
            if (model.SubmissionMode != InventoryTransportSubmissionMode.Loaded)
            {
                throw Rule("INVENTORY_TRANSPORT_VESSEL_DRAFT", "بار روی کشتی فقط با «ثبت و بارگیری» قابل حمل است؛ ثبت پیش‌نویس پشتیبانی نمی‌شود.");
            }
        }
        foreach (var source in selected)
        {
            if (!availableById.TryGetValue(source.SourceInventoryMovementId, out var available))
            {
                throw Rule("INVENTORY_TRANSPORT_SOURCE_UNAVAILABLE", "یکی از منابع انتخاب‌شده دیگر موجودی قابل حمل ندارد.");
            }
            if (source.QuantityMt.GetValueOrDefault() - available.AvailableQuantityMt > Tolerance)
            {
                throw Rule(
                    "INVENTORY_TRANSPORT_SOURCE_OVERDRAW",
                    $"مقدار انتخابی {available.ContractNumber} / {available.ReceiptReference} از موجودی قابل حمل بیشتر است.");
            }
        }

        var vehicles = (model.Vehicles ?? []).Where(v => v.QuantityMt > 0m).ToList();
        if (vehicles.Count == 0)
        {
            throw Rule("INVENTORY_TRANSPORT_VEHICLE_REQUIRED", "حداقل یک موتر یا واگن وارد کنید.");
        }

        var truckIds = vehicles.Where(v => v.TruckId.HasValue).Select(v => v.TruckId!.Value).Distinct().ToArray();
        var wagonIds = vehicles.Where(v => v.WagonId.HasValue).Select(v => v.WagonId!.Value).Distinct().ToArray();
        var driverIds = vehicles.Where(v => v.DriverId.HasValue).Select(v => v.DriverId!.Value).Distinct().ToArray();
        var providerIds = vehicles.Where(v => v.ServiceProviderId.HasValue).Select(v => v.ServiceProviderId!.Value).Distinct().ToArray();
        var assetIds = vehicles.Where(v => v.OperationalAssetId.HasValue).Select(v => v.OperationalAssetId!.Value).Distinct().ToArray();
        var currencyIds = vehicles.Where(v => v.FreightCurrencyId.HasValue).Select(v => v.FreightCurrencyId!.Value).Distinct().ToArray();

        var assets = await _db.OperationalAssets.AsNoTracking().Where(a => assetIds.Contains(a.Id) && a.IsActive).ToDictionaryAsync(a => a.Id, ct);
        var linkedTruckIds = assets.Values
            .Where(a => a.LinkedTruckId.HasValue)
            .Select(a => a.LinkedTruckId!.Value);
        var resolvedTruckIds = truckIds.Concat(linkedTruckIds).Distinct().ToArray();
        var trucks = await _db.Trucks.AsNoTracking().Where(t => resolvedTruckIds.Contains(t.Id) && t.IsActive).ToDictionaryAsync(t => t.Id, ct);
        var wagons = await _db.Wagons.AsNoTracking().Where(w => wagonIds.Contains(w.Id) && w.IsActive).ToDictionaryAsync(w => w.Id, ct);
        var drivers = (await _db.Drivers.AsNoTracking().Where(d => driverIds.Contains(d.Id) && d.IsActive).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        var providers = (await _db.ServiceProviders.AsNoTracking().Where(p => providerIds.Contains(p.Id) && p.IsActive).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        var currencies = (await _db.Currencies.AsNoTracking().Where(c => currencyIds.Contains(c.Id) && c.IsActive).Select(c => c.Id).ToListAsync(ct)).ToHashSet();

        var seenVehicles = new HashSet<string>(StringComparer.Ordinal);
        var preparedVehicles = new List<PreparedVehicle>();
        for (var i = 0; i < vehicles.Count; i++)
        {
            var vehicle = vehicles[i];
            decimal capacity;
            string? wagonNumber = null;
            string vehicleKey;
            assets.TryGetValue(vehicle.OperationalAssetId.GetValueOrDefault(), out var selectedAsset);
            var assetCanBeVehicle = vehicle.CarrierType == CarrierType.OperationalAsset
                && selectedAsset is not null;

            if (assetCanBeVehicle
                && vehicle.TransportType == LoadingTransportType.Truck
                && !vehicle.TruckId.HasValue
                && selectedAsset!.LinkedTruckId.HasValue)
            {
                vehicle.TruckId = selectedAsset.LinkedTruckId;
            }

            if (vehicle.TransportType == LoadingTransportType.Truck)
            {
                trucks.TryGetValue(vehicle.TruckId.GetValueOrDefault(), out var truck);
                if (truck is null && !assetCanBeVehicle)
                {
                    throw Rule("INVENTORY_TRANSPORT_TRUCK_INVALID", $"موتر ردیف {i + 1} فعال یا معتبر نیست.");
                }
                if (vehicle.WagonId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_VEHICLE_CONFLICT", $"در ردیف {i + 1} فقط موتر باید انتخاب شود.");
                }
                // راننده اختیاری است؛ اگر انتخاب شد باید فعال/معتبر باشد.
                if (vehicle.DriverId.HasValue && !drivers.Contains(vehicle.DriverId.Value))
                {
                    throw Rule("INVENTORY_TRANSPORT_DRIVER_INVALID", $"راننده ردیف {i + 1} فعال یا معتبر نیست.");
                }
                capacity = assetCanBeVehicle
                    ? PositiveCapacity(selectedAsset!.CapacityMt)
                        ?? PositiveCapacity(truck?.MaxLoadMt)
                        ?? PositiveCapacity(vehicle.CapacityMt)
                        ?? 0m
                    : truck!.MaxLoadMt.GetValueOrDefault();
                wagonNumber = assetCanBeVehicle ? selectedAsset!.AssetCode : null;
                vehicleKey = assetCanBeVehicle ? $"A:{selectedAsset!.Id}" : $"T:{truck!.Id}";
            }
            else if (vehicle.TransportType == LoadingTransportType.Wagon)
            {
                wagons.TryGetValue(vehicle.WagonId.GetValueOrDefault(), out var wagon);
                if (wagon is null && !assetCanBeVehicle)
                {
                    throw Rule("INVENTORY_TRANSPORT_WAGON_INVALID", $"واگن ردیف {i + 1} فعال یا معتبر نیست.");
                }
                if (vehicle.TruckId.HasValue || vehicle.DriverId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_VEHICLE_CONFLICT", $"در ردیف {i + 1} فقط واگن باید انتخاب شود.");
                }
                capacity = assetCanBeVehicle
                    ? PositiveCapacity(selectedAsset!.CapacityMt)
                        ?? PositiveCapacity(wagon?.CapacityMt)
                        ?? PositiveCapacity(vehicle.CapacityMt)
                        ?? 0m
                    : wagon!.CapacityMt.GetValueOrDefault();
                wagonNumber = wagon?.WagonNumber ?? selectedAsset?.AssetCode;
                vehicleKey = assetCanBeVehicle ? $"A:{selectedAsset!.Id}" : $"W:{wagon!.Id}";
            }
            else
            {
                throw Rule("INVENTORY_TRANSPORT_VEHICLE_TYPE", $"نوع وسیله ردیف {i + 1} باید موتر یا واگن باشد.");
            }

            if (!seenVehicles.Add(vehicleKey))
            {
                throw Rule("INVENTORY_TRANSPORT_VEHICLE_DUPLICATE", "یک موتر یا واگن در این سند تکرار شده است.");
            }
            // Capacity is optional: when master data has a positive capacity we still
            // guard against overloading, but a missing/unknown capacity no longer blocks.
            if (capacity > 0m && vehicle.QuantityMt - capacity > Tolerance)
            {
                throw Rule("INVENTORY_TRANSPORT_CAPACITY_EXCEEDED", $"مقدار ردیف {i + 1} از ظرفیت وسیله بیشتر است.");
            }

            if (vehicle.CarrierType == CarrierType.ServiceProvider)
            {
                if (!vehicle.ServiceProviderId.HasValue || !providers.Contains(vehicle.ServiceProviderId.Value) || vehicle.OperationalAssetId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_PROVIDER_INVALID", $"شرکت خدماتی فعال ردیف {i + 1} را انتخاب و دارایی عملیاتی را خالی کنید.");
                }
            }
            else if (vehicle.CarrierType == CarrierType.OperationalAsset)
            {
                if (!vehicle.OperationalAssetId.HasValue || !assets.TryGetValue(vehicle.OperationalAssetId.Value, out var asset) || vehicle.ServiceProviderId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_ASSET_INVALID", $"دارایی عملیاتی فعال ردیف {i + 1} را انتخاب و شرکت خدماتی را خالی کنید.");
                }
                var validAssetType = vehicle.TransportType == LoadingTransportType.Truck
                    ? asset.AssetType is OperationalAssetType.Truck or OperationalAssetType.TankerTruck
                    : asset.AssetType == OperationalAssetType.Wagon;
                if (!validAssetType || (asset.LinkedTruckId.HasValue && asset.LinkedTruckId != vehicle.TruckId))
                {
                    throw Rule("INVENTORY_TRANSPORT_ASSET_VEHICLE", $"دارایی عملیاتی ردیف {i + 1} با وسیله انتخاب‌شده سازگار نیست.");
                }
            }
            else
            {
                throw Rule("INVENTORY_TRANSPORT_CARRIER_TYPE", $"نوع حمل‌کننده ردیف {i + 1} معتبر نیست.");
            }

            if (vehicle.FreightAmount.GetValueOrDefault() < 0m)
            {
                throw Rule("INVENTORY_TRANSPORT_FREIGHT_NEGATIVE", "کرایه نمی‌تواند منفی باشد.");
            }
            if (vehicle.FreightAmount.GetValueOrDefault() > 0m
                && (!vehicle.FreightCurrencyId.HasValue || !currencies.Contains(vehicle.FreightCurrencyId.Value)))
            {
                throw Rule("INVENTORY_TRANSPORT_CURRENCY_INVALID", $"واحد پول فعال کرایه ردیف {i + 1} را انتخاب کنید.");
            }

            var allocations = (vehicle.Allocations ?? []).Where(a => a.QuantityMt > 0m).ToList();
            if (allocations.Count == 0 || allocations.Select(a => a.SourceInventoryMovementId).Distinct().Count() != allocations.Count)
            {
                throw Rule("INVENTORY_TRANSPORT_ALLOCATION_REQUIRED", $"سهم منابع ردیف {i + 1} کامل یا یکتا نیست.");
            }
            if (allocations.Any(a => !selected.Any(s => s.SourceInventoryMovementId == a.SourceInventoryMovementId)))
            {
                throw Rule("INVENTORY_TRANSPORT_ALLOCATION_SOURCE", $"سهم ردیف {i + 1} به منبع انتخاب‌نشده وصل است.");
            }
            if (Math.Abs(allocations.Sum(a => a.QuantityMt) - vehicle.QuantityMt) > Tolerance)
            {
                throw Rule("INVENTORY_TRANSPORT_LEG_TOTAL", $"جمع سهم منابع ردیف {i + 1} باید برابر مقدار همان وسیله باشد.");
            }

            preparedVehicles.Add(new PreparedVehicle(vehicle, allocations, capacity, wagonNumber));
        }

        var selectedTotal = selected.Sum(s => s.QuantityMt.GetValueOrDefault());
        var vehicleTotal = vehicles.Sum(v => v.QuantityMt);
        if (Math.Abs(selectedTotal - vehicleTotal) > Tolerance)
        {
            throw Rule("INVENTORY_TRANSPORT_BATCH_TOTAL", "جمع موجودی انتخاب‌شده باید برابر جمع مقدار وسایط باشد.");
        }
        foreach (var source in selected)
        {
            var allocated = preparedVehicles.Sum(v => v.Allocations
                .Where(a => a.SourceInventoryMovementId == source.SourceInventoryMovementId)
                .Sum(a => a.QuantityMt));
            if (Math.Abs(allocated - source.QuantityMt.GetValueOrDefault()) > Tolerance)
            {
                throw Rule("INVENTORY_TRANSPORT_SOURCE_TOTAL", "جمع سهم وسایط از هر منبع باید برابر مقدار انتخاب‌شده همان منبع باشد.");
            }
        }

        return new PreparedBatch(
            availableById,
            preparedVehicles,
            decimal.Round(selectedTotal, 4, MidpointRounding.AwayFromZero));
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? PositiveCapacity(decimal? value)
        => value.GetValueOrDefault() > 0m ? value : null;

    private static BusinessRuleException Rule(string code, string message) => new(code, message);

    // تخصیص یک قرارداد خرید داخل محموله برای محاسبهٔ «بار روی کشتی» (فقط خواندنی).
    private sealed record VesselContractAllocation(int ContractId, string? ContractNumber, decimal AllocatedMt);

    private sealed record PreparedBatch(
        IReadOnlyDictionary<int, InventoryTransportSourceAvailabilityViewModel> Sources,
        IReadOnlyList<PreparedVehicle> Vehicles,
        decimal TotalQuantityMt);

    private sealed record PreparedVehicle(
        InventoryTransportVehicleInput Input,
        IReadOnlyList<InventoryTransportVehicleAllocationInput> Allocations,
        decimal CapacityMt,
        string? WagonNumber);
}

// نتیجهٔ استنتاج کشتی: یا کشتیِ مشخص، یا مبهم (به چند کشتی وصل می‌شود)، یا هیچ‌کدام (منبعِ غیرکشتی).
public sealed record ShipmentLinkInference(int? ShipmentId, bool IsAmbiguous);
