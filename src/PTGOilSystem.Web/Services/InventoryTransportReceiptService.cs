using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.InventoryTransport;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

// منطق واحدِ ثبت رسید انتقال از موجودی برای یک تخصیص/leg.
// هم کنترلر تک‌تخصیص (InventoryTransportReceipts/Create) و هم workflow گروهی (InventoryTransportLegs)
// از همین سرویس استفاده می‌کنند تا هیچ منطق مالی موازی ساخته نشود.
// این سرویس تراکنش را مدیریت نمی‌کند؛ فراخوان باید همهٔ legها را در یک تراکنش واحد بسازد و در صورت خطا rollback کند.
public sealed class InventoryTransportReceiptService
{
    public const string ReceiptFreightExpenseCode = "TRANSPORT-RECEIPT-FREIGHT";

    // نوع مصرف استاندارد «کرایه حمل» که از مودال «ثبت مصرف» انتخاب می‌شود.
    // وقتی کرایه با این نوع ثبت شده باشد، فرم رسید همان مبلغ را به‌عنوان «کرایه نهایی»
    // می‌خواند و کرایهٔ جداگانه روی رسید ذخیره نمی‌کند تا در P&L دوبار شمرده نشود.
    public const string TransportFreightExpenseCode = "TRANSPORT-FREIGHT";

    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IInventoryLineageWriter _lineage;

    // writer اختیاری: call siteهای موجود بدون تغییر می‌مانند و در نبودِ آن writerِ خاموش (no-op) استفاده می‌شود.
    // مراحل ۵، ۷ و ۸ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe.
    private readonly Accounting.IExpenseAccountingAdapter? _expenseAccounting;
    private readonly Accounting.ISalesAccountingAdapter? _salesAccounting;
    private readonly Accounting.IShortageChargeAccountingAdapter? _shortageAccounting;
    private readonly Accounting.IInventoryTransferAccountingAdapter? _transferAccounting;

    public InventoryTransportReceiptService(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IInventoryLineageWriter? lineage = null,
        Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        Accounting.ISalesAccountingAdapter? salesAccounting = null,
        Accounting.IShortageChargeAccountingAdapter? shortageAccounting = null,
        Accounting.IInventoryTransferAccountingAdapter? transferAccounting = null)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _lineage = lineage ?? InventoryLineageWriterFactory.Disabled(db);
        _expenseAccounting = expenseAccounting;
        _salesAccounting = salesAccounting;
        _shortageAccounting = shortageAccounting;
        _transferAccounting = transferAccounting;
    }

    public async Task<InventoryTransportLeg?> LoadLegAsync(int id, bool tracking)
    {
        var query = _db.InventoryTransportLegs
            .Include(l => l.SourcePurchaseContract)
            .Include(l => l.Product)
            .Include(l => l.SourceStorageTank)
            .Include(l => l.DestinationTerminal)
            .Include(l => l.DestinationStorageTank)
            .Include(l => l.DestinationLocation)
            .Where(l => l.Id == id);

        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync();
    }

    // اعتبارسنجی کامل یک رسید. خطاها با کلیدِ پیشوندی (برای فرم گروهی) به ModelState اضافه می‌شوند.
    public async Task ValidateAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        ModelStateDictionary modelState,
        string keyPrefix = "")
    {
        // باقیمانده حمل = مقدار کل منهای مجموع رسیدهای قبلی (دریافت + کسری). چند رسید جزئی مجاز است تا باقیمانده صفر شود.
        var remainingMt = await GetRemainingQuantityAsync(leg);

        NormalizeTruckReceiptFields(model, leg, remainingMt);

        model.ServiceProviderId = NormalizePositiveInt(model.ServiceProviderId);
        model.OperationalAssetId = NormalizePositiveInt(model.OperationalAssetId);

        if (leg.Status is not (InventoryTransportLegStatus.Loaded or InventoryTransportLegStatus.InTransit))
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "فقط حمل‌های Loaded یا InTransit می‌توانند رسید مقصد بگیرند.");
        }

        if (remainingMt <= 0.0001m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "این حمل کاملاً تخلیه شده است؛ باقیمانده‌ای برای رسید جدید وجود ندارد.");
        }

        if (model.ReceiptDate == default)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceiptDate), "Receipt date is required.");
        }

        if (!model.SettlementOnly && model.ReceivedQuantityMt <= 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceivedQuantityMt), "Received quantity must be greater than zero.");
        }

        // فقط موتر اجازهٔ دریافت بیش از باقیمانده (و کسری منفی) را دارد؛ واگن مثل سایر حالت‌ها محدود به باقیمانده است.
        if (leg.TransportType != LoadingTransportType.Truck && model.ReceivedQuantityMt > remainingMt + 0.0001m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceivedQuantityMt), $"مقدار دریافت نمی‌تواند از باقیمانده حمل بیشتر باشد ({remainingMt:N4} MT).");
        }

        // «فقط تسویه» کسری منفی (اضافه‌وزن ترازوی مقصد) را برای هر نوع حملی می‌پذیرد.
        if (!model.SettlementOnly
            && leg.TransportType != LoadingTransportType.Truck
            && model.ShortageQuantityMt < 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ShortageQuantityMt), "Shortage quantity cannot be negative.");
        }

        if (UsesUnloadFreightFlow(leg))
        {
            ValidateTruckReceiptFields(model, leg, remainingMt, modelState, keyPrefix);
        }

        await ValidateOperationalPartyAsync(model.ServiceProviderId, model.OperationalAssetId, modelState, keyPrefix);

        // «فقط تسویه»: تخلیه‌ای در کار نیست، پس مقصد/مخزن لازم نیست و اعتبارسنجی مقصد رد می‌شود.
        if (model.SettlementOnly)
        {
            return;
        }

        if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale)
        {
            await ValidateDirectSaleAsync(model, leg, modelState, keyPrefix);
            return;
        }

        if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch)
        {
            await ValidateDirectDispatchAsync(model, leg, modelState, keyPrefix);
            return;
        }

        if (model.ReceiptDestination != InventoryTransportReceiptDestination.ToInventory)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ReceiptDestination), "DirectDispatch و Mixed برای رسید انتقال از موجودی در این فاز فعال نیستند.");
            return;
        }

        if (!model.DestinationTerminalId.HasValue)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DestinationTerminalId), "Destination terminal is required for ToInventory receipt.");
            return;
        }

        var terminalExists = await _db.Terminals.AsNoTracking()
            .AnyAsync(t => t.Id == model.DestinationTerminalId.Value);
        if (!terminalExists)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DestinationTerminalId), "Destination terminal is invalid.");
        }

        if (model.DestinationStorageTankId.HasValue)
        {
            var tank = await _db.StorageTanks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == model.DestinationStorageTankId.Value);
            if (tank is null)
            {
                modelState.AddModelError(keyPrefix + nameof(model.DestinationStorageTankId), "Destination tank is invalid.");
            }
            else if (tank.TerminalId != model.DestinationTerminalId)
            {
                modelState.AddModelError(keyPrefix + nameof(model.DestinationStorageTankId), "Destination tank must belong to destination terminal.");
            }
        }
    }

    // FX فروش مستقیم را resolve می‌کند؛ در صورت خطا به ModelState اضافه می‌کند و null برمی‌گرداند.
    public async Task<CurrencyConversionResult?> ResolveSaleConversionAsync(
        InventoryTransportReceiptCreateViewModel model,
        ModelStateDictionary modelState,
        string keyPrefix = "")
    {
        if (model.ReceiptDestination != InventoryTransportReceiptDestination.DirectSale)
        {
            return null;
        }

        try
        {
            return await _currencyConversion.ResolveToBaseAsync(
                model.SaleCurrency,
                model.SaleDate!.Value.Date,
                model.SaleAppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleAppliedFxRateToUsd), ex.Message);
            return null;
        }
    }

    // ساختِ تمام رکوردهای یک رسید (رسید، کرایه، کسری، حرکت موجودی/فروش/دیسپچ، وضعیت leg).
    // تراکنش را مدیریت نمی‌کند. leg باید tracked باشد.
    public async Task<InventoryTransportReceipt> ApplyAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult? saleConversion)
    {
        var receipt = new InventoryTransportReceipt
        {
            InventoryTransportLegId = model.InventoryTransportLegId,
            ReceiptDate = model.ReceiptDate,
            ReceivedQuantityMt = model.ReceivedQuantityMt,
            ShortageQuantityMt = model.ShortageQuantityMt,
            AllowanceMt = model.AllowanceMt,
            ChargeableShortageMt = model.ChargeableShortageMt,
            FreightRateUsdPerMt = model.FreightRateUsdPerMt,
            FreightCostUsd = model.FreightCostUsd,
            ShortageRateUsd = model.ShortageRateUsd,
            ShortageChargeUsd = model.ShortageChargeUsd,
            FreightPayableUsd = model.FreightPayableUsd,
            ServiceProviderId = model.ServiceProviderId,
            OperationalAssetId = model.OperationalAssetId,
            ReceiptDestination = model.ReceiptDestination,
            DestinationTerminalId = model.DestinationTerminalId,
            DestinationStorageTankId = model.DestinationStorageTankId,
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };

        _db.InventoryTransportReceipts.Add(receipt);
        await _db.SaveChangesAsync();

        await SyncReceiptFreightExpenseAsync(receipt, leg);
        await SyncShortageDebtAsync(receipt, leg, model.ShortageAsSeparateDebt);

        LossEvent? shortageLoss = null;
        InventoryMovement? inboundMovement = null;
        SalesTransaction? directSale = null;

        if (model.ShortageQuantityMt > 0m)
        {
            shortageLoss = new LossEvent
            {
                Stage = LossEventStage.ReceiptShortage,
                ProductId = leg.ProductId,
                ContractId = leg.SourcePurchaseContractId,
                ShipmentId = leg.ShipmentId,
                TransportLegId = leg.Id,
                TerminalId = model.DestinationTerminalId,
                StorageTankId = model.DestinationStorageTankId,
                EventDate = model.ReceiptDate,
                ExpectedQuantityMt = model.ReceivedQuantityMt + model.ShortageQuantityMt,
                ActualQuantityMt = model.ReceivedQuantityMt,
                DifferenceQuantityMt = model.ShortageQuantityMt,
                ToleranceQuantityMt = model.AllowanceMt ?? 0m,
                AllowableLossMt = model.AllowanceMt ?? 0m,
                ChargeableLossMt = model.ChargeableShortageMt ?? model.ShortageQuantityMt,
                AffectsInventory = false,
                Reference = $"TRANSPORT-RECEIPT:{receipt.Id}",
                Notes = "Inventory transport receipt shortage"
            };
            _db.LossEvents.Add(shortageLoss);
        }

        // «فقط تسویه» (ReceivedQuantityMt=0) هیچ حرکت موجودی نمی‌سازد؛ بار داخل وسیله می‌ماند.
        if (model.ReceiptDestination == InventoryTransportReceiptDestination.ToInventory
            && model.ReceivedQuantityMt > 0m)
        {
            inboundMovement = new InventoryMovement
            {
                ProductId = leg.ProductId,
                ContractId = leg.SourcePurchaseContractId,
                TerminalId = model.DestinationTerminalId!.Value,
                StorageTankId = model.DestinationStorageTankId,
                Direction = MovementDirection.In,
                MovementDate = model.ReceiptDate,
                QuantityMt = model.ReceivedQuantityMt,
                ReferenceDocument = $"TRANSPORT-RECEIPT:{receipt.Id}",
                Notes = "Inventory transport leg destination receipt"
            };

            _db.InventoryMovements.Add(inboundMovement);
            await _db.SaveChangesAsync();

            receipt.InventoryMovementId = inboundMovement.Id;
        }
        else if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectSale)
        {
            directSale = BuildDirectSale(model, leg, saleConversion!);
            _db.SalesTransactions.Add(directSale);
            await _db.SaveChangesAsync();

            receipt.SalesTransactionId = directSale.Id;

            _db.LedgerEntries.Add(BuildDirectSaleLedgerEntry(directSale, leg.SourcePurchaseContractId, saleConversion!));
            await _db.SaveChangesAsync();

            // مرحله ۷ — Dual-write داخل همان Transaction قدیمی.
            if (_salesAccounting is not null)
            {
                await _salesAccounting.TryPostSaleAsync(directSale);
                await _salesAccounting.TryPostCogsAsync(directSale);
            }
        }
        else if (model.ReceiptDestination == InventoryTransportReceiptDestination.DirectDispatch)
        {
            // رسیدهای «همراهِ» موتر چندواگنه دیسپچ جدا نمی‌سازند؛ دیسپچ واحد روی رسید اول ثبت شده است.
            if (!model.SkipDirectDispatchRecord)
            {
                var dispatch = BuildDirectDispatch(model, leg, receipt.Id);
                _db.TruckDispatches.Add(dispatch);
                await _db.SaveChangesAsync();
            }
        }

        // Dual-write داخل همان Transaction قدیمی: سهمِ این رسید از بهای «کالای در راه» خارج و به حوضچهٔ
        // ترمینال مقصد وارد می‌شود؛ بهای کسری هم همان‌جا به حساب ضایعات می‌رود. آداپتر خودش فقط مسیر
        // ToInventory با دریافتِ مثبت را می‌پذیرد و بقیهٔ مقصدها را Skip می‌کند.
        if (_transferAccounting is not null)
        {
            await _transferAccounting.TryPostReceiptAsync(receipt);
        }

        // فقط وقتی باقیمانده حمل صفر شد، حمل «تکمیل» می‌شود؛ در تخلیهٔ جزئی حمل باز می‌ماند تا باقیمانده هم رسید بگیرد.
        var remainingAfterMt = await GetRemainingQuantityAsync(leg);
        if (remainingAfterMt <= 0.0001m)
        {
            leg.Status = InventoryTransportLegStatus.Received;
            await _db.SaveChangesAsync();
        }

        // لایهٔ Lineage (پشت flag Lineage:WriteLots؛ با flag خاموش no-op). فقط رکوردهای نسب‌نامه insert می‌شود.
        await _lineage.OnLegReceiptAsync(leg, receipt, inboundMovement, shortageLoss);
        if (directSale is not null)
        {
            await _lineage.OnDirectSaleAsync(leg, directSale);
        }

        return receipt;
    }

    private async Task ValidateDirectSaleAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        if (!model.SaleCustomerId.HasValue || model.SaleCustomerId.Value <= 0)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleCustomerId), "Customer is required for direct sale.");
        }
        else if (!await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.SaleCustomerId.Value && c.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleCustomerId), "Customer is invalid.");
        }

        var normalizedInvoice = model.SaleInvoiceNumber?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInvoice))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleInvoiceNumber), "Invoice number is required for direct sale.");
        }
        else if (await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.InvoiceNumber == normalizedInvoice))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleInvoiceNumber), "Invoice number already exists.");
        }
        else
        {
            model.SaleInvoiceNumber = normalizedInvoice;
        }

        if (!model.SaleDate.HasValue || model.SaleDate.Value == default)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleDate), "Sale date is required for direct sale.");
        }

        if (!model.SaleUnitPriceInCurrency.HasValue || model.SaleUnitPriceInCurrency.Value <= 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleUnitPriceInCurrency), "Unit price is required for direct sale.");
        }

        model.SaleCurrency = SystemCurrency.Normalize(model.SaleCurrency);
        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.SaleCurrency && c.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.SaleCurrency), "Invalid currency selection.");
        }

        if (leg.SourcePurchaseContract is null)
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "Source purchase contract context is missing.");
        }
    }

    private async Task ValidateDirectDispatchAsync(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        if (!model.DirectDispatchTruckId.HasValue || model.DirectDispatchTruckId.Value <= 0)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchTruckId), "Truck is required for direct dispatch.");
        }
        else if (!await _db.Trucks.AsNoTracking().AnyAsync(t => t.Id == model.DirectDispatchTruckId.Value && t.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchTruckId), "Truck is invalid.");
        }

        if (model.DirectDispatchDriverId.HasValue
            && !await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == model.DirectDispatchDriverId.Value && d.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchDriverId), "Driver is invalid.");
        }

        if (model.DirectDispatchDestinationLocationId.HasValue
            && !await _db.Locations.AsNoTracking().AnyAsync(l => l.Id == model.DirectDispatchDestinationLocationId.Value && l.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchDestinationLocationId), "Destination is invalid.");
        }

        if (!model.DirectDispatchDate.HasValue || model.DirectDispatchDate.Value == default)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchDate), "Dispatch date is required.");
        }

        if (!model.DirectDispatchLoadedQuantityMt.HasValue || model.DirectDispatchLoadedQuantityMt.Value <= 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchLoadedQuantityMt), "Loaded quantity is required for direct dispatch.");
        }
        // در انتقال گروهیِ چندواگنه، دیسپچِ واحدِ موتر وزن کامل را دارد و از دریافتِ رسید اول بیشتر است؛
        // بقیهٔ وزن در رسیدهای همراه (بدون دیسپچ) حساب می‌شود — پس این سقف فقط با flag خاموش اعمال می‌شود.
        else if (!model.AllowDirectDispatchBeyondReceipt && model.DirectDispatchLoadedQuantityMt.Value > model.ReceivedQuantityMt)
        {
            modelState.AddModelError(keyPrefix + nameof(model.DirectDispatchLoadedQuantityMt), "Loaded quantity cannot exceed received quantity for direct dispatch.");
        }

        if (leg.SourcePurchaseContract is null)
        {
            modelState.AddModelError(keyPrefix + nameof(model.InventoryTransportLegId), "Source purchase contract context is missing.");
        }
    }

    private static SalesTransaction BuildDirectSale(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        CurrencyConversionResult conversion)
    {
        var totalInCurrency = decimal.Round(
            model.ReceivedQuantityMt * model.SaleUnitPriceInCurrency!.Value,
            4,
            MidpointRounding.AwayFromZero);

        return new SalesTransaction
        {
            ContractId = null,
            CompanyId = leg.SourcePurchaseContract!.CompanyId,
            CustomerId = model.SaleCustomerId!.Value,
            ProductId = leg.ProductId,
            DestinationLocationId = leg.DestinationLocationId,
            ShipmentId = leg.ShipmentId,
            SaleStage = SaleStage.InTransit,
            InvoiceNumber = model.SaleInvoiceNumber!,
            SaleDate = model.SaleDate!.Value.Date,
            QuantityMt = model.ReceivedQuantityMt,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.SaleUnitPriceInCurrency.Value,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(model.SaleUnitPriceInCurrency.Value),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };
    }

    private static LedgerEntry BuildDirectSaleLedgerEntry(
        SalesTransaction sale,
        int sourcePurchaseContractId,
        CurrencyConversionResult conversion)
        => SaleLedgerFactory.BuildSaleLedgerEntry(sale, conversion, contractId: sourcePurchaseContractId);

    private static TruckDispatch BuildDirectDispatch(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        int receiptId)
        => new()
        {
            DispatchMode = TruckDispatchMode.DirectFromReceipt,
            InventoryTransportReceiptId = receiptId,
            ContractId = leg.SourcePurchaseContractId,
            ProductId = leg.ProductId,
            TruckId = model.DirectDispatchTruckId!.Value,
            DriverId = model.DirectDispatchDriverId,
            DestinationLocationId = model.DirectDispatchDestinationLocationId ?? leg.DestinationLocationId,
            ServiceProviderId = model.ServiceProviderId,
            OperationalAssetId = model.OperationalAssetId,
            DispatchDate = model.DirectDispatchDate!.Value.Date,
            Status = DispatchStatus.Loaded,
            LoadedQuantityMt = model.DirectDispatchLoadedQuantityMt!.Value,
            FreightCostUsd = model.FreightCostUsd,
            FreightPayableUsd = model.FreightPayableUsd,
            TicketSerialNumber = string.IsNullOrWhiteSpace(model.DirectDispatchTicketSerialNumber)
                ? null
                : model.DirectDispatchTicketSerialNumber.Trim(),
            Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim()
        };

    // باقیمانده حمل = مقدار کل منهای مجموع رسیدهای فعال (دریافت + کسری). مبنای مجاز بودن رسید جزئی بعدی.
    private async Task<decimal> GetRemainingQuantityAsync(InventoryTransportLeg leg)
    {
        var consumedMt = await _db.InventoryTransportReceipts
            .Where(r => r.InventoryTransportLegId == leg.Id && !r.IsCancelled)
            .SumAsync(r => r.ReceivedQuantityMt + r.ShortageQuantityMt);
        return decimal.Round(leg.QuantityMt - consumedMt, 4, MidpointRounding.AwayFromZero);
    }

    // موتر و واگن هر دو از جریان «تخلیه + کرایه» (تلورانس، کسری قابل مجرا، کرایهٔ فی‌تن) استفاده می‌کنند.
    private static bool UsesUnloadFreightFlow(InventoryTransportLeg leg)
        => leg.TransportType is LoadingTransportType.Truck or LoadingTransportType.Wagon;

    private static void NormalizeTruckReceiptFields(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        decimal remainingMt)
    {
        model.TransportType = leg.TransportType;
        model.LoadedQuantityMt = leg.QuantityMt;

        // «فقط تسویه»: دریافت صفر و مقصد ToInventory (بدون Movement) اجبار می‌شود.
        if (model.SettlementOnly)
        {
            model.ReceivedQuantityMt = 0m;
            model.ReceiptDestination = InventoryTransportReceiptDestination.ToInventory;
        }

        if (!UsesUnloadFreightFlow(leg))
        {
            model.AllowanceMt = null;
            model.ChargeableShortageMt = null;
            model.FreightRateUsdPerMt = null;
            model.FreightCostUsd = null;
            model.ShortageRateUsd = null;
            model.ShortageChargeUsd = null;
            model.FreightPayableUsd = null;
            return;
        }

        // کسری دیگر «مقدار کل منهای دریافت» نیست؛ کاربر آن را در همان تخلیه دستی ثبت می‌کند (باقیمانده در وسیله می‌ماند).
        // در «فقط تسویه» کسری منفی نگه داشته می‌شود: اضافه‌وزن ترازوی مقصد، باقیماندهٔ قابل تخلیه/فروش را بالا می‌برد.
        if (!model.SettlementOnly && model.ShortageQuantityMt < 0m)
        {
            model.ShortageQuantityMt = 0m;
        }

        // مقدار مبنای این رسید = دریافت + کسری همین تخلیه (در تخلیهٔ کامل برابر با مقدار حمل می‌شود).
        // در «فقط تسویه» کرایه بر کل باقیماندهٔ حمل (بارِ رسیده + کسری مسیر) محاسبه می‌شود.
        var baseQuantityMt = model.SettlementOnly
            ? remainingMt
            : model.ReceivedQuantityMt + model.ShortageQuantityMt;

        var allowanceMt = model.AllowanceMt ?? 0m;
        model.AllowanceMt = allowanceMt;

        var chargeableShortageMt = ComputeChargeableShortage(model.ShortageQuantityMt, allowanceMt);
        model.ChargeableShortageMt = chargeableShortageMt;

        // کرایهٔ ناخالص (Gross): اگر مستقیم وارد نشده باشد، از نرخ فی‌تن × مقدار مبنای همین تخلیه ساخته می‌شود.
        if (!model.FreightCostUsd.HasValue && model.FreightRateUsdPerMt.HasValue)
        {
            model.FreightCostUsd = FreightShortageMath.GrossFreightUsd(
                baseQuantityMt,
                model.FreightRateUsdPerMt.Value);
        }

        // دو حالتِ ناسازگار برای خسارتِ کسری (کاربر یکی را انتخاب می‌کند):
        //   • DeductShortageFromFreight ⇒ خسارت از کرایه کم می‌شود؛ کرایهٔ پرداختنی = Gross − خسارت.
        //   • ShortageAsSeparateDebt    ⇒ کرایه دست‌نخورده (Gross) می‌ماند و خسارت جدا به‌عنوان بدهیِ
        //                                 مسئول در ApplyAsync ثبت می‌شود.
        // اگر هر دو تیک بخورند، «بدهیِ جدا» اولویت دارد و کسر از کرایه خنثی می‌شود (بدون double-count).
        if (model.ShortageAsSeparateDebt)
        {
            model.DeductShortageFromFreight = false;
        }

        // مبلغِ خسارت فقط وقتی محاسبه/ذخیره می‌شود که قرار باشد از مسئول وصول شود (کسر یا بدهیِ جدا).
        // این مبلغ مبنای «خالص‌شدنِ» کرایه در P&L هم هست، پس در هر دو حالت پر می‌شود.
        if (model.DeductShortageFromFreight || model.ShortageAsSeparateDebt)
        {
            model.ShortageChargeUsd = model.ShortageRateUsd.HasValue
                ? decimal.Round(chargeableShortageMt * model.ShortageRateUsd.Value, 4, MidpointRounding.AwayFromZero)
                : 0m;
        }
        else
        {
            model.ShortageChargeUsd = null;
        }

        // فقط در حالتِ کسر از کرایه، خسارت از کرایهٔ پرداختنی کم می‌شود؛ در «بدهیِ جدا» کرایه کامل می‌ماند.
        var freightDeduction = model.DeductShortageFromFreight ? (model.ShortageChargeUsd ?? 0m) : 0m;
        model.FreightPayableUsd = model.FreightCostUsd.HasValue
            ? decimal.Round(model.FreightCostUsd.Value - freightDeduction, 4, MidpointRounding.AwayFromZero)
            : null;
    }

    private void ValidateTruckReceiptFields(
        InventoryTransportReceiptCreateViewModel model,
        InventoryTransportLeg leg,
        decimal remainingMt,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        // کسری دستی است؛ فقط منفی نبودن و اینکه مجموع دریافت + کسری از باقیمانده حمل بیشتر نشود بررسی می‌شود.
        // استثنا: «فقط تسویه» کسری منفی (اضافه‌وزن ترازو) را مجاز می‌داند.
        if (!model.SettlementOnly && model.ShortageQuantityMt < 0m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ShortageQuantityMt), "کسری نمی‌تواند منفی باشد.");
        }

        var baseQuantityMt = model.ReceivedQuantityMt + model.ShortageQuantityMt;
        if (baseQuantityMt > remainingMt + 0.0001m)
        {
            modelState.AddModelError(keyPrefix + nameof(model.ShortageQuantityMt), $"مجموع دریافت و کسری نمی‌تواند از باقیمانده حمل بیشتر باشد ({remainingMt:N4} MT).");
        }

        if (model.AllowanceMt is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.AllowanceMt), "Allowance cannot be negative.");
        if (model.ChargeableShortageMt is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.ChargeableShortageMt), "Chargeable shortage cannot be negative.");
        if (model.FreightRateUsdPerMt is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.FreightRateUsdPerMt), "Freight rate cannot be negative.");
        if (model.FreightCostUsd is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.FreightCostUsd), "Freight cost cannot be negative.");
        if (model.ShortageRateUsd is < 0m)
            modelState.AddModelError(keyPrefix + nameof(model.ShortageRateUsd), "Shortage rate cannot be negative.");
    }

    private async Task ValidateOperationalPartyAsync(
        int? serviceProviderId,
        int? operationalAssetId,
        ModelStateDictionary modelState,
        string keyPrefix)
    {
        if (serviceProviderId.HasValue && operationalAssetId.HasValue)
        {
            modelState.AddModelError(keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.OperationalAssetId), "Select either a service provider or an operational asset, not both.");
        }

        if (serviceProviderId.HasValue
            && !await _db.ServiceProviders.AsNoTracking().AnyAsync(p => p.Id == serviceProviderId.Value && p.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.ServiceProviderId), "Service provider selection is invalid.");
        }

        if (operationalAssetId.HasValue
            && !await _db.OperationalAssets.AsNoTracking().AnyAsync(a => a.Id == operationalAssetId.Value && a.IsActive))
        {
            modelState.AddModelError(keyPrefix + nameof(InventoryTransportReceiptCreateViewModel.OperationalAssetId), "Operational asset selection is invalid.");
        }
    }

    private async Task SyncReceiptFreightExpenseAsync(InventoryTransportReceipt receipt, InventoryTransportLeg leg)
    {
        // طرفِ کرایه: شرکت خدماتی یا (اگر انتخاب نشد) موتروانِ مستقلِ همین حمل.
        // موترِ خودِ شرکت (دارایی عملیاتی) کرایه نمی‌گیرد — کرایه‌ای به خودش پرداخت نمی‌شود؛
        // سوخت/حق‌سفر/راه به‌صورت مصرف جدا ثبت می‌شود، نه اینجا.
        if (receipt.OperationalAssetId.HasValue)
        {
            return;
        }

        var serviceProviderId = receipt.ServiceProviderId;
        var driverId = serviceProviderId.HasValue ? (int?)null : leg.DriverId;
        if (!serviceProviderId.HasValue && !driverId.HasValue)
        {
            return;
        }

        var amountUsd = GetFreightExpenseAmountUsd(receipt.FreightPayableUsd, receipt.FreightCostUsd);
        if (amountUsd <= 0m)
        {
            return;
        }

        var expenseType = await EnsureReceiptFreightExpenseTypeAsync();
        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = expenseType.Id,
            ContractId = leg.SourcePurchaseContractId,
            ShipmentId = leg.ShipmentId,
            TransportLegId = leg.Id,
            ServiceProviderId = serviceProviderId,
            DriverId = driverId,
            ExpenseDate = receipt.ReceiptDate.Date,
            Amount = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = $"Truck receipt freight for transport leg #{leg.Id}, receipt #{receipt.Id}"
        };

        _db.ExpenseTransactions.Add(expense);
        await _db.SaveChangesAsync();

        // مرحله ۵ — Dual-write داخل همان Transaction قدیمی.
        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseAsync(expense);
        }

        // کرایه بدهیِ ما به حمل‌کننده است ⇒ همیشه Credit روی حساب همان طرف (شرکت خدماتی یا راننده).
        _db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = expense.ExpenseDate,
            Side = LedgerSide.Credit,
            AmountUsd = expense.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = expense.Amount,
            SourceCurrencyCode = expense.Currency,
            AppliedFxRateToUsd = expense.AppliedFxRateToUsd,
            AppliedFxRateDate = expense.ExpenseDate,
            AppliedFxRateSource = "Base currency",
            Description = expense.Description ?? "Truck receipt freight",
            SourceType = "Expense",
            SourceId = expense.Id,
            Reference = $"TRANSPORT-RECEIPT:{receipt.Id}",
            ContractId = expense.ContractId,
            ShipmentId = expense.ShipmentId,
            ServiceProviderId = expense.ServiceProviderId,
            DriverId = expense.DriverId
        });
        await _db.SaveChangesAsync();
    }

    // «بدهیِ جدا»: خسارتِ کسری را به‌عنوان مطالبه (Debit) روی حساب مسئول (شرکت خدماتی یا موتروانِ مستقل) ثبت می‌کند.
    // یک رکورد به‌ازای هر رسید با کلید یکتا TRANSPORT-SHORTAGE:{receiptId} تا ثبت تکراری نشود.
    // موترِ خودِ شرکت (دارایی عملیاتی) مطالبه نمی‌گیرد. کرایه اینجا دست‌نمی‌خورد؛ P&L به‌صورت خالص می‌بیند.
    private async Task SyncShortageDebtAsync(InventoryTransportReceipt receipt, InventoryTransportLeg leg, bool asSeparateDebt)
    {
        if (!asSeparateDebt || receipt.OperationalAssetId.HasValue)
        {
            return;
        }

        var amountUsd = receipt.ShortageChargeUsd ?? 0m;
        if (amountUsd <= 0m)
        {
            return;
        }

        var serviceProviderId = receipt.ServiceProviderId;
        var driverId = serviceProviderId.HasValue ? (int?)null : leg.DriverId;
        if (!serviceProviderId.HasValue && !driverId.HasValue)
        {
            return;
        }

        _db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = receipt.ReceiptDate.Date,
            Side = LedgerSide.Debit,
            AmountUsd = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = amountUsd,
            SourceCurrencyCode = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = receipt.ReceiptDate.Date,
            AppliedFxRateSource = "Base currency",
            Description = $"Shortage charge for transport leg #{leg.Id}, receipt #{receipt.Id}",
            SourceType = "ShortageCharge",
            SourceId = receipt.Id,
            Reference = $"TRANSPORT-SHORTAGE:{receipt.Id}",
            ContractId = leg.SourcePurchaseContractId,
            ShipmentId = leg.ShipmentId,
            ServiceProviderId = serviceProviderId,
            DriverId = driverId
        });
        await _db.SaveChangesAsync();

        if (_shortageAccounting is not null)
        {
            await _shortageAccounting.TryPostShortageChargeAsync(receipt);
        }
    }

    private async Task<ExpenseType> EnsureReceiptFreightExpenseTypeAsync()
    {
        var expenseType = await _db.ExpenseTypes.FirstOrDefaultAsync(e => e.Code == ReceiptFreightExpenseCode);
        if (expenseType is not null)
        {
            if (!expenseType.IsActive)
            {
                expenseType.IsActive = true;
                await _db.SaveChangesAsync();
            }

            return expenseType;
        }

        expenseType = new ExpenseType
        {
            Code = ReceiptFreightExpenseCode,
            Name = "Transport Receipt Freight",
            NamePersian = "کرایه رسید حمل",
            Category = "Transport",
            IsActive = true
        };
        _db.ExpenseTypes.Add(expenseType);
        await _db.SaveChangesAsync();
        return expenseType;
    }

    // نوع مصرف استاندارد «کرایه حمل» را تضمین می‌کند تا در مودال «ثبت مصرف» قابل انتخاب باشد.
    public async Task<ExpenseType> EnsureTransportFreightExpenseTypeAsync()
    {
        var expenseType = await _db.ExpenseTypes.FirstOrDefaultAsync(e => e.Code == TransportFreightExpenseCode);
        if (expenseType is not null)
        {
            if (!expenseType.IsActive)
            {
                expenseType.IsActive = true;
                await _db.SaveChangesAsync();
            }

            return expenseType;
        }

        expenseType = new ExpenseType
        {
            Code = TransportFreightExpenseCode,
            Name = "Transport Freight",
            NamePersian = "کرایه حمل",
            Category = "Transport",
            IsActive = true
        };
        _db.ExpenseTypes.Add(expenseType);
        await _db.SaveChangesAsync();
        return expenseType;
    }

    private static decimal GetFreightExpenseAmountUsd(decimal? payableUsd, decimal? grossUsd)
        => payableUsd.HasValue && payableUsd.Value > 0m
            ? payableUsd.Value
            : grossUsd.GetValueOrDefault() > 0m
                ? grossUsd!.Value
                : 0m;

    private static int? NormalizePositiveInt(int? value)
        => value.HasValue && value.Value > 0 ? value.Value : null;

    private static decimal ComputeChargeableShortage(decimal shortageMt, decimal allowanceMt)
        => FreightShortageMath.ChargeableShortage(shortageMt, allowanceMt);

    private static bool QuantitiesMatch(decimal left, decimal right)
        => decimal.Abs(left - right) <= 0.0001m;
}
