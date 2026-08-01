using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.ContractBalanceTransfers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class ContractBalanceTransfersController : Controller
{
    private const int LookupLimit = 200;
    private readonly ApplicationDbContext _db;
    private readonly IContractBalanceTransferService _transfers;

    public ContractBalanceTransfersController(
        ApplicationDbContext db,
        IContractBalanceTransferService transfers)
    {
        _db = db;
        _transfers = transfers;
    }

    private const int IndexPageSize = 50;

    public async Task<IActionResult> Index(int? contractId = null, string? search = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var query = _db.ContractBalanceTransfers
            .Include(t => t.FromContract)
            .Include(t => t.ToContract)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t =>
                (t.FromContract != null && t.FromContract.ContractNumber.Contains(search))
                || (t.ToContract != null && t.ToContract.ContractNumber.Contains(search))
                || (t.Reference != null && t.Reference.Contains(search)));
        }

        Contract? selectedContract = null;
        if (contractId.HasValue)
        {
            selectedContract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value);
            if (selectedContract is null)
            {
                return NotFound();
            }

            query = query.Where(t => t.FromContractId == contractId.Value || t.ToContractId == contractId.Value);
        }

        // صفحه‌بندی سمت SQL — جایگزین سقف ثابت Take(200) که ردیف‌های بعدی را نامرئی می‌کرد.
        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var items = await query
            .OrderByDescending(t => t.TransferDate)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new ContractBalanceTransferListItemViewModel
            {
                Id = t.Id,
                TransferDate = t.TransferDate,
                FromContractNumber = t.FromContract != null ? t.FromContract.ContractNumber : "",
                ToContractNumber = t.ToContract != null ? t.ToContract.ContractNumber : "",
                AmountOriginal = t.AmountOriginal,
                CurrencyCode = t.CurrencyCode,
                FxRateToUsd = t.FxRateToUsd,
                AmountUsd = t.AmountUsd,
                Reference = t.Reference,
                IsCancelled = t.IsCancelled
            })
            .ToListAsync();

        ViewData["Search"] = search;
        return View(new ContractBalanceTransferIndexViewModel
        {
            ContractId = contractId,
            ContractNumber = selectedContract?.ContractNumber,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var transfer = await _db.ContractBalanceTransfers
            .Include(t => t.FromContract)
            .Include(t => t.ToContract)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (transfer is null)
        {
            return NotFound();
        }

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        var ledgerEntries = await _db.LedgerEntries
            .Include(l => l.Contract)
            .AsNoTracking()
            .Where(l => l.SourceType == ContractBalanceTransferService.LedgerSourceType && l.SourceId == transfer.Id)
            .OrderBy(l => l.Side)
            .ThenBy(l => l.Id)
            .Select(l => new ContractBalanceTransferLedgerEntryViewModel
            {
                Id = l.Id,
                EntryDate = l.EntryDate,
                Side = l.Side == LedgerSide.Debit ? "Debit" : "Credit",
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : "",
                AmountUsd = l.AmountUsd,
                SourceCurrencyCode = l.SourceCurrencyCode,
                SourceAmount = l.SourceAmount
            })
            .ToListAsync();

        return View(new ContractBalanceTransferDetailsViewModel
        {
            Id = transfer.Id,
            TransferDate = transfer.TransferDate,
            FromContractId = transfer.FromContractId,
            FromContractNumber = transfer.FromContract?.ContractNumber ?? "",
            ToContractId = transfer.ToContractId,
            ToContractNumber = transfer.ToContract?.ContractNumber ?? "",
            AmountOriginal = transfer.AmountOriginal,
            CurrencyCode = transfer.CurrencyCode,
            FxRateToUsd = transfer.FxRateToUsd,
            AmountUsd = transfer.AmountUsd,
            FxRateDate = transfer.FxRateDate,
            FxRateSource = transfer.FxRateSource,
            OriginalPaymentTransactionId = transfer.OriginalPaymentTransactionId,
            OriginalPaymentFxRateToUsd = transfer.OriginalPaymentFxRateToUsd,
            Reference = transfer.Reference,
            Notes = transfer.Notes,
            IsCancelled = transfer.IsCancelled,
            LedgerEntries = ledgerEntries
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(int? fromContractId = null, int? toContractId = null, string? returnUrl = null)
    {
        var model = new ContractBalanceTransferCreateViewModel
        {
            TransferDate = AfghanistanBusinessClock.SystemToday,
            FromContractId = fromContractId ?? 0,
            ToContractId = toContractId ?? 0,
            CurrencyCode = SystemCurrency.BaseCurrencyCode,
            FxRateToUsd = 1m,
            ReturnUrl = returnUrl
        };

        await PopulateLookupsAsync(model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ContractBalanceTransferCreateViewModel model)
    {
        NormalizeCreateModel(model);

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model);
            return View(model);
        }

        try
        {
            var transfer = await _transfers.CreateAsync(new ContractBalanceTransferCreateRequest(
                model.TransferDate,
                model.FromContractId,
                model.ToContractId,
                model.AmountOriginal,
                model.CurrencyCode,
                model.FxRateToUsd,
                model.FxRateDate,
                model.FxRateSource,
                model.OriginalPaymentTransactionId,
                model.OriginalPaymentFxRateToUsd,
                model.Reference,
                model.Notes));

            TempData["ok"] = "انتقال مانده قرارداد با موفقیت ثبت شد.";
            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id = transfer.Id });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateLookupsAsync(model);
            return View(model);
        }
    }

    private async Task PopulateLookupsAsync(ContractBalanceTransferCreateViewModel model)
    {
        var selectedContractIds = new[] { model.FromContractId, model.ToContractId }
            .Where(id => id > 0)
            .ToHashSet();

        var contracts = await _db.Contracts
            .AsNoTracking()
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Where(c => c.Status == ContractStatus.Active || selectedContractIds.Contains(c.Id))
            .OrderBy(c => selectedContractIds.Contains(c.Id) ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .ToListAsync();

        var contractOptions = ContractUiText.ToLookupOptions(contracts);
        ViewBag.FromContracts = new SelectList(contractOptions, nameof(ContractLookupOption.Id), nameof(ContractLookupOption.Display), model.FromContractId);
        ViewBag.ToContracts = new SelectList(contractOptions, nameof(ContractLookupOption.Id), nameof(ContractLookupOption.Display), model.ToContractId);

        var currencyCodes = await _db.Currencies
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => c.Code)
            .ToListAsync();
        if (currencyCodes.Count == 0)
        {
            currencyCodes = [SystemCurrency.BaseCurrencyCode, "RUB"];
        }

        ViewBag.Currencies = new SelectList(currencyCodes.Select(c => new { Code = c }), "Code", "Code", model.CurrencyCode);

        var paymentRows = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Contract)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Take(LookupLimit)
            .Select(p => new
            {
                p.Id,
                Label = $"{DateDisplay.Date(p.PaymentDate)} / {p.Reference ?? ("Payment #" + p.Id)} / {p.Amount:N2} {p.Currency}"
                    + (p.Contract != null ? $" / {p.Contract.ContractNumber}" : string.Empty)
            })
            .ToListAsync();

        ViewBag.OriginalPayments = new SelectList(paymentRows, "Id", "Label", model.OriginalPaymentTransactionId);
    }

    private static void NormalizeCreateModel(ContractBalanceTransferCreateViewModel model)
    {
        model.TransferDate = model.TransferDate.Date;
        if (!string.IsNullOrWhiteSpace(model.CurrencyCode))
        {
            model.CurrencyCode = SystemCurrency.Normalize(model.CurrencyCode);
            if (SystemCurrency.IsBaseCurrency(model.CurrencyCode))
            {
                model.FxRateToUsd = 1m;
            }
            else if (model.DocumentCurrencyPerUsdRate.HasValue && model.DocumentCurrencyPerUsdRate.Value > 0m)
            {
                model.FxRateToUsd = decimal.Round(
                    1m / model.DocumentCurrencyPerUsdRate.Value,
                    6,
                    MidpointRounding.AwayFromZero);
            }
        }

        model.FxRateDate = model.FxRateDate?.Date;
        model.FxRateSource = string.IsNullOrWhiteSpace(model.FxRateSource) ? null : model.FxRateSource.Trim();
        model.Reference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private bool TryGetLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url?.IsLocalUrl(returnUrl) == true)
        {
            localReturnUrl = returnUrl;
            return true;
        }

        localReturnUrl = string.Empty;
        return false;
    }
}
