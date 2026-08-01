using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sarrafs;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class SarrafSettlementsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ISarrafSettlementService _settlementService;

    public SarrafSettlementsController(ApplicationDbContext db, ISarrafSettlementService settlementService)
    {
        _db = db;
        _settlementService = settlementService;
    }

    private const int IndexPageSize = 50;

    public async Task<IActionResult> Index(int? sarrafId = null, int? supplierId = null, int? contractId = null, string? search = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var query = _db.SarrafSettlements
            .AsNoTracking()
            .Include(s => s.Sarraf)
            .Include(s => s.Supplier)
            .Include(s => s.Customer)
            .Include(s => s.ServiceProvider)
            .Include(s => s.Contract)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(s =>
                (s.ReferenceNumber != null && s.ReferenceNumber.Contains(search))
                || (s.Sarraf != null && s.Sarraf.Name.Contains(search))
                || (s.Supplier != null && s.Supplier.Name.Contains(search))
                || (s.Contract != null && s.Contract.ContractNumber.Contains(search)));
        }

        if (sarrafId.HasValue)
        {
            query = query.Where(s => s.SarrafId == sarrafId.Value);
        }

        if (supplierId.HasValue)
        {
            query = query.Where(s => s.SupplierId == supplierId.Value);
        }

        if (contractId.HasValue)
        {
            query = query.Where(s => s.ContractId == contractId.Value);
        }

        // صفحه‌بندی سمت SQL — جایگزین سقف ثابت Take(300) که ردیف‌های بعدی را نامرئی می‌کرد.
        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SarrafSettlementListItemViewModel
            {
                Id = s.Id,
                SettlementDate = s.SettlementDate,
                SarrafName = s.Sarraf != null ? s.Sarraf.Name : "",
                Direction = s.Direction,
                CounterpartyType = s.CounterpartyType,
                SupplierId = s.SupplierId,
                SupplierName = s.Supplier != null ? s.Supplier.Name : null,
                CustomerId = s.CustomerId,
                CustomerName = s.Customer != null ? s.Customer.Name : null,
                ServiceProviderId = s.ServiceProviderId,
                ServiceProviderName = s.ServiceProvider != null ? s.ServiceProvider.Name : null,
                ContractId = s.ContractId,
                ContractNumber = s.Contract != null ? s.Contract.ContractNumber : null,
                ReferenceNumber = s.ReferenceNumber,
                RequestedAmount = s.RequestedAmount,
                RequestedCurrency = s.RequestedCurrency,
                RequestedAmountUsd = s.RequestedAmountUsd,
                SarrafChargedAmount = s.SarrafChargedAmount,
                SarrafCurrency = s.SarrafCurrency,
                SarrafChargedAmountUsd = s.SarrafChargedAmountUsd,
                SupplierAcceptedAmount = s.SupplierAcceptedAmount,
                SupplierAcceptedCurrency = s.SupplierAcceptedCurrency,
                SupplierAcceptedAmountUsd = s.SupplierAcceptedAmountUsd,
                DifferenceAmountUsd = s.DifferenceAmountUsd,
                DifferenceType = s.DifferenceType,
                DifferenceTreatment = s.DifferenceTreatment,
                Status = s.Status,
                LedgerEntryId = s.LedgerEntryId,
                ExchangeDifferenceLedgerEntryId = s.ExchangeDifferenceLedgerEntryId
            })
            .ToListAsync();

        ViewData["Search"] = search;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;
        return View(items);
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var settlement = await _db.SarrafSettlements
            .AsNoTracking()
            .Include(s => s.Sarraf)
            .Include(s => s.Supplier)
            .Include(s => s.Customer)
            .Include(s => s.ServiceProvider)
            .Include(s => s.Contract)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (settlement is null)
        {
            return NotFound();
        }

        ViewBag.ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        return View(ToDetails(settlement));
    }

    public IActionResult Create(
        int? sarrafId = null,
        int? supplierId = null,
        int? contractId = null,
        string? returnUrl = null)
    {
        TempData["error"] = "جریان قدیمی تسویه صراف بسته شده است. برای پرداخت تأمین‌کننده از مسیر ساده پرداخت از طریق صراف استفاده کنید.";
        return RedirectToAction("Create", "Payments", new { method = "sarraf", sarrafId, supplierId, contractId, returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(SarrafSettlementCreateViewModel model)
    {
        TempData["error"] = "ثبت جدید با فرم قدیمی صراف مجاز نیست. از پرداخت از طریق صراف استفاده کنید.";
        return RedirectToAction("Create", "Payments", new
        {
            method = "sarraf",
            sarrafId = model.SarrafId > 0 ? model.SarrafId : (int?)null,
            supplierId = model.SupplierId,
            contractId = model.ContractId,
            returnUrl = model.ReturnUrl
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason = null, string? returnUrl = null)
    {
        try
        {
            await _settlementService.CancelAsync(id, reason);
            TempData["ok"] = "تسویه صراف لغو و اثر دفتر کل آن برگشت داده شد.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["error"] = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task PopulateLookupsAsync(SarrafSettlementCreateViewModel model)
    {
        ViewBag.Sarrafs = new SelectList(
            await _db.Sarrafs
                .AsNoTracking()
                .Where(s => s.IsActive || s.Id == model.SarrafId)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            model.SarrafId);

        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .Where(s => s.IsActive || s.Id == model.SupplierId)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            model.SupplierId);

        ViewBag.Contracts = new SelectList(
            await _db.Contracts
                .AsNoTracking()
                .Where(c => c.ContractType == ContractType.Purchase)
                .OrderByDescending(c => c.ContractDate)
                .ThenBy(c => c.ContractNumber)
                .Select(c => new { c.Id, Label = c.ContractNumber })
                .Take(300)
                .ToListAsync(),
            "Id",
            "Label",
            model.ContractId);

        ViewBag.CashAccounts = new SelectList(
            await _db.CashAccounts
                .AsNoTracking()
                .OrderBy(c => c.Code)
                .Select(c => new { c.Id, Label = c.Name + " (" + c.Currency + ")" })
                .ToListAsync(),
            "Id",
            "Label",
            model.CashAccountId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code");

        ViewBag.DifferenceTreatments = Enum.GetValues<SarrafSettlementDifferenceTreatment>()
            .Select(t => new SelectListItem
            {
                Value = ((int)t).ToString(),
                Text = SarrafSettlementLabels.ToPersian(t),
                Selected = model.DifferenceTreatment == t
            })
            .ToList();
    }

    private static SarrafSettlementCommand ToCommand(SarrafSettlementCreateViewModel model)
        => new(
            model.SettlementDate,
            model.SarrafId,
            model.SupplierId,
            model.ContractId,
            model.PaymentTransactionId,
            model.CashAccountId,
            model.ReferenceNumber,
            model.Description,
            model.RequestedAmount,
            model.RequestedCurrency,
            model.RequestedFxRateToUsd,
            model.SarrafCurrency,
            model.SarrafRate,
            model.SarrafChargedAmount,
            model.SarrafFxRateToUsd,
            model.SupplierAcceptedAmount,
            model.SupplierAcceptedCurrency,
            model.SupplierAcceptedFxRateToUsd,
            model.SupplierRate,
            model.DifferenceTreatment);

    private static SarrafSettlementDetailsViewModel ToDetails(SarrafSettlement settlement)
        => new()
        {
            Id = settlement.Id,
            SettlementDate = settlement.SettlementDate,
            SarrafName = settlement.Sarraf?.Name ?? "",
            Direction = settlement.Direction,
            CounterpartyType = settlement.CounterpartyType,
            SupplierId = settlement.SupplierId,
            SupplierName = settlement.Supplier?.Name,
            CustomerId = settlement.CustomerId,
            CustomerName = settlement.Customer?.Name,
            ServiceProviderId = settlement.ServiceProviderId,
            ServiceProviderName = settlement.ServiceProvider?.Name,
            ContractId = settlement.ContractId,
            ContractNumber = settlement.Contract?.ContractNumber,
            ReferenceNumber = settlement.ReferenceNumber,
            Description = settlement.Description,
            RequestedAmount = settlement.RequestedAmount,
            RequestedCurrency = settlement.RequestedCurrency,
            RequestedFxRateToUsd = settlement.RequestedFxRateToUsd,
            RequestedAmountUsd = settlement.RequestedAmountUsd,
            SarrafChargedAmount = settlement.SarrafChargedAmount,
            SarrafCurrency = settlement.SarrafCurrency,
            SarrafRate = settlement.SarrafRate,
            SarrafFxRateToUsd = settlement.SarrafFxRateToUsd,
            SarrafChargedAmountUsd = settlement.SarrafChargedAmountUsd,
            SupplierAcceptedAmount = settlement.SupplierAcceptedAmount,
            SupplierAcceptedCurrency = settlement.SupplierAcceptedCurrency,
            SupplierAcceptedFxRateToUsd = settlement.SupplierAcceptedFxRateToUsd,
            SupplierAcceptedAmountUsd = settlement.SupplierAcceptedAmountUsd,
            SupplierRate = settlement.SupplierRate,
            DifferenceAmountUsd = settlement.DifferenceAmountUsd,
            DifferenceType = settlement.DifferenceType,
            DifferenceTreatment = settlement.DifferenceTreatment,
            Status = settlement.Status,
            PostedAtUtc = settlement.PostedAtUtc,
            CancelledAtUtc = settlement.CancelledAtUtc,
            CancelReason = settlement.CancelReason,
            LedgerEntryId = settlement.LedgerEntryId,
            ExchangeDifferenceLedgerEntryId = settlement.ExchangeDifferenceLedgerEntryId
        };
}
