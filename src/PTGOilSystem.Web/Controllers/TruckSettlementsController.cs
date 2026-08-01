using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.TruckSettlements;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

// «تسویهٔ کرایه موترها»: صفحهٔ واحد برای موترها/واگن‌هایی که هنوز بار دارند و کرایه‌شان تسویه نشده.
// فقط کرایه/کسری را با طرف حمل تسویه می‌کند — تخلیهٔ موجودی/فروش ندارد (مرحلهٔ بعدی و جداست).
// هیچ منطق مالی جدیدی ندارد:
//   • حمل از موجودی (leg) → InventoryTransportReceiptService با SettlementOnly (کرایه/کسری/لجر، بدون InventoryMovement).
//   • ارسال موتر (dispatch) → فیلدهای کرایهٔ دیسپچ + DispatchFreightExpenseSync (بدون DeliveryReceipt/حرکت موجودی/Delivered).
// پس از تسویه، leg/dispatch با IsFreightSettled=true از لیست خارج و برچسب «کرایه تسویه‌شده» می‌گیرد؛ بار برای تخلیهٔ بعدی می‌ماند.
// موترِ خودِ شرکت (دارایی عملیاتی) کرایهٔ بیرونی ندارد؛ کرایهٔ خالص به‌عنوان مصرفِ همان حمل با
// OperationalAssetId ثبت می‌شود که در پروفایل دارایی «عواید کرایه» شمرده می‌شود (بدون لجر — طلب خارجی نیست).
[Authorize]
public partial class TruckSettlementsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IInventoryLineageWriter _lineage;
    private readonly ILossEventWorkflowService _lossWorkflow;

    public TruckSettlementsController(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IInventoryLineageWriter lineage,
        ILossEventWorkflowService lossWorkflow,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _lineage = lineage;
        _lossWorkflow = lossWorkflow;
        _expenseAccounting = expenseAccounting;
    }

    // مرحله ۵ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;

    private const decimal QuantityEpsilon = 0.0001m;
    private const string ArrivalRefPrefix = "TRUCK-ARRIVAL:";

    private sealed record FreightParty(int? ServiceProviderId, int? OperationalAssetId, int? DriverId);

    public async Task<IActionResult> Index(string? q, TruckSettlementSourceKind? kind)
    {
        var model = await BuildIndexAsync(preserveInputs: null, q, kind);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    // فرم گروهی همهٔ وسایط دارای بار را با ~۲۲ فیلد در هر ردیف پست می‌کند؛ با انباشته‌شدن ردیف‌ها
    // مجموع فیلدها از سقف پیش‌فرض ۱۰۲۴ فرم (FormOptions.ValueCountLimit) عبور می‌کند و درخواست پیش از
    // رسیدن به اکشن با 400 رد می‌شود (صفحهٔ خالی، «هیچ اتفاقی نمی‌افتد»). سقف را برای همین اکشن بالا می‌بریم.
    [RequestFormLimits(ValueCountLimit = 100_000)]
    public async Task<IActionResult> Settle(TruckSettlementIndexViewModel model)
    {
        var inputs = model.Inputs ?? [];
        var selectedIndexes = inputs
            .Select((row, index) => (row, index))
            .Where(x => x.row.Selected && x.row.SourceId > 0)
            .ToList();

        if (selectedIndexes.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "هیچ موتر/واگنی برای تسویه انتخاب نشده است.");
            return View("Index", await BuildIndexAsync(inputs));
        }

        var receiptService = new InventoryTransportReceiptService(_db, _currencyConversion, _lineage);

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        var settledCount = 0;
        try
        {
            foreach (var (row, index) in selectedIndexes)
            {
                var prefix = $"Inputs[{index}].";
                var errorsBefore = ModelState.ErrorCount;
                var ok = row.Kind == TruckSettlementSourceKind.Leg
                    ? await SettleLegAsync(receiptService, row, prefix)
                    : await SettleDispatchAsync(row, prefix);
                if (ok)
                {
                    settledCount++;
                }
                else if (ModelState.ErrorCount > errorsBefore)
                {
                    // پیام‌های خطای این ردیف را با نمبر موتر/واگن نشان‌دار می‌کنیم تا در خلاصهٔ خطا معلوم باشد کدام ردیف است.
                    PrefixRowErrorsWithVehicle(prefix, await ResolveVehicleLabelAsync(row));
                }
            }

            if (!ModelState.IsValid)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync();
                }

                return View("Index", await BuildIndexAsync(inputs));
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
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

        TempData["ok"] = $"کرایهٔ {settledCount} موتر/واگن تسویه شد.";
        return RedirectToAction(nameof(Index));
    }

    // ── واردات اکسل: نمبر موتر/واگن + وزن تخلیه + حواکت را روی همان لیست پیش‌پر می‌کند ──
    // هیچ ثبتی انجام نمی‌شود؛ فقط ردیف‌های منطبق انتخاب و مقادیرشان پر می‌شود تا کاربر
    // بازبینی و «ثبت تخلیه و تسویه» کند. تطبیق بر اساس نمبر وسیله (نرمال‌شده) است.
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportExcel(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            TempData["err"] = "فایلی انتخاب نشده است.";
            return RedirectToAction(nameof(Index));
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            TempData["err"] = "حجم فایل زیاد است (حداکثر ۵ مگابایت).";
            return RedirectToAction(nameof(Index));
        }

        IReadOnlyList<TruckSettlementImportRow> parsed;
        try
        {
            await using var workbook = await ExcelWorkbookNormalizer.OpenAsync(file);
            parsed = TruckSettlementWorkbookParser.Parse(workbook.Stream);
        }
        catch (Exception ex)
        {
            TempData["err"] = "خواندن فایل اکسل ناموفق بود: " + ex.Message;
            return RedirectToAction(nameof(Index));
        }

        if (parsed.Count == 0)
        {
            TempData["err"] = "در فایل هیچ ردیف معتبری (نمبر موتر و وزن تخلیه) یافت نشد.";
            return RedirectToAction(nameof(Index));
        }

        var model = await BuildIndexAsync(preserveInputs: null);

        static string NormalizePlate(string? value)
            => new((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var rowByPlate = new Dictionary<string, int>();
        for (var i = 0; i < model.Rows.Count; i++)
        {
            var key = NormalizePlate(model.Rows[i].VehicleNumber);
            if (key.Length > 0)
            {
                rowByPlate.TryAdd(key, i);
            }
        }

        var matched = 0;
        var unmatched = new List<string>();
        foreach (var import in parsed)
        {
            if (rowByPlate.TryGetValue(NormalizePlate(import.VehicleNumber), out var index))
            {
                var input = model.Inputs[index];
                input.Selected = true;
                input.QuantityMt = import.DischargeWeightMt;
                input.AllowanceMt = import.AllowanceMt;
                matched++;
            }
            else
            {
                unmatched.Add(import.VehicleNumber);
            }
        }

        if (matched == 0)
        {
            TempData["err"] = "هیچ نمبر موتر/واگنی از فایل با ردیف‌های دارای بار مطابقت نداشت.";
        }
        else
        {
            var msg = $"{matched} ردیف از فایل اکسل مطابقت یافت و پیش‌پر شد. لطفاً بازبینی و «ثبت تخلیه و تسویه» کنید.";
            if (unmatched.Count > 0)
            {
                msg += $" {unmatched.Count} نمبر بدون تطبیق: {string.Join("، ", unmatched.Take(20))}";
            }
            TempData["ok"] = msg;
        }

        return View("Index", model);
    }

    // ── تسویهٔ کرایهٔ یک حمل از موجودی (موتر/واگن) — فقط تسویه، بدون تخلیهٔ موجودی ──
    // از حالت SettlementOnly سرویس رسید استفاده می‌شود: کرایه/کسری/لجر ثبت می‌شود، InventoryMovement ساخته نمی‌شود،
    // بار داخل وسیله برای تخلیهٔ بعدی می‌ماند. کسری = باقیمانده − وزن تخلیه؛ حواکت از کسری کم می‌شود.
    private async Task<bool> SettleLegAsync(
        InventoryTransportReceiptService receiptService,
        TruckSettlementRowInputViewModel row,
        string prefix)
    {
        var leg = await receiptService.LoadLegAsync(row.SourceId, tracking: true);
        if (leg is null)
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "حمل موردنظر یافت نشد.");
            return false;
        }

        if (leg.IsFreightSettled)
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "کرایهٔ این حمل قبلاً تسویه شده است.");
            return false;
        }

        var vehicleNumber = leg.Truck?.PlateNumber ?? leg.WagonNumber ?? $"حمل #{leg.Id}";
        var party = await ResolveFreightPartyAsync(
            row,
            new FreightParty(leg.ServiceProviderId, leg.OperationalAssetId, leg.DriverId),
            vehicleNumber,
            prefix);
        if (party is null)
        {
            return false;
        }

        // سرویس رسید، راننده را از خود leg می‌خواند (وقتی شرکت خدماتی/دارایی انتخاب نشده باشد).
        if (party.DriverId.HasValue)
        {
            leg.DriverId = party.DriverId;
        }

        // کسری = باقیماندهٔ بار منهای وزن تخلیه (باقیمانده = مقدار حمل − رسیدهای قبلی).
        // اگر ترازوی مقصد بیشتر از بارگیری نشان دهد، این عدد منفی می‌شود (اضافه‌وزن) و باقیماندهٔ
        // قابل تخلیه/فروش حمل را به همان اندازه بالا می‌برد. کسری منفی هیچ جریمه/ضایعاتی نمی‌سازد.
        var consumedMt = await _db.InventoryTransportReceipts
            .Where(r => r.InventoryTransportLegId == leg.Id && !r.IsCancelled)
            .SumAsync(r => r.ReceivedQuantityMt + r.ShortageQuantityMt);
        var remainingMt = decimal.Round(leg.QuantityMt - consumedMt, 4, MidpointRounding.AwayFromZero);
        var shortageMt = decimal.Round(remainingMt - row.QuantityMt, 4, MidpointRounding.AwayFromZero);

        // مبنای کرایه = کل باقیماندهٔ بار (وزن تخلیه + کسری). کسورات دیگر از کرایهٔ ناخالص کم می‌شود.
        var netFreightUsd = ComputeNetFreight(row, remainingMt);
        var vm = new InventoryTransportReceiptCreateViewModel
        {
            InventoryTransportLegId = leg.Id,
            ReceiptDate = row.OperationDate.Date,
            SettlementOnly = true,
            ShortageQuantityMt = shortageMt,
            AllowanceMt = NormalizePositiveDecimal(row.AllowanceMt),
            ShortageRateUsd = NormalizePositiveDecimal(row.ShortageRateUsd),
            DeductShortageFromFreight = true,
            FreightRateUsdPerMt = NormalizePositiveDecimal(row.FreightRateUsdPerMt),
            FreightCostUsd = netFreightUsd,
            ServiceProviderId = party.ServiceProviderId,
            OperationalAssetId = party.OperationalAssetId,
            Notes = NormalizeNullable(row.Notes)
        };

        await receiptService.ValidateAsync(vm, leg, ModelState, prefix);
        if (!ModelState.IsValid)
        {
            return false;
        }

        var receipt = await receiptService.ApplyAsync(vm, leg, saleConversion: null);

        leg.IsFreightSettled = true;
        leg.FreightSettledDate = row.OperationDate.Date;
        await _db.SaveChangesAsync();

        if (party.OperationalAssetId.HasValue)
        {
            await AddAssetFreightIncomeAsync(
                receiptService,
                party.OperationalAssetId.Value,
                vm.FreightPayableUsd ?? vm.FreightCostUsd ?? 0m,
                row.OperationDate.Date,
                contractId: leg.SourcePurchaseContractId,
                shipmentId: leg.ShipmentId,
                transportLegId: leg.Id,
                truckDispatchId: null,
                reference: $"TRANSPORT-RECEIPT:{receipt.Id}");
        }

        return true;
    }

    // ── تسویهٔ کرایهٔ یک ارسال موتر (dispatch) — فقط تسویه، بدون تخلیه/رسید تحویل/حرکت موجودی ──
    private async Task<bool> SettleDispatchAsync(TruckSettlementRowInputViewModel row, string prefix)
    {
        var dispatch = await _db.TruckDispatches
            .Include(d => d.Truck)
            .Include(d => d.Driver)
            .Include(d => d.Contract)
            .FirstOrDefaultAsync(d => d.Id == row.SourceId);

        if (dispatch is null)
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "ارسال موتر موردنظر یافت نشد.");
            return false;
        }

        if (dispatch.IsFreightSettled)
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "کرایهٔ این ارسال قبلاً تسویه شده است.");
            return false;
        }

        if (dispatch.Status is DispatchStatus.Delivered or DispatchStatus.Cancelled)
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "این ارسال قبلاً نهایی/لغو شده است.");
            return false;
        }

        if (dispatch.SalesTransactionId.HasValue)
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "این ارسال قبلاً به فروش وصل شده است.");
            return false;
        }

        if (await _db.DeliveryReceipts.AsNoTracking().AnyAsync(r => r.TruckDispatchId == dispatch.Id))
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "برای این ارسال قبلاً رسید تحویل ثبت شده است.");
            return false;
        }

        // تخلیه‌های جزئی فرم قدیمی (TRUCK-ARRIVAL) — باقیمانده منهای آن‌ها حساب می‌شود.
        var arrivalsMt = await GetArrivalDischargedMtAsync(dispatch.Id);
        var remainingMt = decimal.Round(dispatch.LoadedQuantityMt - arrivalsMt, 4, MidpointRounding.AwayFromZero);
        if (remainingMt <= QuantityEpsilon)
        {
            ModelState.AddModelError(prefix + nameof(row.SourceId), "این ارسال بار باقیمانده‌ای ندارد.");
            return false;
        }

        var vehicleNumber = dispatch.Truck?.PlateNumber ?? $"دیسپچ #{dispatch.Id}";
        var party = await ResolveFreightPartyAsync(
            row,
            new FreightParty(dispatch.ServiceProviderId, dispatch.OperationalAssetId, dispatch.DriverId),
            vehicleNumber,
            prefix);
        if (party is null)
        {
            return false;
        }

        return await SettleDispatchFreightAsync(dispatch, row, party, remainingMt, prefix);
    }

    // تسویهٔ کرایهٔ دیسپچ: فقط فیلدهای کرایه/کسری روی دیسپچ + DispatchFreightExpenseSync (مصرف/لجر کرایه).
    // بدون DeliveryReceipt، بدون InventoryMovement، بدون Status=Delivered — تخلیهٔ واقعی مرحلهٔ بعدی و جداست.
    private async Task<bool> SettleDispatchFreightAsync(
        TruckDispatch dispatch,
        TruckSettlementRowInputViewModel row,
        FreightParty party,
        decimal remainingMt,
        string prefix)
    {
        // وزن تخلیه از ترازوی مقصد می‌آید و می‌تواند از وزن بارگیری بیشتر باشد (اضافه‌وزن ترازو).
        // همین وزن مبنای تخلیه و فروش است؛ کرایه همچنان روی باقیماندهٔ بارگیری‌شده حساب می‌شود.
        if (row.QuantityMt <= 0m)
        {
            ModelState.AddModelError(prefix + nameof(row.QuantityMt), "وزن تخلیه باید بزرگ‌تر از صفر باشد.");
        }

        if (!ModelState.IsValid)
        {
            return false;
        }

        // تفاوت = باقیماندهٔ بار منهای وزن تخلیه، با علامت واقعی (همان قرارداد DispatchController):
        //   مثبت = کسری، منفی = اضافه‌بار. حواکت (تلورانس) فقط از کسری کم می‌شود و اضافه‌بار هرگز جریمه نمی‌گیرد.
        var differenceMt = TransportVarianceMath.Difference(remainingMt, row.QuantityMt);
        var allowanceMt = NormalizePositiveDecimal(row.AllowanceMt) ?? 0m;
        var chargeableShortageMt = TransportVarianceMath.ChargeableShortage(differenceMt, allowanceMt);
        var shortageRateUsd = NormalizePositiveDecimal(row.ShortageRateUsd);
        var shortageChargeUsd = decimal.Round(
            FreightShortageMath.ShortageChargeUsd(chargeableShortageMt, shortageRateUsd), 4, MidpointRounding.AwayFromZero);
        var netFreightUsd = ComputeNetFreight(row, remainingMt);
        var freightPayableUsd = netFreightUsd.HasValue
            ? decimal.Round(netFreightUsd.Value - shortageChargeUsd, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        // وزن تخلیه‌شده = مبنای مؤثر این ارسال (نه وزن بارگیری). فروش گروهی همین را نمایش/می‌فروشد.
        dispatch.DischargedQuantityMt = row.QuantityMt;
        dispatch.ShortageMt = differenceMt;
        dispatch.AllowanceMt = allowanceMt;
        dispatch.ToleranceMt = allowanceMt;
        dispatch.ChargeableShortageMt = chargeableShortageMt;
        dispatch.FreightCostUsd = netFreightUsd;
        dispatch.ShortageRateUsd = shortageRateUsd;
        dispatch.FreightPayableUsd = freightPayableUsd;
        dispatch.PayableUsd = shortageChargeUsd > 0m ? shortageChargeUsd : null;
        dispatch.ServiceProviderId = party.ServiceProviderId;
        dispatch.OperationalAssetId = party.OperationalAssetId;
        if (party.DriverId.HasValue)
        {
            dispatch.DriverId = party.DriverId;
        }
        dispatch.IsFreightSettled = true;
        dispatch.FreightSettledDate = row.OperationDate.Date;

        // رکورد تفاوت برای سابقه (بدون اثر موجودی) — تخلیه‌ای رخ نمی‌دهد، بار برای مرحلهٔ بعدی می‌ماند.
        await UpsertDispatchVarianceAsync(
            dispatch,
            expectedQuantityMt: remainingMt,
            dischargedQuantityMt: row.QuantityMt,
            allowanceMt: allowanceMt,
            eventDate: row.OperationDate.Date,
            shortageChargeUsd: shortageChargeUsd,
            terminalId: null,
            storageTankId: null,
            reference: $"TRUCK-FREIGHT-SETTLE:{dispatch.Id}",
            notes: NormalizeNullable(row.Notes));

        await _db.SaveChangesAsync();
        await DispatchFreightExpenseSync.SyncAsync(_db, dispatch, _expenseAccounting);

        if (party.OperationalAssetId.HasValue)
        {
            await AddAssetFreightIncomeAsync(
                receiptService: null,
                party.OperationalAssetId.Value,
                freightPayableUsd ?? netFreightUsd ?? 0m,
                row.OperationDate.Date,
                contractId: dispatch.ContractId,
                shipmentId: null,
                transportLegId: null,
                truckDispatchId: dispatch.Id,
                reference: $"TRUCK-DISPATCH:{dispatch.Id}");
        }

        return true;
    }

    /// <summary>
    /// ثبت/به‌روزرسانی رکورد «تفاوت حمل» یک دیسپچ — دقیقاً همان قرارداد <see cref="TransportVarianceMath"/>
    /// که مسیر تخلیهٔ DispatchController دارد، تا «راپور کسری و اضافه‌بار حمل» هر دو مسیر را یکسان ببیند:
    ///   • تفاوت = بارگیری − تخلیه ⇒ مثبت = کسری، منفی = اضافه‌بار.
    ///   • هر تفاوت غیرصفر یک رکورد قابل ردیابی می‌سازد؛ رکورد موجودِ همین دیسپچ به‌روز می‌شود (نه رکورد تکراری).
    ///   • تفاوت صفر ⇒ رکورد قبلی لغو می‌شود.
    ///   • اضافه‌بار هرگز مقدار قابل جریمه ندارد.
    /// این متد چیزی را ذخیره نمی‌کند؛ SaveChanges با فراخوان است.
    /// </summary>
    private async Task UpsertDispatchVarianceAsync(
        TruckDispatch dispatch,
        decimal expectedQuantityMt,
        decimal dischargedQuantityMt,
        decimal allowanceMt,
        DateTime eventDate,
        decimal shortageChargeUsd,
        int? terminalId,
        int? storageTankId,
        string? reference,
        string? notes)
    {
        var lossEvent = await _db.LossEvents
            .Where(l => l.TruckDispatchId == dispatch.Id && l.Stage == LossEventStage.DispatchShortage)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        var differenceMt = TransportVarianceMath.Difference(expectedQuantityMt, dischargedQuantityMt);
        if (!TransportVarianceMath.HasVariance(differenceMt))
        {
            if (lossEvent is not null && !lossEvent.IsCancelled)
            {
                lossEvent.IsCancelled = true;
            }

            return;
        }

        var metrics = _lossWorkflow.ComputeMetrics(expectedQuantityMt, dischargedQuantityMt, allowanceMt);
        var isSurplus = TransportVarianceMath.IsSurplus(differenceMt);
        var lossEventIsNew = lossEvent is null;
        lossEvent ??= new LossEvent
        {
            Stage = LossEventStage.DispatchShortage,
            TruckDispatchId = dispatch.Id
        };

        lossEvent.Stage = LossEventStage.DispatchShortage;
        lossEvent.TruckDispatchId = dispatch.Id;
        lossEvent.ProductId = dispatch.ProductId;
        lossEvent.ContractId = dispatch.ContractId;
        lossEvent.EventDate = eventDate;
        lossEvent.ExpectedQuantityMt = expectedQuantityMt;
        lossEvent.ActualQuantityMt = dischargedQuantityMt;
        lossEvent.DifferenceQuantityMt = metrics.DifferenceQuantityMt;
        lossEvent.ToleranceQuantityMt = allowanceMt;
        lossEvent.AllowableLossMt = metrics.AllowableLossMt;
        lossEvent.ChargeableLossMt = metrics.ChargeableLossMt;
        lossEvent.ResponsiblePartyType = "Driver";
        lossEvent.ResponsiblePartyName = dispatch.Driver?.FullName;
        lossEvent.FinancialTreatment = isSurplus
            ? "Truck unload positive variance (surplus). Never charged to the driver."
            : shortageChargeUsd > 0m
                ? $"Driver shortage charge/deduction: {shortageChargeUsd:N2} USD."
                : "No driver shortage charge.";
        lossEvent.AffectsInventory = false;
        lossEvent.InventoryMovementId = null;
        lossEvent.IsCancelled = false;

        // مقصد/سند/یادداشت فقط وقتی بازنویسی می‌شوند که فراخوان مقدار تازه داشته باشد،
        // تا مرحلهٔ تخلیه، اطلاعات ثبت‌شدهٔ مرحلهٔ تسویه را پاک نکند.
        if (terminalId.HasValue)
        {
            lossEvent.TerminalId = terminalId;
        }

        if (storageTankId.HasValue)
        {
            lossEvent.StorageTankId = storageTankId;
        }

        if (reference is not null)
        {
            lossEvent.Reference = reference;
        }

        if (notes is not null)
        {
            lossEvent.Notes = notes;
        }

        if (lossEventIsNew)
        {
            _db.LossEvents.Add(lossEvent);
        }
    }

    // ── طرف کرایه: شرکت خدماتی / دارایی عملیاتی (موتر خودی) / راننده (موجود یا ساخت خودکار) ──
    private async Task<FreightParty?> ResolveFreightPartyAsync(
        TruckSettlementRowInputViewModel row,
        FreightParty fallback,
        string vehicleNumber,
        string prefix)
    {
        var value = NormalizeNullable(row.FreightParty);
        int? spId = null, assetId = null, driverId = null;
        var wantsNewDriver = false;

        if (value is null)
        {
            spId = fallback.ServiceProviderId;
            assetId = spId.HasValue ? null : fallback.OperationalAssetId;
            driverId = spId.HasValue || assetId.HasValue ? null : fallback.DriverId;
        }
        else if (value.StartsWith("sp:", StringComparison.OrdinalIgnoreCase) && int.TryParse(value[3..], out var sp))
        {
            spId = sp;
        }
        else if (value.StartsWith("asset:", StringComparison.OrdinalIgnoreCase) && int.TryParse(value[6..], out var asset))
        {
            assetId = asset;
        }
        else if (string.Equals(value, "driver:new", StringComparison.OrdinalIgnoreCase))
        {
            wantsNewDriver = true;
        }
        else if (value.StartsWith("driver:", StringComparison.OrdinalIgnoreCase) && int.TryParse(value[7..], out var driver))
        {
            driverId = driver;
        }
        else
        {
            ModelState.AddModelError(prefix + nameof(row.FreightParty), "طرف کرایه معتبر نیست.");
            return null;
        }

        if (spId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == spId.Value && p.IsActive))
        {
            ModelState.AddModelError(prefix + nameof(row.FreightParty), "شرکت خدماتی انتخاب‌شده معتبر نیست.");
            return null;
        }

        if (assetId.HasValue
            && !await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == assetId.Value && a.IsActive))
        {
            ModelState.AddModelError(prefix + nameof(row.FreightParty), "دارایی عملیاتی انتخاب‌شده معتبر نیست.");
            return null;
        }

        if (driverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == driverId.Value && d.IsActive))
        {
            ModelState.AddModelError(prefix + nameof(row.FreightParty), "راننده انتخاب‌شده معتبر نیست.");
            return null;
        }

        // کرایه دارد ولی هیچ طرفی مشخص نیست ⇒ راننده ساخته می‌شود (پروفایل خودکار در تعاریف پایه).
        var freightUsd = ComputeNetFreight(row, row.QuantityMt + Math.Max(row.ShortageMt, 0m)) ?? 0m;
        if (wantsNewDriver || (freightUsd > 0m && !spId.HasValue && !assetId.HasValue && !driverId.HasValue))
        {
            driverId = await FindOrCreateDriverAsync(NormalizeNullable(row.NewDriverName), vehicleNumber);
        }

        return new FreightParty(spId, assetId, driverId);
    }

    // نام خالی ⇒ نام خودکار از نمبر وسیله. اگر راننده فعالِ هم‌نام موجود بود، همان استفاده می‌شود.
    private async Task<int> FindOrCreateDriverAsync(string? requestedName, string vehicleNumber)
    {
        var name = requestedName ?? $"راننده {vehicleNumber}".Trim();

        var existing = await _db.Drivers
            .Where(d => d.FullName == name && d.IsActive)
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            return existing.Id;
        }

        var driver = new Driver { FullName = name, IsActive = true };
        _db.Drivers.Add(driver);
        await _db.SaveChangesAsync();
        return driver.Id;
    }

    // کرایهٔ خالص موتر/واگنِ خودِ شرکت = عوایدِ دارایی: مصرفِ همان حمل با OperationalAssetId
    // (نوع استاندارد «کرایه حمل») — بدون LedgerEntry چون طلبِ طرف خارجی نیست.
    // پروفایل دارایی همین رکورد را به‌عنوان «عواید کرایه» می‌شمارد (IsAssetFreightIncome).
    private async Task AddAssetFreightIncomeAsync(
        InventoryTransportReceiptService? receiptService,
        int operationalAssetId,
        decimal amountUsd,
        DateTime date,
        int? contractId,
        int? shipmentId,
        int? transportLegId,
        int? truckDispatchId,
        string reference)
    {
        if (amountUsd <= 0m)
        {
            return;
        }

        receiptService ??= new InventoryTransportReceiptService(_db, _currencyConversion, _lineage);
        var expenseType = await receiptService.EnsureTransportFreightExpenseTypeAsync();
        var description = $"Freight income for operational asset — {reference}";

        var exists = await _db.ExpenseTransactions.AnyAsync(e => !e.IsCancelled
            && e.OperationalAssetId == operationalAssetId
            && e.ExpenseTypeId == expenseType.Id
            && e.Description == description);
        if (exists)
        {
            return;
        }

        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = contractId,
            ShipmentId = shipmentId,
            TransportLegId = transportLegId,
            TruckDispatchId = truckDispatchId,
            OperationalAssetId = operationalAssetId,
            ExpenseDate = date,
            Amount = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = description
        };
        _db.ExpenseTransactions.Add(expense);
        await _db.SaveChangesAsync();

        // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseAsync(expense);
        }
    }

    // ── ساخت لیست وسایط دارای بار (حمل‌های موجودی + ارسال‌های موتر نهایی‌نشده) ──
    private async Task<TruckSettlementIndexViewModel> BuildIndexAsync(
        List<TruckSettlementRowInputViewModel>? preserveInputs,
        string? query = null,
        TruckSettlementSourceKind? kind = null)
    {
        var rows = new List<TruckSettlementRowViewModel>();

        var legs = await _db.InventoryTransportLegs
            .AsNoTracking()
            .Where(l => (l.Status == InventoryTransportLegStatus.Loaded || l.Status == InventoryTransportLegStatus.InTransit)
                && !l.IsFreightSettled
                && (l.TransportType == LoadingTransportType.Truck || l.TransportType == LoadingTransportType.Wagon))
            .Select(l => new
            {
                l.Id,
                l.TransportType,
                TruckPlateNumber = l.Truck != null ? l.Truck.PlateNumber : null,
                l.WagonNumber,
                DriverName = l.Driver != null ? l.Driver.FullName : null,
                ProductName = l.Product != null ? l.Product.Name : null,
                ContractNumber = l.SourcePurchaseContract != null ? l.SourcePurchaseContract.ContractNumber : null,
                SourceName = l.SourceTerminal != null ? l.SourceTerminal.Name : null,
                DestinationTerminalName = l.DestinationTerminal != null ? l.DestinationTerminal.Name : null,
                DestinationLocationName = l.DestinationLocation != null ? l.DestinationLocation.Name : null,
                l.LoadedDate,
                l.QuantityMt,
                l.ServiceProviderId,
                l.OperationalAssetId,
                l.DriverId
            })
            .ToListAsync();

        var legIds = legs.Select(l => l.Id).ToList();
        var consumedByLeg = legIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.InventoryTransportReceipts
                .AsNoTracking()
                .Where(r => legIds.Contains(r.InventoryTransportLegId) && !r.IsCancelled)
                .GroupBy(r => r.InventoryTransportLegId)
                .Select(g => new { LegId = g.Key, Mt = g.Sum(r => r.ReceivedQuantityMt + r.ShortageQuantityMt) })
                .ToDictionaryAsync(g => g.LegId, g => g.Mt);

        foreach (var leg in legs)
        {
            consumedByLeg.TryGetValue(leg.Id, out var consumedMt);
            var remainingMt = decimal.Round(leg.QuantityMt - consumedMt, 4, MidpointRounding.AwayFromZero);
            if (remainingMt <= QuantityEpsilon)
            {
                continue;
            }

            rows.Add(new TruckSettlementRowViewModel
            {
                Kind = TruckSettlementSourceKind.Leg,
                SourceId = leg.Id,
                TypeLabel = leg.TransportType == LoadingTransportType.Wagon
                    ? UiText.T(HttpContext, "واگن (حمل از موجودی)", "Wagon (inventory transfer)")
                    : UiText.T(HttpContext, "موتر (حمل از موجودی)", "Truck (inventory transfer)"),
                VehicleNumber = leg.TruckPlateNumber ?? leg.WagonNumber ?? $"#{leg.Id}",
                DriverName = leg.DriverName,
                ProductName = leg.ProductName ?? "",
                ContractNumber = leg.ContractNumber ?? "",
                SourceName = leg.SourceName,
                DestinationName = leg.DestinationTerminalName ?? leg.DestinationLocationName,
                Date = leg.LoadedDate,
                RemainingQuantityMt = remainingMt,
                DefaultFreightParty = BuildDefaultParty(leg.ServiceProviderId, leg.OperationalAssetId, leg.DriverId)
            });
        }

        var dispatches = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.Status != DispatchStatus.Delivered
                && d.Status != DispatchStatus.Cancelled
                && !d.IsFreightSettled
                && d.SalesTransactionId == null
                && !_db.DeliveryReceipts.Any(r => r.TruckDispatchId == d.Id))
            .Select(d => new
            {
                d.Id,
                TruckPlateNumber = d.Truck != null ? d.Truck.PlateNumber : null,
                DriverName = d.Driver != null ? d.Driver.FullName : null,
                ProductName = d.Product != null ? d.Product.Name : null,
                ContractNumber = d.Contract != null ? d.Contract.ContractNumber : null,
                DestinationLocationName = d.DestinationLocation != null ? d.DestinationLocation.Name : null,
                d.DispatchDate,
                d.LoadedQuantityMt,
                d.ServiceProviderId,
                d.OperationalAssetId,
                d.DriverId
            })
            .ToListAsync();

        var arrivalMovements = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.Direction == MovementDirection.In
                && m.ReferenceDocument != null
                && m.ReferenceDocument.StartsWith(ArrivalRefPrefix))
            .Select(m => new { m.ReferenceDocument, m.QuantityMt })
            .ToListAsync();

        var arrivalsByDispatch = arrivalMovements
            .Select(m => new { DispatchId = ParseArrivalDispatchId(m.ReferenceDocument!), m.QuantityMt })
            .Where(x => x.DispatchId.HasValue)
            .GroupBy(x => x.DispatchId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityMt));

        foreach (var dispatch in dispatches)
        {
            arrivalsByDispatch.TryGetValue(dispatch.Id, out var arrivalsMt);
            var remainingMt = decimal.Round(dispatch.LoadedQuantityMt - arrivalsMt, 4, MidpointRounding.AwayFromZero);
            if (remainingMt <= QuantityEpsilon)
            {
                continue;
            }

            rows.Add(new TruckSettlementRowViewModel
            {
                Kind = TruckSettlementSourceKind.Dispatch,
                SourceId = dispatch.Id,
                TypeLabel = UiText.T(HttpContext, "ارسال موتر", "Truck send"),
                VehicleNumber = dispatch.TruckPlateNumber ?? $"#{dispatch.Id}",
                DriverName = dispatch.DriverName,
                ProductName = dispatch.ProductName ?? "",
                ContractNumber = dispatch.ContractNumber ?? "",
                SourceName = null,
                DestinationName = dispatch.DestinationLocationName,
                Date = dispatch.DispatchDate,
                RemainingQuantityMt = remainingMt,
                DefaultFreightParty = BuildDefaultParty(dispatch.ServiceProviderId, dispatch.OperationalAssetId, dispatch.DriverId)
            });
        }

        rows = rows
            .OrderByDescending(r => r.Date)
            .ThenByDescending(r => r.SourceId)
            .ToList();

        // فیلتر نوار جست‌وجو (نوع منبع + متن آزاد) — فقط نمایش لیست را محدود می‌کند؛ منطق تسویه دست‌نخورده.
        if (kind.HasValue)
        {
            rows = rows.Where(r => r.Kind == kind.Value).ToList();
        }

        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            rows = rows.Where(r =>
                    Contains(r.VehicleNumber, term)
                    || Contains(r.DriverName, term)
                    || Contains(r.ProductName, term)
                    || Contains(r.ContractNumber, term)
                    || Contains(r.SourceName, term)
                    || Contains(r.DestinationName, term)
                    || Contains(r.TypeLabel, term)
                    || Contains(r.Date.ToString("yyyy-MM-dd"), term))
                .ToList();

            static bool Contains(string? value, string term)
                => !string.IsNullOrEmpty(value)
                    && value.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        var inputs = new List<TruckSettlementRowInputViewModel>(rows.Count);
        foreach (var row in rows)
        {
            var preserved = preserveInputs?.FirstOrDefault(i => i.Kind == row.Kind && i.SourceId == row.SourceId);
            inputs.Add(preserved ?? new TruckSettlementRowInputViewModel
            {
                Kind = row.Kind,
                SourceId = row.SourceId,
                OperationDate = AfghanistanBusinessClock.SystemToday,
                QuantityMt = row.RemainingQuantityMt,
                FreightParty = row.DefaultFreightParty
            });
        }

        await PopulateLookupsAsync();

        return new TruckSettlementIndexViewModel
        {
            Rows = rows,
            Inputs = inputs,
            Query = term,
            Kind = kind
        };
    }

    private async Task PopulateLookupsAsync()
    {
        // فقط طرف‌های کرایه لازم است؛ ترمینال/مخزن/مشتری/اسعار حذف شد (تخلیه/فروش در این صفحه نیست).
        ViewBag.ServiceProviders = await _db.ServiceProviders.AsNoTracking()
            .Where(p => p.IsActive).OrderBy(p => p.Name)
            .Select(p => new TruckSettlementPartyOption(p.Id, p.Name)).ToListAsync();

        ViewBag.OperationalAssets = await _db.OperationalAssets.AsNoTracking()
            .Where(a => a.IsActive).OrderBy(a => a.AssetCode).ThenBy(a => a.Name)
            .Select(a => new TruckSettlementPartyOption(a.Id, a.AssetCode + " - " + a.Name)).ToListAsync();

        ViewBag.Drivers = await _db.Drivers.AsNoTracking()
            .Where(d => d.IsActive).OrderBy(d => d.FullName)
            .Select(d => new TruckSettlementPartyOption(d.Id, d.FullName)).ToListAsync();
    }

    private async Task<decimal> GetArrivalDischargedMtAsync(int dispatchId)
    {
        var refPrefix = $"{ArrivalRefPrefix}{dispatchId}:";
        return await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.Direction == MovementDirection.In
                && m.ReferenceDocument != null
                && m.ReferenceDocument.StartsWith(refPrefix))
            .SumAsync(m => (decimal?)m.QuantityMt) ?? 0m;
    }

    private static int? ParseArrivalDispatchId(string referenceDocument)
    {
        // فرمت فرم قدیمی: TRUCK-ARRIVAL:{dispatchId}:R{n}
        var rest = referenceDocument[ArrivalRefPrefix.Length..];
        var separator = rest.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        return int.TryParse(rest[..separator], out var id) ? id : null;
    }

    // نمبر موتر/واگن ردیف برای نشان‌دار کردن پیام خطا (تا در خلاصهٔ خطا مشخص باشد کدام ردیف).
    private async Task<string> ResolveVehicleLabelAsync(TruckSettlementRowInputViewModel row)
    {
        if (row.Kind == TruckSettlementSourceKind.Leg)
        {
            var leg = await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => l.Id == row.SourceId)
                .Select(l => new { Plate = l.Truck != null ? l.Truck.PlateNumber : null, l.WagonNumber })
                .FirstOrDefaultAsync();
            return leg?.Plate ?? leg?.WagonNumber ?? $"حمل #{row.SourceId}";
        }

        var plate = await _db.TruckDispatches.AsNoTracking()
            .Where(d => d.Id == row.SourceId)
            .Select(d => d.Truck != null ? d.Truck.PlateNumber : null)
            .FirstOrDefaultAsync();
        return plate ?? $"دیسپچ #{row.SourceId}";
    }

    // پیام‌های خطای کلیدهای این ردیف را با «نمبر موتر» جلو می‌اندازد.
    private void PrefixRowErrorsWithVehicle(string prefix, string vehicleLabel)
    {
        foreach (var entry in ModelState)
        {
            if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal) || entry.Value.Errors.Count == 0)
            {
                continue;
            }

            var messages = entry.Value.Errors.Select(e => e.ErrorMessage).ToList();
            entry.Value.Errors.Clear();
            foreach (var message in messages)
            {
                entry.Value.Errors.Add($"«{vehicleLabel}» — {message}");
            }
        }
    }

    private static string? BuildDefaultParty(int? serviceProviderId, int? operationalAssetId, int? driverId)
        => serviceProviderId.HasValue ? $"sp:{serviceProviderId.Value}"
            : operationalAssetId.HasValue ? $"asset:{operationalAssetId.Value}"
                : driverId.HasValue ? $"driver:{driverId.Value}" : null;

    // کرایهٔ خالص = کرایهٔ کلی منهای کسورات دیگر (حداقل صفر). کسریِ قابل مجرا جدا کم می‌شود.
    // کرایهٔ کلی: مستقیم واردشده، یا نرخ فی تن × مقدار مبنا (وزن تخلیه + کسری = بار حمل).
    private static decimal? ComputeNetFreight(TruckSettlementRowInputViewModel row, decimal baseQuantityMt)
    {
        var grossUsd = row.FreightUsd
            ?? (row.FreightRateUsdPerMt.HasValue && row.FreightRateUsdPerMt.Value > 0m
                ? FreightShortageMath.GrossFreightUsd(baseQuantityMt, row.FreightRateUsdPerMt.Value)
                : (decimal?)null);
        if (!grossUsd.HasValue)
        {
            return null;
        }

        var net = grossUsd.Value - (row.OtherDeductionsUsd ?? 0m);
        return decimal.Round(Math.Max(net, 0m), 4, MidpointRounding.AwayFromZero);
    }

    private static decimal? NormalizePositiveDecimal(decimal? value)
        => value.HasValue && value.Value > 0m ? value.Value : null;

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
