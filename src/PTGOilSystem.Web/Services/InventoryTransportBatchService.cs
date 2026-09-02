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
    private readonly IInventoryMovementWriter _movements;
    private readonly IInventoryLineageWriter _lineage;

    // Dual-write اختیاری به دفتر کل جدید — همان آداپتری که مسیر حمل تکی استفاده می‌کند.
    // پشت Feature Flag و null-safe؛ با ساختِ دستیِ سرویس، دقیقاً مثل قبل خاموش می‌ماند.
    private readonly Accounting.IInventoryTransferAccountingAdapter? _transferAccounting;

    // برگشتِ ژورنالِ مصارفِ وصل به حمل هنگام لغو. مثل بقیه قلاب‌های حسابداری اختیاری و null-safe
    // است؛ با ساختِ دستیِ سرویس (تست‌ها) دقیقاً مثل قبل خاموش می‌ماند.
    private readonly Accounting.IExpenseAccountingAdapter? _expenseAccounting;

    public InventoryTransportBatchService(
        ApplicationDbContext db,
        IStockService stock,
        IFormTokenGuard? formTokens = null,
        IInventoryMovementWriter? movements = null,
        IInventoryLineageWriter? lineage = null,
        Accounting.IInventoryTransferAccountingAdapter? transferAccounting = null,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null)
    {
        _db = db;
        _stock = stock;
        _formTokens = formTokens ?? new FormTokenGuard(db);
        _movements = movements ?? new InventoryMovementWriter(db, stock);
        _lineage = lineage ?? InventoryLineageWriterFactory.Disabled(db);
        _transferAccounting = transferAccounting;
        _expenseAccounting = expenseAccounting;
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
            .Where(a => a.SourceInventoryMovementId != null
                && sourceMovementIds.Contains(a.SourceInventoryMovementId.Value)
                && a.OutboundInventoryMovementId != null)
            .Select(a => new { SourceInventoryMovementId = a.SourceInventoryMovementId!.Value, a.QuantityMt })
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
            var prepared = await ValidateAndPrepareAsync(model, ct, enforceFifo: true);

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

            AddLegs(batch, model, prepared, vesselSentinelRemap, groupKey);

            _db.InventoryTransportBatches.Add(batch);
            _formTokens.Stamp(formToken, FormPurpose, nameof(InventoryTransportBatch));
            await _db.SaveChangesAsync(ct);
            var usageWriter = new AssetUsageChargeService(_db);
            foreach (var leg in batch.Legs)
            {
                var carrierParty = await usageWriter.ResolveCarrierPartyAsync(
                    leg.ServiceProviderId,
                    leg.DriverId,
                    leg.OperationalAssetId,
                    leg.LoadedDate,
                    ct);
                leg.CarrierPartyType = carrierParty?.PartyType;
                leg.CarrierPartyId = carrierParty?.PartyId;
                await usageWriter.SyncOperationAsync(leg, ct);
            }

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

    // ═════════════ نگهبانِ پایین‌دست و برگشتِ اثرِ یک سند حمل ═════════════
    //
    // ویرایش و لغو هر دو روی «سندی که بارش هنوز جای دیگری نرفته» کار می‌کنند. تفاوتشان فقط
    // در سخت‌گیری است: لغو legها را سرِ جا نگه می‌دارد و فقط اثرشان را برمی‌گرداند، ولی ویرایش
    // legها را فیزیکی حذف و از نو می‌سازد، پس هر سندی که به شناسهٔ leg چسبیده باشد (مصرف،
    // کرایهٔ دارایی) هم مانعِ ویرایش است — وگرنه آن سند به legِ مرده اشاره می‌کرد.

    /// <summary>
    /// عملیات پایین‌دستیِ ثبت‌شده روی legهای یک سند حمل. لیستِ خالی یعنی سند هنوز آزاد است.
    /// <paramref name="forEdit"/> نگهبان‌های اضافیِ ویرایش (بازساختِ leg) را هم اعمال می‌کند.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindDownstreamBlockersAsync(
        int batchId,
        bool forEdit,
        CancellationToken ct = default)
    {
        var legIds = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.InventoryTransportBatchId == batchId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        return await FindDownstreamBlockersForLegsAsync(legIds, forEdit, ct);
    }

    private async Task<IReadOnlyList<string>> FindDownstreamBlockersForLegsAsync(
        IReadOnlyCollection<int> legIds,
        bool forEdit,
        CancellationToken ct)
    {
        var blockers = new List<string>();
        if (legIds.Count == 0)
        {
            return blockers;
        }

        // بارِ این سند از وسیلهٔ دیگری آمده (زنجیرهٔ وسیله → وسیله). چنین سهمی هیچ حرکت موجودی
        // ندارد، پس برگرداندنِ خروجیِ مبدأ کارِ این سرویس نیست؛ موتورِ درست همان لغوِ زنجیره است
        // (TransportChainService از مسیر «لغو / برگشت») که رسیدهای والد را هم آزاد می‌کند.
        if (await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .AnyAsync(a => legIds.Contains(a.InventoryTransportLegId)
                && (a.SourceTransportLegId.HasValue || a.SourceTransportReceiptId.HasValue), ct))
        {
            blockers.Add("منبع این سند حملِ دیگری است؛ از «لغو / برگشت» همان مرحله استفاده کنید");
        }

        // رسیدِ مقصد: بار تحویل شده و کسرِ مبدأ دیگر تنها اثرِ این سند نیست.
        if (await _db.InventoryTransportReceipts
            .AsNoTracking()
            .AnyAsync(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled, ct))
        {
            blockers.Add("رسید تحویل ثبت شده است");
        }

        // فروش: چه مستقیم از همین حمل، چه از رسیدِ آن.
        if (await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .AnyAsync(a => (a.TransportLegId.HasValue && legIds.Contains(a.TransportLegId.Value))
                || (a.SourceTransportLegId.HasValue && legIds.Contains(a.SourceTransportLegId.Value)), ct))
        {
            blockers.Add("فروش ثبت‌شده دارد");
        }

        // مرحلهٔ بعدی حمل (انتقال وسیله → وسیله) که بارِ همین سند را برده است.
        if (await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .AnyAsync(a => a.SourceTransportLegId.HasValue
                && legIds.Contains(a.SourceTransportLegId.Value)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled, ct))
        {
            blockers.Add("مرحلهٔ بعدی حمل ثبت شده است");
        }

        if (await _db.LossEvents
            .AsNoTracking()
            .AnyAsync(l => l.TransportLegId.HasValue && legIds.Contains(l.TransportLegId.Value) && !l.IsCancelled, ct))
        {
            blockers.Add("کسری/ضایعات ثبت شده است");
        }

        if (await _db.CustomsDeclarations
            .AsNoTracking()
            .AnyAsync(c => c.TransportLegId.HasValue && legIds.Contains(c.TransportLegId.Value), ct))
        {
            blockers.Add("اظهار گمرکی دارد");
        }

        if (forEdit)
        {
            // ویرایش legها را دوباره می‌سازد و شناسه عوض می‌شود؛ این دو سند به شناسهٔ leg
            // چسبیده‌اند، پس اول باید خودشان لغو شوند. در لغو مانع نیستند، چون همان‌جا برگشت می‌خورند.
            if (await _db.ExpenseTransactions
                .AsNoTracking()
                .AnyAsync(e => e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value) && !e.IsCancelled, ct))
            {
                blockers.Add("مصرف ثبت‌شده دارد");
            }

            if (await _db.AssetRentTransactions
                .AsNoTracking()
                .AnyAsync(r => r.TransportLegId.HasValue && legIds.Contains(r.TransportLegId.Value), ct))
            {
                blockers.Add("کرایهٔ دارایی ثبت شده است");
            }
        }

        return blockers;
    }

    private static BusinessRuleException BlockedByDownstream(IReadOnlyList<string> blockers, string action)
        => new(
            "INVENTORY_TRANSPORT_BATCH_HAS_DOWNSTREAM",
            $"{action} این سند ممکن نیست: " + string.Join("، ", blockers) + ".");

    /// <summary>
    /// اثرِ بارگیریِ یک سند حمل را برمی‌گرداند: ژورنالِ انتقال، حرکت‌های خروجیِ موجودی و
    /// نسب‌نامه. legها و سهم‌ها دست‌نخورده می‌مانند — تصمیم دربارهٔ آن‌ها با فراخوان است.
    ///
    /// ترتیب عمداً همان <c>DispatchController.Cancel</c> است: اول برگشتِ حسابداری (تا آداپتر
    /// بتواند مالکیت را از همان روابط فعلی حل کند)، بعد برگشتِ حرکت موجودی. همه‌چیز داخل
    /// تراکنشِ فراخوان اجرا می‌شود و هر مرحله idempotent است.
    ///
    /// <para><paramref name="preserveOriginalDate"/> تاریخِ سندِ معکوس را تعیین می‌کند و فرقِ
    /// «اصلاح» با «لغو» است:</para>
    /// <list type="bullet">
    /// <item>ویرایش (<c>true</c>): معکوس با تاریخِ خودِ سندِ اصلی ثبت می‌شود. سندِ اصلاح‌شده هم
    /// همان تاریخ حمل را دارد، پس نگهبانِ موجودیِ «تا این تاریخ» باید کسرِ قبلی را برگشت‌خورده
    /// ببیند؛ وگرنه ثبتِ دوباره با «موجودی کافی نیست» رد می‌شود، در حالی که موجودی واقعاً آزاد است.</item>
    /// <item>لغو (<c>false</c>): معکوس با تاریخ امروز ثبت می‌شود — همان قاعدهٔ
    /// <c>DispatchController.Cancel</c>؛ کالا تا امروز واقعاً در راه بوده و تاریخچه بازنویسی نمی‌شود.</item>
    /// </list>
    /// </summary>
    private async Task ReverseBatchPostingsAsync(
        InventoryTransportBatch batch,
        bool preserveOriginalDate,
        CancellationToken ct)
    {
        var today = Time.AfghanistanBusinessClock.SystemToday;

        foreach (var leg in batch.Legs)
        {
            var movementIds = leg.Allocations
                .Where(a => a.OutboundInventoryMovementId.HasValue)
                .Select(a => a.OutboundInventoryMovementId!.Value)
                .Distinct()
                .ToList();
            if (movementIds.Count == 0)
            {
                continue;
            }

            if (_transferAccounting is not null)
            {
                await _transferAccounting.TryPostLegLoadReversalAsync(leg, ct);
            }

            var movements = await _db.InventoryMovements
                .AsNoTracking()
                .Where(m => movementIds.Contains(m.Id))
                .ToListAsync(ct);

            foreach (var movement in movements)
            {
                // Writer خودش idempotent است: اگر معکوسِ همین سند از قبل باشد، سند دوم نمی‌سازد.
                await _movements.PostReversalAsync(
                    movement,
                    preserveOriginalDate ? movement.MovementDate : today,
                    $"Reversal for InventoryTransportBatchId={batch.Id}, TransportLegId={leg.Id}",
                    ct);
            }

            await _lineage.OnLegLoadReversedAsync(leg, ct);

            // پیوندِ خروجی پاک می‌شود تا سند دیگر «بارگیری‌شده» شمرده نشود؛ خودِ حرکت‌ها و
            // معکوس‌هایشان به‌عنوان تاریخچهٔ مالی سرِ جا می‌مانند.
            foreach (var allocation in leg.Allocations)
            {
                allocation.OutboundInventoryMovementId = null;
            }
            leg.OutboundInventoryMovementId = null;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// «بار روی کشتی» هنگام ثبت به رسیدِ کشتی→ترمینال تبدیل شده و یک حرکت ورودی ساخته است.
    /// اگر خودِ سند لغو شود آن ورودی هم باید برگردد، وگرنه ترمینال موجودیِ بی‌صاحب نگه می‌دارد.
    /// فقط وقتی برگشت می‌خورد که هیچ سهمی بیرون از همین سند آن ورودی را مصرف نکرده باشد.
    /// </summary>
    private async Task ReverseVesselMaterializationAsync(InventoryTransportBatch batch, CancellationToken ct)
    {
        var batchLegIds = batch.Legs.Select(l => l.Id).ToList();
        var sourceMovementIds = batch.Legs
            .SelectMany(l => l.Allocations)
            .Where(a => a.SourceInventoryMovementId.HasValue)
            .Select(a => a.SourceInventoryMovementId!.Value)
            .Distinct()
            .ToList();
        if (sourceMovementIds.Count == 0)
        {
            return;
        }

        // ورودی‌هایی که خودِ همین گروه ساخته است — با همان قالبِ Reference که هنگام ثبت نوشته شد.
        var vesselLegIds = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.TransportGroupKey == batch.TransportGroupKey
                && l.InventoryTransportBatchId == null
                && l.Status != InventoryTransportLegStatus.Cancelled)
            .Select(l => l.Id)
            .ToListAsync(ct);
        if (vesselLegIds.Count == 0)
        {
            return;
        }

        var vesselReferences = vesselLegIds.Select(id => $"VESSEL-DIRECT-LEG:{id}").ToList();
        var inboundMovements = await _db.InventoryMovements
            .Where(m => m.ReferenceDocument != null
                && vesselReferences.Contains(m.ReferenceDocument)
                && m.Direction == MovementDirection.In
                && sourceMovementIds.Contains(m.Id))
            .ToListAsync(ct);
        if (inboundMovements.Count == 0)
        {
            return;
        }

        var inboundIds = inboundMovements.Select(m => m.Id).ToList();
        // حملِ لغوشدهٔ دیگری که قبلاً از همین بار برداشته بود، مصرف‌کننده نیست و نباید مانع شود.
        var consumedElsewhere = await _db.InventoryTransportLegAllocations
            .AsNoTracking()
            .AnyAsync(a => a.SourceInventoryMovementId.HasValue
                && inboundIds.Contains(a.SourceInventoryMovementId.Value)
                && !batchLegIds.Contains(a.InventoryTransportLegId)
                && a.InventoryTransportLeg != null
                && a.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled, ct);
        if (consumedElsewhere)
        {
            throw Rule(
                "INVENTORY_TRANSPORT_VESSEL_SOURCE_CONSUMED",
                "بارِ تخلیه‌شدهٔ کشتیِ این سند در حمل دیگری هم استفاده شده است؛ ابتدا آن حمل را لغو کنید.");
        }

        var reversalDate = Time.AfghanistanBusinessClock.SystemToday;
        foreach (var movement in inboundMovements)
        {
            await _movements.PostReversalAsync(
                movement,
                reversalDate,
                $"Reversal for cancelled InventoryTransportBatchId={batch.Id}",
                ct);
        }

        // رسید و legِ تخلیهٔ کشتی هم باید کنار بروند تا «تخلیه‌شدهٔ» محموله دوباره پایین بیاید.
        var vesselReceipts = await _db.InventoryTransportReceipts
            .Where(r => vesselLegIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .ToListAsync(ct);
        foreach (var receipt in vesselReceipts)
        {
            receipt.IsCancelled = true;
            receipt.UpdatedAtUtc = DateTime.UtcNow;
        }

        var vesselLegs = await _db.InventoryTransportLegs
            .Where(l => vesselLegIds.Contains(l.Id))
            .ToListAsync(ct);
        foreach (var vesselLeg in vesselLegs)
        {
            vesselLeg.Status = InventoryTransportLegStatus.Cancelled;
            vesselLeg.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// لغو کاملِ یک سند حمل: موجودیِ کسرشده برمی‌گردد، ژورنالِ انتقال و مصارفِ وصل به حمل
    /// معکوس می‌شوند و وضعیت سند و همهٔ legها Cancelled می‌شود. اگر عملیات بعدی ثبت شده باشد
    /// لغو رد می‌شود. کل کار داخل یک تراکنش است و اجرای دوباره سند تازه‌ای نمی‌سازد.
    /// </summary>
    public async Task<InventoryTransportBatch> CancelAsync(int batchId, CancellationToken ct = default)
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

            // لغو دوباره هیچ سندی نمی‌سازد و خطا هم نیست؛ همان حالتِ خواسته‌شده از قبل برقرار است.
            if (batch.Status == InventoryTransportBatchStatus.Cancelled)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(ct);
                }
                return batch;
            }

            var blockers = await FindDownstreamBlockersForLegsAsync(
                batch.Legs.Select(l => l.Id).ToList(), forEdit: false, ct);
            if (blockers.Count > 0)
            {
                throw BlockedByDownstream(blockers, "لغو");
            }

            await ReverseBatchPostingsAsync(batch, preserveOriginalDate: false, ct);
            await ReverseVesselMaterializationAsync(batch, ct);
            await CancelLegExpensesAsync(batch, ct);

            foreach (var leg in batch.Legs)
            {
                leg.Status = InventoryTransportLegStatus.Cancelled;
                leg.UpdatedAtUtc = DateTime.UtcNow;
            }
            batch.Status = InventoryTransportBatchStatus.Cancelled;
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // سطر مصرفِ دارایی بعد از Cancelled شدن sync می‌شود تا IsReversed درست بنشیند.
            var usageWriter = new AssetUsageChargeService(_db);
            foreach (var leg in batch.Legs)
            {
                await usageWriter.SyncOperationAsync(leg, ct);
            }

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

    // مصارفِ وصل به legهای این سند (کرایه و هر مصرف دستی) با همان نویسندهٔ مشترکِ لغو مصرف
    // برگشت می‌خورند: ژورنال معکوس، سطر لجرِ معکوس و IsCancelled. خودش idempotent است.
    private async Task CancelLegExpensesAsync(InventoryTransportBatch batch, CancellationToken ct)
    {
        var legIds = batch.Legs.Select(l => l.Id).ToList();
        if (legIds.Count == 0)
        {
            return;
        }

        var expenses = await _db.ExpenseTransactions
            .Where(e => e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value) && !e.IsCancelled)
            .ToListAsync(ct);

        foreach (var expense in expenses)
        {
            await DispatchFreightExpenseSync.CancelExpenseAsync(_db, expense, _expenseAccounting);
        }
    }

    /// <summary>
    /// ویرایش یک سند حمل: legها و سهم‌های منبع دوباره از روی همان اعتبارسنجی ثبت جدید ساخته
    /// می‌شوند. سندِ بارگیری‌شده هم قابل ویرایش است، به شرطی که هنوز هیچ عملیات پایین‌دستی
    /// (رسید، فروش، مرحلهٔ بعدی، کسری، گمرک، مصرف، کرایهٔ دارایی) روی آن ثبت نشده باشد.
    ///
    /// برای سندِ بارگیری‌شده اول اثرِ قبلی کامل برگردانده می‌شود (ژورنال انتقال، خروجی موجودی،
    /// نسب‌نامه) و بعد سند از نو ساخته و ــ در صورت انتخابِ حالت بارگیری ــ دوباره ثبت می‌شود.
    /// همه‌چیز داخل یک تراکنش است؛ خطا یعنی هیچ‌کدام از دو طرف اعمال نشده.
    /// </summary>
    public async Task<InventoryTransportBatch> UpdateDraftAsync(
        int batchId,
        InventoryTransportFromInventoryViewModel model,
        CancellationToken ct = default)
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

            // سندِ لغوشده دیگر برنمی‌گردد؛ اصلاح یعنی ثبت سند تازه، نه زنده‌کردن سند مرده.
            if (batch.Status == InventoryTransportBatchStatus.Cancelled
                || batch.Legs.Any(l => l.Status == InventoryTransportLegStatus.Cancelled))
            {
                throw Rule(
                    "INVENTORY_TRANSPORT_BATCH_NOT_EDITABLE",
                    "این سند لغو شده است و دیگر قابل ویرایش نیست.");
            }

            var legIds = batch.Legs.Select(l => l.Id).ToList();
            var blockers = await FindDownstreamBlockersForLegsAsync(legIds, forEdit: true, ct);
            if (blockers.Count > 0)
            {
                throw BlockedByDownstream(blockers, "ویرایش");
            }

            // اثرِ بارگیریِ قبلی پیش از بازساختِ legها برگردانده می‌شود: آداپتر حسابداری مالکیت
            // را از همان سهم‌های فعلی می‌خواند، پس بعد از حذفشان دیگر قابل حل نبود. برای سندِ
            // پیش‌نویس این مرحله هیچ کاری نمی‌کند (هیچ خروجی‌ای وجود ندارد).
            await ReverseBatchPostingsAsync(batch, preserveOriginalDate: true, ct);

            // سطر مصرفِ دارایی به شناسهٔ leg بسته است و legها همین حالا حذف می‌شوند؛ برگشتی
            // علامت می‌خورد تا استفادهٔ باطل‌شده در محاسبهٔ کرایه/استهلاک نماند.
            await new AssetUsageChargeService(_db).MarkLegUsagesReversedAsync(legIds, ct);

            await ResolveTypedVehiclesAsync(model, ct);
            var prepared = await ValidateAndPrepareAsync(model, ct, enforceFifo: true);

            // همان قاعدهٔ ثبت جدید: اگر کشتی صریح داده نشده، از حرکت‌های موجودیِ منبع استنتاج می‌شود.
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

            foreach (var leg in batch.Legs)
            {
                _db.InventoryTransportLegAllocations.RemoveRange(leg.Allocations);
            }
            _db.InventoryTransportLegs.RemoveRange(batch.Legs);
            batch.Legs.Clear();

            batch.SourceTerminalId = model.SourceTerminalId;
            batch.SourceStorageTankId = model.SourceStorageTankId > 0 ? model.SourceStorageTankId : null;
            batch.ProductId = model.ProductId;
            batch.TotalQuantityMt = prepared.TotalQuantityMt;
            batch.TransportDate = model.TransportDate.Date;
            batch.Notes = Normalize(model.Notes);
            batch.Status = model.SubmissionMode == InventoryTransportSubmissionMode.Loaded
                ? InventoryTransportBatchStatus.Loaded
                : InventoryTransportBatchStatus.Draft;
            batch.UpdatedAtUtc = DateTime.UtcNow;

            // کلید گروه ثابت می‌ماند تا لینک‌ها و صفحهٔ جریان همان سند بمانند.
            var vesselSentinelRemap = await MaterializeVesselSourcesAsync(model, prepared, batch.TransportGroupKey, ct);
            AddLegs(batch, model, prepared, vesselSentinelRemap, batch.TransportGroupKey);

            await _db.SaveChangesAsync(ct);
            var usageWriter = new AssetUsageChargeService(_db);
            foreach (var leg in batch.Legs)
            {
                var carrierParty = await usageWriter.ResolveCarrierPartyAsync(
                    leg.ServiceProviderId,
                    leg.DriverId,
                    leg.OperationalAssetId,
                    leg.LoadedDate,
                    ct);
                leg.CarrierPartyType = carrierParty?.PartyType;
                leg.CarrierPartyId = carrierParty?.PartyId;
                await usageWriter.SyncOperationAsync(leg, ct);
            }

            if (model.SubmissionMode == InventoryTransportSubmissionMode.Loaded)
            {
                await CreateOutboundMovementsAsync(batch, ct);
                await _db.SaveChangesAsync(ct);
            }

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

    // ساخت leg‌های یک سند حمل از روی نتیجهٔ اعتبارسنجی. هم در ثبت جدید و هم در ویرایش
    // پیش‌نویس صدا زده می‌شود تا قاعدهٔ ساخت leg و سهم منبع فقط یک جا باشد.
    private static void AddLegs(
        InventoryTransportBatch batch,
        InventoryTransportFromInventoryViewModel model,
        PreparedBatch prepared,
        IReadOnlyDictionary<int, int> vesselSentinelRemap,
        string groupKey)
    {
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
                VesselId = vehicle.Input.VesselId,
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
                    // فقط سهم‌های منبع‌مخزنی؛ سهم‌های وسیله‌به‌وسیله سند موجودی ندارند.
                    .Where(a => a.SourceInventoryMovementId != null)
                    .GroupBy(a => a.SourceInventoryMovementId!.Value)
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
                    Allocations = l.Allocations
                        .Where(a => a.SourceInventoryMovementId != null)
                        .Select(a => new InventoryTransportVehicleAllocationInput
                        {
                            SourceInventoryMovementId = a.SourceInventoryMovementId!.Value,
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
            .Where(a => a.SourceInventoryMovementId != null)
            .Select(a => a.SourceInventoryMovementId!.Value)
            .Distinct()
            .ToList();
        var sourceLocations = await _db.InventoryMovements.AsNoTracking()
            .Where(m => sourceMovementIds.Contains(m.Id))
            .Select(m => new { m.Id, m.TerminalId, m.StorageTankId })
            .ToDictionaryAsync(m => m.Id, m => (m.TerminalId, m.StorageTankId), ct);

        foreach (var leg in batch.Legs)
        {
            var legMovements = new List<InventoryMovement>(leg.Allocations.Count);
            foreach (var allocation in leg.Allocations)
            {
                var location = allocation.SourceInventoryMovementId is { } sourceMovementId
                    && sourceLocations.TryGetValue(sourceMovementId, out var loc)
                        ? loc
                        : (batch.SourceTerminalId, batch.SourceStorageTankId);
                // قفل هم‌زمانی روی مخزن/کالا پیش از چک موجودی، و هر دو داخل تراکنشِ caller.
                var movement = await _movements.PostOutboundAsync(
                    new InventoryMovementRequest
                    {
                        ProductId = batch.ProductId,
                        ContractId = allocation.SourcePurchaseContractId,
                        TerminalId = location.Item1,
                        StorageTankId = location.Item2,
                        MovementDate = batch.TransportDate,
                        QuantityMt = allocation.QuantityMt,
                        ReferenceDocument = $"TRANSPORT-ALLOCATION:{allocation.Id}",
                        Notes = $"Inventory transport batch {batch.BatchNumber}, leg {leg.Id}"
                    },
                    StockGuard.Standard,
                    ct);
                allocation.OutboundInventoryMovementId = movement.Id;
                legMovements.Add(movement);
            }

            if (leg.Allocations.Count == 1)
            {
                leg.OutboundInventoryMovementId = leg.Allocations.Single().OutboundInventoryMovementId;
            }
            leg.Status = InventoryTransportLegStatus.Loaded;
            leg.UpdatedAtUtc = DateTime.UtcNow;

            // همان قلاب‌هایی که مسیر حمل تکی بعد از بارگیری اجرا می‌کند
            // (InventoryTransportLegLoadService.LoadAsync). داخل همان تراکنشِ caller اجرا
            // می‌شوند، هر دو پشت Feature Flag و هر دو idempotent با کلیدِ leg.Id — پس
            // بارگیریِ دوباره یا retry سند تکراری نمی‌سازد.
            if (_transferAccounting is not null)
            {
                await _transferAccounting.TryPostLegLoadAsync(leg, ct);
            }

            await _lineage.OnLegLoadedAsync(leg, legMovements, ct);
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

            var inboundMovement = await _movements.PostInboundAsync(
                new InventoryMovementRequest
                {
                    ProductId = model.ProductId,
                    ContractId = source.SourcePurchaseContractId,
                    TerminalId = model.SourceTerminalId,
                    StorageTankId = null,
                    MovementDate = model.TransportDate.Date,
                    QuantityMt = quantity,
                    ReferenceDocument = $"VESSEL-DIRECT-LEG:{vesselReceiptLeg.Id}",
                    Notes = "Direct-from-vessel discharge (no tank)"
                },
                ct);

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

    private static string? NormalizeName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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
        var createdDrivers = new List<(InventoryTransportVehicleInput Vehicle, Driver Driver)>();
        var driverCache = new Dictionary<string, Driver>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in vehicles)
        {
            // نام راننده تایپ می‌شود: راننده فعالِ هم‌نام استفاده و در نبودش پروفایل جدید ساخته می‌شود.
            // پیش از شرط دارایی عملیاتی است، چون آن حالت هم برای موتر راننده می‌خواهد.
            if (vehicle.TransportType == LoadingTransportType.Truck)
            {
                var driverName = NormalizeName(vehicle.DriverNameInput);
                if (driverName is null)
                {
                    vehicle.DriverId = null;
                }
                else
                {
                    if (!driverCache.TryGetValue(driverName, out var driver))
                    {
                        driver = await _db.Drivers.FirstOrDefaultAsync(d => d.FullName == driverName, ct);
                        if (driver is not null && !driver.IsActive)
                        {
                            throw Rule("INVENTORY_TRANSPORT_DRIVER_INACTIVE", $"راننده «{driverName}» قبلاً غیرفعال ثبت شده است؛ ابتدا آن را در داده‌های پایه فعال کنید.");
                        }
                        if (driver is null)
                        {
                            driver = new Driver { FullName = driverName, IsActive = true };
                            _db.Drivers.Add(driver);
                        }
                        driverCache[driverName] = driver;
                    }

                    if (driver.Id > 0)
                    {
                        vehicle.DriverId = driver.Id;
                    }
                    else
                    {
                        vehicle.DriverId = null;
                        createdDrivers.Add((vehicle, driver));
                    }
                }
            }
            else
            {
                vehicle.DriverId = null;
            }

            // فقط دارایی عملیاتی وسیله‌اش را از خودِ دارایی می‌گیرد؛ شرکت خدماتی و
            // حمل‌کنندهٔ شخصی نمبر وسیله را تایپ می‌کنند.
            if (vehicle.CarrierType == CarrierType.OperationalAsset)
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
                    vehicle.VesselId = null;
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
                vehicle.VesselId = null;
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
                vehicle.VesselId = null;
            }
            else if (vehicle.TransportType == LoadingTransportType.Vessel)
            {
                vehicle.TruckId = null;
                vehicle.WagonId = null;
                vehicle.DriverId = null;
            }
        }

        if (createdTrucks.Count > 0 || createdWagons.Count > 0 || createdDrivers.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            foreach (var (vehicle, driver) in createdDrivers)
            {
                vehicle.DriverId = driver.Id;
            }
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

    // enforceFifo فقط برای ثبت/ویرایشِ فرم روشن است. LoadDraftAsync دادهٔ ذخیره‌شده را دوباره
    // اعتبارسنجی می‌کند (برای کفایت موجودی)، نه شکلِ فرم را؛ اگر آنجا هم اجرا شود، پیش‌نویسی که
    // موجودیِ منابعش از زمان ثبت جابه‌جا شده دیگر قابل بارگیری نمی‌ماند.
    private async Task<PreparedBatch> ValidateAndPrepareAsync(
        InventoryTransportFromInventoryViewModel model,
        CancellationToken ct,
        bool enforceFifo = false)
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
            throw Rule("INVENTORY_TRANSPORT_VEHICLE_REQUIRED", "حداقل یک وسیلهٔ حمل وارد کنید.");
        }

        var truckIds = vehicles.Where(v => v.TruckId.HasValue).Select(v => v.TruckId!.Value).Distinct().ToArray();
        var wagonIds = vehicles.Where(v => v.WagonId.HasValue).Select(v => v.WagonId!.Value).Distinct().ToArray();
        var vesselIds = vehicles.Where(v => v.VesselId.HasValue).Select(v => v.VesselId!.Value).Distinct().ToArray();
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
        var vessels = await _db.Vessels.AsNoTracking().Where(v => vesselIds.Contains(v.Id) && v.IsActive).ToDictionaryAsync(v => v.Id, ct);
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
                if (vehicle.WagonId.HasValue || vehicle.VesselId.HasValue)
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
                if (vehicle.TruckId.HasValue || vehicle.VesselId.HasValue || vehicle.DriverId.HasValue)
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
            else if (vehicle.TransportType == LoadingTransportType.Vessel)
            {
                vessels.TryGetValue(vehicle.VesselId.GetValueOrDefault(), out var vessel);
                if (vessel is null)
                {
                    throw Rule("INVENTORY_TRANSPORT_VESSEL_INVALID", $"کشتی ردیف {i + 1} فعال یا معتبر نیست.");
                }
                if (vehicle.TruckId.HasValue || vehicle.WagonId.HasValue || vehicle.DriverId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_VEHICLE_CONFLICT", $"در ردیف {i + 1} فقط کشتی باید انتخاب شود.");
                }
                if (vehicle.CarrierType != CarrierType.ServiceProvider || vehicle.OperationalAssetId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_VESSEL_CARRIER", $"برای کشتی ردیف {i + 1} شرکت خدماتی را انتخاب کنید.");
                }
                capacity = PositiveCapacity(vehicle.CapacityMt) ?? 0m;
                wagonNumber = null;
                vehicleKey = $"V:{vessel.Id}";
            }
            else
            {
                throw Rule("INVENTORY_TRANSPORT_VEHICLE_TYPE", $"نوع وسیله ردیف {i + 1} باید موتر، واگن یا کشتی باشد.");
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

            // حمل‌کننده باید دقیقاً یکی از سه حالت معتبر باشد: شرکت خدماتی، دارایی عملیاتی،
            // یا حمل‌کنندهٔ شخصی. ثبت بدون هیچ‌کدام ممنوع است تا leg بدون طرفِ حمل ساخته نشود.
            if (vehicle.CarrierType == CarrierType.ServiceProvider)
            {
                if (!vehicle.ServiceProviderId.HasValue || !providers.Contains(vehicle.ServiceProviderId.Value) || vehicle.OperationalAssetId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_PROVIDER_INVALID", $"شرکت خدماتی فعال ردیف {i + 1} را انتخاب و دارایی عملیاتی را خالی کنید.");
                }
            }
            else if (vehicle.CarrierType == CarrierType.PersonalCarrier)
            {
                // حمل‌کنندهٔ شخصی فقط با موتر و فقط با رانندهٔ مشخص معنا دارد؛
                // واگن و کشتی راننده ندارند پس طرفِ حمل قابل شناسایی نمی‌ماند.
                if (vehicle.ServiceProviderId.HasValue || vehicle.OperationalAssetId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_PERSONAL_CARRIER_CONFLICT", $"برای حمل‌کنندهٔ شخصی ردیف {i + 1} نباید شرکت خدماتی یا دارایی عملیاتی انتخاب شود.");
                }
                if (vehicle.TransportType != LoadingTransportType.Truck)
                {
                    throw Rule("INVENTORY_TRANSPORT_PERSONAL_CARRIER_VEHICLE", $"حمل‌کنندهٔ شخصی ردیف {i + 1} فقط برای حمل با موتر قابل ثبت است.");
                }
                if (!vehicle.DriverId.HasValue || !drivers.Contains(vehicle.DriverId.Value))
                {
                    throw Rule("INVENTORY_TRANSPORT_PERSONAL_CARRIER_DRIVER", $"برای حمل‌کنندهٔ شخصی ردیف {i + 1} رانندهٔ فعال را انتخاب کنید.");
                }
            }
            else if (vehicle.CarrierType == CarrierType.OperationalAsset)
            {
                if (!vehicle.OperationalAssetId.HasValue || !assets.TryGetValue(vehicle.OperationalAssetId.Value, out var asset) || vehicle.ServiceProviderId.HasValue)
                {
                    throw Rule("INVENTORY_TRANSPORT_ASSET_INVALID", $"دارایی عملیاتی فعال ردیف {i + 1} را انتخاب و شرکت خدماتی را خالی کنید.");
                }
                var validAssetType = vehicle.TransportType switch
                {
                    LoadingTransportType.Truck => asset.AssetType is OperationalAssetType.Truck or OperationalAssetType.TankerTruck,
                    LoadingTransportType.Wagon => asset.AssetType == OperationalAssetType.Wagon,
                    _ => false
                };
                if (!validAssetType || (asset.LinkedTruckId.HasValue && asset.LinkedTruckId != vehicle.TruckId))
                {
                    throw Rule("INVENTORY_TRANSPORT_ASSET_VEHICLE", $"دارایی عملیاتی ردیف {i + 1} با وسیله انتخاب‌شده سازگار نیست.");
                }
                // دارایی عملیاتی در حمل با موتر باید رانندهٔ مشخص داشته باشد؛ واگن راننده نمی‌گیرد.
                if (vehicle.TransportType == LoadingTransportType.Truck
                    && (!vehicle.DriverId.HasValue || !drivers.Contains(vehicle.DriverId.Value)))
                {
                    throw Rule("INVENTORY_TRANSPORT_ASSET_DRIVER_REQUIRED", $"برای دارایی عملیاتی ردیف {i + 1} رانندهٔ فعال را انتخاب کنید.");
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

        if (enforceFifo)
        {
            EnsureFifoAllocation(availableSources, selected, preparedVehicles);
        }

        return new PreparedBatch(
            availableById,
            preparedVehicles,
            decimal.Round(selectedTotal, 4, MidpointRounding.AwayFromZero));
    }

    // فرمِ حمل توزیع سهم‌ها را خودش نمی‌پرسد؛ آن را می‌سازد. تابع autoAllocate در
    // wwwroot/js/inventory-transport-form.js جمعِ بارِ هر وسیله را به‌ترتیب بین منابعِ
    // تیک‌خورده تقسیم می‌کند: وسیله‌ها به ترتیبِ ردیفِ جدول، و برای هر وسیله منابع به ترتیبِ
    // همان فهرستی که GetAvailableSourcesAsync برگردانده — یعنی قدیمی‌ترین ورودی اول
    // (OrderBy MovementDate, ThenBy Id). سقفِ هر منبع، «قابل حمل»ِ همان فهرست است.
    //
    // اینجا همان محاسبه در سرور تکرار و با توزیعِ رسیده مقایسه می‌شود، تا کلاینت نتواند
    // توزیعی بفرستد که جمع‌هایش درست است ولی ترتیب مصرف موجودی را رعایت نکرده.
    private static void EnsureFifoAllocation(
        IReadOnlyList<InventoryTransportSourceAvailabilityViewModel> availableSources,
        IReadOnlyList<InventoryTransportSourceSelectionInput> selected,
        IReadOnlyList<PreparedVehicle> preparedVehicles)
    {
        var selectedIds = selected.Select(s => s.SourceInventoryMovementId).ToHashSet();

        // ترتیبِ حوض = ترتیبِ فهرستِ موجودی = همان چیزی که کاربر در جدول می‌بیند.
        var pool = availableSources
            .Where(s => selectedIds.Contains(s.SourceInventoryMovementId))
            .Select(s => new FifoPoolEntry(s.SourceInventoryMovementId, s.AvailableQuantityMt))
            .ToList();
        if (pool.Count == 0)
        {
            return;
        }

        for (var i = 0; i < preparedVehicles.Count; i++)
        {
            var vehicle = preparedVehicles[i];
            var expected = TakeFifoShares(pool, vehicle.Input.QuantityMt);

            if (!FifoSharesMatch(expected, vehicle.Allocations))
            {
                throw Rule(
                    "INVENTORY_TRANSPORT_ALLOCATION_NOT_FIFO",
                    $"تقسیم سهم منابع ردیف {i + 1} با ترتیب مصرف موجودی (قدیمی‌ترین منبع اول) نمی‌خواند. صفحه را تازه کنید و دوباره ثبت نمایید.");
            }
        }
    }

    // یک وسیله را از حوضِ FIFO پر می‌کند و مصرفِ حوض را جلو می‌برد (پس وسیلهٔ بعدی از
    // باقیماندهٔ همان حوض برمی‌دارد). تک‌منبعِ توزیعِ FIFO: هم برای اعتبارسنجیِ ثبت/ویرایش و
    // هم برای آماده‌سازیِ فرمِ ویرایش استفاده می‌شود تا هر دو دقیقاً یک قاعده باشند.
    private static Dictionary<int, decimal> TakeFifoShares(List<FifoPoolEntry> pool, decimal needed)
    {
        var shares = new Dictionary<int, decimal>();
        foreach (var entry in pool)
        {
            if (needed <= Tolerance)
            {
                break;
            }
            var free = entry.AvailableQuantityMt - entry.ConsumedQuantityMt;
            if (free <= Tolerance)
            {
                continue;
            }
            var share = Math.Min(needed, free);
            shares[entry.SourceInventoryMovementId] = share;
            entry.ConsumedQuantityMt += share;
            needed -= share;
        }
        return shares;
    }

    // آماده‌سازیِ فرمِ ویرایشِ یک پیش‌نویس: توزیعِ ذخیره‌شده با «قابل حملِ امروز» بازسازی می‌شود.
    // پیش‌نویس موجودی را رزرو نمی‌کند، پس اگر بعد از ثبت، موجودیِ منابع جابه‌جا شده باشد،
    // توزیعِ ذخیره‌شده دیگر با FIFO جاری نمی‌خواند و ذخیرهٔ بدون تغییر با
    // INVENTORY_TRANSPORT_ALLOCATION_NOT_FIFO رد می‌شد.
    //
    // فقط توزیعِ سهم‌ها عوض می‌شود: مقدار هر وسیله و مجموعهٔ منابعِ انتخاب‌شدهٔ کاربر دست‌نخورده
    // می‌ماند و هیچ چیزی در دیتابیس نوشته نمی‌شود (فقط ViewModel). اگر موجودیِ منابعِ انتخابی
    // کمتر از جمع وسایط باشد، سهم‌ها ناقص می‌مانند و همان اعتبارسنجی‌های موجود جلوی ذخیره را
    // می‌گیرند — یعنی کمبود موجودی همچنان رد می‌شود.
    public static void ApplyCurrentFifoAllocations(
        InventoryTransportFromInventoryViewModel model,
        IReadOnlyList<InventoryTransportSourceAvailabilityViewModel> availableSources)
    {
        var selectedIds = (model.Sources ?? [])
            .Where(s => s.QuantityMt.GetValueOrDefault() > Tolerance)
            .Select(s => s.SourceInventoryMovementId)
            .ToHashSet();
        if (selectedIds.Count == 0)
        {
            return;
        }

        // همان ترتیبِ حوضِ EnsureFifoAllocation: ترتیبِ فهرستِ موجودی (قدیمی‌ترین ورودی اول).
        var pool = availableSources
            .Where(s => selectedIds.Contains(s.SourceInventoryMovementId))
            .Select(s => new FifoPoolEntry(s.SourceInventoryMovementId, s.AvailableQuantityMt))
            .ToList();
        if (pool.Count == 0)
        {
            return;
        }

        foreach (var vehicle in model.Vehicles ?? [])
        {
            var shares = vehicle.QuantityMt > Tolerance
                ? TakeFifoShares(pool, vehicle.QuantityMt)
                : [];
            vehicle.Allocations = shares
                .Where(x => x.Value > Tolerance)
                .Select(x => new InventoryTransportVehicleAllocationInput
                {
                    SourceInventoryMovementId = x.Key,
                    QuantityMt = decimal.Round(x.Value, 4, MidpointRounding.AwayFromZero)
                })
                .ToList();
        }

        var consumedBySource = pool.ToDictionary(p => p.SourceInventoryMovementId, p => p.ConsumedQuantityMt);
        foreach (var source in model.Sources ?? [])
        {
            var consumed = consumedBySource.GetValueOrDefault(source.SourceInventoryMovementId);
            source.QuantityMt = consumed > Tolerance
                ? decimal.Round(consumed, 4, MidpointRounding.AwayFromZero)
                : null;
        }
    }

    // مقایسه با همان روادارییِ مقداریِ بقیهٔ اعتبارسنجی‌ها؛ سهم‌های تقریباً صفر شمرده نمی‌شوند
    // چون فرم هم آن‌ها را اصلاً نمی‌فرستد.
    private static bool FifoSharesMatch(
        IReadOnlyDictionary<int, decimal> expected,
        IReadOnlyList<InventoryTransportVehicleAllocationInput> submitted)
    {
        var expectedShares = expected
            .Where(x => x.Value > Tolerance)
            .ToDictionary(x => x.Key, x => x.Value);
        var submittedShares = submitted
            .Where(a => a.QuantityMt > Tolerance)
            .ToDictionary(a => a.SourceInventoryMovementId, a => a.QuantityMt);

        if (expectedShares.Count != submittedShares.Count)
        {
            return false;
        }

        foreach (var (sourceId, quantityMt) in expectedShares)
        {
            if (!submittedShares.TryGetValue(sourceId, out var actual)
                || Math.Abs(actual - quantityMt) > Tolerance)
            {
                return false;
            }
        }

        return true;
    }

    // ردیفِ حوضِ FIFO. قابل تغییر است چون مصرفِ هر وسیله روی وسیلهٔ بعدی اثر می‌گذارد.
    private sealed class FifoPoolEntry(int sourceInventoryMovementId, decimal availableQuantityMt)
    {
        public int SourceInventoryMovementId { get; } = sourceInventoryMovementId;
        public decimal AvailableQuantityMt { get; } = availableQuantityMt;
        public decimal ConsumedQuantityMt { get; set; }
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
