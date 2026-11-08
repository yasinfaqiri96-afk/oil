using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Imports;

namespace PTGOilSystem.Web.Controllers;

public partial class SalesController
{
    private SalesExcelImportWorkflow ExcelSales => new(_db, CargoSources, _formTokens);
    private IDataProtector ImportProtector => HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("Sales.ExcelImport.Manifest.v1");
    private string ImportUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new BusinessRuleException("IMPORT_USER_REQUIRED", "شناسهٔ کاربر معتبر نیست؛ دوباره وارد شوید.");
    private string ProtectImport(SalesExcelPlan plan) => ImportProtector.Protect(JsonSerializer.Serialize(plan));
    private SalesExcelPlan ReadImport(string manifest)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<SalesExcelPlan>(ImportProtector.Unprotect(manifest));
            if (plan is null || plan.UserId != ImportUserId || plan.ExpiresUtc <= DateTime.UtcNow)
                throw new CryptographicException();
            return plan;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        { throw new BusinessRuleException("IMPORT_MANIFEST_INVALID", "پیش‌نمایش نامعتبر یا منقضی شده است؛ فایل را دوباره بارگذاری کنید."); }
    }

    [HttpGet, Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult ImportSales() => View("ImportSales", new SalesExcelImportViewModel());

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = AuthPolicies.ManageData)]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> PreviewSalesExcel(IFormFile? file)
    {
        try
        {
            if (file is null || file.Length is <= 0 or > SalesExcelWorkbookParser.MaxFileBytes
                || !Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("یک فایل xlsx آماده‌سازی فروشات، حداکثر ۵ مگابایت، انتخاب کنید.");
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, HttpContext.RequestAborted); stream.Position = 0;
            var rows = SalesExcelWorkbookParser.Parse(stream);
            var plan = new SalesExcelPlan { UserId = ImportUserId, ExpiresUtc = DateTime.UtcNow.AddMinutes(30), Rows = rows,
                Mappings = rows.Select(r => new SalesExcelMapping { ImportKey = r.ImportKey }).ToList() };
            return await ShowImportAsync(plan);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sales workbook preview rejected.");
            ModelState.AddModelError("", ex is InvalidDataException or BusinessRuleException ? ex.Message : "فایل اکسل معتبر نیست؛ قالب آماده‌سازی را بررسی کنید.");
            return View("ImportSales", new SalesExcelImportViewModel());
        }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = AuthPolicies.ManageData)]
    [RequestFormLimits(ValueCountLimit = 10000)]
    public async Task<IActionResult> ValidateSalesExcel(string manifest, List<SalesExcelMapping> mappings)
    {
        try
        {
            var plan = ReadImport(manifest) with { Ready = false, Mappings = mappings };
            if (!ModelState.IsValid) { ModelState.AddModelError("", "مقادیر تطبیق معتبر نیستند."); return await ShowImportAsync(plan); }
            var sources = await LoadImportSourcesAsync();
            var errors = await ExcelSales.ValidateAsync(plan, sources, HttpContext.RequestAborted);
            return await ShowImportAsync(plan with { Ready = errors.Count == 0 }, errors, sources);
        }
        catch (BusinessRuleException ex) { ModelState.AddModelError("", ex.Message); return View("ImportSales", new SalesExcelImportViewModel()); }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> CommitSalesExcel(string manifest, bool confirmed = false)
    {
        SalesExcelPlan? plan = null;
        try
        {
            plan = ReadImport(manifest);
            if (!confirmed || !plan.Ready) throw new BusinessRuleException("IMPORT_CONFIRM_REQUIRED", "ثبت نهایی فقط پس از اعتبارسنجی و تأیید صریح پیش‌نمایش مجاز است.");
            var ids = await ExcelSales.CommitAsync(plan, LoadImportSourcesAsync, CreateImportedSaleAsync, HttpContext.RequestAborted);
            TempData["ok"] = $"{ids.Count} سند مستقل فروش از اکسل ثبت شد. وزن: {plan.Rows.Sum(r => r.QuantityMt):N4} تن؛ مبلغ: {plan.Rows.Sum(r => r.SourceAmountUsd):N2} دالر.";
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessRuleException ex) { ModelState.AddModelError("", ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sales Excel import rolled back in full.");
            ModelState.AddModelError("", "ثبت انجام نشد؛ تمام این درخواست برگشت داده شد. موجودی/اسناد مالی یا تغییر هم‌زمان را بررسی و دوباره اعتبارسنجی کنید.");
        }
        return plan is null ? View("ImportSales", new SalesExcelImportViewModel()) : await ShowImportAsync(plan with { Ready = false });
    }

    private async Task<Dictionary<string, GroupSaleSourceItem>> LoadImportSourcesAsync()
    {
        var result = (await LoadSellableSourcesAsync()).ToDictionary(s => s.Key);
        // Include historical pools even when today's balance is zero. Availability
        // is always re-evaluated at EACH row's date, never inferred from this list.
        var pools = await _db.InventoryMovements.AsNoTracking().Where(m => m.StorageTankId.HasValue)
            .Select(m => new { m.ProductId, m.TerminalId, m.StorageTankId,
                ContractId = m.ContractId ?? (m.LoadingReceipt != null ? (int?)m.LoadingReceipt.LoadingRegister!.ContractId : null) })
            .Where(p => p.ContractId.HasValue).Distinct().ToListAsync();
        var contractIds = pools.Select(p => p.ContractId!.Value).Distinct().ToArray();
        var contracts = await _db.Contracts.AsNoTracking().Include(c => c.Company).Include(c => c.Product)
            .Where(c => contractIds.Contains(c.Id) && c.ContractType == ContractType.Purchase).ToDictionaryAsync(c => c.Id);
        var tanks = await _db.StorageTanks.AsNoTracking().ToDictionaryAsync(t => t.Id);
        foreach (var p in pools)
        {
            if (!contracts.TryGetValue(p.ContractId!.Value, out var c) || !tanks.TryGetValue(p.StorageTankId!.Value, out var t)) continue;
            var key = $"Stock:{p.ProductId}-{p.TerminalId}-{p.StorageTankId}-{p.ContractId}";
            result.TryAdd(key, new GroupSaleSourceItem { Key = key, Kind = GroupSaleSourceKind.TerminalStock,
                KindLabel = "موجودی مخزن (تاریخی)", ProductId = p.ProductId, TerminalId = p.TerminalId, StorageTankId = t.Id,
                SourcePurchaseContractId = c.Id, CompanyId = c.CompanyId, ContractNumber = c.ContractNumber,
                CompanyName = c.Company?.Name ?? "", ProductName = c.Product?.Name ?? "", Number = t.DisplayName ?? t.TankCode });
        }
        return result;
    }

    private async Task<IActionResult> ShowImportAsync(SalesExcelPlan plan,
        Dictionary<string, List<string>>? errors = null, Dictionary<string, GroupSaleSourceItem>? sources = null)
    {
        sources ??= await LoadImportSourcesAsync();
        await PopulateBuyerLookupAsync(null);
        var model = new SalesExcelImportViewModel { Manifest = ProtectImport(plan with { Ready = false }),
            ReadyManifest = plan.Ready ? ProtectImport(plan) : null, Rows = plan.Rows, Mappings = plan.Mappings,
            Errors = errors ?? [], Buyers = (List<SelectListItem>)ViewBag.Buyers,
            Sources = sources.Values.OrderBy(s => s.ContractNumber).ThenBy(s => s.Key).Select(s => new SelectListItem(
                $"{s.KindLabel} | {s.Number} | قرارداد {s.ContractNumber} | {s.CompanyName} | {s.ProductName} | {s.Route}", s.Key)).ToList(),
            Terminals = await _db.Terminals.AsNoTracking().OrderBy(t => t.Name).Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToListAsync() };
        return View("ImportSales", model);
    }

    private async Task<SalesTransaction> CreateImportedSaleAsync(SalesExcelRow row, SalesExcelMapping mapping, GroupSaleSourceItem source)
    {
        var buyer = SaleBuyerKey.Parse(mapping.BuyerKey);
        var model = new GroupSaleCreateViewModel { CustomerId = buyer.CustomerId, SupplierId = buyer.SupplierId,
            SaleDate = row.SaleDate, Currency = "USD", UnitPriceInCurrency = row.UnitPriceUsd,
            Notes = SalesExcelWorkbookParser.BuildNotes(row, mapping.ReviewNote), LoadingSaleTerminalId = mapping.SaleTerminalId };
        var conversion = await _currencyConversion.ResolveToBaseAsync("USD", row.SaleDate, null);
        var input = new GroupSaleSelectedInput { Kind = source.Kind, Id = source.Id, QuantityMt = row.QuantityMt,
            ProductId = source.ProductId, TerminalId = source.TerminalId, StorageTankId = source.StorageTankId,
            SourcePurchaseContractId = source.SourcePurchaseContractId, CompanyId = source.CompanyId };
        var invoice = "XI-" + SalesExcelWorkbookParser.Token(row.ImportKey)[..32];
        var owner = new SaleLineOwner(null, null, "Excel:" + row.ImportKey);
        var sale = source.Kind switch
        {
            GroupSaleSourceKind.TerminalStock => await CreateTerminalStockLineAsync(owner, input, model, conversion, invoice),
            GroupSaleSourceKind.TruckDispatch => await CreateTruckDispatchLineAsync(owner, input, model, conversion, invoice),
            GroupSaleSourceKind.WagonLeg or GroupSaleSourceKind.TransportLeg => await CreateLegLineAsync(owner, input, model, conversion, invoice, _receiptService),
            GroupSaleSourceKind.LoadingRegister => await CreateLoadingLineAsync(owner, input, model, conversion, invoice,
                (await CargoSources.LoadLoadingSourcesAsync([source.Id])).SingleOrDefault(),
                await _db.LoadingRegisters.Include(l => l.Contract).SingleOrDefaultAsync(l => l.Id == source.Id)),
            _ => throw new BusinessRuleException("IMPORT_SOURCE_INVALID", "منبع فروش معتبر نیست.")
        };
        sale.TicketSerialNumber = row.TicketSerial;
        // Existing adapters are idempotent. A skipped required journal must abort
        // this import; intentional disabled feature flags retain normal behaviour.
        if (_salesAccounting is not null)
            foreach (var result in new[] { await _salesAccounting.TryPostSaleAsync(sale), await _salesAccounting.TryPostCogsAsync(sale) })
                if (result.Status == PaymentPostingStatus.Skipped && result.Reason is not ("ACCOUNTING_DISABLED" or "PILOT_DISABLED"))
                    throw new BusinessRuleException("IMPORT_ACCOUNTING_INCOMPLETE", $"سند مالی ردیف {row.ImportKey} کامل نشد: {result.Reason}");
        await _audit.LogAndSaveAsync(nameof(SalesTransaction), sale.Id, AuditAction.Insert,
            diff: $"Excel ImportKey={row.ImportKey}; source={source.Key}; buyer={mapping.BuyerKey}; review={mapping.ReviewNote}");
        return sale;
    }
}
