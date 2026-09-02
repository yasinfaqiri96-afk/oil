using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.TruckSettlements;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

public partial class TruckSettlementsController
{
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> GroupUnload(string? returnUrl = null)
    {
        var model = new GroupUnloadCreateViewModel
        {
            ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? returnUrl
                : null
        };
        await PopulateGroupUnloadViewAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 100_000)]
    public async Task<IActionResult> GroupUnload(
        GroupUnloadCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        model.ReceiptDate = model.ReceiptDate.Date;
        model.DocumentReference = NormalizeNullable(model.DocumentReference);
        model.Notes = NormalizeNullable(model.Notes);
        model.ReturnUrl = !string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl)
            ? model.ReturnUrl
            : null;

        var selected = (model.Items ?? [])
            .Where(item => item.Selected && item.SourceId > 0)
            .ToList();
        if (selected.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Items), "حداقل یک حمل را برای تخلیه انتخاب کنید.");
        }
        if (!model.SourceKind.HasValue
            || selected.Any(item => item.Kind != model.SourceKind.Value))
        {
            ModelState.AddModelError(nameof(model.SourceKind), "همهٔ ردیف‌های انتخاب‌شده باید از نوع منبع انتخاب‌شده باشند.");
        }
        if (selected.Select(item => $"{(int)item.Kind}:{item.SourceId}").Distinct().Count() != selected.Count)
        {
            ModelState.AddModelError(nameof(model.Items), "یک حمل بیشتر از یک‌بار انتخاب شده است.");
        }

        var tank = await _db.StorageTanks
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == model.DestinationStorageTankId && item.IsActive);
        if (tank is null)
        {
            ModelState.AddModelError(nameof(model.DestinationStorageTankId), "مخزن مقصد فعال یا معتبر نیست.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateGroupUnloadViewAsync(model, selected);
            return View(model);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            }

            var sources = await LoadGroupUnloadSourcesAsync();
            var sourceByKey = sources.ToDictionary(source => source.Key, StringComparer.Ordinal);
            var selectedSources = new List<GroupUnloadSourceItem>(selected.Count);
            for (var index = 0; index < selected.Count; index++)
            {
                var input = selected[index];
                if (!sourceByKey.TryGetValue($"{(int)input.Kind}:{input.SourceId}", out var source))
                {
                    ModelState.AddModelError(nameof(model.Items), $"ردیف {index + 1} دیگر آمادهٔ تخلیه نیست؛ فهرست را تازه‌سازی کنید.");
                    continue;
                }

                if (tank!.ProductId.HasValue && tank.ProductId.Value != source.ProductId)
                {
                    ModelState.AddModelError(
                        nameof(model.DestinationStorageTankId),
                        $"مخزن انتخاب‌شده برای کالای «{source.ProductName}» تعریف نشده است.");
                    continue;
                }

                selectedSources.Add(source);
            }

            var receiptService = _receiptService;
            var legOperations = new List<(InventoryTransportLeg Leg, InventoryTransportReceiptCreateViewModel Receipt)>();
            foreach (var source in selectedSources.Where(item => item.Kind == TruckSettlementSourceKind.Leg))
            {
                var leg = await receiptService.LoadLegAsync(source.SourceId, tracking: true);
                if (leg is null || !leg.IsFreightSettled
                    || leg.Status is not (InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit))
                {
                    ModelState.AddModelError(nameof(model.Items), $"حمل «{source.VehicleNumber}» دیگر آمادهٔ تخلیه نیست.");
                    continue;
                }

                var receipt = new InventoryTransportReceiptCreateViewModel
                {
                    InventoryTransportLegId = leg.Id,
                    ReceiptDate = model.ReceiptDate,
                    ReceivedQuantityMt = source.QuantityMt,
                    ShortageQuantityMt = 0m,
                    AllowanceMt = 0m,
                    ReceiptDestination = InventoryTransportReceiptDestination.ToInventory,
                    DestinationTerminalId = tank!.TerminalId,
                    DestinationStorageTankId = tank.Id,
                    Notes = BuildGroupUnloadNotes(model, source)
                };
                await receiptService.ValidateAsync(receipt, leg, ModelState);
                legOperations.Add((leg, receipt));
            }

            var dispatchIds = selectedSources
                .Where(item => item.Kind == TruckSettlementSourceKind.Dispatch)
                .Select(item => item.SourceId)
                .ToList();
            var dispatches = dispatchIds.Count == 0
                ? new Dictionary<int, TruckDispatch>()
                : await _db.TruckDispatches
                    .Include(item => item.Driver)
                    .Where(item => dispatchIds.Contains(item.Id))
                    .ToDictionaryAsync(item => item.Id);
            foreach (var source in selectedSources.Where(item => item.Kind == TruckSettlementSourceKind.Dispatch))
            {
                if (!dispatches.TryGetValue(source.SourceId, out var dispatch)
                    || !dispatch.IsFreightSettled
                    || dispatch.Status is DispatchStatus.Delivered or DispatchStatus.Cancelled
                    || dispatch.SalesTransactionId.HasValue
                    || !dispatch.DischargedQuantityMt.HasValue
                    || dispatch.DischargedQuantityMt.Value <= QuantityEpsilon)
                {
                    ModelState.AddModelError(nameof(model.Items), $"ارسال «{source.VehicleNumber}» دیگر آمادهٔ تخلیه نیست.");
                    continue;
                }

                var alreadyUnloaded = await _db.DeliveryReceipts.AsNoTracking()
                        .AnyAsync(item => item.TruckDispatchId == dispatch.Id)
                    || await _db.InventoryMovements.AsNoTracking()
                        .AnyAsync(item => item.ReferenceDocument == $"TRUCK-UNLOAD:{dispatch.Id}");
                if (alreadyUnloaded)
                {
                    ModelState.AddModelError(nameof(model.Items), $"برای ارسال «{source.VehicleNumber}» قبلاً تخلیه ثبت شده است.");
                }
            }

            if (!ModelState.IsValid)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }
                await PopulateGroupUnloadViewAsync(model, selected);
                return View(model);
            }

            foreach (var operation in legOperations)
            {
                await receiptService.ApplyAsync(operation.Receipt, operation.Leg, saleConversion: null);
            }

            foreach (var source in selectedSources.Where(item => item.Kind == TruckSettlementSourceKind.Dispatch))
            {
                var dispatch = dispatches[source.SourceId];
                var dischargedQuantityMt = dispatch.DischargedQuantityMt!.Value;
                dispatch.Status = DispatchStatus.Delivered;

                _db.DeliveryReceipts.Add(new DeliveryReceipt
                {
                    TruckDispatchId = dispatch.Id,
                    ReceiptDate = model.ReceiptDate,
                    ReceivedQuantityMt = dischargedQuantityMt,
                    DocumentReference = model.DocumentReference
                });
                await _movements.PostInboundAsync(new InventoryMovementRequest
                {
                    ProductId = dispatch.ProductId,
                    ContractId = dispatch.ContractId,
                    TerminalId = tank!.TerminalId,
                    StorageTankId = tank.Id,
                    MovementDate = model.ReceiptDate,
                    QuantityMt = dischargedQuantityMt,
                    ReferenceDocument = $"TRUCK-UNLOAD:{dispatch.Id}",
                    Notes = BuildGroupUnloadNotes(model, source)
                });

                // هر موتر جداگانه کسری/اضافه‌بارِ خودش را می‌گیرد و مقصد واقعی روی همان رکورد می‌نشیند.
                // مبنای مقایسه همان مبنای تسویه است (بارگیری منهای تخلیه‌های جزئی فرم قدیمی)، پس ثبت
                // دوباره رکورد تکراری نمی‌سازد و اگر تفاوت صفر باشد رکورد قبلی لغو می‌شود.
                var arrivalsMt = await GetArrivalDischargedMtAsync(dispatch.Id);
                var expectedQuantityMt = decimal.Round(
                    dispatch.LoadedQuantityMt - arrivalsMt, 4, MidpointRounding.AwayFromZero);
                await UpsertDispatchVarianceAsync(
                    dispatch,
                    expectedQuantityMt,
                    dischargedQuantityMt,
                    allowanceMt: dispatch.AllowanceMt ?? 0m,
                    eventDate: model.ReceiptDate,
                    shortageChargeUsd: dispatch.PayableUsd ?? 0m,
                    terminalId: tank.TerminalId,
                    storageTankId: tank.Id,
                    reference: null,
                    notes: null);
            }

            // PTG-P0-01 — توکن با همان SaveChanges و همان Transaction مصرف می‌شود.
            _formTokens.Stamp(formToken, "TruckSettlement.GroupUnload", nameof(TruckDispatch));

            await _db.SaveChangesAsync();
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"{selectedSources.Count:N0} حمل به‌صورت گروهی در مخزن انتخاب‌شده تخلیه شد.";
            return model.ReturnUrl is not null
                ? Redirect(model.ReturnUrl)
                : RedirectToAction(nameof(GroupUnload));
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            TempData["err"] = "این تخلیهٔ گروهی قبلاً ثبت شده است و دوباره ثبت نشد.";
            return model.ReturnUrl is not null
                ? Redirect(model.ReturnUrl)
                : RedirectToAction(nameof(GroupUnload));
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
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

    private async Task PopulateGroupUnloadViewAsync(
        GroupUnloadCreateViewModel model,
        IReadOnlyCollection<GroupUnloadSelectedInput>? selectedItems = null)
    {
        var sources = await LoadGroupUnloadSourcesAsync();
        var selectedKeys = (selectedItems ?? model.Items ?? [])
            .Where(item => item.Selected)
            .Select(item => $"{(int)item.Kind}:{item.SourceId}")
            .ToHashSet(StringComparer.Ordinal);
        model.Items = sources
            .Select(source => new GroupUnloadSelectedInput
            {
                Selected = selectedKeys.Contains(source.Key),
                Kind = source.Kind,
                SourceId = source.SourceId
            })
            .ToList();
        ViewBag.GroupUnloadSources = sources;

        var tanks = await _db.StorageTanks
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.DisplayName ?? item.TankCode)
            .Select(item => new
            {
                item.Id,
                item.TerminalId,
                item.ProductId,
                item.DisplayName,
                item.TankCode
            })
            .ToListAsync();
        var tankOptions = tanks
            .Select(item => new
            {
                item.Id,
                Display = StorageTankDisplay.Build(item.Id, item.DisplayName, item.TankCode)
            })
            .ToList();
        ViewBag.DestinationStorageTanks = new SelectList(
            tankOptions,
            "Id",
            "Display",
            model.DestinationStorageTankId);
        ViewBag.GroupUnloadTankMap = tanks.Select(item => new
        {
            id = item.Id,
            terminalId = item.TerminalId,
            productId = item.ProductId,
            display = StorageTankDisplay.Build(item.Id, item.DisplayName, item.TankCode)
        }).ToList();
    }

    private async Task<List<GroupUnloadSourceItem>> LoadGroupUnloadSourcesAsync()
    {
        var result = new List<GroupUnloadSourceItem>();
        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(item => item.IsFreightSettled
                && (item.Status == InventoryTransportLegStatus.Loaded
                    || item.Status == InventoryTransportLegStatus.InTransit)
                && (item.TransportType == LoadingTransportType.Truck
                    || item.TransportType == LoadingTransportType.Wagon))
            .Select(item => new
            {
                item.Id,
                item.TransportType,
                VehicleNumber = item.Truck != null ? item.Truck.PlateNumber : item.WagonNumber,
                DriverName = item.Driver != null ? item.Driver.FullName : null,
                item.ProductId,
                ProductName = item.Product != null ? item.Product.Name : "",
                ContractNumber = item.SourcePurchaseContract != null ? item.SourcePurchaseContract.ContractNumber : "",
                SourceName = item.SourceTerminal != null ? item.SourceTerminal.Name : null,
                DestinationTerminalName = item.DestinationTerminal != null ? item.DestinationTerminal.Name : null,
                DestinationLocationName = item.DestinationLocation != null ? item.DestinationLocation.Name : null,
                item.LoadedDate,
                item.QuantityMt
            })
            .ToListAsync();
        var legIds = legs.Select(item => item.Id).ToList();
        var consumedByLeg = legIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(item => legIds.Contains(item.InventoryTransportLegId) && !item.IsCancelled)
                .GroupBy(item => item.InventoryTransportLegId)
                .Select(group => new
                {
                    LegId = group.Key,
                    QuantityMt = group.Sum(item => item.ReceivedQuantityMt + item.ShortageQuantityMt)
                })
                .ToDictionaryAsync(item => item.LegId, item => item.QuantityMt);
        foreach (var leg in legs)
        {
            var quantityMt = decimal.Round(
                leg.QuantityMt - consumedByLeg.GetValueOrDefault(leg.Id),
                4,
                MidpointRounding.AwayFromZero);
            if (quantityMt <= QuantityEpsilon)
            {
                continue;
            }
            result.Add(new GroupUnloadSourceItem
            {
                Kind = TruckSettlementSourceKind.Leg,
                SourceId = leg.Id,
                TypeLabel = leg.TransportType == LoadingTransportType.Wagon
                    ? "واگن (حمل از موجودی)"
                    : "موتر (حمل از موجودی)",
                VehicleNumber = leg.VehicleNumber ?? $"#{leg.Id}",
                DriverName = leg.DriverName,
                ProductId = leg.ProductId,
                ProductName = leg.ProductName,
                ContractNumber = leg.ContractNumber,
                Route = BuildGroupUnloadRoute(
                    leg.SourceName,
                    leg.DestinationTerminalName ?? leg.DestinationLocationName),
                OperationDate = leg.LoadedDate,
                QuantityMt = quantityMt
            });
        }

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(item => item.IsFreightSettled
                && item.Status != DispatchStatus.Delivered
                && item.Status != DispatchStatus.Cancelled
                && item.SalesTransactionId == null
                && item.DischargedQuantityMt != null
                && item.DischargedQuantityMt > QuantityEpsilon
                && !_db.DeliveryReceipts.Any(receipt => receipt.TruckDispatchId == item.Id)
                && !_db.InventoryMovements.Any(movement =>
                    movement.ReferenceDocument == "TRUCK-UNLOAD:" + item.Id))
            .Select(item => new
            {
                item.Id,
                VehicleNumber = item.Truck != null ? item.Truck.PlateNumber : null,
                DriverName = item.Driver != null ? item.Driver.FullName : null,
                item.ProductId,
                ProductName = item.Product != null ? item.Product.Name : "",
                ContractNumber = item.Contract != null ? item.Contract.ContractNumber : "",
                DestinationName = item.DestinationLocation != null ? item.DestinationLocation.Name : null,
                item.DispatchDate,
                QuantityMt = item.DischargedQuantityMt!.Value
            })
            .ToListAsync();
        result.AddRange(dispatches.Select(dispatch => new GroupUnloadSourceItem
        {
            Kind = TruckSettlementSourceKind.Dispatch,
            SourceId = dispatch.Id,
            TypeLabel = "ارسال موتر",
            VehicleNumber = dispatch.VehicleNumber ?? $"#{dispatch.Id}",
            DriverName = dispatch.DriverName,
            ProductId = dispatch.ProductId,
            ProductName = dispatch.ProductName,
            ContractNumber = dispatch.ContractNumber,
            Route = BuildGroupUnloadRoute("ارسال موتر", dispatch.DestinationName),
            OperationDate = dispatch.DispatchDate,
            QuantityMt = dispatch.QuantityMt
        }));

        return result
            .OrderByDescending(item => item.OperationDate)
            .ThenByDescending(item => item.SourceId)
            .ToList();
    }

    private static string BuildGroupUnloadRoute(string? source, string? destination)
        => $"{(string.IsNullOrWhiteSpace(source) ? "-" : source)} ← {(string.IsNullOrWhiteSpace(destination) ? "-" : destination)}";

    private static string BuildGroupUnloadNotes(
        GroupUnloadCreateViewModel model,
        GroupUnloadSourceItem source)
    {
        var parts = new List<string>
        {
            $"Group unload: {(source.Kind == TruckSettlementSourceKind.Leg ? "TRANSPORT" : "DISPATCH")}:{source.SourceId}"
        };
        if (!string.IsNullOrWhiteSpace(model.DocumentReference))
        {
            parts.Add($"Reference: {model.DocumentReference}");
        }
        if (!string.IsNullOrWhiteSpace(model.Notes))
        {
            parts.Add(model.Notes);
        }
        return string.Join(" | ", parts);
    }
}
