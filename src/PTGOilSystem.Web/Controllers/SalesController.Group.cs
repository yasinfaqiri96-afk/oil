using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Controllers;

// ثبت فروش گروهی — یک رکورد اصلی SalesBatch + هر ردیف یک SalesTransaction عادی با SalesBatchId.
// هر ردیف از همان primitiveهای فروشِ موجود استفاده می‌کند تا Ledger/موجودی/سود‌وزیان دقیقاً یکسان بماند:
//   • موجودی مخزن → SalesTransaction(TerminalStock) + InventoryMovement خروج + Ledger + Lineage.
//   • موتر در جریان → SalesTransaction(InTransit) + Ledger + لینک دیسپچ (بدون خروج مجدد موجودی).
//   • واگن / انتقال در مسیر → رسید DirectSale از InventoryTransportReceiptService (بدون خروج مجدد موجودی).
public partial class SalesController
{
    private const decimal QtyEpsilon = 0.0001m;

    // مالکِ ردیفِ فروشی که با primitiveهای زیر ساخته می‌شود: یا یک SalesBatch (فروش گروهی)
    // یا یک PreSaleOrder (تحویل پیش‌فروش). خودِ ردیف در هر دو حالت SalesTransaction عادی است.
    private sealed record SaleLineOwner(int? SalesBatchId, int? PreSaleOrderId, string Reference);

    // ---------- بارگذاری منابع قابل‌فروش ----------

    private sealed record StockTupleKey(int ProductId, int TerminalId, int StorageTankId, int ContractId);

    private async Task<List<GroupSaleSourceItem>> LoadSellableSourcesAsync()
    {
        var items = new List<GroupSaleSourceItem>();
        items.AddRange(await LoadSellableTerminalStockAsync());
        items.AddRange(await LoadSellableTruckDispatchesAsync());
        items.AddRange(await LoadSellableLegsAsync());

        return items
            .OrderByDescending(i => i.MoveDate)
            .ThenBy(i => i.KindLabel)
            .ThenByDescending(i => i.Id)
            .ToList();
    }

    private async Task<List<GroupSaleSourceItem>> LoadSellableTerminalStockAsync()
    {
        var asOf = _businessClock.Today;

        var balances = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.StorageTankId != null)
            .Select(m => new
            {
                m.ProductId,
                m.TerminalId,
                StorageTankId = m.StorageTankId!.Value,
                ContractId = m.ContractId
                    ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                        ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                        : null),
                m.Direction,
                m.QuantityMt
            })
            .Where(x => x.ContractId != null)
            .GroupBy(x => new { x.ProductId, x.TerminalId, x.StorageTankId, ContractId = x.ContractId!.Value })
            .Select(g => new
            {
                g.Key,
                Net = g.Sum(x =>
                    x.Direction == MovementDirection.In || x.Direction == MovementDirection.Adjustment
                        ? x.QuantityMt
                        : x.Direction == MovementDirection.Out || x.Direction == MovementDirection.Transfer
                            ? -x.QuantityMt
                            : 0m)
            })
            .Where(x => x.Net > QtyEpsilon)
            .ToListAsync();

        if (balances.Count == 0)
        {
            return [];
        }

        var contractIds = balances.Select(b => b.Key.ContractId).Distinct().ToArray();
        var terminalIds = balances.Select(b => b.Key.TerminalId).Distinct().ToArray();
        var tankIds = balances.Select(b => b.Key.StorageTankId).Distinct().ToArray();
        var productIds = balances.Select(b => b.Key.ProductId).Distinct().ToArray();

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => contractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .Select(c => new { c.Id, c.ContractNumber, c.CompanyId, CompanyName = c.Company != null ? c.Company.Name : "" })
            .ToDictionaryAsync(c => c.Id);
        var terminals = await _db.Terminals.AsNoTracking()
            .Where(t => terminalIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name);
        var tanks = await _db.StorageTanks.AsNoTracking()
            .Where(t => tankIds.Contains(t.Id))
            .Select(t => new { t.Id, Display = t.DisplayName ?? t.TankCode }).ToDictionaryAsync(t => t.Id, t => t.Display);
        var products = await _db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name);

        var result = new List<GroupSaleSourceItem>();
        foreach (var b in balances)
        {
            if (!contracts.TryGetValue(b.Key.ContractId, out var contract))
            {
                continue; // فقط قرارداد خرید معتبر
            }

            var free = await _stock.GetFreeQuantityMtAsync(
                b.Key.ProductId,
                terminalId: b.Key.TerminalId,
                contractId: b.Key.ContractId,
                storageTankId: b.Key.StorageTankId,
                asOfUtc: asOf);
            if (free <= QtyEpsilon)
            {
                continue;
            }

            result.Add(new GroupSaleSourceItem
            {
                Key = $"Stock:{b.Key.ProductId}-{b.Key.TerminalId}-{b.Key.StorageTankId}-{b.Key.ContractId}",
                Kind = GroupSaleSourceKind.TerminalStock,
                KindLabel = "موجودی مخزن",
                Id = 0,
                VehicleKind = "مخزن",
                Number = tanks.GetValueOrDefault(b.Key.StorageTankId, $"#{b.Key.StorageTankId}"),
                Route = terminals.GetValueOrDefault(b.Key.TerminalId, $"#{b.Key.TerminalId}"),
                ProductName = products.GetValueOrDefault(b.Key.ProductId, ""),
                CompanyName = contract.CompanyName,
                ContractNumber = contract.ContractNumber,
                AvailableMt = decimal.Round(free, 4, MidpointRounding.AwayFromZero),
                IsFullVehicle = false,
                StatusLabel = "قابل فروش",
                MoveDate = asOf,
                ProductId = b.Key.ProductId,
                TerminalId = b.Key.TerminalId,
                StorageTankId = b.Key.StorageTankId,
                SourcePurchaseContractId = b.Key.ContractId,
                CompanyId = contract.CompanyId
            });
        }

        return result;
    }

    private async Task<List<GroupSaleSourceItem>> LoadSellableTruckDispatchesAsync()
    {
        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => (d.Status == DispatchStatus.Loaded || d.Status == DispatchStatus.InTransit)
                        && d.SalesTransactionId == null)
            .OrderByDescending(d => d.DispatchDate)
            .Select(d => new
            {
                d.Id,
                TruckPlate = d.Truck != null ? d.Truck.PlateNumber : null,
                ProductName = d.Product != null ? d.Product.Name : "",
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : "",
                CompanyName = d.Contract != null && d.Contract.Company != null ? d.Contract.Company.Name : "",
                DestinationName = d.DestinationLocation != null ? d.DestinationLocation.Name : null,
                d.LoadedQuantityMt,
                d.DischargedQuantityMt,
                d.IsFreightSettled,
                d.Status,
                d.DispatchDate
            })
            .ToListAsync();

        // وزن مؤثر فروش = وزن تخلیه‌شده (اگر کرایه تسویه شده)، وگرنه وزن بارگیری.
        return dispatches.Select(d => new GroupSaleSourceItem
        {
            Key = $"Dispatch:{d.Id}",
            Kind = GroupSaleSourceKind.TruckDispatch,
            KindLabel = "موتر در جریان",
            Id = d.Id,
            VehicleKind = "موتر",
            Number = d.TruckPlate ?? $"#{d.Id}",
            Route = BuildGroupRoute(d.ContractNumber, d.DestinationName),
            ProductName = d.ProductName,
            CompanyName = d.CompanyName,
            ContractNumber = d.ContractNumber,
            AvailableMt = decimal.Round(d.DischargedQuantityMt ?? d.LoadedQuantityMt, 4, MidpointRounding.AwayFromZero),
            IsFullVehicle = true,
            StatusLabel = d.IsFreightSettled ? "کرایه تسویه‌شده"
                : d.Status == DispatchStatus.InTransit ? "در راه" : "بارگیری‌شده",
            MoveDate = d.DispatchDate
        }).ToList();
    }

    private async Task<List<GroupSaleSourceItem>> LoadSellableLegsAsync()
    {
        // حمل‌های Loaded/InTransit که هنوز به موجودی تخلیه/فروخته نشده‌اند. رسیدِ «فقط تسویهٔ کرایه»
        // (دریافت صفر، بدون فروش) مانع فروش نیست؛ بار (وزن تخلیه) هنوز روی وسیله و قابل فروش است.
        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => (l.Status == InventoryTransportLegStatus.Loaded || l.Status == InventoryTransportLegStatus.InTransit)
                        // رسیدِ ToInventory/DirectDispatch حمل را از فهرست خارج می‌کند (رفتار تاریخی).
                        // رسیدِ DirectSale مانع نیست: حملِ نیمه‌فروخته باید تا صفرشدن باقیمانده (که با
                        // Received شدن از فهرست خارج می‌شود) دوباره دیده شود تا تحویل جزئیِ بعدی ممکن باشد.
                        && !_db.InventoryTransportReceipts.Any(r => r.InventoryTransportLegId == l.Id && !r.IsCancelled
                            && r.ReceiptDestination != InventoryTransportReceiptDestination.DirectSale
                            && (r.ReceivedQuantityMt > 0m || r.SalesTransactionId != null)))
            .OrderByDescending(l => l.LoadedDate)
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                l.WagonNumber,
                l.RwbNo,
                TruckPlate = l.Truck != null ? l.Truck.PlateNumber : null,
                ProductName = l.Product != null ? l.Product.Name : "",
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : "",
                CompanyName = l.SourcePurchaseContract != null && l.SourcePurchaseContract.Company != null
                    ? l.SourcePurchaseContract.Company.Name : "",
                SourceName = l.SourceTerminal != null ? l.SourceTerminal.Name : null,
                DestinationName = l.DestinationTerminal != null
                    ? l.DestinationTerminal.Name
                    : (l.DestinationLocation != null ? l.DestinationLocation.Name : null),
                l.QuantityMt,
                l.IsFreightSettled,
                l.Status,
                l.LoadedDate
            })
            .ToListAsync();

        if (legs.Count == 0)
        {
            return [];
        }

        // مقدار مصرف‌شده (کسری تسویه) هر حمل تا وزن مؤثر فروش = باقیمانده = مقدار حمل − مصرف = وزن تخلیه.
        var legIds = legs.Select(l => l.Id).ToList();
        var consumedByLeg = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
            .GroupBy(r => r.InventoryTransportLegId)
            .Select(g => new { LegId = g.Key, Mt = g.Sum(r => r.ReceivedQuantityMt + r.ShortageQuantityMt) })
            .ToDictionaryAsync(g => g.LegId, g => g.Mt);

        var result = new List<GroupSaleSourceItem>();
        foreach (var l in legs)
        {
            consumedByLeg.TryGetValue(l.Id, out var consumedMt);
            var availableMt = decimal.Round(l.QuantityMt - consumedMt, 4, MidpointRounding.AwayFromZero);
            if (availableMt <= QtyEpsilon)
            {
                continue;
            }

            var isWagon = l.TransportType == LoadingTransportType.Wagon;
            result.Add(new GroupSaleSourceItem
            {
                Key = $"Leg:{l.Id}",
                Kind = isWagon ? GroupSaleSourceKind.WagonLeg : GroupSaleSourceKind.TransportLeg,
                KindLabel = isWagon ? "واگن در جریان" : "انتقال در مسیر",
                Id = l.Id,
                VehicleKind = isWagon ? "واگن" : (l.TransportType == LoadingTransportType.Truck ? "موتر" : "انتقال"),
                Number = l.WagonNumber ?? l.RwbNo ?? l.TruckPlate ?? $"#{l.Id}",
                Route = BuildGroupRoute(l.SourceName, l.DestinationName),
                ProductName = l.ProductName,
                CompanyName = l.CompanyName,
                ContractNumber = l.ContractNumber,
                AvailableMt = availableMt,
                IsFullVehicle = true,
                StatusLabel = l.IsFreightSettled ? "کرایه تسویه‌شده"
                    : l.Status == InventoryTransportLegStatus.InTransit ? "در راه" : "بارگیری‌شده",
                MoveDate = l.LoadedDate
            });
        }

        return result;
    }

    // شمارهٔ محمولهٔ منبعِ فروش را از قرارداد خرید پیدا می‌کند تا فروش گروهی در «فروشات محموله» شمرده شود.
    // (فروش حمل‌ها ShipmentId را از خود leg می‌گیرد؛ این فقط برای موجودی مخزن و موتر است.)
    // اگر قرارداد به چند محموله وصل باشد (مبهم) یا هیچ‌کدام، null برمی‌گرداند و رفتار قبلی حفظ می‌شود.
    private async Task<int?> ResolveShipmentIdForContractAsync(int purchaseContractId)
    {
        if (purchaseContractId <= 0)
        {
            return null;
        }

        var fromShipmentContracts = await _db.ShipmentContracts.AsNoTracking()
            .Where(sc => sc.ContractId == purchaseContractId)
            .Select(sc => sc.ShipmentId)
            .Distinct()
            .ToListAsync();
        if (fromShipmentContracts.Count == 1)
        {
            return fromShipmentContracts[0];
        }

        if (fromShipmentContracts.Count == 0)
        {
            var fromLegs = await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => l.SourcePurchaseContractId == purchaseContractId && l.ShipmentId != null)
                .Select(l => l.ShipmentId!.Value)
                .Distinct()
                .ToListAsync();
            if (fromLegs.Count == 1)
            {
                return fromLegs[0];
            }
        }

        return null;
    }

    // محمولهٔ یک فروشِ از موجودی مخزن، از منشأ فیزیکیِ خودِ مخزن استنباط می‌شود، نه از قرارداد.
    // کاربرِ صفحهٔ فروش نمی‌داند بارِ داخل مخزن از کدام قرارداد خرید آمده (به همین دلیل انتخاب
    // قرارداد منبع اختیاری است)، و یک قرارداد می‌تواند به چند محموله وصل باشد که
    // ResolveShipmentIdForContractAsync را به‌درستی مبهم و بی‌جواب می‌گذارد.
    // اما موجودی فقط از راه رسیدهای انتقال به مخزن می‌رسد و هر رسید محمولهٔ خودش را می‌داند؛
    // اگر همهٔ رسیدهای یک مخزن به یک محموله برسند، فروشِ آن مخزن هم به همان محموله تعلق دارد.
    // در حالت مخلوط (چند محموله در یک مخزن) عمداً null برمی‌گردد تا حدس زده نشود.
    private async Task<int?> ResolveShipmentIdForTankAsync(int storageTankId)
    {
        if (storageTankId <= 0)
        {
            return null;
        }

        var fromReceipts = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.DestinationStorageTankId == storageTankId
                && r.InventoryTransportLeg != null
                && r.InventoryTransportLeg.ShipmentId != null)
            .Select(r => r.InventoryTransportLeg!.ShipmentId!.Value)
            .Distinct()
            .ToListAsync();

        return fromReceipts.Count == 1 ? fromReceipts[0] : null;
    }

    private static string BuildGroupRoute(string? source, string? destination)
    {
        if (!string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(destination))
        {
            return $"{(string.IsNullOrWhiteSpace(source) ? "؟" : source)} ← {(string.IsNullOrWhiteSpace(destination) ? "؟" : destination)}";
        }

        return "-";
    }

    private async Task PopulateGroupSaleLookupsAsync(GroupSaleCreateViewModel model)
    {
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync(),
            "Id", "Name", model.CustomerId > 0 ? model.CustomerId : null);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code)
                .Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
    }

    // ---------- CreateGroup ----------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CreateGroup(string? returnUrl = null)
    {
        var model = new GroupSaleCreateViewModel
        {
            SaleDate = _businessClock.Today,
            Currency = SystemCurrency.BaseCurrencyCode,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var local) ? local : null
        };

        await PopulateGroupSaleLookupsAsync(model);
        ViewBag.Sources = await LoadSellableSourcesAsync();
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(GroupSaleCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        model.PaymentNote = string.IsNullOrWhiteSpace(model.PaymentNote) ? null : model.PaymentNote.Trim();

        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.CustomerId && c.IsActive);
        if (customer is null)
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        if (model.UnitPriceInCurrency <= 0m)
        {
            ModelState.AddModelError(nameof(model.UnitPriceInCurrency), "نرخ فروش هر تن باید بزرگ‌تر از صفر باشد.");
        }

        if (model.SaleDate == default)
        {
            ModelState.AddModelError(nameof(model.SaleDate), "تاریخ فروش الزامی است.");
        }

        // انتخاب‌ها: dedupe (وسیله با Kind:Id، مخزن با tuple).
        var selections = (model.Items ?? [])
            .Where(i => i.Kind == GroupSaleSourceKind.TerminalStock
                ? (i.ProductId > 0 && i.TerminalId > 0 && i.StorageTankId > 0 && i.SourcePurchaseContractId > 0)
                : i.Id > 0)
            .GroupBy(i => i.Kind == GroupSaleSourceKind.TerminalStock
                ? $"Stock:{i.ProductId}-{i.TerminalId}-{i.StorageTankId}-{i.SourcePurchaseContractId}"
                : $"{i.Kind}:{i.Id}")
            .Select(g => g.First())
            .ToList();

        if (selections.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "حداقل یک منبع فروش را انتخاب کنید.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateGroupSaleLookupsAsync(model);
            ViewBag.Sources = await LoadSellableSourcesAsync();
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(model.Currency, model.SaleDate.Date, model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            await PopulateGroupSaleLookupsAsync(model);
            ViewBag.Sources = await LoadSellableSourcesAsync();
            return View(model);
        }

        var receiptService = new InventoryTransportReceiptService(_db, _currencyConversion, _lineage);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var batch = new SalesBatch
            {
                CustomerId = model.CustomerId,
                SaleDate = model.SaleDate.Date,
                Currency = conversion.SourceCurrencyCode,
                AppliedFxRateToUsd = conversion.AppliedRateToBase,
                UnitPriceInCurrency = model.UnitPriceInCurrency,
                LineCount = selections.Count,
                Notes = model.Notes,
                PaymentNote = model.PaymentNote
            };
            _db.SalesBatches.Add(batch);
            await _db.SaveChangesAsync();

            batch.BatchNumber = $"GSALE-{batch.Id}";
            await _db.SaveChangesAsync();

            decimal totalQty = 0m, totalInCurrency = 0m, totalUsd = 0m;
            var lineNo = 0;

            foreach (var selection in selections)
            {
                lineNo++;
                var invoice = $"{batch.BatchNumber}-{lineNo}";

                var owner = new SaleLineOwner(batch.Id, null, batch.BatchNumber);
                var sale = selection.Kind switch
                {
                    GroupSaleSourceKind.TerminalStock =>
                        await CreateTerminalStockLineAsync(owner, selection, model, conversion, invoice),
                    GroupSaleSourceKind.TruckDispatch =>
                        await CreateTruckDispatchLineAsync(owner, selection, model, conversion, invoice),
                    _ => await CreateLegLineAsync(owner, selection, model, conversion, invoice, receiptService)
                };

                totalQty += sale.QuantityMt;
                totalInCurrency += sale.TotalInCurrency;
                totalUsd += sale.TotalUsd;
            }

            batch.TotalQuantityMt = decimal.Round(totalQty, 4, MidpointRounding.AwayFromZero);
            batch.TotalInCurrency = decimal.Round(totalInCurrency, 4, MidpointRounding.AwayFromZero);
            batch.TotalUsd = decimal.Round(totalUsd, 4, MidpointRounding.AwayFromZero);
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(SalesBatch),
                batch.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("BatchNumber", batch.BatchNumber),
                    ("CustomerId", batch.CustomerId),
                    ("SaleDate", batch.SaleDate),
                    ("Currency", batch.Currency),
                    ("UnitPriceInCurrency", batch.UnitPriceInCurrency),
                    ("TotalQuantityMt", batch.TotalQuantityMt),
                    ("TotalUsd", batch.TotalUsd),
                    ("LineCount", batch.LineCount)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"فروش گروهی {batch.BatchNumber} برای {selections.Count} منبع ثبت شد.";
            return RedirectToAction(nameof(GroupDetails), new { id = batch.Id });
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to create group sale batch.");
            ModelState.AddModelError(string.Empty, "ثبت فروش گروهی انجام نشد. لطفاً منابع و مقادیر را بررسی و دوباره تلاش کنید.");
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        await PopulateGroupSaleLookupsAsync(model);
        ViewBag.Sources = await LoadSellableSourcesAsync();
        return View(model);
    }

    // ---------- ساخت ردیف‌ها (هر کدام از primitiveهای فروشِ موجود) ----------

    private async Task<SalesTransaction> CreateTerminalStockLineAsync(
        SaleLineOwner owner,
        GroupSaleSelectedInput input,
        GroupSaleCreateViewModel model,
        CurrencyConversionResult conversion,
        string invoice)
    {
        var qty = input.QuantityMt ?? 0m;
        if (qty <= 0m)
        {
            throw new BusinessRuleException("GROUP_SALE_QTY_REQUIRED", "برای فروش از موجودی مخزن، مقدار هر ردیف باید بزرگ‌تر از صفر باشد.");
        }

        var contract = await _db.Contracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == input.SourcePurchaseContractId && c.ContractType == ContractType.Purchase)
            ?? throw new BusinessRuleException("GROUP_SALE_STOCK_CONTRACT_INVALID", "قرارداد خرید منبع موجودی معتبر نیست.");

        if (contract.ProductId != input.ProductId)
        {
            throw new BusinessRuleException("GROUP_SALE_STOCK_PRODUCT_MISMATCH", "کالای منبع موجودی با قرارداد هم‌خوان نیست.");
        }

        var lockedTank = await LockStorageTankAsync(input.StorageTankId)
            ?? throw new BusinessRuleException("GROUP_SALE_STOCK_TANK_NOT_FOUND", "مخزن منبع دیگر معتبر نیست.");
        if (lockedTank.TerminalId != input.TerminalId)
        {
            throw new BusinessRuleException("GROUP_SALE_STOCK_TANK_TERMINAL", "مخزن به ترمینال انتخابی تعلق ندارد.");
        }

        var allocations = await EnsureSufficientTerminalStockAsync(
            input.ProductId, qty, model.SaleDate.Date,
            input.TerminalId, input.StorageTankId, contract.CompanyId, input.SourcePurchaseContractId);

        var totalInCurrency = decimal.Round(qty * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var sale = new SalesTransaction
        {
            ContractId = null,
            CompanyId = contract.CompanyId,
            CustomerId = model.CustomerId,
            ProductId = input.ProductId,
            ShipmentId = await ResolveShipmentIdForContractAsync(input.SourcePurchaseContractId),
            SaleStage = SaleStage.TerminalStock,
            SalesBatchId = owner.SalesBatchId,
            PreSaleOrderId = owner.PreSaleOrderId,
            InvoiceNumber = invoice,
            SaleDate = model.SaleDate.Date,
            QuantityMt = qty,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = model.Notes
        };
        _db.SalesTransactions.Add(sale);
        await _db.SaveChangesAsync();

        foreach (var allocation in allocations)
        {
            var movement = new InventoryMovement
            {
                ProductId = input.ProductId,
                ContractId = allocation.ContractId,
                TerminalId = input.TerminalId,
                StorageTankId = input.StorageTankId,
                SalesTransactionId = sale.Id,
                Direction = MovementDirection.Out,
                MovementDate = sale.SaleDate,
                QuantityMt = allocation.QuantityMt,
                ReferenceDocument = sale.InvoiceNumber,
                Notes = BuildSaleInventoryNotes(sale.SaleStage, sale.InvoiceNumber, $"SaleId={sale.Id} | {owner.Reference}")
            };
            // قفل هم‌زمانی + چک نقطه‌ای در تاریخ خودِ فروش، داخل تراکنش گروهی.
            await _stock.AcquireStockMutationLockAsync(movement);
            await _stock.EnsureSufficientStockForMovementAsync(movement);
            await _stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(movement);
            _db.InventoryMovements.Add(movement);
        }
        await _db.SaveChangesAsync();

        await _lineage.AllocateSaleAsync(sale, input.SourcePurchaseContractId, input.TerminalId, input.StorageTankId);

        var ledger = SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: input.SourcePurchaseContractId);
        _db.LedgerEntries.Add(ledger);
        await _db.SaveChangesAsync();

        await PostSaleAccountingAsync(sale);

        return sale;
    }

    private async Task<SalesTransaction> CreateTruckDispatchLineAsync(
        SaleLineOwner owner,
        GroupSaleSelectedInput input,
        GroupSaleCreateViewModel model,
        CurrencyConversionResult conversion,
        string invoice)
    {
        var dispatch = await _db.TruckDispatches
            .Include(d => d.Contract)
            .FirstOrDefaultAsync(d => d.Id == input.Id)
            ?? throw new BusinessRuleException("GROUP_SALE_DISPATCH_NOT_FOUND", "موتر انتخاب‌شده یافت نشد.");

        if (dispatch.SalesTransactionId.HasValue)
        {
            throw new BusinessRuleException("GROUP_SALE_DISPATCH_ALREADY_SOLD", $"موتر #{dispatch.Id} قبلاً فروخته شده است.");
        }

        if (dispatch.Status is not (DispatchStatus.Loaded or DispatchStatus.InTransit))
        {
            throw new BusinessRuleException("GROUP_SALE_DISPATCH_NOT_IN_TRANSIT", $"موتر #{dispatch.Id} دیگر در جریان نیست.");
        }

        var sourceContract = dispatch.Contract
            ?? throw new BusinessRuleException("GROUP_SALE_DISPATCH_CONTRACT", "قرارداد خرید این موتر معتبر نیست.");

        // وزن فروش = وزن تخلیه‌شده (اگر کرایه تسویه شده)، وگرنه وزن بارگیری.
        var qty = dispatch.DischargedQuantityMt ?? dispatch.LoadedQuantityMt;
        var totalInCurrency = decimal.Round(qty * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var sale = new SalesTransaction
        {
            ContractId = null,
            CompanyId = sourceContract.CompanyId,
            CustomerId = model.CustomerId,
            ProductId = dispatch.ProductId,
            DestinationLocationId = dispatch.DestinationLocationId,
            ShipmentId = await ResolveShipmentIdForContractAsync(dispatch.ContractId),
            // قرارداد خرید و موترِ منبع از خودِ دیسپچ می‌آیند (Lineage واقعی) تا عاید این فروش در
            // سود و زیان همان قرارداد شمرده شود؛ همان فیلدهایی که فروش مستقیم از موتر پر می‌کند.
            SourcePurchaseContractId = sourceContract.Id,
            TruckDispatchId = dispatch.Id,
            SaleStage = SaleStage.InTransit,
            SalesBatchId = owner.SalesBatchId,
            PreSaleOrderId = owner.PreSaleOrderId,
            InvoiceNumber = invoice,
            SaleDate = model.SaleDate.Date,
            QuantityMt = qty,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = model.Notes,
            TicketSerialNumber = dispatch.TicketSerialNumber
        };
        _db.SalesTransactions.Add(sale);
        await _db.SaveChangesAsync();

        dispatch.SalesTransactionId = sale.Id;

        var ledger = SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: sourceContract.Id);
        _db.LedgerEntries.Add(ledger);
        await _db.SaveChangesAsync();

        await PostSaleAccountingAsync(sale);

        return sale;
    }

    private async Task<SalesTransaction> CreateLegLineAsync(
        SaleLineOwner owner,
        GroupSaleSelectedInput input,
        GroupSaleCreateViewModel model,
        CurrencyConversionResult conversion,
        string invoice,
        InventoryTransportReceiptService receiptService,
        // فقط مسیر تحویل پیش‌فروش از حملِ کشتی (TransportLeg) این را می‌فرستد → تحویل جزئی.
        // null یعنی مسیر تاریخیِ فروش گروهی/واگن: کل باقیماندهٔ حمل فروخته می‌شود (بدون تغییر رفتار).
        decimal? requestedQtyMt = null)
    {
        var leg = await receiptService.LoadLegAsync(input.Id, tracking: true)
            ?? throw new BusinessRuleException("GROUP_SALE_LEG_NOT_FOUND", "حمل انتخاب‌شده یافت نشد.");

        if (leg.Status is not (InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit))
        {
            throw new BusinessRuleException("GROUP_SALE_LEG_NOT_IN_TRANSIT", $"حمل #{leg.Id} دیگر در جریان نیست.");
        }

        // مسیر کل‌وسیله (گروهی/واگن): حملی که رسید/فروش قبلی دارد اصلاً دوباره فروخته نمی‌شود.
        // مسیر جزئیِ پیش‌فروش (requestedQtyMt): تحویل چندمرحله‌ای مجاز است تا باقیماندهٔ حمل صفر شود،
        // پس این گارد فقط در مسیر تاریخی اعمال می‌شود. رسیدِ «فقط تسویهٔ کرایه» هیچ‌کدام را مانع نمی‌شود.
        if (requestedQtyMt is null
            && await _db.InventoryTransportReceipts.AsNoTracking()
                .AnyAsync(r => r.InventoryTransportLegId == leg.Id && !r.IsCancelled
                    && (r.ReceivedQuantityMt > 0m || r.SalesTransactionId != null)))
        {
            throw new BusinessRuleException("GROUP_SALE_LEG_ALREADY_RECEIVED", $"حمل #{leg.Id} قبلاً رسید/فروش دارد.");
        }

        // وزن فروش = باقیماندهٔ حمل (مقدار حمل − کسری تسویه) = وزن تخلیه‌شده، نه وزن بارگیری اولیه.
        var consumedMt = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => r.InventoryTransportLegId == leg.Id && !r.IsCancelled)
            .SumAsync(r => r.ReceivedQuantityMt + r.ShortageQuantityMt);
        var sellableMt = decimal.Round(leg.QuantityMt - consumedMt, 4, MidpointRounding.AwayFromZero);
        if (sellableMt <= 0.0001m)
        {
            throw new BusinessRuleException("GROUP_SALE_LEG_NOTHING_TO_SELL", $"حمل #{leg.Id} باری برای فروش ندارد.");
        }

        // مقدار این تحویل: در مسیر تاریخی کل باقیمانده؛ در مسیر جزئی فقط مقدار واردشدهٔ کاربر،
        // سقف‌گذاری‌شده به باقیماندهٔ واقعی حمل (سقف مانده پیش‌فروش جداگانه در PreSaleDeliver کنترل می‌شود).
        var receiptQtyMt = sellableMt;
        if (requestedQtyMt is not null)
        {
            receiptQtyMt = decimal.Round(requestedQtyMt.Value, 4, MidpointRounding.AwayFromZero);
            if (receiptQtyMt <= 0m)
            {
                throw new BusinessRuleException("GROUP_SALE_LEG_QTY_REQUIRED", "مقدار تحویل باید بزرگ‌تر از صفر باشد.");
            }

            if (receiptQtyMt > sellableMt + 0.0001m)
            {
                throw new BusinessRuleException(
                    "GROUP_SALE_LEG_OVER_AVAILABLE",
                    $"مقدار تحویل از موجودی قابل فروش این حمل ({sellableMt:N4} تن) بیشتر است.");
            }
        }

        var receiptModel = new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDate = model.SaleDate.Date,
            ReceivedQuantityMt = receiptQtyMt,
            ShortageQuantityMt = 0m,
            ReceiptDestination = InventoryTransportReceiptDestination.DirectSale,
            SaleCustomerId = model.CustomerId,
            SaleInvoiceNumber = invoice,
            SaleDate = model.SaleDate.Date,
            SaleCurrency = model.Currency,
            SaleUnitPriceInCurrency = model.UnitPriceInCurrency,
            SaleAppliedFxRateToUsd = model.AppliedFxRateToUsd,
            Notes = model.Notes
        };

        await receiptService.ValidateAsync(receiptModel, leg, ModelState, keyPrefix: $"leg{leg.Id}.");
        if (!ModelState.IsValid)
        {
            var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage
                ?? "اطلاعات فروش حمل معتبر نیست.";
            throw new BusinessRuleException("GROUP_SALE_LEG_INVALID", $"حمل #{leg.Id}: {firstError}");
        }

        var saleConversion = await receiptService.ResolveSaleConversionAsync(receiptModel, ModelState, keyPrefix: $"leg{leg.Id}.");
        if (saleConversion is null)
        {
            throw new BusinessRuleException("GROUP_SALE_LEG_FX", $"حمل #{leg.Id}: نرخ تبدیل ارز فروش قابل محاسبه نیست.");
        }

        var receipt = await receiptService.ApplyAsync(receiptModel, leg, saleConversion);

        var sale = await _db.SalesTransactions.FirstOrDefaultAsync(s => s.Id == receipt.SalesTransactionId)
            ?? throw new BusinessRuleException("GROUP_SALE_LEG_SALE_MISSING", $"سند فروش حمل #{leg.Id} ساخته نشد.");
        sale.SalesBatchId = owner.SalesBatchId;
        sale.PreSaleOrderId = owner.PreSaleOrderId;
        await _db.SaveChangesAsync();

        return sale;
    }

    // ---------- GroupDetails ----------

    public async Task<IActionResult> GroupDetails(int id)
    {
        var batch = await _db.SalesBatches
            .AsNoTracking()
            .Include(b => b.Customer)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (batch is null)
        {
            return NotFound();
        }

        var lines = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.SalesBatchId == batch.Id)
            .OrderBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.SaleStage,
                s.ProductId,
                ProductName = s.Product != null ? s.Product.Name : "",
                s.InvoiceNumber,
                s.QuantityMt,
                s.TotalInCurrency,
                s.TotalUsd,
                s.IsCancelled
            })
            .ToListAsync();

        var saleIds = lines.Select(l => l.Id).ToArray();

        var dispatchBySale = await _db.TruckDispatches.AsNoTracking()
            .Where(d => d.SalesTransactionId != null && saleIds.Contains(d.SalesTransactionId!.Value))
            .Select(d => new { SaleId = d.SalesTransactionId!.Value, Plate = d.Truck != null ? d.Truck.PlateNumber : null })
            .ToDictionaryAsync(x => x.SaleId, x => x.Plate);

        var legBySale = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => r.SalesTransactionId != null && saleIds.Contains(r.SalesTransactionId!.Value) && !r.IsCancelled)
            .Select(r => new
            {
                SaleId = r.SalesTransactionId!.Value,
                r.InventoryTransportLeg!.TransportType,
                Number = r.InventoryTransportLeg.WagonNumber ?? r.InventoryTransportLeg.RwbNo,
                Plate = r.InventoryTransportLeg.Truck != null ? r.InventoryTransportLeg.Truck.PlateNumber : null
            })
            .ToDictionaryAsync(x => x.SaleId);

        var vm = new GroupSaleDetailsViewModel
        {
            Id = batch.Id,
            BatchNumber = batch.BatchNumber,
            CustomerName = batch.Customer?.Name ?? "",
            SaleDate = batch.SaleDate,
            Currency = batch.Currency,
            AppliedFxRateToUsd = batch.AppliedFxRateToUsd,
            UnitPriceInCurrency = batch.UnitPriceInCurrency,
            TotalQuantityMt = batch.TotalQuantityMt,
            TotalInCurrency = batch.TotalInCurrency,
            TotalUsd = batch.TotalUsd,
            LineCount = batch.LineCount,
            PaymentNote = batch.PaymentNote,
            Notes = batch.Notes,
            IsCancelled = batch.IsCancelled,
            Lines = lines.Select(l =>
            {
                string kindLabel, vehicleKind, number;
                if (l.SaleStage == SaleStage.TerminalStock)
                {
                    kindLabel = "موجودی مخزن"; vehicleKind = "مخزن"; number = "-";
                }
                else if (dispatchBySale.TryGetValue(l.Id, out var plate))
                {
                    kindLabel = "موتر در جریان"; vehicleKind = "موتر"; number = plate ?? "-";
                }
                else if (legBySale.TryGetValue(l.Id, out var leg))
                {
                    var isWagon = leg.TransportType == LoadingTransportType.Wagon;
                    kindLabel = isWagon ? "واگن در جریان" : "انتقال در مسیر";
                    vehicleKind = isWagon ? "واگن" : "انتقال";
                    number = leg.Number ?? leg.Plate ?? "-";
                }
                else
                {
                    kindLabel = "فروش"; vehicleKind = "-"; number = "-";
                }

                return new GroupSaleLineViewModel
                {
                    SalesTransactionId = l.Id,
                    KindLabel = kindLabel,
                    VehicleKind = vehicleKind,
                    Number = number,
                    ProductName = l.ProductName,
                    InvoiceNumber = l.InvoiceNumber,
                    QuantityMt = l.QuantityMt,
                    TotalInCurrency = l.TotalInCurrency,
                    TotalUsd = l.TotalUsd,
                    IsCancelled = l.IsCancelled
                };
            }).ToList()
        };

        return View(vm);
    }

    // ---------- CancelGroup ----------
    // هر ردیف را با همان الگوی لغوِ فروش تکی برمی‌گرداند (لجرِ معکوس + بازگشت موجودی)،
    // و لینک وسیله را آزاد می‌کند تا موتر/واگن دوباره «در جریان» شود.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelGroup(int id)
    {
        var batch = await _db.SalesBatches.FirstOrDefaultAsync(b => b.Id == id);
        if (batch is null)
        {
            return NotFound();
        }

        if (batch.IsCancelled)
        {
            TempData["ok"] = "این فروش گروهی قبلاً لغو شده است.";
            return RedirectToAction(nameof(GroupDetails), new { id });
        }

        var sales = await _db.SalesTransactions
            .Where(s => s.SalesBatchId == batch.Id && !s.IsCancelled)
            .ToListAsync();

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            foreach (var sale in sales)
            {
                await ReverseGroupSaleLineAsync(sale);
                sale.IsCancelled = true;
            }

            batch.IsCancelled = true;
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(SalesBatch),
                batch.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(("IsCancelled", false, true)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"فروش گروهی {batch.BatchNumber} لغو شد.";
        }
        catch (Exception ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to cancel group sale batch {BatchId}.", batch.Id);
            TempData["err"] = "لغو فروش گروهی انجام نشد.";
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        return RedirectToAction(nameof(GroupDetails), new { id });
    }

    private async Task ReverseGroupSaleLineAsync(SalesTransaction sale)
    {
        // لجرِ معکوس (مطابق لغوِ فروش تکی).
        var originalLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.SourceId == sale.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        if (originalLedger is not null)
        {
            _db.LedgerEntries.Add(new LedgerEntry
            {
                EntryDate = _businessClock.Today,
                Side = LedgerSide.Debit,
                AmountUsd = originalLedger.AmountUsd,
                Currency = originalLedger.Currency,
                SourceAmount = originalLedger.SourceAmount,
                SourceCurrencyCode = originalLedger.SourceCurrencyCode,
                AppliedFxRateToUsd = originalLedger.AppliedFxRateToUsd,
                AppliedFxRateDate = originalLedger.AppliedFxRateDate,
                AppliedFxRateSource = originalLedger.AppliedFxRateSource,
                Description = $"لغو فروش گروهی #{sale.Id} | {originalLedger.Description}",
                SourceType = "Sale",
                SourceId = sale.Id,
                Reference = (originalLedger.Reference ?? sale.InvoiceNumber) + "-CANCEL",
                ContractId = originalLedger.ContractId,
                CustomerId = originalLedger.CustomerId,
                ShipmentId = originalLedger.ShipmentId
            });
        }

        // بازگشت موجودی برای فروش از مخزن (خروج → ورود معکوس).
        var stockOutMovements = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.SalesTransactionId == sale.Id && m.Direction == MovementDirection.Out)
            .ToListAsync();
        foreach (var m in stockOutMovements)
        {
            _db.InventoryMovements.Add(new InventoryMovement
            {
                ProductId = m.ProductId,
                ContractId = m.ContractId,
                TerminalId = m.TerminalId,
                StorageTankId = m.StorageTankId,
                SalesTransactionId = sale.Id,
                Direction = MovementDirection.In,
                MovementDate = _businessClock.Today,
                QuantityMt = m.QuantityMt,
                ReferenceDocument = (m.ReferenceDocument ?? sale.InvoiceNumber) + "-CANCEL",
                Notes = $"Reversal for cancelled group SaleId={sale.Id}"
            });
        }

        // آزادسازی موترِ لینک‌شده تا دوباره «در جریان» شود.
        var dispatch = await _db.TruckDispatches.FirstOrDefaultAsync(d => d.SalesTransactionId == sale.Id);
        if (dispatch is not null)
        {
            dispatch.SalesTransactionId = null;
        }

        // لغو رسیدِ فروش حمل و بازگرداندن وضعیت حمل به «در راه».
        var receipt = await _db.InventoryTransportReceipts
            .FirstOrDefaultAsync(r => r.SalesTransactionId == sale.Id && !r.IsCancelled);
        if (receipt is not null)
        {
            receipt.IsCancelled = true;
            var leg = await _db.InventoryTransportLegs.FirstOrDefaultAsync(l => l.Id == receipt.InventoryTransportLegId);
            if (leg is not null && leg.Status == InventoryTransportLegStatus.Received)
            {
                leg.Status = InventoryTransportLegStatus.InTransit;
            }
        }
    }
}
