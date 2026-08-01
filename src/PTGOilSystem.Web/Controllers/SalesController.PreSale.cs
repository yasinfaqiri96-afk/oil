using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Exports;

namespace PTGOilSystem.Web.Controllers;

// پیش‌فروش مرحله‌ای — تعهدِ فروش، جدا از تحویل و جدا از پول.
//
//   ثبت تعهد  → هیچ InventoryMovement، هیچ LedgerEntry، هیچ درآمد/بهای تمام‌شده.
//   هر تحویل  → یک SalesTransaction کاملاً عادی با PreSaleOrderId که از همان primitiveهای
//               فروشِ موجود ساخته می‌شود (مخزن / موتر / واگن / انتقال). یعنی موجودی، Ledger،
//               درآمد و COGS فقط به اندازهٔ همان تحویل و از مسیر همیشگی ثبت می‌شوند.
//   لغو تحویل → همان Cancel فروشِ موجود (برگشت لجر + برگشت موجودی)، بعد وضعیت تعهد بازمحاسبه.
//   پرداخت    → PaymentTransaction موجود؛ تخصیص فقط یک لینک است و هیچ سند مالی جدیدی نمی‌سازد.
//
// مقدار تحویل‌شده هرگز ذخیره نمی‌شود؛ همیشه از مجموع تحویل‌های لغونشده محاسبه می‌شود.
public partial class SalesController
{
    private ICustomerPaymentAllocationService? _customerAllocations;

    // ---------- محاسبهٔ مجموع‌ها و وضعیت ----------

    private async Task<decimal> GetDeliveredMtAsync(int preSaleOrderId)
        => await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.PreSaleOrderId == preSaleOrderId && !s.IsCancelled)
            .SumAsync(s => (decimal?)s.QuantityMt) ?? 0m;

    // وضعیت فقط از روی تحویل‌های معتبر مشتق می‌شود. Closed و Cancelled دستی‌اند و بازنویسی نمی‌شوند.
    private static PreSaleOrderStatus ResolveStatus(PreSaleOrder order, decimal deliveredMt)
    {
        if (order.Status is PreSaleOrderStatus.Cancelled or PreSaleOrderStatus.Closed)
        {
            return order.Status;
        }

        if (deliveredMt <= QtyEpsilon)
        {
            return order.Status == PreSaleOrderStatus.Draft
                ? PreSaleOrderStatus.Draft
                : PreSaleOrderStatus.Confirmed;
        }

        return deliveredMt + QtyEpsilon >= order.QuantityMt
            ? PreSaleOrderStatus.FullyDelivered
            : PreSaleOrderStatus.PartiallyDelivered;
    }

    private async Task RecomputePreSaleStatusAsync(PreSaleOrder order)
    {
        var delivered = await GetDeliveredMtAsync(order.Id);
        order.Status = ResolveStatus(order, delivered);
        await _db.SaveChangesAsync();
    }

    // قفل سطرِ تعهد تا دو تحویل هم‌زمان نتوانند با هم از مانده عبور کنند (همان الگوی LockStorageTankAsync).
    private async Task<PreSaleOrder?> LockPreSaleOrderAsync(int id)
    {
        if (_db.Database.IsRelational()
            && string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"SELECT 1 FROM ""PreSaleOrders"" WHERE ""Id"" = {id} FOR UPDATE");
        }

        return await _db.PreSaleOrders.FirstOrDefaultAsync(o => o.Id == id);
    }

    // ---------- فهرست ----------

    public async Task<IActionResult> PreSales(int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 25);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 25;
        page = page < 1 ? 1 : page;

        var query = _db.PreSaleOrders.AsNoTracking().OrderByDescending(o => o.Id);
        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));

        var activeOrders = _db.PreSaleOrders.AsNoTracking()
            .Where(o => o.Status != PreSaleOrderStatus.Closed
                && o.Status != PreSaleOrderStatus.Cancelled);
        var sumQuantityMt = await activeOrders.SumAsync(o => (decimal?)o.QuantityMt) ?? 0m;
        var sumDeliveredMt = await _db.SalesTransactions.AsNoTracking()
            .Where(s => s.PreSaleOrderId != null
                && !s.IsCancelled
                && s.PreSaleOrder != null
                && s.PreSaleOrder.Status != PreSaleOrderStatus.Closed
                && s.PreSaleOrder.Status != PreSaleOrderStatus.Cancelled)
            .SumAsync(s => (decimal?)s.QuantityMt) ?? 0m;
        var customerCount = await activeOrders.Select(o => o.CustomerId).Distinct().CountAsync();
        var activeOrderCount = await activeOrders.CountAsync();
        var closedOrderCount = await _db.PreSaleOrders.AsNoTracking()
            .CountAsync(o => o.Status == PreSaleOrderStatus.Closed);
        var cancelledOrderCount = await _db.PreSaleOrders.AsNoTracking()
            .CountAsync(o => o.Status == PreSaleOrderStatus.Cancelled);

        var orders = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.OrderNumber,
                CustomerName = o.Customer != null ? o.Customer.Name : "",
                ProductName = o.Product != null ? o.Product.Name : "",
                o.OrderDate,
                o.QuantityMt,
                o.Currency,
                o.TotalInCurrency,
                o.Status,
                DeliveredMt = _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => (decimal?)s.QuantityMt) ?? 0m
            })
            .ToListAsync();

        return View(new PreSaleIndexViewModel
        {
            Items = orders.Select(o => new PreSaleListItemViewModel
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                CustomerName = o.CustomerName,
                ProductName = o.ProductName,
                OrderDate = o.OrderDate,
                QuantityMt = o.QuantityMt,
                DeliveredMt = o.DeliveredMt,
                Currency = o.Currency,
                TotalInCurrency = o.TotalInCurrency,
                Status = o.Status
            }).ToList(),
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount,
            ActiveOrderCount = activeOrderCount,
            ClosedOrderCount = closedOrderCount,
            CancelledOrderCount = cancelledOrderCount,
            SumQuantityMt = sumQuantityMt,
            SumRemainingMt = decimal.Round(Math.Max(sumQuantityMt - sumDeliveredMt, 0m), 4, MidpointRounding.AwayFromZero),
            CustomerCount = customerCount
        });
    }

    /// <summary>
    /// خروجی «تعهدات پیش‌فروش». همان مجموعهٔ صفحهٔ <see cref="PreSales"/> است — همان جدول،
    /// همان ترتیب و همان محاسبهٔ تحویل‌شده — فقط بدون صفحه‌بندی صفحه، چون خروجی کل فهرست است.
    /// هیچ فرمول مالی جدیدی اینجا نوشته نشده است.
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> PreSalesExport(string? format, CancellationToken ct = default)
    {
        var isEn = UiText.IsEn(HttpContext);

        var rows = await _db.PreSaleOrders.AsNoTracking()
            .OrderByDescending(o => o.Id)
            .Select(o => new
            {
                o.Id,
                o.OrderNumber,
                CustomerName = o.Customer != null ? o.Customer.Name : "",
                ProductName = o.Product != null ? o.Product.Name : "",
                o.OrderDate,
                o.ExpectedDeliveryTo,
                o.QuantityMt,
                o.Currency,
                o.TotalInCurrency,
                o.TotalUsd,
                o.Status,
                DeliveredMt = _db.SalesTransactions
                    .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                    .Sum(s => (decimal?)s.QuantityMt) ?? 0m
            })
            .ToListAsync(ct);

        return TabularExportSupport.File(this, format, new TabularExportDocument
        {
            FileNameStem = "PTG_PreSale_Commitments",
            TitleFa = "تعهدات پیش‌فروش",
            TitleEn = "Pre-sale Commitments",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters = TabularExportSupport.FilterSummary(
                ("تاریخ تولید (کابل) / Generated (Kabul)", _businessClock.Today.ToString("yyyy-MM-dd"))),
            Columns =
            [
                new("شمارهٔ سفارش", "Order no.", Width: 18),
                new("مشتری", "Customer", Width: 22),
                new("جنس", "Product", Width: 16),
                new("تاریخ سفارش", "Order date", TabularExportValueType.Date, 13),
                new("مهلت تحویل", "Delivery due", TabularExportValueType.Date, 13),
                new("تعهد MT", "Committed MT", TabularExportValueType.Number, 14),
                new("تحویل‌شده MT", "Delivered MT", TabularExportValueType.Number, 14),
                new("باقیمانده MT", "Remaining MT", TabularExportValueType.Number, 14),
                new("ارز", "Currency", Width: 10),
                new("مبلغ به ارز", "Amount in currency", TabularExportValueType.Number, 16),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16),
                new("وضعیت", "Status", Width: 16)
            ],
            Rows = rows.Select(r => new TabularExportRow(
            [
                TabularExportCell.Text(r.OrderNumber),
                TabularExportCell.Text(r.CustomerName),
                TabularExportCell.Text(r.ProductName),
                TabularExportCell.Date(r.OrderDate),
                TabularExportCell.Date(r.ExpectedDeliveryTo),
                TabularExportCell.Number(r.QuantityMt),
                TabularExportCell.Number(r.DeliveredMt),
                TabularExportCell.Number(decimal.Round(Math.Max(r.QuantityMt - r.DeliveredMt, 0m), 4, MidpointRounding.AwayFromZero)),
                TabularExportCell.Text(r.Currency),
                TabularExportCell.Number(r.TotalInCurrency),
                TabularExportCell.Number(r.TotalUsd),
                TabularExportCell.Text(isEn ? r.Status.ToString() : PreSaleStatusFa(r.Status))
            ])),
            Totals = new TabularExportRow(
            [
                TabularExportCell.Text(isEn ? "Total" : "جمع"),
                TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Date(null), TabularExportCell.Date(null),
                TabularExportCell.Number(rows.Sum(r => r.QuantityMt)),
                TabularExportCell.Number(rows.Sum(r => r.DeliveredMt)),
                TabularExportCell.Number(rows.Sum(r => Math.Max(r.QuantityMt - r.DeliveredMt, 0m))),
                TabularExportCell.Text(null), TabularExportCell.Text(null),
                TabularExportCell.Number(rows.Sum(r => r.TotalUsd)),
                TabularExportCell.Text(null)
            ])
        });
    }

    private static string PreSaleStatusFa(PreSaleOrderStatus status) => status switch
    {
        PreSaleOrderStatus.Draft => "پیش‌نویس",
        PreSaleOrderStatus.Confirmed => "تأییدشده",
        PreSaleOrderStatus.PartiallyDelivered => "تحویل جزئی",
        PreSaleOrderStatus.FullyDelivered => "تحویل کامل",
        PreSaleOrderStatus.Closed => "بسته",
        PreSaleOrderStatus.Cancelled => "لغوشده",
        _ => status.ToString()
    };

    // ---------- ایجاد ----------

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> PreSaleCreate(string? returnUrl = null)
    {
        var model = new PreSaleCreateViewModel
        {
            OrderDate = _businessClock.Today,
            Currency = SystemCurrency.BaseCurrencyCode,
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var local) ? local : null
        };

        await PopulatePreSaleLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreSaleCreate(PreSaleCreateViewModel model)
    {
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        model.ReferenceNumber = string.IsNullOrWhiteSpace(model.ReferenceNumber) ? null : model.ReferenceNumber.Trim();
        model.PaymentTerms = string.IsNullOrWhiteSpace(model.PaymentTerms) ? null : model.PaymentTerms.Trim();

        if (!await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.CustomerId && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
        }

        if (!await _db.Products.AsNoTracking().AnyAsync(p => p.Id == model.ProductId && p.IsActive))
        {
            ModelState.AddModelError(nameof(model.ProductId), "کالای انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        if (model.OrderDate == default)
        {
            ModelState.AddModelError(nameof(model.OrderDate), "تاریخ پیش‌فروش الزامی است.");
        }

        if (model.ExpectedDeliveryFrom.HasValue && model.ExpectedDeliveryTo.HasValue
            && model.ExpectedDeliveryTo.Value.Date < model.ExpectedDeliveryFrom.Value.Date)
        {
            ModelState.AddModelError(nameof(model.ExpectedDeliveryTo), "پایان بازه تحویل نمی‌تواند قبل از شروع آن باشد.");
        }

        CurrencyConversionResult? conversion = null;
        if (ModelState.IsValid)
        {
            try
            {
                conversion = await _currencyConversion.ResolveToBaseAsync(
                    model.Currency, model.OrderDate.Date, model.AppliedFxRateToUsd);
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            }
        }

        if (!ModelState.IsValid || conversion is null)
        {
            await PopulatePreSaleLookupsAsync(model);
            return View(model);
        }

        var totalInCurrency = decimal.Round(model.QuantityMt * model.UnitPriceInCurrency, 4, MidpointRounding.AwayFromZero);
        var order = new PreSaleOrder
        {
            CustomerId = model.CustomerId,
            ProductId = model.ProductId,
            OrderDate = model.OrderDate.Date,
            ExpectedDeliveryFrom = model.ExpectedDeliveryFrom?.Date,
            ExpectedDeliveryTo = model.ExpectedDeliveryTo?.Date,
            QuantityMt = model.QuantityMt,
            Currency = conversion.SourceCurrencyCode,
            UnitPriceInCurrency = model.UnitPriceInCurrency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            UnitPriceUsd = conversion.ConvertToBase(model.UnitPriceInCurrency),
            TotalInCurrency = totalInCurrency,
            TotalUsd = conversion.ConvertToBase(totalInCurrency),
            ReferenceNumber = model.ReferenceNumber,
            PaymentTerms = model.PaymentTerms,
            Notes = model.Notes,
            // قیمت در همین لحظه قفل می‌شود و همهٔ تحویل‌ها با همین قیمت محاسبه می‌شوند.
            Status = PreSaleOrderStatus.Confirmed,
            CreatedByUserName = User?.Identity?.Name
        };

        _db.PreSaleOrders.Add(order);
        await _db.SaveChangesAsync();

        order.OrderNumber = $"PRE-{order.Id}";
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(
            nameof(PreSaleOrder),
            order.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("OrderNumber", order.OrderNumber),
                ("CustomerId", order.CustomerId),
                ("ProductId", order.ProductId),
                ("OrderDate", order.OrderDate),
                ("QuantityMt", order.QuantityMt),
                ("Currency", order.Currency),
                ("UnitPriceInCurrency", order.UnitPriceInCurrency),
                ("TotalInCurrency", order.TotalInCurrency),
                ("TotalUsd", order.TotalUsd),
                ("Status", order.Status)));

        TempData["ok"] = $"پیش‌فروش {order.OrderNumber} ثبت شد. موجودی و درآمد فقط هنگام تحویل ثبت می‌شود.";

        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(PreSaleDetails), new { id = order.Id });
    }

    // ---------- جزئیات ----------

    public async Task<IActionResult> PreSaleDetails(int id)
    {
        var order = await _db.PreSaleOrders
            .AsNoTracking()
            .Include(o => o.Customer)
            .Include(o => o.Product)
            .Include(o => o.Company)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order is null)
        {
            return NotFound();
        }

        var deliveries = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.PreSaleOrderId == id)
            .OrderBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.SaleDate,
                s.QuantityMt,
                s.TotalInCurrency,
                s.TotalUsd,
                s.SaleStage,
                s.IsCancelled
            })
            .ToListAsync();

        var deliveryIds = deliveries.Select(d => d.Id).ToArray();

        var tankSourceBySale = await _db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.SalesTransactionId != null
                && deliveryIds.Contains(m.SalesTransactionId!.Value)
                && m.Direction == MovementDirection.Out)
            .Select(m => new
            {
                SaleId = m.SalesTransactionId!.Value,
                TankName = m.StorageTank != null ? (m.StorageTank.DisplayName ?? m.StorageTank.TankCode) : null,
                TerminalName = m.Terminal != null ? m.Terminal.Name : null
            })
            .ToListAsync();

        var dispatchBySale = await _db.TruckDispatches
            .AsNoTracking()
            .Where(d => d.SalesTransactionId != null && deliveryIds.Contains(d.SalesTransactionId!.Value))
            .Select(d => new { SaleId = d.SalesTransactionId!.Value, Plate = d.Truck != null ? d.Truck.PlateNumber : null })
            .ToListAsync();

        var legBySale = await _db.InventoryTransportReceipts
            .AsNoTracking()
            .Where(r => r.SalesTransactionId != null && deliveryIds.Contains(r.SalesTransactionId!.Value) && !r.IsCancelled)
            .Select(r => new
            {
                SaleId = r.SalesTransactionId!.Value,
                Number = r.InventoryTransportLeg != null
                    ? (r.InventoryTransportLeg.WagonNumber ?? r.InventoryTransportLeg.RwbNo)
                    : null
            })
            .ToListAsync();

        string SourceLabel(int saleId)
        {
            var tank = tankSourceBySale.FirstOrDefault(x => x.SaleId == saleId);
            if (tank is not null)
            {
                return $"مخزن {tank.TankName ?? "-"} ({tank.TerminalName ?? "-"})";
            }

            var dispatch = dispatchBySale.FirstOrDefault(x => x.SaleId == saleId);
            if (dispatch is not null)
            {
                return $"موتر {dispatch.Plate ?? "-"}";
            }

            var leg = legBySale.FirstOrDefault(x => x.SaleId == saleId);
            return leg is not null ? $"حمل {leg.Number ?? "-"}" : "-";
        }

        var payments = await _db.CustomerPaymentAllocations
            .AsNoTracking()
            .Where(a => a.PreSaleOrderId == id && a.Status == CustomerPaymentAllocationStatus.Active)
            .OrderBy(a => a.AllocationDate).ThenBy(a => a.Id)
            .Select(a => new PreSalePaymentViewModel
            {
                AllocationId = a.Id,
                PaymentTransactionId = a.PaymentTransactionId,
                PaymentDate = a.PaymentTransaction!.PaymentDate,
                AllocationDate = a.AllocationDate,
                AllocatedAmount = a.AllocatedPaymentAmount,
                Currency = a.PaymentCurrencyCode,
                AllocatedAmountUsd = a.AllocatedAmountUsd,
                IsAdvance = a.PaymentTransaction!.IsCustomerAdvance == true,
                Reference = a.PaymentTransaction!.Reference
            })
            .ToListAsync();

        // دریافت‌های همین مشتری که هنوز مانده قابل تخصیص دارند (تخصیص جزئی و چندگانه مجاز است).
        var customerReceipts = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.CustomerId == order.CustomerId && p.PaymentKind == PaymentKind.CustomerReceipt)
            .OrderByDescending(p => p.PaymentDate)
            .Take(100)
            .Select(p => new
            {
                p.Id,
                p.PaymentDate,
                p.Amount,
                p.Currency,
                p.Reference,
                IsAdvance = p.IsCustomerAdvance == true,
                Allocated = _db.CustomerPaymentAllocations
                    .Where(a => a.PaymentTransactionId == p.Id
                        && a.Status == CustomerPaymentAllocationStatus.Active)
                    .Sum(a => (decimal?)a.AllocatedPaymentAmount) ?? 0m
            })
            .ToListAsync();

        var allocatablePayments = customerReceipts
            .Select(p => new PreSaleAllocatablePaymentViewModel
            {
                PaymentTransactionId = p.Id,
                PaymentDate = p.PaymentDate,
                Amount = p.Amount,
                AllocatableAmount = decimal.Round(p.Amount - p.Allocated, 4, MidpointRounding.AwayFromZero),
                Currency = p.Currency,
                IsAdvance = p.IsAdvance,
                Reference = p.Reference
            })
            .Where(p => p.AllocatableAmount > 0.0001m)
            .ToList();

        var active = deliveries.Where(d => !d.IsCancelled).ToList();

        // پیش‌دریافتِ واقعاً مصرف‌شده = مجموع Applicationهای فعالِ همهٔ تخصیص‌های این پیش‌فروش.
        var consumedAdvanceUsd = await _db.CustomerPaymentAllocationApplications
            .AsNoTracking()
            .Where(a => a.Status == CustomerPaymentAllocationApplicationStatus.Active
                && _db.CustomerPaymentAllocations.Any(al => al.Id == a.CustomerPaymentAllocationId
                    && al.PreSaleOrderId == id))
            .SumAsync(a => (decimal?)a.AppliedAmountUsd) ?? 0m;

        var model = new PreSaleDetailsViewModel
        {
            Id = order.Id,
            OrderNumber = order.OrderNumber,
            CustomerId = order.CustomerId,
            CustomerName = order.Customer?.Name ?? "",
            ProductName = order.Product?.Name ?? "",
            CompanyName = order.Company?.Name,
            OrderDate = order.OrderDate,
            ExpectedDeliveryFrom = order.ExpectedDeliveryFrom,
            ExpectedDeliveryTo = order.ExpectedDeliveryTo,
            Currency = order.Currency,
            AppliedFxRateToUsd = order.AppliedFxRateToUsd,
            UnitPriceInCurrency = order.UnitPriceInCurrency,
            QuantityMt = order.QuantityMt,
            TotalInCurrency = order.TotalInCurrency,
            TotalUsd = order.TotalUsd,
            ReferenceNumber = order.ReferenceNumber,
            PaymentTerms = order.PaymentTerms,
            Notes = order.Notes,
            Status = order.Status,
            CancelReason = order.CancelReason,
            DeliveredMt = active.Sum(d => d.QuantityMt),
            DeliveredValueInCurrency = active.Sum(d => d.TotalInCurrency),
            DeliveredValueUsd = active.Sum(d => d.TotalUsd),
            ReceivedUsd = payments.Sum(p => p.AllocatedAmountUsd),
            ConsumedAdvanceUsd = decimal.Round(consumedAdvanceUsd, 4, MidpointRounding.AwayFromZero),
            UnallocatedOrderUsd = decimal.Round(
                order.TotalUsd - payments.Sum(p => p.AllocatedAmountUsd), 4, MidpointRounding.AwayFromZero),
            Deliveries = deliveries.Select(d => new PreSaleDeliveryViewModel
            {
                SalesTransactionId = d.Id,
                InvoiceNumber = d.InvoiceNumber,
                DeliveryDate = d.SaleDate,
                QuantityMt = d.QuantityMt,
                TotalInCurrency = d.TotalInCurrency,
                TotalUsd = d.TotalUsd,
                SourceLabel = SourceLabel(d.Id),
                StageLabel = SaleStageLabels.ToPersian(d.SaleStage),
                IsCancelled = d.IsCancelled
            }).ToList(),
            Payments = payments,
            AllocatablePayments = allocatablePayments,
            Sources = (await LoadSellableSourcesAsync())
                .Where(s => s.Kind != GroupSaleSourceKind.TerminalStock || s.ProductId == order.ProductId)
                .ToList()
        };

        return View(model);
    }

    // ---------- ثبت تحویل ----------

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreSaleDeliver(PreSaleDeliveryCreateViewModel model)
    {
        var order = await _db.PreSaleOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == model.PreSaleOrderId);
        if (order is null)
        {
            return NotFound();
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var locked = await LockPreSaleOrderAsync(order.Id)
                ?? throw new BusinessRuleException("PRESALE_NOT_FOUND", "پیش‌فروش یافت نشد.");

            if (locked.Status is PreSaleOrderStatus.Cancelled or PreSaleOrderStatus.Closed)
            {
                throw new BusinessRuleException(
                    "PRESALE_CLOSED", "این پیش‌فروش بسته یا لغو شده است و تحویل جدید نمی‌پذیرد.");
            }

            var deliveredMt = await GetDeliveredMtAsync(locked.Id);
            var remainingMt = decimal.Round(locked.QuantityMt - deliveredMt, 4, MidpointRounding.AwayFromZero);
            if (remainingMt <= QtyEpsilon)
            {
                throw new BusinessRuleException("PRESALE_NO_REMAINING", "مانده‌ای برای تحویل باقی نمانده است.");
            }

            if (model.DeliveryDate == default)
            {
                throw new BusinessRuleException("PRESALE_DELIVERY_DATE_REQUIRED", "تاریخ تحویل الزامی است.");
            }

            // منابعی که مقدارشان دستی و جزئی است: مخزن و حملِ کشتی (TransportLeg). موتر و واگن کل‌وسیله‌اند.
            var requiresExplicitQty = model.Kind is GroupSaleSourceKind.TerminalStock or GroupSaleSourceKind.TransportLeg;

            if (requiresExplicitQty && (model.QuantityMt is null or <= 0m))
            {
                throw new BusinessRuleException("PRESALE_DELIVERY_QTY_REQUIRED", "مقدار تحویل باید بزرگ‌تر از صفر باشد.");
            }

            // سقفِ مانده پیش‌فروش؛ سقفِ باقیماندهٔ واقعی حمل جداگانه در CreateLegLineAsync کنترل می‌شود.
            // مقدار مجاز = کمترین این دو.
            if (requiresExplicitQty && model.QuantityMt!.Value > remainingMt + QtyEpsilon)
            {
                throw new BusinessRuleException(
                    "PRESALE_OVER_DELIVERY",
                    $"مقدار تحویل بیشتر از مانده پیش‌فروش است. مانده: {remainingMt:N4} تن.");
            }

            // قیمت قفل‌شدهٔ تعهد؛ نرخ ارز مطابق همان روال فروشِ سیستم در تاریخ تحویل حل می‌شود.
            var lineModel = new GroupSaleCreateViewModel
            {
                CustomerId = locked.CustomerId,
                SaleDate = model.DeliveryDate.Date,
                Currency = locked.Currency,
                UnitPriceInCurrency = locked.UnitPriceInCurrency,
                Notes = string.IsNullOrWhiteSpace(model.Notes) ? locked.Notes : model.Notes.Trim()
            };

            var conversion = await _currencyConversion.ResolveToBaseAsync(
                locked.Currency, model.DeliveryDate.Date, null);
            lineModel.AppliedFxRateToUsd = conversion.AppliedRateToBase;

            var input = new GroupSaleSelectedInput
            {
                Kind = model.Kind,
                Id = model.Id,
                QuantityMt = model.QuantityMt,
                ProductId = order.ProductId,
                TerminalId = model.TerminalId,
                StorageTankId = model.StorageTankId,
                SourcePurchaseContractId = model.SourcePurchaseContractId
            };

            await EnsureDeliverySourceProductAsync(input, locked.ProductId);

            var deliveryNo = await _db.SalesTransactions.CountAsync(s => s.PreSaleOrderId == locked.Id) + 1;
            var invoice = $"{locked.OrderNumber}-D{deliveryNo}";
            var owner = new SaleLineOwner(null, locked.Id, locked.OrderNumber);
            var receiptService = new InventoryTransportReceiptService(_db, _currencyConversion, _lineage);

            var sale = model.Kind switch
            {
                GroupSaleSourceKind.TerminalStock =>
                    await CreateTerminalStockLineAsync(owner, input, lineModel, conversion, invoice),
                GroupSaleSourceKind.TruckDispatch =>
                    await CreateTruckDispatchLineAsync(owner, input, lineModel, conversion, invoice),
                // فقط حملِ کشتی (TransportLeg) جزئی می‌شود؛ واگن کل‌وسیله می‌ماند (requestedQtyMt = null).
                _ => await CreateLegLineAsync(owner, input, lineModel, conversion, invoice, receiptService,
                    requestedQtyMt: model.Kind == GroupSaleSourceKind.TransportLeg ? model.QuantityMt : null)
            };

            // منابعِ کاملِ وسیله مقدارشان از خودِ وسیله می‌آید، پس بیش‌تحویلی اینجا کنترل می‌شود.
            if (deliveredMt + sale.QuantityMt > locked.QuantityMt + QtyEpsilon)
            {
                throw new BusinessRuleException(
                    "PRESALE_OVER_DELIVERY",
                    $"مقدار این منبع ({sale.QuantityMt:N4} تن) از مانده پیش‌فروش ({remainingMt:N4} تن) بیشتر است.");
            }

            locked.Status = ResolveStatus(locked, deliveredMt + sale.QuantityMt);
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(PreSaleOrder),
                locked.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForCreate(
                    ("DeliverySalesTransactionId", sale.Id),
                    ("DeliveryInvoiceNumber", sale.InvoiceNumber),
                    ("DeliveryQuantityMt", sale.QuantityMt),
                    ("DeliveryDate", sale.SaleDate),
                    ("Status", locked.Status)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = $"تحویل {sale.QuantityMt:N4} تن با فاکتور {sale.InvoiceNumber} ثبت شد.";
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            TempData["err"] = ex.Message;
        }
        catch (Exception ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to create pre-sale delivery for order {OrderId}.", model.PreSaleOrderId);
            TempData["err"] = "ثبت تحویل انجام نشد. لطفاً منبع و مقدار را بررسی و دوباره تلاش کنید.";
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        return RedirectToAction(nameof(PreSaleDetails), new { id = model.PreSaleOrderId });
    }

    // منبع باید دقیقاً همان کالای تعهد را داشته باشد.
    private async Task EnsureDeliverySourceProductAsync(GroupSaleSelectedInput input, int productId)
    {
        var sourceProductId = input.Kind switch
        {
            GroupSaleSourceKind.TerminalStock => input.ProductId,
            GroupSaleSourceKind.TruckDispatch => await _db.TruckDispatches.AsNoTracking()
                .Where(d => d.Id == input.Id).Select(d => (int?)d.ProductId).FirstOrDefaultAsync(),
            _ => await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => l.Id == input.Id).Select(l => (int?)l.ProductId).FirstOrDefaultAsync()
        };

        if (sourceProductId is null)
        {
            throw new BusinessRuleException("PRESALE_SOURCE_NOT_FOUND", "منبع انتخاب‌شده یافت نشد.");
        }

        if (sourceProductId.Value != productId)
        {
            throw new BusinessRuleException(
                "PRESALE_SOURCE_PRODUCT_MISMATCH", "کالای منبع انتخاب‌شده با کالای پیش‌فروش یکی نیست.");
        }
    }

    // ---------- لغو تحویل ----------

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreSaleDeliveryCancel(int id, int salesTransactionId)
    {
        var order = await _db.PreSaleOrders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null)
        {
            return NotFound();
        }

        var delivery = await _db.SalesTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == salesTransactionId && s.PreSaleOrderId == id);
        if (delivery is null)
        {
            return NotFound();
        }

        // برگشت موجودی و لجر دقیقاً از همان مسیر لغو فروشِ موجود انجام می‌شود؛ هیچ سند مالی حذف نمی‌شود.
        await Cancel(salesTransactionId);
        await RecomputePreSaleStatusAsync(order);

        return RedirectToAction(nameof(PreSaleDetails), new { id });
    }

    // ---------- لغو / بستن تعهد ----------

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreSaleCancel(int id, string? reason = null)
    {
        var order = await _db.PreSaleOrders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null)
        {
            return NotFound();
        }

        if (await _db.SalesTransactions.AnyAsync(s => s.PreSaleOrderId == id && !s.IsCancelled))
        {
            TempData["err"] = "این پیش‌فروش تحویل معتبر دارد؛ ابتدا تحویل‌ها را لغو کنید.";
            return RedirectToAction(nameof(PreSaleDetails), new { id });
        }

        order.Status = PreSaleOrderStatus.Cancelled;
        order.CancelledAtUtc = DateTime.UtcNow;
        order.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(
            nameof(PreSaleOrder), order.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForCreate(("Status", order.Status), ("CancelReason", order.CancelReason)));

        TempData["ok"] = "پیش‌فروش لغو شد.";
        return RedirectToAction(nameof(PreSaleDetails), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreSaleClose(int id)
    {
        var order = await _db.PreSaleOrders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null)
        {
            return NotFound();
        }

        var delivered = await GetDeliveredMtAsync(id);
        if (delivered + QtyEpsilon < order.QuantityMt)
        {
            TempData["err"] = "تا کامل‌نشدن تحویل، بستن پیش‌فروش ممکن نیست.";
            return RedirectToAction(nameof(PreSaleDetails), new { id });
        }

        order.Status = PreSaleOrderStatus.Closed;
        await _db.SaveChangesAsync();

        TempData["ok"] = "پیش‌فروش بسته شد.";
        return RedirectToAction(nameof(PreSaleDetails), new { id });
    }

    // ---------- تخصیص دریافت مشتری ----------

    // تخصیص جزئی/چندگانه از طریق سرویس انجام می‌شود؛ Controller فقط هماهنگ‌کننده است.
    private ICustomerPaymentAllocationService CustomerAllocations
        => _customerAllocations ??= new CustomerPaymentAllocationService(_db);

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreSaleAllocatePayment(
        int id,
        int paymentTransactionId,
        decimal amount,
        DateTime? allocationDate = null)
    {
        if (!await _db.PreSaleOrders.AsNoTracking().AnyAsync(o => o.Id == id))
        {
            return NotFound();
        }

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            var allocation = await CustomerAllocations.AllocateAsync(new CustomerPaymentAllocationCreateRequest(
                paymentTransactionId,
                id,
                allocationDate ?? _businessClock.Today,
                amount,
                CreatedByUserName: User?.Identity?.Name));

            // تخصیصِ «بعد از تحویل»: اگر تحویلِ قبلیِ دارای طلبِ باز باشد، همین‌جا از قدیمی‌ترین تحویل
            // تسویه می‌شود (Application واقعی + ژورنالِ انتقالِ متوازن). منطق در Adapter است، نه اینجا.
            var settled = _salesAccounting is null
                ? 0
                : await _salesAccounting.TrySettleDeliveredReceivableAsync(allocation.Id);

            await _audit.LogAndSaveAsync(
                nameof(CustomerPaymentAllocation), allocation.Id, AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("PaymentTransactionId", allocation.PaymentTransactionId),
                    ("PreSaleOrderId", allocation.PreSaleOrderId),
                    ("AllocationDate", allocation.AllocationDate),
                    ("AllocatedPaymentAmount", allocation.AllocatedPaymentAmount),
                    ("PaymentCurrencyCode", allocation.PaymentCurrencyCode),
                    ("PaymentFxRateToUsd", allocation.PaymentFxRateToUsd),
                    ("AllocatedAmountUsd", allocation.AllocatedAmountUsd),
                    ("SettledDeliveries", settled)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = settled > 0
                ? $"{allocation.AllocatedPaymentAmount:N2} {allocation.PaymentCurrencyCode} تخصیص یافت و طلبِ {settled} تحویلِ قبلی تسویه شد."
                : $"{allocation.AllocatedPaymentAmount:N2} {allocation.PaymentCurrencyCode} به این پیش‌فروش تخصیص یافت.";
        }
        catch (BusinessRuleException ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            TempData["err"] = ex.Message;
        }
        catch (Exception ex)
        {
            if (transaction is not null) await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to allocate customer payment to pre-sale {OrderId}.", id);
            TempData["err"] = "تخصیص دریافت انجام نشد؛ هیچ تغییری ثبت نشد.";
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        return RedirectToAction(nameof(PreSaleDetails), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreSaleReverseAllocation(int id, int allocationId, string? reason = null)
    {
        try
        {
            var allocation = await CustomerAllocations.ReverseAsync(new CustomerPaymentAllocationReverseRequest(
                allocationId,
                reason,
                User?.Identity?.Name));

            await _audit.LogAndSaveAsync(
                nameof(CustomerPaymentAllocation), allocation.Id, AuditAction.Update,
                diff: AuditDiffFormatter.ForCreate(
                    ("Status", allocation.Status),
                    ("ReversalReason", allocation.ReversalReason)));

            TempData["ok"] = "تخصیص برگشت خورد و مانده دریافت آزاد شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["err"] = ex.Message;
        }

        return RedirectToAction(nameof(PreSaleDetails), new { id });
    }

    private async Task PopulatePreSaleLookupsAsync(PreSaleCreateViewModel model)
    {
        ViewBag.Customers = new SelectList(
            await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).Take(LookupLimit).ToListAsync(),
            "Id", "Name", model.CustomerId > 0 ? model.CustomerId : null);

        ViewBag.Products = new SelectList(
            await _db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name }).Take(LookupLimit).ToListAsync(),
            "Id", "Name", model.ProductId > 0 ? model.ProductId : null);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code)
                .Select(c => new { c.Code }).ToListAsync(),
            "Code", "Code", model.Currency);
    }
}
