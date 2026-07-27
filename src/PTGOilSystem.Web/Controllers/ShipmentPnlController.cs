using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.LossEvents;
using PTGOilSystem.Web.Models.ShipmentPnl;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ShipmentPnlController : Controller
{
    private const int IndexPageSize = 5;
    private const string FooterTotalsCacheKey = "ShipmentPnl.Index.FooterTotals";
    private static readonly TimeSpan FooterTotalsCacheTtl = TimeSpan.FromSeconds(60);
    private readonly ApplicationDbContext _db;
    private readonly InventoryLineagePnlService _lineagePnl;
    private readonly LineageOptions _lineageOptions;
    private readonly IMemoryCache? _cache;

    public ShipmentPnlController(
        ApplicationDbContext db,
        InventoryLineagePnlService? lineagePnl = null,
        IOptions<LineageOptions>? lineageOptions = null,
        IMemoryCache? cache = null)
    {
        _db = db;
        _lineagePnl = lineagePnl ?? new InventoryLineagePnlService(db);
        _lineageOptions = lineageOptions?.Value ?? new LineageOptions();
        _cache = cache;
    }

    public async Task<IActionResult> Index(int page = 1)
    {
        var shipmentQuery = _db.Shipments.AsNoTracking();
        var totalCount = await shipmentQuery.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)IndexPageSize));
        page = Math.Clamp(page, 1, pageCount);

        var shipmentIds = await shipmentQuery
            .OrderByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * IndexPageSize)
            .Take(IndexPageSize)
            .Select(s => s.Id)
            .ToListAsync();

        var items = await BuildIndexItemsAsync(shipmentIds);

        // مجموع کلِ مقدار و نتیجهٔ مالی روی همهٔ محموله‌ها (نه فقط این صفحه) برای ردیف جمع در انتهای لیست.
        // محاسبهٔ این جمع، P&L همهٔ محموله‌ها را می‌سازد و سنگین است؛ برای همین با کش کوتاه‌مدت
        // نگه داشته می‌شود تا جابه‌جایی بین صفحات لیست فوری باشد. منطق مالی تغییری نمی‌کند.
        var totals = await GetFooterTotalsAsync();
        ViewBag.SumQuantity = totals.SumQuantity;
        ViewBag.SumGrossMargin = totals.SumGrossMargin;
        ViewBag.SumRelatedSales = totals.SumRelatedSales;

        return View(new ShipmentPnlIndexViewModel
        {
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    private async Task<(decimal SumQuantity, decimal SumGrossMargin, int SumRelatedSales)> GetFooterTotalsAsync()
    {
        if (_cache != null && _cache.TryGetValue(FooterTotalsCacheKey, out (decimal SumQuantity, decimal SumGrossMargin, int SumRelatedSales) cached))
        {
            return cached;
        }

        var allItems = await BuildAllIndexItemsAsync();
        var totals = (
            allItems.Sum(i => i.QuantityMt),
            allItems.Sum(i => i.GrossMarginUsd),
            allItems.Sum(i => i.RelatedSalesCount));

        _cache?.Set(FooterTotalsCacheKey, totals, FooterTotalsCacheTtl);
        return totals;
    }

    internal async Task<IReadOnlyList<ShipmentPnlListItemViewModel>> BuildAllIndexItemsAsync()
    {
        var shipmentIds = await _db.Shipments
            .AsNoTracking()
            .OrderByDescending(s => s.DepartureDate)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync();

        return await BuildIndexItemsAsync(shipmentIds);
    }

    private async Task<IReadOnlyList<ShipmentPnlListItemViewModel>> BuildIndexItemsAsync(IReadOnlyList<int> shipmentIds)
    {
        if (shipmentIds.Count == 0)
        {
            return [];
        }

        var shipments = await _db.Shipments
            .Include(s => s.Vessel)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Product)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Unit)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Customer)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(s => s.ShipmentContracts)
                .ThenInclude(sc => sc.Contract)
                    .ThenInclude(c => c!.Product)
            .Include(s => s.ShipmentContracts)
                .ThenInclude(sc => sc.Contract)
                    .ThenInclude(c => c!.Unit)
            .Include(s => s.ShipmentContracts)
                .ThenInclude(sc => sc.Contract)
                    .ThenInclude(c => c!.Supplier)
            .Include(s => s.OriginLocation)
            .Include(s => s.DestinationLocation)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(s => shipmentIds.Contains(s.Id))
            .ToListAsync();

        var order = shipmentIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        shipments = shipments
            .OrderBy(s => order[s.Id])
            .ToList();

        var rollups = await BuildFinancialRollupsAsync(shipmentIds);

        return shipments.Select(shipment =>
        {
            var rollup = rollups[shipment.Id];
            var contractCount = rollup.TransportLegs
                .Select(l => l.ContractNumber)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var totalExpensesUsd = rollup.TotalPurchaseCostUsd + rollup.TotalOperationalExpensesUsd;

            return new ShipmentPnlListItemViewModel
            {
                Id = shipment.Id,
                ShipmentCode = shipment.ShipmentCode,
                VesselName = shipment.Vessel?.Name,
                DepartureDate = shipment.DepartureDate,
                ArrivalDate = shipment.ArrivalDate,
                QuantityMt = ResolveOriginalShipmentQuantity(shipment),
                ContractNumber = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.ContractNumber),
                    contractCount > 0 ? $"{contractCount} contracts" : "Mixed contracts")
                    ?? shipment.Contract?.ContractNumber,
                ContractUnitText = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.ContractUnitText),
                    "Mixed units")
                    ?? (shipment.Contract != null ? ContractUiText.ResolveUnitText(shipment.Contract.Unit) : "-"),
                ProductName = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.ProductName),
                    "Mixed products")
                    ?? shipment.Contract?.Product?.Name,
                CustomerName = shipment.Contract?.Customer?.Name,
                SupplierName = ResolveSingleOrMixed(
                    rollup.TransportLegs.Select(l => l.SupplierName),
                    "Mixed suppliers")
                    ?? shipment.Contract?.Supplier?.Name,
                OriginName = shipment.OriginLocation?.Name,
                DestinationName = shipment.DestinationLocation?.Name,
                TotalSalesUsd = rollup.TotalSalesUsd,
                TotalPurchaseCostUsd = rollup.TotalPurchaseCostUsd,
                TotalOperationalExpensesUsd = rollup.TotalOperationalExpensesUsd,
                TotalExpensesUsd = totalExpensesUsd,
                GrossMarginUsd = rollup.TotalSalesUsd - totalExpensesUsd,
                RelatedTransportLegCount = rollup.TransportLegs.Count,
                RelatedSalesCount = rollup.Sales.Count,
                RelatedExpensesCount = rollup.Expenses.Count,
                RelatedLedgerCount = rollup.LedgerEntries.Count
            };
        }).ToList();
    }

    public async Task<IActionResult> Details(int id)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Vessel)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Company)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Product)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Unit)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Customer)
            .Include(s => s.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(s => s.OriginLocation)
            .Include(s => s.DestinationLocation)
            .Include(s => s.ShipmentContracts)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (shipment is null)
        {
            return NotFound();
        }

        var rollups = await BuildFinancialRollupsAsync([id]);
        var rollup = rollups[id];

        var dispatchTraces = await _db.DeliveryReceipts
            .Include(r => r.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .AsNoTracking()
            .Where(r => r.ShipmentId == id)
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new ShipmentPnlDispatchTraceItemViewModel
            {
                DeliveryReceiptId = r.Id,
                ReceiptDate = r.ReceiptDate,
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                DocumentReference = r.DocumentReference,
                TruckDispatchId = r.TruckDispatchId,
                DispatchDate = r.TruckDispatch != null ? r.TruckDispatch.DispatchDate : null,
                TruckPlateNumber = r.TruckDispatch != null ? r.TruckDispatch.Truck!.PlateNumber : null,
                LoadedQuantityMt = r.TruckDispatch != null ? r.TruckDispatch.LoadedQuantityMt : null
            })
            .ToListAsync();

        var shipmentGroupReceiptTag = $"Group receipt: SHIP:{id} |";
        var registeredVesselReceipts = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
                && r.InventoryMovementId.HasValue
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == id
                && r.Notes != null
                && r.Notes.Contains(shipmentGroupReceiptTag))
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Select(r => new ShipmentPnlRegisteredVesselReceiptItemViewModel
            {
                Id = r.Id,
                ReceiptDate = r.ReceiptDate,
                ContractNumber = r.InventoryTransportLeg!.SourcePurchaseContract != null
                    ? r.InventoryTransportLeg.SourcePurchaseContract.ContractNumber
                    : $"#{r.InventoryTransportLeg.SourcePurchaseContractId}",
                DestinationTerminalName = r.DestinationTerminal != null ? r.DestinationTerminal.Name : "-",
                DestinationTankName = r.DestinationStorageTank != null
                    ? (r.DestinationStorageTank.DisplayName ?? r.DestinationStorageTank.TankCode)
                    : null,
                ReceivedQuantityMt = r.ReceivedQuantityMt,
                DestinationTerminalId = r.DestinationTerminalId,
                DestinationStorageTankId = r.DestinationStorageTankId,
                ProductId = r.InventoryTransportLeg!.ProductId
            })
            .ToListAsync();

        // دریافت‌های نقدیِ مشتری که مستقیماً به همین کشتی وصل شده‌اند (PaymentTransaction.ShipmentId).
        // فقط نمایش/تجمیع؛ محاسبهٔ حاشیهٔ سود تغییری نمی‌کند (درآمد همچنان از فروش است، نه پول رسیده).
        var customerReceiptRows = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.ShipmentId == id && p.CustomerId.HasValue)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new ShipmentPnlCustomerReceiptItemViewModel
            {
                Id = p.Id,
                PaymentDate = p.PaymentDate,
                CustomerName = p.Customer != null ? p.Customer.Name : null,
                Reference = p.Reference,
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : null,
                AmountUsd = p.AmountUsd,
                IsInflow = p.Direction == PaymentDirection.In
            })
            .ToListAsync();
        var customerReceiptsUsd = decimal.Round(
            customerReceiptRows.Sum(r => r.SignedAmountUsd),
            2,
            MidpointRounding.AwayFromZero);

        // قراردادهای خرید داخل محموله + مقدار استفاده‌شده/باقی‌مانده.
        // استفاده‌شده = جمع مقدار همهٔ حمل‌های غیرلغوِ همان قرارداد در همین محموله.
        var contractLines = await BuildShipmentContractLinesAsync(id);

        // ضایعات مستقیم محموله (همان منطق نمای کشتی؛ فقط نمایش، بدون محاسبهٔ مالی جدید).
        var losses = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == id && !l.IsCancelled)
            .OrderByDescending(l => l.EventDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new ShipmentJourneyLossItem
            {
                Id = l.Id,
                EventDate = l.EventDate,
                StageName = LossEventStageLabels.ToPersian(l.Stage),
                DifferenceQuantityMt = l.DifferenceQuantityMt,
                ChargeableLossMt = l.ChargeableLossMt,
                ContractId = l.ContractId,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                ResponsiblePartyType = l.ResponsiblePartyType,
                ResponsiblePartyName = l.ResponsiblePartyName,
                FinancialTreatment = l.FinancialTreatment,
                Reference = l.Reference,
                Notes = l.Notes
            })
            .ToListAsync();

        var directLossQuantityMt = await GetDirectShipmentLossQuantityMtAsync(id);
        var quantityFlow = await BuildShipmentQuantityFlowAsync(shipment, contractLines);
        var totalExpensesUsd = rollup.TotalPurchaseCostUsd + rollup.TotalOperationalExpensesUsd;

        // اخطارهای نمایشی (منتقل‌شده از نمای کشتی؛ صرفاً راهنما، بدون منطق مالی).
        var declaredQuantityMt = ResolveOriginalShipmentQuantity(shipment);
        var allocatedFromContractsMt = contractLines.Sum(c => c.AllocatedQuantityMt);
        var warnings = new List<string>();
        if (contractLines.Count == 0)
        {
            warnings.Add("قراردادی به این محموله وصل نشده است. در فرم محموله، قراردادهای خرید را تخصیص بدهید.");
        }
        if (rollup.TransportLegs.Count == 0)
        {
            warnings.Add("هیچ جریان حملی به این محموله وصل نیست؛ بارگیری اولیه فقط از طریق قرارداد منبع دیده می‌شود.");
        }
        if (declaredQuantityMt > 0m && allocatedFromContractsMt > 0m
            && Math.Abs(declaredQuantityMt - allocatedFromContractsMt) > 0.001m)
        {
            warnings.Add("مقدار اعلام‌شدهٔ محموله با جمع مقدار قراردادها برابر نیست؛ نیاز به بررسی.");
        }
        if (rollup.Sales.Count == 0)
        {
            warnings.Add("هنوز فروشی برای این محموله ثبت نشده است.");
        }
        if (quantityFlow.VesselUnloadedQuantityMt > 0m && !quantityFlow.HasExactSourceTankLineage)
        {
            warnings.Add("تخلیه و مانده مخزن از رسیدها و سهم قراردادهای همین محموله برآورد شده است؛ این بخش هنوز ردیابی مستقیم ندارد.");
        }
        if (quantityFlow.DraftTransportQuantityMt > 0m)
        {
            warnings.Add($"{quantityFlow.DraftTransportQuantityMt:N4} MT انتقال پیش‌نویس جدا نمایش داده می‌شود و از مانده واقعی مخزن کم نشده است.");
        }

        // جواز (Company) متعلق به قرارداد است نه محموله؛ یک محموله می‌تواند چند قرارداد با جواز متفاوت
        // داشته باشد. نمایش از روی قراردادهای تخصیص‌یافته ساخته می‌شود، نه فقط قرارداد اصلی.
        var shipmentCompanyNames = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == id && sc.Contract != null && sc.Contract.Company != null)
            .Select(sc => sc.Contract!.Company!.Name)
            .Distinct()
            .ToListAsync();

        ShipmentLineageRollup? lineageRollup = null;
        if (_lineageOptions.UseInPnl)
        {
            lineageRollup = await _lineagePnl.BuildAsync(id);
        }

        var lineageBadge = lineageRollup is null
            ? ((string Text, string Tone)?)null
            : ResolveLineageBadge(lineageRollup.OverallConfidence);

        return View(new ShipmentPnlDetailsViewModel
        {
            ContractLines = contractLines,
            Losses = losses,
            Warnings = warnings,
            HasLineageData = lineageRollup?.HasLineageData == true,
            IsLineageCalculationActive = _lineageOptions.UseInPnl,
            LineageBadgeText = lineageBadge?.Text,
            LineageBadgeTone = lineageBadge?.Tone,
            OverallLineageConfidence = lineageRollup?.OverallConfidence.ToString(),
            HasVerifiedLineage = lineageRollup?.HasVerified == true,
            HasEstimatedLineage = lineageRollup?.HasEstimated == true,
            HasLegacyLineage = lineageRollup?.HasLegacy == true,
            NeedsReviewCount = lineageRollup?.NeedsReviewCount ?? 0,
            LineageWarnings = lineageRollup?.Warnings ?? [],
            LineageCargoInVesselMt = lineageRollup?.CargoInVesselMt,
            LineageArrivedReceivedMt = lineageRollup?.ArrivedReceivedMt,
            LineageInTransitMt = lineageRollup?.InTransitMt,
            LineageInStockMt = lineageRollup?.InStockMt,
            LineageSoldMt = lineageRollup?.SoldMt,
            LineageSoldUsd = lineageRollup?.SoldUsd,
            LineageLossMt = lineageRollup?.LossMt,
            LineageExpenseUsd = lineageRollup?.ExpenseUsd,
            LineageCustomsUsd = lineageRollup?.CustomsUsd,
            LossQuantityMt = losses.Sum(l => l.DifferenceQuantityMt),
            DirectLossQuantityMt = directLossQuantityMt,
            CompanyLossQuantityMt = losses
                .Where(l => l.ResponsiblePartyType == ShipmentShortageResponsibilityTypes.CompanyLoss)
                .Sum(l => l.DifferenceQuantityMt),
            Id = shipment.Id,
            ShipmentCode = shipment.ShipmentCode,
            VesselId = shipment.VesselId,
            VesselName = shipment.Vessel?.Name,
            DepartureDate = shipment.DepartureDate,
            ArrivalDate = shipment.ArrivalDate,
            QuantityMt = quantityFlow.OriginalShipmentQuantityMt,
            OriginalShipmentQuantityMt = quantityFlow.OriginalShipmentQuantityMt,
            VesselUnloadedQuantityMt = quantityFlow.VesselUnloadedQuantityMt,
            InventoryTransportedOutQuantityMt = quantityFlow.InventoryTransportedOutQuantityMt,
            InTransitQuantityMt = quantityFlow.InTransitQuantityMt,
            DeliveredAtDestinationQuantityMt = quantityFlow.DeliveredAtDestinationQuantityMt,
            RemainingInSourceTankQuantityMt = quantityFlow.RemainingInSourceTankQuantityMt,
            InventoryTransportShortageQuantityMt = quantityFlow.InventoryTransportShortageQuantityMt,
            CancelledTransportQuantityMt = quantityFlow.CancelledTransportQuantityMt,
            DraftTransportQuantityMt = quantityFlow.DraftTransportQuantityMt,
            HasExactSourceTankLineage = quantityFlow.HasExactSourceTankLineage,
            Notes = shipment.Notes,
            ContractNumber = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.ContractNumber), "Mixed contracts")
                ?? shipment.Contract?.ContractNumber,
            ContractUnitText = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.ContractUnitText), "Mixed units")
                ?? (shipment.Contract != null ? ContractUiText.ResolveUnitText(shipment.Contract.Unit) : "-"),
            CompanyName = ResolveSingleOrMixed(
                shipmentCompanyNames,
                shipmentCompanyNames.Count > 0 ? string.Join("، ", shipmentCompanyNames) : "چند جواز")
                ?? shipment.Contract?.Company?.Name,
            ProductName = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.ProductName), "Mixed products")
                ?? shipment.Contract?.Product?.Name,
            CustomerName = shipment.Contract?.Customer?.Name,
            SupplierName = ResolveSingleOrMixed(rollup.TransportLegs.Select(l => l.SupplierName), "Mixed suppliers")
                ?? shipment.Contract?.Supplier?.Name,
            OriginName = shipment.OriginLocation?.Name,
            DestinationName = shipment.DestinationLocation?.Name,
            TotalSalesUsd = rollup.TotalSalesUsd,
            TotalPurchaseCostUsd = rollup.TotalPurchaseCostUsd,
            TotalOperationalExpensesUsd = rollup.TotalOperationalExpensesUsd,
            TotalExpensesUsd = totalExpensesUsd,
            GrossMarginUsd = rollup.TotalSalesUsd - totalExpensesUsd,
            LedgerEntriesCount = rollup.LedgerEntries.Count,
            LedgerDebitTotalUsd = rollup.LedgerEntries.Where(l => l.SideName == "Debit").Sum(l => l.AmountUsd),
            LedgerCreditTotalUsd = rollup.LedgerEntries.Where(l => l.SideName == "Credit").Sum(l => l.AmountUsd),
            Sales = rollup.Sales,
            ShipmentSalesQuantityMt = rollup.SoldQuantityMt,
            Expenses = rollup.Expenses,
            TransportLegs = rollup.TransportLegs,
            LedgerEntries = rollup.LedgerEntries,
            DispatchTraces = dispatchTraces,
            RegisteredVesselReceipts = registeredVesselReceipts,
            CustomerReceipts = customerReceiptRows,
            CustomerReceiptsUsd = customerReceiptsUsd
        });
    }

    private static (string Text, string Tone) ResolveLineageBadge(LineageConfidence confidence) => confidence switch
    {
        LineageConfidence.Verified => ("ردیابی قطعی", "success"),
        LineageConfidence.Estimated => ("داده تخمینی", "warning"),
        LineageConfidence.Legacy => ("داده قدیمی", "muted"),
        LineageConfidence.NeedsReview => ("نیازمند بازبینی", "danger"),
        _ => ("داده تخمینی", "warning")
    };

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterDirectLoss(ShipmentDirectLossCreateViewModel model)
    {
        var redirectUrl = !string.IsNullOrWhiteSpace(model.ReturnUrl) && Url?.IsLocalUrl(model.ReturnUrl) == true
            ? model.ReturnUrl
            : Url?.Action(nameof(Details), new { id = model.ShipmentId }) ?? $"/ShipmentPnl/Details/{model.ShipmentId}";

        var shipment = await _db.Shipments
            .Include(s => s.Contract)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == model.ShipmentId);

        if (shipment is null)
        {
            return NotFound();
        }

        var contractLines = await BuildShipmentContractLinesAsync(model.ShipmentId);
        var shortageRegisterableQuantityMt = contractLines.Sum(c => c.ShortageRegisterableQuantityMt);
        var lossQuantityMt = decimal.Round(model.LossQuantityMt, 4, MidpointRounding.AwayFromZero);
        var responsibilityType = NormalizeShipmentShortageResponsibility(model.ResponsibilityType);

        if (lossQuantityMt <= 0m)
        {
            TempData["error"] = "مقدار ضایعات باید بزرگ‌تر از صفر باشد.";
            return Redirect(redirectUrl!);
        }

        if (responsibilityType is null)
        {
            TempData["error"] = "Ù†ÙˆØ¹ Ø«Ø¨Øª Ú©Ø³Ø±ÛŒ Ù…Ø¹ØªØ¨Ø± Ù†ÛŒØ³Øª.";
            return Redirect(redirectUrl!);
        }

        int? contractId = null;
        if (model.ContractId.HasValue)
        {
            if (!contractLines.Any(c => c.ContractId == model.ContractId.Value))
            {
                TempData["error"] = "Ù‚Ø±Ø§Ø±Ø¯Ø§Ø¯ Ø§Ù†ØªØ®Ø§Ø¨â€ŒØ´Ø¯Ù‡ Ø¯Ø± Ø§ÛŒÙ† Ù…Ø­Ù…ÙˆÙ„Ù‡ ÙˆØ¬ÙˆØ¯ Ù†Ø¯Ø§Ø±Ø¯.";
                return Redirect(redirectUrl!);
            }

            contractId = model.ContractId.Value;
        }
        else if (contractLines.Count == 1)
        {
            contractId = contractLines[0].ContractId;
        }

        var maxAvailableQuantityMt = contractId.HasValue
            ? contractLines.First(c => c.ContractId == contractId.Value).ShortageRegisterableQuantityMt
            : shortageRegisterableQuantityMt;

        if (lossQuantityMt - maxAvailableQuantityMt > 0.0001m)
        {
            TempData["error"] = $"مقدار ضایعات ({lossQuantityMt:N4} MT) از مقدار قابل ثبت کسری ({maxAvailableQuantityMt:N4} MT) بیشتر است.";
            return Redirect(redirectUrl!);
        }

        var partyName = NormalizeFreeText(model.ResponsiblePartyName);
        if (RequiresResponsiblePartyName(responsibilityType) && string.IsNullOrWhiteSpace(partyName))
        {
            TempData["error"] = "Ø¨Ø±Ø§ÛŒ Ø§ÛŒÙ† Ù†ÙˆØ¹ Ø«Ø¨ØªØŒ Ù†Ø§Ù… Ø·Ø±Ù Ù…Ø³Ø¦ÙˆÙ„ Ø§Ù„Ø²Ø§Ù…ÛŒ Ø§Ø³Øª.";
            return Redirect(redirectUrl!);
        }

        var splitLines = NormalizeSplitLines(model.SplitLines);
        if (responsibilityType == ShipmentShortageResponsibilityTypes.Split)
        {
            var splitError = ValidateSplitLines(splitLines, lossQuantityMt);
            if (splitError is not null)
            {
                TempData["error"] = splitError;
                return Redirect(redirectUrl!);
            }

            partyName = "Ú†Ù†Ø¯ Ø·Ø±Ù";
        }
        else
        {
            splitLines = [];
        }

        var productId = await ResolveShipmentProductIdAsync(model.ShipmentId, shipment);
        if (!productId.HasValue)
        {
            TempData["error"] = "جنس این محموله مشخص نیست؛ اول قرارداد یا حمل محموله را کامل کنید.";
            return Redirect(redirectUrl!);
        }

        var eventDate = model.EventDate == default
            ? DateTime.UtcNow.Date
            : model.EventDate.Date;
        var estimatedAmountUsd = model.LossAmountUsd.HasValue && model.LossAmountUsd.Value > 0m
            ? decimal.Round(model.LossAmountUsd.Value, 2, MidpointRounding.AwayFromZero)
            : EstimateShipmentLossValueUsd(lossQuantityMt, contractLines, contractId);
        var claimStatus = NormalizeClaimStatus(model.ClaimStatus);
        var notes = BuildShipmentShortageNotes(model.Notes, estimatedAmountUsd, claimStatus, splitLines);
        var reference = BuildShipmentShortageReference(model.ShipmentId, eventDate, lossQuantityMt, responsibilityType, contractId);

        var duplicateExists = await _db.LossEvents
            .AsNoTracking()
            .AnyAsync(l => l.ShipmentId == model.ShipmentId
                && !l.IsCancelled
                && l.Reference == reference);
        if (duplicateExists)
        {
            TempData["error"] = "Ø§ÛŒÙ† Ú©Ø³Ø±ÛŒ Ø¨Ø§ Ù‡Ù…ÛŒÙ† Ù…Ø´Ø®ØµØ§Øª Ù‚Ø¨Ù„Ø§Ù‹ Ø«Ø¨Øª Ø´Ø¯Ù‡ Ø§Ø³Øª.";
            return Redirect(redirectUrl!);
        }

        _db.LossEvents.Add(new LossEvent
        {
            Stage = LossEventStage.TransitLoss,
            ProductId = productId.Value,
            ContractId = contractId,
            ShipmentId = model.ShipmentId,
            EventDate = eventDate,
            ExpectedQuantityMt = maxAvailableQuantityMt,
            ActualQuantityMt = maxAvailableQuantityMt - lossQuantityMt,
            DifferenceQuantityMt = lossQuantityMt,
            ToleranceQuantityMt = 0m,
            AllowableLossMt = 0m,
            ChargeableLossMt = lossQuantityMt,
            ResponsiblePartyType = responsibilityType,
            ResponsiblePartyName = partyName ?? ResponsibilityLabelFa(responsibilityType),
            FinancialTreatment = FinancialTreatmentLabelFa(responsibilityType),
            AffectsInventory = false,
            LossCertainty = contractId.HasValue ? LossCertaintyLevel.Measured : LossCertaintyLevel.Estimated,
            Reference = reference,
            Notes = notes
        });
        await _db.SaveChangesAsync();

        TempData["ok"] = "ضایعات محموله ثبت شد و از باقی‌مانده محموله کم شد.";
        return Redirect(redirectUrl!);
    }

    // قراردادهای خرید داخل یک محموله با مقدار تخصیص/جریان اصلی/باقی‌مانده.
    // «استفاده‌شده» فقط مرحلهٔ اصلی کشتی است؛ انتقال موجودی جدا گزارش می‌شود.
    private async Task<IReadOnlyList<ShipmentContractLineViewModel>> BuildShipmentContractLinesAsync(int shipmentId)
    {
        var shipmentContracts = await _db.ShipmentContracts
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Supplier)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Product)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Unit)
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId)
            .OrderBy(sc => sc.Id)
            .ToListAsync();

        if (shipmentContracts.Count == 0)
        {
            return [];
        }

        var shipmentTransportLegs = await _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId
                && l.Status != InventoryTransportLegStatus.Cancelled)
            .ToListAsync();

        // Vessel legs are the normal shipment root. The current tank-backed
        // shipment flow records its root as Unspecified, so accept that fallback
        // only when a persisted outbound movement proves stock was loaded.
        var originalVesselLegs = shipmentTransportLegs
            .Where(l => l.TransportType == LoadingTransportType.Vessel
                && l.Status != InventoryTransportLegStatus.Draft)
            .ToList();
        var tankLoadedRootLegs = originalVesselLegs.Count > 0
            ? []
            : shipmentTransportLegs
                .Where(l => l.TransportType == LoadingTransportType.Unspecified
                    && l.Status != InventoryTransportLegStatus.Draft
                    && l.OutboundInventoryMovementId.HasValue
                    && l.SourceStorageTankId.HasValue)
                .ToList();
        var shipmentRootLegs = originalVesselLegs.Count > 0
            ? originalVesselLegs
            : tankLoadedRootLegs;
        var shipmentRootLegIds = shipmentRootLegs.Select(l => l.Id).ToHashSet();

        var loadedByContract = shipmentRootLegs
            .GroupBy(l => l.SourcePurchaseContractId)
            .Select(g => new
            {
                ContractId = g.Key,
                LoadedMt = g.Sum(x => x.QuantityMt)
            })
            .ToDictionary(x => x.ContractId, x => x.LoadedMt);

        if (originalVesselLegs.Count == 0)
        {
            foreach (var shipmentContract in shipmentContracts)
            {
                if (!loadedByContract.TryGetValue(shipmentContract.ContractId, out var loadedMt))
                {
                    continue;
                }

                var allocationCap = Math.Max(shipmentContract.QuantityMt ?? 0m, 0m);
                loadedByContract[shipmentContract.ContractId] = decimal.Round(
                    Math.Min(loadedMt, allocationCap),
                    4,
                    MidpointRounding.AwayFromZero);
            }
        }

        var transportedFromInventoryByContract = shipmentTransportLegs
            .Where(l => l.TransportType != LoadingTransportType.Vessel
                && l.Status != InventoryTransportLegStatus.Draft
                && l.OutboundInventoryMovementId.HasValue
                && !shipmentRootLegIds.Contains(l.Id))
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));

        var contractIds = shipmentTransportLegs
            .Select(l => l.SourcePurchaseContractId)
            .Distinct()
            .ToList();

        var finalPriceByContract = shipmentContracts
            .Where(sc => sc.Contract is not null)
            .ToDictionary(sc => sc.ContractId, sc => ContractPricingAdapter.GetCanonicalFinalPrice(sc.Contract!));

        var purchaseSnapshots = await new PurchaseAggregationService(_db)
            .AggregateForContractsAsync(contractIds, finalPriceByContract);

        var actualPurchaseUnitCostByContract = shipmentTransportLegs
            .Select(l =>
            {
                var (unitCost, _) = ResolvePurchaseUnitCost(l, purchaseSnapshots);
                return new { l.SourcePurchaseContractId, l.QuantityMt, UnitCost = unitCost };
            })
            .Where(x => x.UnitCost.HasValue && x.UnitCost.Value > 0m && x.QuantityMt > 0m)
            .GroupBy(x => x.SourcePurchaseContractId)
            .ToDictionary(
                g => g.Key,
                g => decimal.Round(
                    g.Sum(x => x.QuantityMt * x.UnitCost!.Value) / g.Sum(x => x.QuantityMt),
                    6,
                    MidpointRounding.AwayFromZero));

        // کسری تخلیهٔ ریشه (کشتی/واگن → مخزن) باید «کسری واقعیِ ورودی» را نشان دهد؛ یعنی اختلاف
        // مقدار لِج با مقداری که واقعاً رسید. این هم کسریِ صریح (ShortageQuantityMt) را می‌گیرد و هم
        // «کسری خاموش» را — حالتی که فقط رسیدِ کمتر ثبت شده ولی ShortageQuantityMt صفر مانده و در نتیجه
        // ضایعه در پرونده دیده نمی‌شد. برای سازگاری، بیشینهٔ این دو گرفته می‌شود، پس در شیپمنت‌هایی که
        // کسری درست ثبت شده نتیجه دقیقاً مثل قبل است.
        // فقط لِج‌هایی که رسید (تخلیه) دارند؛ لِج بدون رسید هنوز در راه است و کسری قطعی ندارد.
        var rootReceiptTotals = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipmentId
                && shipmentRootLegIds.Contains(r.InventoryTransportLegId)
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Draft
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .GroupBy(r => r.InventoryTransportLegId)
            .Select(g => new
            {
                LegId = g.Key,
                ReceivedMt = g.Sum(x => x.ReceivedQuantityMt),
                RecordedShortageMt = g.Sum(x => x.ShortageQuantityMt)
            })
            .ToDictionaryAsync(x => x.LegId, x => new { x.ReceivedMt, x.RecordedShortageMt });

        var transportShortageByContract = shipmentRootLegs
            .Where(l => rootReceiptTotals.ContainsKey(l.Id))
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(l =>
                {
                    var totals = rootReceiptTotals[l.Id];
                    var unaccountedGapMt = Math.Max(0m, l.QuantityMt - totals.ReceivedMt);
                    return decimal.Round(
                        Math.Max(totals.RecordedShortageMt, unaccountedGapMt),
                        4,
                        MidpointRounding.AwayFromZero);
                }));

        var directLosses = await GetDirectShipmentLossesAsync(shipmentId);
        var lossCapacityByContract = shipmentContracts
            .ToDictionary(
                sc => sc.ContractId,
                sc =>
                {
                    var loaded = loadedByContract.TryGetValue(sc.ContractId, out var loadedMt) ? loadedMt : 0m;
                    var shortage = transportShortageByContract.TryGetValue(sc.ContractId, out var shortageMt) ? shortageMt : 0m;
                    return decimal.Round(Math.Max(loaded - shortage, 0m), 4, MidpointRounding.AwayFromZero);
                });
        var directLossByContract = AllocateDirectLossesByContract(directLosses, lossCapacityByContract);

        return shipmentContracts
            .Select(sc =>
            {
                var allocated = sc.QuantityMt ?? 0m;
                var contractUnitPrice = sc.Contract is not null
                    ? ContractPricingAdapter.GetCanonicalFinalPrice(sc.Contract)
                    : null;
                var unitPrice = actualPurchaseUnitCostByContract.TryGetValue(sc.ContractId, out var actualUnitCost)
                    ? actualUnitCost
                    : contractUnitPrice;
                var totalValue = unitPrice.HasValue && unitPrice.Value > 0m && allocated > 0m
                    ? RoundMoney(allocated * unitPrice.Value)
                    : (decimal?)null;
                return new ShipmentContractLineViewModel
                {
                    ContractId = sc.ContractId,
                    ContractNumber = sc.Contract?.ContractNumber ?? $"#{sc.ContractId}",
                    SupplierName = sc.Contract?.Supplier?.Name,
                    ProductName = sc.Contract?.Product?.Name,
                    ContractUnitText = sc.Contract != null ? ContractUiText.ResolveUnitText(sc.Contract.Unit) : "-",
                    AllocatedQuantityMt = allocated,
                    // نرخ نهاییِ خرید = همان معیاری که TotalPurchaseCostUsd با آن جمع می‌زند (canonical final price).
                    HasFinalPrice = sc.Contract != null
                        && ContractPricingAdapter.GetCanonicalFinalPrice(sc.Contract) is { } fp && fp > 0m,
                    UsedQuantityMt = loadedByContract.TryGetValue(sc.ContractId, out var loaded) ? loaded : 0m,
                    TransportedFromInventoryQuantityMt = transportedFromInventoryByContract.TryGetValue(sc.ContractId, out var transported) ? transported : 0m,
                    TransportShortageQuantityMt = transportShortageByContract.TryGetValue(sc.ContractId, out var shortage) ? shortage : 0m,
                    DirectLossQuantityMt = directLossByContract.TryGetValue(sc.ContractId, out var loss) ? loss : 0m,
                    UnitPriceUsd = unitPrice,
                    TotalValueUsd = totalValue
                };
            })
            .ToList();
    }

    private sealed record ShipmentSourceReceiptRow(
        int ContractId,
        int ProductId,
        int TerminalId,
        int? StorageTankId,
        decimal QuantityMt);

    private sealed record ShipmentQuantityFlowSnapshot(
        decimal OriginalShipmentQuantityMt,
        decimal VesselUnloadedQuantityMt,
        decimal InventoryTransportedOutQuantityMt,
        decimal InTransitQuantityMt,
        decimal DeliveredAtDestinationQuantityMt,
        decimal RemainingInSourceTankQuantityMt,
        decimal InventoryTransportShortageQuantityMt,
        decimal CancelledTransportQuantityMt,
        decimal DraftTransportQuantityMt,
        bool HasExactSourceTankLineage);

    private async Task<ShipmentQuantityFlowSnapshot> BuildShipmentQuantityFlowAsync(
        Shipment shipment,
        IReadOnlyList<ShipmentContractLineViewModel> contractLines)
    {
        var originalShipmentQuantityMt = ResolveOriginalShipmentQuantity(shipment);
        var inventoryLegs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipment.Id
                && l.TransportType != LoadingTransportType.Vessel)
            .ToListAsync();

        var activeInventoryLegs = inventoryLegs
            .Where(l => l.Status != InventoryTransportLegStatus.Draft
                && l.Status != InventoryTransportLegStatus.Cancelled
                && l.OutboundInventoryMovementId.HasValue)
            .ToList();
        var activeInventoryLegIds = activeInventoryLegs.Select(l => l.Id).ToList();

        var destinationReceipts = activeInventoryLegIds.Count == 0
            ? []
            : await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => activeInventoryLegIds.Contains(r.InventoryTransportLegId)
                    && !r.IsCancelled)
                .Select(r => new
                {
                    r.InventoryTransportLegId,
                    r.ReceivedQuantityMt,
                    r.ShortageQuantityMt
                })
                .ToListAsync();

        var receivedByLeg = destinationReceipts
            .GroupBy(r => r.InventoryTransportLegId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    ReceivedQuantityMt = group.Sum(r => r.ReceivedQuantityMt),
                    ShortageQuantityMt = group.Sum(r => r.ShortageQuantityMt)
                });
        var inTransitQuantityMt = activeInventoryLegs
            .Where(l => l.Status is InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit)
            .Sum(l =>
            {
                var receipt = receivedByLeg.GetValueOrDefault(l.Id);
                return Math.Max(
                    l.QuantityMt
                    - (receipt?.ReceivedQuantityMt ?? 0m)
                    - (receipt?.ShortageQuantityMt ?? 0m),
                    0m);
            });

        var shipmentGroupReceiptTag = $"Group receipt: SHIP:{shipment.Id} |";
        var exactVesselSourceRows = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
                && r.DestinationTerminalId.HasValue
                && r.InventoryMovementId.HasValue
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId == shipment.Id
                && (r.InventoryTransportLeg.TransportType == LoadingTransportType.Vessel
                    || (r.Notes != null && r.Notes.Contains(shipmentGroupReceiptTag)))
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Draft
                && r.InventoryTransportLeg.Status != InventoryTransportLegStatus.Cancelled)
            .Select(r => new ShipmentSourceReceiptRow(
                r.InventoryTransportLeg!.SourcePurchaseContractId,
                r.InventoryTransportLeg.ProductId,
                r.DestinationTerminalId!.Value,
                r.DestinationStorageTankId,
                r.ReceivedQuantityMt))
            .ToListAsync();

        var sourceRows = exactVesselSourceRows;
        var hasExactSourceTankLineage = sourceRows.Count > 0;
        if (sourceRows.Count == 0)
        {
            var allocatedByContract = contractLines
                .Where(line => line.AllocatedQuantityMt > 0m)
                .ToDictionary(line => line.ContractId, line => line.AllocatedQuantityMt);
            if (allocatedByContract.Count == 0
                && shipment.ContractId.HasValue
                && originalShipmentQuantityMt > 0m)
            {
                allocatedByContract[shipment.ContractId.Value] = originalShipmentQuantityMt;
            }

            var contractIds = allocatedByContract.Keys.ToList();
            if (contractIds.Count > 0)
            {
                // LoadingReceipt/InventoryMovement currently has no ShipmentId. Prefer
                // the same vessel, then fall back to contract-scoped rows and cap them
                // to this shipment's allocation. This is conservative, not exact lineage.
                var loadingReceiptCandidates = await _db.LoadingReceiptAllocations
                    .AsNoTracking()
                    .Where(a => a.Destination == LoadingReceiptAllocationDestination.ToInventory
                        && a.Status != LoadingReceiptAllocationStatus.Cancelled
                        && a.InventoryMovementId.HasValue
                        && a.LoadingReceipt != null
                        && a.LoadingReceipt.LoadingRegister != null
                        && contractIds.Contains(a.SourcePurchaseContractId ?? a.LoadingReceipt.LoadingRegister.ContractId))
                    .Select(a => new
                    {
                        ContractId = a.SourcePurchaseContractId ?? a.LoadingReceipt!.LoadingRegister!.ContractId,
                        ProductId = a.LoadingReceipt!.LoadingRegister!.ProductId,
                        a.TerminalId,
                        a.StorageTankId,
                        a.QuantityMt,
                        a.LoadingReceipt.LoadingRegister.VesselId
                    })
                    .ToListAsync();

                var preferredCandidates = shipment.VesselId.HasValue
                    ? loadingReceiptCandidates.Where(row => row.VesselId == shipment.VesselId).ToList()
                    : [];
                var selectedCandidates = preferredCandidates.Count > 0
                    ? preferredCandidates
                    : loadingReceiptCandidates;
                sourceRows = CapSourceRowsToShipmentAllocation(
                    selectedCandidates.Select(row => new ShipmentSourceReceiptRow(
                        row.ContractId,
                        row.ProductId,
                        row.TerminalId,
                        row.StorageTankId,
                        row.QuantityMt)).ToList(),
                    allocatedByContract);
            }
        }

        var sourceScopes = sourceRows
            .GroupBy(row => new { row.ContractId, row.ProductId, row.TerminalId, row.StorageTankId })
            .Select(group => new ShipmentSourceReceiptRow(
                group.Key.ContractId,
                group.Key.ProductId,
                group.Key.TerminalId,
                group.Key.StorageTankId,
                group.Sum(row => row.QuantityMt)))
            .ToList();

        var outboundByScope = activeInventoryLegs
            .GroupBy(l => new { ContractId = l.SourcePurchaseContractId, l.ProductId, TerminalId = l.SourceTerminalId, StorageTankId = l.SourceStorageTankId })
            .ToDictionary(group => group.Key, group => group.Sum(l => l.QuantityMt));
        var salesOutRows = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.Direction == MovementDirection.Out
                && m.SalesTransaction != null
                && m.SalesTransaction.ShipmentId == shipment.Id
                && !m.SalesTransaction.IsCancelled)
            .GroupBy(m => new { ContractId = m.ContractId, m.ProductId, m.TerminalId, m.StorageTankId })
            .Select(group => new
            {
                group.Key.ContractId,
                group.Key.ProductId,
                group.Key.TerminalId,
                group.Key.StorageTankId,
                QuantityMt = group.Sum(m => m.QuantityMt)
            })
            .ToListAsync();
        var salesOutByScope = salesOutRows.ToDictionary(
            row => (row.ContractId, row.ProductId, row.TerminalId, row.StorageTankId),
            row => row.QuantityMt);
        var inventoryLossRows = await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipment.Id
                && !l.IsCancelled
                && l.AffectsInventory
                && l.ContractId.HasValue
                && l.TerminalId.HasValue)
            .GroupBy(l => new { l.ContractId, l.ProductId, l.TerminalId, l.StorageTankId })
            .Select(group => new
            {
                group.Key.ContractId,
                group.Key.ProductId,
                group.Key.TerminalId,
                group.Key.StorageTankId,
                QuantityMt = group.Sum(l => l.DifferenceQuantityMt)
            })
            .ToListAsync();
        var inventoryLossByScope = inventoryLossRows.ToDictionary(
            row => (row.ContractId, row.ProductId, row.TerminalId, row.StorageTankId),
            row => row.QuantityMt);

        var stockService = new StockService(_db);
        var remainingInSourceTankQuantityMt = 0m;
        foreach (var source in sourceScopes)
        {
            var scope = new
            {
                ContractId = source.ContractId,
                source.ProductId,
                source.TerminalId,
                source.StorageTankId
            };
            var calculatedRemaining = Math.Max(
                source.QuantityMt
                - outboundByScope.GetValueOrDefault(scope)
                - salesOutByScope.GetValueOrDefault((source.ContractId, source.ProductId, source.TerminalId, source.StorageTankId))
                - inventoryLossByScope.GetValueOrDefault(((int?)source.ContractId, source.ProductId, (int?)source.TerminalId, source.StorageTankId)),
                0m);
            // Physical stock also captures adjustments and other stock-affecting
            // movements that cannot be tied exactly to ShipmentId in the current schema.
            var physicalStock = await stockService.GetFreeQuantityMtAsync(
                source.ProductId,
                terminalId: source.TerminalId,
                contractId: source.ContractId,
                storageTankId: source.StorageTankId);
            remainingInSourceTankQuantityMt += Math.Min(calculatedRemaining, Math.Max(physicalStock, 0m));
        }

        // «حمل از موجودی» فقط legهای واقعیِ وسیله (موتر/واگون) است. legهای منبع/ورودیِ محموله
        // (TransportType=Unspecified، بدون وسیله و مقصد) نباید به‌عنوان حملِ خارج‌شده شمرده شوند،
        // وگرنه کل بار محموله دوباره از «باقی‌مانده» کسر می‌شود و منفی/صفر می‌شود.
        var transportedToVehicleQuantityMt = activeInventoryLegs
            .Where(l => l.TransportType is LoadingTransportType.Wagon or LoadingTransportType.Truck)
            .Sum(l => l.QuantityMt);

        return new ShipmentQuantityFlowSnapshot(
            RoundQuantity(originalShipmentQuantityMt),
            RoundQuantity(sourceScopes.Sum(row => row.QuantityMt)),
            RoundQuantity(transportedToVehicleQuantityMt),
            RoundQuantity(inTransitQuantityMt),
            RoundQuantity(destinationReceipts.Sum(r => r.ReceivedQuantityMt)),
            RoundQuantity(remainingInSourceTankQuantityMt),
            RoundQuantity(destinationReceipts.Sum(r => r.ShortageQuantityMt)),
            RoundQuantity(inventoryLegs.Where(l => l.Status == InventoryTransportLegStatus.Cancelled).Sum(l => l.QuantityMt)),
            RoundQuantity(inventoryLegs.Where(l => l.Status == InventoryTransportLegStatus.Draft).Sum(l => l.QuantityMt)),
            hasExactSourceTankLineage);
    }

    private static List<ShipmentSourceReceiptRow> CapSourceRowsToShipmentAllocation(
        IReadOnlyList<ShipmentSourceReceiptRow> rows,
        IReadOnlyDictionary<int, decimal> allocatedByContract)
    {
        var result = new List<ShipmentSourceReceiptRow>();
        foreach (var contractGroup in rows.GroupBy(row => row.ContractId))
        {
            var sourceRows = contractGroup.ToList();
            var total = sourceRows.Sum(row => row.QuantityMt);
            var allocation = allocatedByContract.GetValueOrDefault(contractGroup.Key);
            if (allocation <= 0m || total <= allocation + 0.0001m)
            {
                result.AddRange(sourceRows);
                continue;
            }

            var allocated = 0m;
            for (var index = 0; index < sourceRows.Count; index++)
            {
                var row = sourceRows[index];
                var quantityMt = index == sourceRows.Count - 1
                    ? RoundQuantity(allocation - allocated)
                    : RoundQuantity(allocation * row.QuantityMt / total);
                allocated += quantityMt;
                result.Add(row with { QuantityMt = quantityMt });
            }
        }

        return result;
    }

    private static decimal ResolveOriginalShipmentQuantity(Shipment shipment)
    {
        if (shipment.QuantityMt > 0m)
        {
            return RoundQuantity(shipment.QuantityMt);
        }

        return RoundQuantity(shipment.ShipmentContracts.Sum(sc => sc.QuantityMt ?? 0m));
    }

    private async Task<Dictionary<int, ShipmentFinancialRollup>> BuildFinancialRollupsAsync(IReadOnlyCollection<int> shipmentIds)
    {
        var shipmentIdList = shipmentIds.Distinct().ToList();
        var rollups = shipmentIdList.ToDictionary(id => id, id => new ShipmentFinancialRollup(id));
        if (shipmentIdList.Count == 0)
        {
            return rollups;
        }

        var shipmentContractRows = await _db.ShipmentContracts
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Product)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Unit)
            .Include(sc => sc.Contract)
                .ThenInclude(c => c!.Supplier)
            .AsNoTracking()
            .Where(sc => shipmentIdList.Contains(sc.ShipmentId)
                && sc.QuantityMt.HasValue
                && sc.QuantityMt.Value > 0m)
            .ToListAsync();

        foreach (var allocation in shipmentContractRows)
        {
            if (allocation.Contract is null || !allocation.QuantityMt.HasValue)
            {
                continue;
            }

            var unitCost = ContractPricingAdapter.GetCanonicalFinalPrice(allocation.Contract);
            if (!unitCost.HasValue || unitCost.Value <= 0m)
            {
                continue;
            }

            rollups[allocation.ShipmentId].TotalPurchaseCostUsd += RoundMoney(allocation.QuantityMt.Value * unitCost.Value);
        }

        var transportLegs = await _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Product)
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Unit)
            .Include(l => l.SourcePurchaseContract)
                .ThenInclude(c => c!.Supplier)
            .Include(l => l.Product)
            .Include(l => l.SourceTerminal)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Include(l => l.DestinationLocation)
            .AsNoTracking()
            .Where(l => l.ShipmentId.HasValue
                && shipmentIdList.Contains(l.ShipmentId.Value)
                && l.Status != InventoryTransportLegStatus.Cancelled)
            .OrderBy(l => l.LoadedDate)
            .ThenBy(l => l.Id)
            .ToListAsync();

        var contractIds = transportLegs
            .Select(l => l.SourcePurchaseContractId)
            .Distinct()
            .ToList();

        var finalPriceByContract = transportLegs
            .Where(l => l.SourcePurchaseContract is not null)
            .GroupBy(l => l.SourcePurchaseContractId)
            .ToDictionary(
                g => g.Key,
                g => ContractPricingAdapter.GetCanonicalFinalPrice(g.First().SourcePurchaseContract!));

        var purchaseSnapshots = await new PurchaseAggregationService(_db)
            .AggregateForContractsAsync(contractIds, finalPriceByContract);

        var transportPnlByLegId = await new InventoryTransportPnlService(_db)
            .BuildForLegsAsync(transportLegs.Select(l => l.Id).ToList());
        var transportLegById = transportLegs.ToDictionary(leg => leg.Id);
        var saleBreakdownBySaleId = transportPnlByLegId
            .SelectMany(pair =>
            {
                var leg = transportLegById[pair.Key];
                var contractNumber = leg.SourcePurchaseContract?.ContractNumber
                    ?? $"#{leg.SourcePurchaseContractId}";
                return pair.Value.Sales.Select(sale => new
                {
                    sale.SaleId,
                    Line = new ShipmentContractBreakdownLine
                    {
                        ContractNumber = contractNumber,
                        QuantityMt = sale.QuantityMt,
                        AmountUsd = sale.AmountUsd,
                        Description = sale.TraceKind
                    }
                });
            })
            .GroupBy(row => row.SaleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ShipmentContractBreakdownLine>)group
                    .Select(row => row.Line)
                    .ToList());

        var legToShipment = new Dictionary<int, int>();
        foreach (var leg in transportLegs)
        {
            var shipmentId = leg.ShipmentId!.Value;
            legToShipment[leg.Id] = shipmentId;

            transportPnlByLegId.TryGetValue(leg.Id, out var legPnl);

            var (fallbackUnitCost, fallbackCostSource) = ResolvePurchaseUnitCost(leg, purchaseSnapshots);
            var unitCost = legPnl?.PurchaseUnitCostUsd ?? fallbackUnitCost;
            var costSource = legPnl?.PurchaseCostSource ?? fallbackCostSource;
            var purchaseCost = legPnl?.PurchaseCostUsd
                ?? (unitCost.HasValue
                    ? RoundMoney(leg.QuantityMt * unitCost.Value)
                    : 0m);

            var contract = leg.SourcePurchaseContract;
            rollups[shipmentId].TransportLegs.Add(new ShipmentPnlTransportLegItemViewModel
            {
                Id = leg.Id,
                LoadedDate = leg.LoadedDate,
                ContractNumber = contract?.ContractNumber,
                ContractUnitText = contract != null ? ContractUiText.ResolveUnitText(contract.Unit) : "-",
                ProductName = contract?.Product?.Name ?? leg.Product?.Name,
                SupplierName = contract?.Supplier?.Name,
                TransportReference = ResolveTransportReference(leg),
                DocumentReference = leg.RwbNo ?? leg.BillOfLadingNumber,
                TransportGroupKey = leg.TransportGroupKey,
                TransportTypeName = ToTransportTypeName(leg.TransportType),
                TransportStatusName = ToTransportStatusName(leg.Status),
                IsOriginalVesselMovement = leg.TransportType == LoadingTransportType.Vessel,
                SourceIsStorageTank = leg.SourceStorageTankId.HasValue,
                IsDraft = leg.Status == InventoryTransportLegStatus.Draft,
                SourceName = BuildInventoryLocationName(leg.SourceTerminal, leg.SourceStorageTank),
                DestinationName = BuildDestinationName(leg),
                Notes = leg.Notes,
                QuantityMt = leg.QuantityMt,
                HasOutboundInventoryMovement = leg.OutboundInventoryMovementId.HasValue,
                PurchaseUnitCostUsd = unitCost,
                PurchaseCostUsd = purchaseCost,
                CostSource = costSource,
                ReceivedQuantityMt = legPnl?.ReceivedQuantityMt ?? 0m,
                ShortageQuantityMt = legPnl?.ShortageQuantityMt ?? 0m,
                SoldQuantityMt = legPnl?.SoldQuantityMt ?? 0m,
                SalesUsd = legPnl?.SalesUsd ?? 0m,
                OperationalExpensesUsd = legPnl?.OperationalExpensesUsd ?? 0m,
                TotalCostUsd = legPnl?.TotalCostUsd ?? purchaseCost,
                GrossMarginUsd = legPnl?.GrossMarginUsd ?? -purchaseCost,
                UnsoldQuantityMt = legPnl?.UnsoldQuantityMt ?? leg.QuantityMt,
                SalesTraceNote = legPnl?.SalesTraceNote ?? "No traceable sale"
            });
            if (legPnl is not null && legPnl.ReceiptFreightExpenseUsd > 0m)
            {
                rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
                {
                    Id = leg.Id,
                    ExpenseDate = leg.LoadedDate,
                    ExpenseTypeName = "کرایه رسید حمل",
                    AmountUsd = legPnl.ReceiptFreightExpenseUsd,
                    Description = $"حمل #{leg.Id}",
                    ContractNumber = contract?.ContractNumber,
                    AllocationQuantityMt = leg.QuantityMt,
                    SourceKey = $"RECEIPT-FREIGHT:{leg.Id}",
                    ExpenseTypeCategory = "Transport"
                });
                rollups[shipmentId].TotalOperationalExpensesUsd += legPnl.ReceiptFreightExpenseUsd;
            }
        }

        foreach (var shipmentId in shipmentIdList)
        {
            var rollup = rollups[shipmentId];
            if (rollup.TransportLegs.Count == 0)
            {
                continue;
            }

            // Purchase cost belongs to the original vessel cargo. Repeated
            // tank-to-truck/wagon movement must not purchase the same oil again.
            var originalVesselLegs = rollup.TransportLegs
                .Where(l => l.IsOriginalVesselMovement && !l.IsDraft)
                .ToList();
            if (originalVesselLegs.Count > 0)
            {
                rollup.TotalPurchaseCostUsd = originalVesselLegs.Sum(l => l.PurchaseCostUsd);
            }
            else if (rollup.TotalPurchaseCostUsd <= 0m)
            {
                // Backward-compatible fallback for old shipments that only have
                // a non-vessel leg and no ShipmentContract allocation snapshot.
                rollup.TotalPurchaseCostUsd = rollup.TransportLegs
                    .Where(l => !l.IsDraft)
                    .Sum(l => l.PurchaseCostUsd);
            }
        }

        var legIds = legToShipment.Keys.ToList();
        var saleToShipmentFromLeg = await BuildSaleShipmentMapFromTransportReceiptsAsync(legIds, legToShipment);
        var saleIdsFromLeg = saleToShipmentFromLeg.Keys.ToList();

        var sales = await _db.SalesTransactions
            .Include(s => s.Contract)
            .Include(s => s.Customer)
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && s.SaleStage != SaleStage.PreSale
                && ((s.ShipmentId.HasValue && shipmentIdList.Contains(s.ShipmentId.Value))
                    || saleIdsFromLeg.Contains(s.Id)))
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        var saleToShipment = new Dictionary<int, int>();
        foreach (var sale in sales)
        {
            int shipmentId;
            // فروش مستقیم از محموله = فروشی که ShipmentId روی خودش ست است. فروش پس از ورود به مخزن فقط از
            // مسیر Lineage رسیدهای حمل به محموله وصل می‌شود. این تفکیک فقط برای نمایش است؛ dedup روی sale.Id
            // تضمین می‌کند هر فروش دقیقاً یک بار در جمع بیاید.
            bool isDirectShipmentSale;
            if (sale.ShipmentId.HasValue && rollups.ContainsKey(sale.ShipmentId.Value))
            {
                shipmentId = sale.ShipmentId.Value;
                isDirectShipmentSale = true;
            }
            else if (!saleToShipmentFromLeg.TryGetValue(sale.Id, out shipmentId))
            {
                continue;
            }
            else
            {
                isDirectShipmentSale = false;
            }

            saleToShipment[sale.Id] = shipmentId;
            rollups[shipmentId].Sales.Add(new ShipmentPnlSalesItemViewModel
            {
                Id = sale.Id,
                SaleDate = sale.SaleDate,
                InvoiceNumber = sale.InvoiceNumber,
                CustomerName = sale.Customer?.Name,
                ContractNumber = sale.Contract?.ContractNumber,
                QuantityMt = sale.QuantityMt,
                UnitPriceUsd = sale.UnitPriceUsd,
                TotalUsd = sale.TotalUsd,
                IsDirectShipmentSale = isDirectShipmentSale,
                ContractBreakdownLines = saleBreakdownBySaleId.TryGetValue(sale.Id, out var breakdown)
                    ? breakdown
                    : sale.Contract is null
                        ? []
                        :
                        [
                            new ShipmentContractBreakdownLine
                            {
                                ContractNumber = sale.Contract.ContractNumber,
                                QuantityMt = sale.QuantityMt,
                                AmountUsd = sale.TotalUsd,
                                Description = sale.InvoiceNumber
                            }
                        ]
            });
            rollups[shipmentId].TotalSalesUsd += sale.TotalUsd;
            rollups[shipmentId].SoldQuantityMt += sale.QuantityMt;
        }

        // مصرف‌هایی که از مسیر موتر ثبت شده‌اند (کرایه موتر، مصرف دستی/گروهی روی دیسپچ) فقط TruckDispatchId دارند؛
        // آن‌ها را از مسیر امن dispatch → رسید حمل → leg → کشتی وصل می‌کنیم (همان الگوی گمرک موتر پایین‌تر).
        var expenses = await _db.ExpenseTransactions
            .Include(e => e.ExpenseType)
            .Include(e => e.Contract)
            .Include(e => e.TransportLeg)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.InventoryTransportReceipt)
            .AsNoTracking()
            // کرایهٔ رسیدِ حمل جداگانه به‌عنوان «کرایه رسید حمل» از legPnl.ReceiptFreightExpenseUsd افزوده می‌شود
            // (بالاتر). این نوع مصرف (TRANSPORT-RECEIPT-FREIGHT) با TransportLegId هم ثبت می‌شود؛ اگر اینجا هم
            // شمرده شود کرایهٔ حمل دوبار حساب می‌شود. مثل InventoryTransportPnlService این نوع را کنار می‌گذاریم.
            .Where(e => !e.IsCancelled
                && (e.ExpenseType == null || e.ExpenseType.Code != InventoryTransportReceiptService.ReceiptFreightExpenseCode)
                && ((e.ShipmentId.HasValue && shipmentIdList.Contains(e.ShipmentId.Value))
                    || (e.TransportLegId.HasValue && legIds.Contains(e.TransportLegId.Value))
                    || (!e.TransportLegId.HasValue
                        && e.TruckDispatchId.HasValue
                        && e.TruckDispatch!.InventoryTransportReceipt != null
                        && legIds.Contains(e.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))))
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToListAsync();

        var expenseToShipment = new Dictionary<int, int>();
        foreach (var expense in expenses)
        {
            int shipmentId;
            if (expense.ShipmentId.HasValue && rollups.ContainsKey(expense.ShipmentId.Value))
            {
                shipmentId = expense.ShipmentId.Value;
            }
            else if (expense.TransportLegId.HasValue
                     && legToShipment.TryGetValue(expense.TransportLegId.Value, out var legShipmentId))
            {
                shipmentId = legShipmentId;
            }
            else if (expense.TruckDispatch?.InventoryTransportReceipt is { } dispatchReceipt
                     && legToShipment.TryGetValue(dispatchReceipt.InventoryTransportLegId, out var dispatchShipmentId))
            {
                shipmentId = dispatchShipmentId;
            }
            else
            {
                continue;
            }

            expenseToShipment[expense.Id] = shipmentId;
            rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
            {
                Id = expense.Id,
                ExpenseDate = expense.ExpenseDate,
                ExpenseTypeName = expense.ExpenseType != null ? (expense.ExpenseType.NamePersian ?? expense.ExpenseType.Name) : string.Empty,
                AmountUsd = expense.AmountUsd,
                Description = UserDescription(expense.Description),
                ContractNumber = expense.Contract?.ContractNumber,
                TruckDispatchLabel = expense.TruckDispatch == null
                    ? null
                    : $"#{expense.TruckDispatch.Id} - {expense.TruckDispatch.Truck!.PlateNumber}",
                AllocationQuantityMt = expense.TransportLeg?.QuantityMt
                    ?? expense.TruckDispatch?.LoadedQuantityMt
                    ?? 0m,
                SourceKey = ExpenseDisplaySourceKey(expense.Description, expense.Id),
                ExpenseTypeCategory = expense.ExpenseType?.Category
            });
            rollups[shipmentId].TotalOperationalExpensesUsd += expense.AmountUsd;
        }

        var customsRows = legIds.Count == 0
            ? []
            : await _db.CustomsDeclarations
                .Include(c => c.TransportLeg)
                    .ThenInclude(l => l!.SourcePurchaseContract)
                .AsNoTracking()
                .Where(c => c.TransportLegId.HasValue && legIds.Contains(c.TransportLegId.Value))
                .OrderByDescending(c => c.DeclarationDate)
                .ThenByDescending(c => c.Id)
                .ToListAsync();

        foreach (var customs in customsRows)
        {
            if (!customs.TransportLegId.HasValue
                || !legToShipment.TryGetValue(customs.TransportLegId.Value, out var shipmentId))
            {
                continue;
            }

            rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
            {
                Id = customs.Id,
                ExpenseDate = customs.DeclarationDate,
                ExpenseTypeName = "مصارف محصولی",
                AmountUsd = customs.TotalUsd,
                Description = BuildCustomsDescription(customs),
                ContractNumber = customs.TransportLeg?.SourcePurchaseContract?.ContractNumber,
                AllocationQuantityMt = customs.TransportLeg?.QuantityMt ?? 0m,
                SourceKey = $"CUSTOMS:{customs.Id}",
                IsCustoms = true
            });
            rollups[shipmentId].TotalOperationalExpensesUsd += customs.TotalUsd;
        }

        // گمرک‌هایی که از مسیر موتر (Dispatch) ثبت شده‌اند فقط TruckDispatchId دارند و TransportLegId ندارند،
        // پس در حلقهٔ بالا خوانده نمی‌شوند. آن‌ها را از مسیر امن dispatch → رسید حمل → leg → کشتی وصل می‌کنیم.
        // فیلترِ !TransportLegId.HasValue تضمین می‌کند گمرکی که هر دو مسیر را دارد فقط یک‌بار (در حلقهٔ leg بالا) حساب شود.
        var dispatchCustomsRows = legIds.Count == 0
            ? []
            : await _db.CustomsDeclarations
                .Include(c => c.TruckDispatch)
                    .ThenInclude(d => d!.InventoryTransportReceipt)
                .AsNoTracking()
                .Where(c => !c.TransportLegId.HasValue
                    && c.TruckDispatchId.HasValue
                    && c.TruckDispatch!.InventoryTransportReceipt != null
                    && legIds.Contains(c.TruckDispatch.InventoryTransportReceipt.InventoryTransportLegId))
                .OrderByDescending(c => c.DeclarationDate)
                .ThenByDescending(c => c.Id)
                .ToListAsync();

        foreach (var customs in dispatchCustomsRows)
        {
            var legId = customs.TruckDispatch!.InventoryTransportReceipt!.InventoryTransportLegId;
            if (!legToShipment.TryGetValue(legId, out var shipmentId))
            {
                continue;
            }

            rollups[shipmentId].Expenses.Add(new ShipmentPnlExpenseItemViewModel
            {
                Id = customs.Id,
                ExpenseDate = customs.DeclarationDate,
                ExpenseTypeName = "مصارف محصولی",
                AmountUsd = customs.TotalUsd,
                Description = BuildCustomsDescription(customs),
                ContractNumber = null,
                AllocationQuantityMt = customs.TruckDispatch.LoadedQuantityMt,
                SourceKey = $"CUSTOMS:{customs.Id}",
                IsCustoms = true
            });
            rollups[shipmentId].TotalOperationalExpensesUsd += customs.TotalUsd;
        }

        var saleIds = saleToShipment.Keys.ToList();
        var expenseIds = expenseToShipment.Keys.ToList();
        var ledgerEntries = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => (l.ShipmentId.HasValue && shipmentIdList.Contains(l.ShipmentId.Value))
                || (l.SourceType == "Sale" && saleIds.Contains(l.SourceId))
                || (l.SourceType == "Expense" && expenseIds.Contains(l.SourceId)))
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .ToListAsync();

        foreach (var ledger in ledgerEntries)
        {
            int shipmentId;
            if (ledger.ShipmentId.HasValue && rollups.ContainsKey(ledger.ShipmentId.Value))
            {
                shipmentId = ledger.ShipmentId.Value;
            }
            else if (ledger.SourceType == "Sale" && saleToShipment.TryGetValue(ledger.SourceId, out shipmentId))
            {
            }
            else if (ledger.SourceType == "Expense" && expenseToShipment.TryGetValue(ledger.SourceId, out shipmentId))
            {
            }
            else
            {
                continue;
            }

            rollups[shipmentId].LedgerEntries.Add(new ShipmentPnlLedgerItemViewModel
            {
                Id = ledger.Id,
                EntryDate = ledger.EntryDate,
                SideName = ledger.Side == LedgerSide.Debit ? "Debit" : "Credit",
                AmountUsd = ledger.AmountUsd,
                Currency = ledger.Currency,
                SourceType = LedgerSourceFa(ledger.SourceType),
                SourceId = ledger.SourceId,
                Reference = UserDescription(ledger.Reference),
                Description = ledger.Description
            });
        }

        return rollups;
    }

    private async Task<Dictionary<int, int>> BuildSaleShipmentMapFromTransportReceiptsAsync(
        IReadOnlyCollection<int> legIds,
        IReadOnlyDictionary<int, int> legToShipment)
    {
        if (legIds.Count == 0)
        {
            return [];
        }

        // فروش مستقیم روی رسید واگن
        var receiptLinks = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.SalesTransactionId.HasValue
                && legIds.Contains(r.InventoryTransportLegId))
            .Select(r => new { r.InventoryTransportLegId, SalesTransactionId = r.SalesTransactionId!.Value })
            .ToListAsync();

        // فروش موتری که با نقل مستقیم از رسید همان محموله بارگیری شده
        var dispatchLinks = await (
            from d in _db.TruckDispatches.AsNoTracking()
            where d.Status != DispatchStatus.Cancelled
                && d.SalesTransactionId.HasValue
                && d.InventoryTransportReceiptId.HasValue
            join r in _db.InventoryTransportReceipts.AsNoTracking()
                on d.InventoryTransportReceiptId!.Value equals r.Id
            where !r.IsCancelled && legIds.Contains(r.InventoryTransportLegId)
            select new { r.InventoryTransportLegId, SalesTransactionId = d.SalesTransactionId!.Value })
            .ToListAsync();

        return receiptLinks
            .Concat(dispatchLinks)
            .Where(r => legToShipment.ContainsKey(r.InventoryTransportLegId))
            .GroupBy(r => r.SalesTransactionId)
            .ToDictionary(g => g.Key, g => legToShipment[g.First().InventoryTransportLegId]);
    }

    private static (decimal? UnitCost, string Source) ResolvePurchaseUnitCost(
        InventoryTransportLeg leg,
        IReadOnlyDictionary<int, PurchaseAggregationSnapshot> purchaseSnapshots)
    {
        if (leg.PurchaseUnitCostUsd.HasValue && leg.PurchaseUnitCostUsd.Value > 0m)
        {
            return (leg.PurchaseUnitCostUsd.Value, "Transport leg actual cost");
        }

        if (purchaseSnapshots.TryGetValue(leg.SourcePurchaseContractId, out var snapshot)
            && snapshot.WeightedAveragePurchasePriceUsd.HasValue
            && snapshot.WeightedAveragePurchasePriceUsd.Value > 0m)
        {
            return (snapshot.WeightedAveragePurchasePriceUsd.Value, "Contract weighted average");
        }

        var contractFinalPrice = leg.SourcePurchaseContract is null
            ? null
            : ContractPricingAdapter.GetCanonicalFinalPrice(leg.SourcePurchaseContract);

        return contractFinalPrice.HasValue && contractFinalPrice.Value > 0m
            ? (contractFinalPrice.Value, "Contract final price")
            : (null, "Missing purchase cost");
    }

    private static string ResolveTransportReference(InventoryTransportLeg leg)
        => leg.WagonNumber
            ?? leg.BillOfLadingNumber
            ?? leg.RwbNo
            ?? $"#{leg.Id}";

    private static string ToTransportTypeName(LoadingTransportType transportType)
        => transportType switch
        {
            LoadingTransportType.Vessel => "کشتی",
            LoadingTransportType.Wagon => "واگن",
            LoadingTransportType.Truck => "موتر",
            _ => "نامشخص"
        };

    private static string ToTransportStatusName(InventoryTransportLegStatus status)
        => status switch
        {
            InventoryTransportLegStatus.Draft => "پیش‌نویس",
            InventoryTransportLegStatus.Loaded => "بارگیری‌شده",
            InventoryTransportLegStatus.InTransit => "در مسیر",
            InventoryTransportLegStatus.Received => "تحویل‌شده",
            InventoryTransportLegStatus.Cancelled => "لغوشده",
            _ => status.ToString()
        };

    private static string BuildInventoryLocationName(Terminal? terminal, StorageTank? tank)
    {
        var terminalName = terminal?.Name ?? "-";
        var tankName = StorageTankDisplay.BuildOptional(tank);
        return string.IsNullOrWhiteSpace(tankName)
            ? terminalName
            : $"{terminalName} / {tankName}";
    }

    private static string BuildDestinationName(InventoryTransportLeg leg)
    {
        if (leg.DestinationTerminal is not null)
        {
            return BuildInventoryLocationName(leg.DestinationTerminal, leg.DestinationStorageTank);
        }

        return leg.DestinationLocation?.Name
            ?? leg.RouteDescription
            ?? "-";
    }

    // شرحی که کاربر هنگام ثبت مصرف نوشته، اول رشتهٔ ذخیره‌شده است؛ متن فنی ردیابی بعد از « | » می‌آید.
    // برای نمایش فقط همان بخش کاربر را برمی‌گردانیم (دادهٔ کامل در دیتابیس دست‌نخورده می‌ماند).
    private static string? UserDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var separatorIndex = description.IndexOf('|');
        return (separatorIndex >= 0 ? description[..separatorIndex] : description).Trim();
    }

    private static string ExpenseDisplaySourceKey(string? description, int expenseId)
    {
        const string tag = "GroupKey:";
        if (!string.IsNullOrWhiteSpace(description))
        {
            var taggedPart = description
                .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(part => part.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
            if (taggedPart is not null)
            {
                var groupKey = taggedPart[tag.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(groupKey))
                {
                    return $"GROUP:{groupKey}";
                }
            }
        }

        return $"EXPENSE:{expenseId}";
    }

    // نوع منبعِ ثبت مالی را برای نمایش فارسی می‌کند؛ مقدار ناشناخته بدون تغییر برمی‌گردد.
    private static string LedgerSourceFa(string? sourceType) => sourceType switch
    {
        "Expense" => "مصرف",
        "Sale" => "فروش",
        "Loading" => "بارگیری",
        "Payment" => "پرداخت",
        "Receipt" => "رسید",
        "Purchase" => "خرید",
        _ => sourceType ?? "-"
    };

    private static string BuildCustomsDescription(CustomsDeclaration customs)
    {
        var parts = new[]
        {
            "CustomsDeclaration",
            customs.DeclarationReference,
            customs.WagonOrTruckNumber,
            customs.ConsignmentWeightMt > 0m ? $"{customs.ConsignmentWeightMt:N4} MT" : null,
            customs.Notes
        };

        return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? NormalizeShipmentShortageResponsibility(string? value)
        => value switch
        {
            ShipmentShortageResponsibilityTypes.CompanyLoss => ShipmentShortageResponsibilityTypes.CompanyLoss,
            ShipmentShortageResponsibilityTypes.SupplierDeduction => ShipmentShortageResponsibilityTypes.SupplierDeduction,
            ShipmentShortageResponsibilityTypes.ServiceProviderClaim => ShipmentShortageResponsibilityTypes.ServiceProviderClaim,
            ShipmentShortageResponsibilityTypes.PartnerShareDeduction => ShipmentShortageResponsibilityTypes.PartnerShareDeduction,
            ShipmentShortageResponsibilityTypes.Split => ShipmentShortageResponsibilityTypes.Split,
            _ => null
        };

    private static string? NormalizeFreeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeClaimStatus(string? value)
        => NormalizeFreeText(value) ?? "در انتظار";

    private static bool RequiresResponsiblePartyName(string responsibilityType)
        => responsibilityType is ShipmentShortageResponsibilityTypes.SupplierDeduction
            or ShipmentShortageResponsibilityTypes.ServiceProviderClaim
            or ShipmentShortageResponsibilityTypes.PartnerShareDeduction;

    private static string ResponsibilityLabelFa(string responsibilityType) => responsibilityType switch
    {
        ShipmentShortageResponsibilityTypes.CompanyLoss => "ضرر شرکت",
        ShipmentShortageResponsibilityTypes.SupplierDeduction => "کسر از حساب تأمین‌کننده",
        ShipmentShortageResponsibilityTypes.ServiceProviderClaim => "طلب از شرکت خدماتی",
        ShipmentShortageResponsibilityTypes.PartnerShareDeduction => "کسر از سهم شریک",
        ShipmentShortageResponsibilityTypes.Split => "تقسیم بین چند طرف",
        _ => "کسری کشتی"
    };

    private static string FinancialTreatmentLabelFa(string responsibilityType) => responsibilityType switch
    {
        ShipmentShortageResponsibilityTypes.CompanyLoss => "ثبت ضرر شرکت؛ صندوق و بانک تغییر نمی‌کند",
        ShipmentShortageResponsibilityTypes.SupplierDeduction => "کسر از حساب تأمین‌کننده؛ بدون پرداخت نقدی",
        ShipmentShortageResponsibilityTypes.ServiceProviderClaim => "ثبت طلب از شرکت خدماتی؛ تا زمان وصول نقد تغییر نمی‌کند",
        ShipmentShortageResponsibilityTypes.PartnerShareDeduction => "کسر از سهم شریک؛ صندوق و بانک تغییر نمی‌کند",
        ShipmentShortageResponsibilityTypes.Split => "تقسیم مسئولیت بین چند طرف؛ بدون پرداخت نقدی",
        _ => "ثبت کسری کشتی"
    };

    private static List<ShipmentDirectLossSplitLineInput> NormalizeSplitLines(IEnumerable<ShipmentDirectLossSplitLineInput>? lines)
        => lines?
            .Select(line => new ShipmentDirectLossSplitLineInput
            {
                ResponsibilityType = NormalizeShipmentShortageResponsibility(line.ResponsibilityType) is { } type
                    && type != ShipmentShortageResponsibilityTypes.Split
                        ? type
                        : null,
                ResponsiblePartyName = NormalizeFreeText(line.ResponsiblePartyName),
                QuantityMt = decimal.Round(Math.Max(line.QuantityMt, 0m), 4, MidpointRounding.AwayFromZero),
                AmountUsd = line.AmountUsd.HasValue && line.AmountUsd.Value > 0m
                    ? decimal.Round(line.AmountUsd.Value, 2, MidpointRounding.AwayFromZero)
                    : null,
                Status = NormalizeFreeText(line.Status),
                Notes = NormalizeFreeText(line.Notes)
            })
            .Where(line => line.QuantityMt > 0m)
            .ToList()
            ?? [];

    private static string? ValidateSplitLines(IReadOnlyList<ShipmentDirectLossSplitLineInput> lines, decimal expectedQuantityMt)
    {
        if (lines.Count == 0)
        {
            return "برای تقسیم کسری، حداقل یک ردیف مسئولیت لازم است.";
        }

        foreach (var line in lines)
        {
            if (line.ResponsibilityType is null)
            {
                return "نوع حساب در ردیف‌های تقسیم معتبر نیست.";
            }

            if (RequiresResponsiblePartyName(line.ResponsibilityType)
                && string.IsNullOrWhiteSpace(line.ResponsiblePartyName))
            {
                return "در ردیف‌های تقسیم، نام طرف مسئول برای تأمین‌کننده/خدماتی/شریک الزامی است.";
            }
        }

        var total = lines.Sum(line => line.QuantityMt);
        return Math.Abs(total - expectedQuantityMt) > 0.0001m
            ? "جمع مقدار ردیف‌های تقسیم باید برابر مقدار کل کسری باشد."
            : null;
    }

    private static decimal EstimateShipmentLossValueUsd(
        decimal lossQuantityMt,
        IReadOnlyList<ShipmentContractLineViewModel> contractLines,
        int? contractId)
    {
        if (lossQuantityMt <= 0m)
        {
            return 0m;
        }

        if (contractId.HasValue)
        {
            var line = contractLines.FirstOrDefault(c => c.ContractId == contractId.Value);
            return line?.UnitPriceUsd.HasValue == true && line.UnitPriceUsd.Value > 0m
                ? RoundMoney(lossQuantityMt * line.UnitPriceUsd.Value)
                : 0m;
        }

        var weights = contractLines
            .Select(line => new
            {
                Line = line,
                Weight = line.ShortageRegisterableQuantityMt
            })
            .Where(x => x.Weight > 0m && x.Line.UnitPriceUsd.HasValue && x.Line.UnitPriceUsd.Value > 0m)
            .ToList();
        var totalWeight = weights.Sum(x => x.Weight);
        if (totalWeight <= 0m)
        {
            return 0m;
        }

        var value = 0m;
        foreach (var weight in weights)
        {
            var quantity = lossQuantityMt * weight.Weight / totalWeight;
            value += quantity * weight.Line.UnitPriceUsd!.Value;
        }

        return RoundMoney(value);
    }

    private static string BuildShipmentShortageReference(
        int shipmentId,
        DateTime eventDate,
        decimal lossQuantityMt,
        string responsibilityType,
        int? contractId)
    {
        var quantity = lossQuantityMt.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return $"SHIPMENT-LOSS:{shipmentId}:{eventDate:yyyyMMdd}:{quantity}:{responsibilityType}:{contractId?.ToString() ?? "ALL"}";
    }

    private static string? BuildShipmentShortageNotes(
        string? userNotes,
        decimal estimatedAmountUsd,
        string claimStatus,
        IReadOnlyList<ShipmentDirectLossSplitLineInput> splitLines)
    {
        var parts = new List<string>();
        if (estimatedAmountUsd > 0m)
        {
            parts.Add($"ارزش تخمینی: {estimatedAmountUsd:N2} USD");
        }

        if (!string.IsNullOrWhiteSpace(claimStatus))
        {
            parts.Add($"وضعیت: {claimStatus}");
        }

        if (splitLines.Count > 0)
        {
            var splitText = string.Join("؛ ", splitLines.Select(line =>
                $"{ResponsibilityLabelFa(line.ResponsibilityType!)}"
                + (string.IsNullOrWhiteSpace(line.ResponsiblePartyName) ? "" : $" - {line.ResponsiblePartyName}")
                + $" - {line.QuantityMt:N4} MT"
                + (line.AmountUsd.HasValue ? $" - {line.AmountUsd.Value:N2} USD" : "")
                + (string.IsNullOrWhiteSpace(line.Status) ? "" : $" - {line.Status}")
                + (string.IsNullOrWhiteSpace(line.Notes) ? "" : $" - {line.Notes}")));
            parts.Add($"تقسیم: {splitText}");
        }

        var normalizedNotes = NormalizeFreeText(userNotes);
        if (!string.IsNullOrWhiteSpace(normalizedNotes))
        {
            parts.Add(normalizedNotes);
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private async Task<int?> ResolveShipmentProductIdAsync(int shipmentId, Shipment shipment)
    {
        var contractProductIds = await _db.ShipmentContracts
            .AsNoTracking()
            .Where(sc => sc.ShipmentId == shipmentId)
            .Join(
                _db.Contracts.AsNoTracking(),
                sc => sc.ContractId,
                c => c.Id,
                (_, c) => c.ProductId)
            .Distinct()
            .ToListAsync();

        if (contractProductIds.Count == 1)
        {
            return contractProductIds[0];
        }

        if (shipment.Contract?.ProductId > 0)
        {
            return shipment.Contract.ProductId;
        }

        var transportProductIds = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId && l.Status != InventoryTransportLegStatus.Cancelled)
            .Select(l => l.ProductId)
            .Distinct()
            .ToListAsync();

        return transportProductIds.Count == 1 ? transportProductIds[0] : null;
    }

    private async Task<decimal> GetDirectShipmentLossQuantityMtAsync(int shipmentId)
    {
        var directLosses = await GetDirectShipmentLossesAsync(shipmentId);
        return directLosses.Sum(l => l.QuantityMt);
    }

    private async Task<List<DirectShipmentLossRow>> GetDirectShipmentLossesAsync(int shipmentId)
        => await _db.LossEvents
            .AsNoTracking()
            .Where(l => l.ShipmentId == shipmentId
                && !l.IsCancelled
                && l.TransportLegId == null
                && l.LoadingRegisterId == null
                && l.LoadingReceiptId == null
                && l.TruckDispatchId == null
                && l.SalesTransactionId == null
                && l.DifferenceQuantityMt > 0m)
            .Select(l => new DirectShipmentLossRow(l.ContractId, l.DifferenceQuantityMt))
            .ToListAsync();

    private static Dictionary<int, decimal> AllocateDirectLossesByContract(
        IReadOnlyList<DirectShipmentLossRow> directLosses,
        IReadOnlyDictionary<int, decimal> lossCapacityByContract)
    {
        var result = lossCapacityByContract.Keys.ToDictionary(id => id, _ => 0m);
        if (directLosses.Count == 0 || lossCapacityByContract.Count == 0)
        {
            return result;
        }

        var unassignedLoss = 0m;
        foreach (var loss in directLosses)
        {
            if (loss.ContractId.HasValue
                && result.ContainsKey(loss.ContractId.Value)
                && lossCapacityByContract.TryGetValue(loss.ContractId.Value, out var capacity))
            {
                var available = Math.Max(capacity - result[loss.ContractId.Value], 0m);
                var applied = Math.Min(loss.QuantityMt, available);
                result[loss.ContractId.Value] += applied;
                unassignedLoss += Math.Max(loss.QuantityMt - applied, 0m);
                continue;
            }

            unassignedLoss += loss.QuantityMt;
        }

        if (unassignedLoss <= 0m)
        {
            return result;
        }

        var weights = lossCapacityByContract
            .Select(kvp => new
            {
                ContractId = kvp.Key,
                Remaining = Math.Max(kvp.Value - result[kvp.Key], 0m)
            })
            .Where(x => x.Remaining > 0m)
            .ToList();

        var totalRemaining = weights.Sum(x => x.Remaining);
        if (totalRemaining <= 0m)
        {
            return result;
        }

        var lossToAllocate = Math.Min(unassignedLoss, totalRemaining);
        var allocated = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            var share = i == weights.Count - 1
                ? lossToAllocate - allocated
                : decimal.Round(lossToAllocate * weights[i].Remaining / totalRemaining, 4, MidpointRounding.AwayFromZero);
            share = Math.Min(share, weights[i].Remaining);
            result[weights[i].ContractId] += share;
            allocated += share;
        }

        return result;
    }

    private static decimal RoundMoney(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static string? ResolveSingleOrMixed(IEnumerable<string?> values, string mixedLabel)
    {
        var distinct = values
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "-")
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => mixedLabel
        };
    }

    private sealed class ShipmentFinancialRollup
    {
        public ShipmentFinancialRollup(int shipmentId)
        {
            ShipmentId = shipmentId;
        }

        public int ShipmentId { get; }
        public decimal TotalSalesUsd { get; set; }
        public decimal SoldQuantityMt { get; set; }
        public decimal TotalPurchaseCostUsd { get; set; }
        public decimal TotalOperationalExpensesUsd { get; set; }
        public List<ShipmentPnlTransportLegItemViewModel> TransportLegs { get; } = [];
        public List<ShipmentPnlSalesItemViewModel> Sales { get; } = [];
        public List<ShipmentPnlExpenseItemViewModel> Expenses { get; } = [];
        public List<ShipmentPnlLedgerItemViewModel> LedgerEntries { get; } = [];
    }

    private sealed record DirectShipmentLossRow(int? ContractId, decimal QuantityMt);
}
