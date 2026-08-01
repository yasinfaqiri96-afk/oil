using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.ServiceProviders;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class ServiceProvidersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPartyStatementReadService? _partyStatements;

    public ServiceProvidersController(ApplicationDbContext db, IPartyStatementReadService? partyStatements = null)
    {
        _db = db;
        _partyStatements = partyStatements;
    }

    public async Task<IActionResult> Index(string? q = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        await PopulateLookupsAsync();
        return View(await BuildIndexModelAsync(q, page, perPage));
    }

    private async Task<ServiceProviderIndexViewModel> BuildIndexModelAsync(string? q = null, int page = 1, int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.ServiceProviders.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            query = query.Where(p =>
                (p.Code != null && p.Code.Contains(search))
                || p.Name.Contains(search)
                || (p.Phone != null && p.Phone.Contains(search))
                || (p.Email != null && p.Email.Contains(search))
                || (p.City != null && p.City.Contains(search)));
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var providers = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var providerIds = providers.Select(p => p.Id).ToArray();

        var activeExpenseTotals = providerIds.Length == 0
            ? new Dictionary<int, decimal>()
            : await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.ServiceProviderId.HasValue
                    && providerIds.Contains(e.ServiceProviderId.Value)
                    && !e.IsCancelled)
                .GroupBy(e => e.ServiceProviderId!.Value)
                .Select(g => new { ServiceProviderId = g.Key, Total = g.Sum(e => e.AmountUsd) })
                .ToDictionaryAsync(g => g.ServiceProviderId, g => g.Total);

        var paymentTotals = providerIds.Length == 0
            ? new Dictionary<int, decimal>()
            : await _db.PaymentTransactions
                .AsNoTracking()
                .Where(p => p.ServiceProviderId.HasValue && providerIds.Contains(p.ServiceProviderId.Value))
                .GroupBy(p => p.ServiceProviderId!.Value)
                .Select(g => new { ServiceProviderId = g.Key, Total = g.Sum(p => p.AmountUsd) })
                .ToDictionaryAsync(g => g.ServiceProviderId, g => g.Total);

        var ledgerTotals = providerIds.Length == 0
            ? new Dictionary<int, (decimal Debit, decimal Credit)>()
            : await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => l.ServiceProviderId.HasValue && providerIds.Contains(l.ServiceProviderId.Value))
                .GroupBy(l => l.ServiceProviderId!.Value)
                .Select(g => new
                {
                    ServiceProviderId = g.Key,
                    Debit = g.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
                    Credit = g.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd)
                })
                .ToDictionaryAsync(g => g.ServiceProviderId, g => (g.Debit, g.Credit));

        var items = providers.Select(p =>
        {
            ledgerTotals.TryGetValue(p.Id, out var ledger);
            return new ServiceProviderIndexItemViewModel
            {
                Id = p.Id,
                Code = p.Code,
                Name = p.Name,
                ProviderType = p.ProviderType,
                ContactText = FirstNonEmpty(p.Phone, p.Email, p.City, p.Country) ?? "-",
                TotalExpensesUsd = activeExpenseTotals.GetValueOrDefault(p.Id),
                TotalPaymentsUsd = paymentTotals.GetValueOrDefault(p.Id),
                LedgerBalanceUsd = ledger.Credit - ledger.Debit,
                IsActive = p.IsActive
            };
        }).ToList();

        return new ServiceProviderIndexViewModel
        {
            Query = q,
            Items = items,
            CurrentPage = page,
            PageCount = pageCount,
            TotalCount = totalCount
        };
    }

    public async Task<IActionResult> Details(int id)
    {
        var model = await BuildProfileAsync(id);
        if (model is null) return NotFound();
        if (_partyStatements is not null)
        {
            var statement = await _partyStatements.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.ServiceProvider, id),
                new PartyStatementFilter { IncludeOperationalColumns = false },
                HttpContext.RequestAborted);
            ViewData["PartyStatementSummary"] = statement.Summary;
            ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        }
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        await PopulateLookupsAsync();
        return View(new ServiceProviderCreateViewModel { IsActive = true });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ServiceProviderCreateViewModel model, string? returnUrl = null)
    {
        Normalize(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model.ProviderType);
            return View(model);
        }

        var provider = new ServiceProviderEntity
        {
            Code = model.Code,
            Name = model.Name,
            ProviderType = model.ProviderType,
            Country = model.Country,
            City = model.City,
            Phone = model.Phone,
            Email = model.Email,
            Address = model.Address,
            TaxNumber = model.TaxNumber,
            Notes = model.Notes,
            IsActive = model.IsActive
        };

        _db.ServiceProviders.Add(provider);
        await _db.SaveChangesAsync();
        TempData["ok"] = UiText.T(HttpContext, "شرکت خدماتی ذخیره شد.", "Service provider saved.");
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Details), new { id = provider.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var provider = await _db.ServiceProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);
        if (provider is null)
        {
            return NotFound();
        }

        await PopulateLookupsAsync(provider.ProviderType);
        return View(new ServiceProviderCreateViewModel
        {
            Id = provider.Id,
            Code = provider.Code,
            Name = provider.Name,
            ProviderType = provider.ProviderType,
            Country = provider.Country,
            City = provider.City,
            Phone = provider.Phone,
            Email = provider.Email,
            Address = provider.Address,
            TaxNumber = provider.TaxNumber,
            Notes = provider.Notes,
            IsActive = provider.IsActive
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.ServiceProviders.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ServiceProviderCreateViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        Normalize(model);
        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(model.ProviderType);
            return View(model);
        }

        var provider = await _db.ServiceProviders.FirstOrDefaultAsync(p => p.Id == id);
        if (provider is null)
        {
            return NotFound();
        }

        provider.Code = model.Code;
        provider.Name = model.Name;
        provider.ProviderType = model.ProviderType;
        provider.Country = model.Country;
        provider.City = model.City;
        provider.Phone = model.Phone;
        provider.Email = model.Email;
        provider.Address = model.Address;
        provider.TaxNumber = model.TaxNumber;
        provider.Notes = model.Notes;
        provider.IsActive = model.IsActive;

        await _db.SaveChangesAsync();
        TempData["ok"] = UiText.T(HttpContext, "شرکت خدماتی به‌روزرسانی شد.", "Service provider updated.");
        return RedirectToAction(nameof(Details), new { id = provider.Id });
    }

    private async Task<ServiceProviderProfileViewModel?> BuildProfileAsync(int id)
    {
        var provider = await _db.ServiceProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);
        if (provider is null)
        {
            return null;
        }

        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Include(e => e.ExpenseType)
            .Include(e => e.Contract)
            .Include(e => e.Shipment)
            .Include(e => e.TransportLeg)
            .Include(e => e.TruckDispatch)
                .ThenInclude(d => d!.Truck)
            .Include(e => e.OperationalAsset)
            .Where(e => e.ServiceProviderId == id)
            .OrderByDescending(e => e.ExpenseDate)
            .ThenByDescending(e => e.Id)
            .ToListAsync();

        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.CashAccount)
            .Include(p => p.Contract)
            .Where(p => p.ServiceProviderId == id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .ToListAsync();

        var ledgerEntries = (await _db.LedgerEntries
                .AsNoTracking()
                .Include(l => l.Contract)
                .Where(l => l.ServiceProviderId == id)
                .OrderBy(l => l.EntryDate)
                .ThenBy(l => l.Id)
                .ToListAsync())
            .DistinctBy(l => l.Id)
            .ToList();

        var balance = 0m;
        var statementRows = new List<ServiceProviderStatementRowViewModel>();
        foreach (var entry in ledgerEntries)
        {
            balance += entry.Side == LedgerSide.Credit ? entry.AmountUsd : -entry.AmountUsd;
            var sourceAmount = entry.SourceAmount ?? entry.AmountUsd;
            statementRows.Add(new ServiceProviderStatementRowViewModel
            {
                LedgerEntryId = entry.Id,
                EntryDate = entry.EntryDate,
                SourceType = entry.SourceType,
                SourceId = entry.SourceId,
                Reference = entry.Reference,
                ContractNumber = entry.Contract?.ContractNumber,
                Description = entry.Description,
                DebitUsd = entry.Side == LedgerSide.Debit ? entry.AmountUsd : null,
                CreditUsd = entry.Side == LedgerSide.Credit ? entry.AmountUsd : null,
                RunningBalanceUsd = balance,
                Currency = entry.SourceCurrencyCode ?? entry.Currency,
                Debit = entry.Side == LedgerSide.Debit ? sourceAmount : null,
                Credit = entry.Side == LedgerSide.Credit ? sourceAmount : null,
                FxRateUsed = entry.AppliedFxRateToUsd
            });
        }

        var activeExpenses = expenses.Where(e => !e.IsCancelled).ToList();

        return new ServiceProviderProfileViewModel
        {
            Id = provider.Id,
            Code = provider.Code,
            Name = provider.Name,
            ProviderType = provider.ProviderType,
            Country = provider.Country,
            City = provider.City,
            Phone = provider.Phone,
            Email = provider.Email,
            Address = provider.Address,
            TaxNumber = provider.TaxNumber,
            Notes = provider.Notes,
            IsActive = provider.IsActive,
            TotalExpensesUsd = activeExpenses.Sum(e => e.AmountUsd),
            TotalPaymentsUsd = payments.Sum(p => p.AmountUsd),
            LedgerDebitUsd = ledgerEntries.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
            LedgerCreditUsd = ledgerEntries.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd),
            Expenses = activeExpenses.Select(ToExpenseRow).ToList(),
            Payments = payments.Select(ToPaymentRow).ToList(),
            StatementRows = statementRows,
            RelatedContracts = BuildRelatedContracts(activeExpenses, payments)
        };
    }

    private static ServiceProviderExpenseRowViewModel ToExpenseRow(ExpenseTransaction expense)
        => new()
        {
            Id = expense.Id,
            ExpenseDate = expense.ExpenseDate,
            ExpenseTypeName = expense.ExpenseType?.NamePersian ?? expense.ExpenseType?.Name ?? "-",
            ContractNumber = expense.Contract?.ContractNumber,
            ShipmentCode = expense.Shipment?.ShipmentCode,
            TransportLegLabel = BuildTransportLegLabel(expense.TransportLeg),
            TruckDispatchLabel = BuildTruckDispatchLabel(expense.TruckDispatch),
            OperationalAssetName = expense.OperationalAsset?.Name,
            AmountUsd = expense.AmountUsd,
            Description = expense.Description
        };

    private static ServiceProviderPaymentRowViewModel ToPaymentRow(PaymentTransaction payment)
        => new()
        {
            Id = payment.Id,
            PaymentDate = payment.PaymentDate,
            PaymentKindName = PaymentKindLabels.ToPersian(payment.PaymentKind),
            CashAccountName = payment.CashAccount is null
                ? "-"
                : $"{payment.CashAccount.Code} - {payment.CashAccount.Name}",
            ContractNumber = payment.Contract?.ContractNumber,
            AmountUsd = payment.AmountUsd,
            Reference = payment.Reference
        };

    private static IReadOnlyList<ServiceProviderRelatedContractViewModel> BuildRelatedContracts(
        IReadOnlyList<ExpenseTransaction> expenses,
        IReadOnlyList<PaymentTransaction> payments)
    {
        var contractIds = expenses
            .Select(e => e.ContractId)
            .Concat(payments.Select(p => p.ContractId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        return contractIds
            .Select(id => new ServiceProviderRelatedContractViewModel
            {
                Id = id,
                ContractNumber = expenses.FirstOrDefault(e => e.ContractId == id)?.Contract?.ContractNumber
                    ?? payments.FirstOrDefault(p => p.ContractId == id)?.Contract?.ContractNumber
                    ?? $"#{id}",
                ExpenseUsd = expenses.Where(e => e.ContractId == id).Sum(e => e.AmountUsd),
                PaymentUsd = payments.Where(p => p.ContractId == id).Sum(p => p.AmountUsd)
            })
            .OrderBy(c => c.ContractNumber)
            .ToList();
    }

    private async Task PopulateLookupsAsync(ServiceProviderType? selected = null)
    {
        ViewBag.ProviderTypes = Enum.GetValues<ServiceProviderType>()
            .Select(type => new SelectListItem
            {
                Value = ((int)type).ToString(),
                Text = ServiceProviderTypeLabels.ToDisplay(type, HttpContext),
                Selected = selected == type
            })
            .ToList();

        await Task.CompletedTask;
    }

    private static void Normalize(ServiceProviderCreateViewModel model)
    {
        model.Code = NormalizeOptional(model.Code);
        model.Name = model.Name.Trim();
        model.Country = NormalizeOptional(model.Country);
        model.City = NormalizeOptional(model.City);
        model.Phone = NormalizeOptional(model.Phone);
        model.Email = NormalizeOptional(model.Email);
        model.Address = NormalizeOptional(model.Address);
        model.TaxNumber = NormalizeOptional(model.TaxNumber);
        model.Notes = NormalizeOptional(model.Notes);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? BuildTransportLegLabel(InventoryTransportLeg? leg)
        => leg is null
            ? null
            : $"#{leg.Id} - {FirstNonEmpty(leg.RwbNo, leg.WagonNumber) ?? leg.TransportType.ToString()}";

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.ServiceProviders.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.Code, s.Name, ProviderType = (int)s.ProviderType, s.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }

    private static string? BuildTruckDispatchLabel(TruckDispatch? dispatch)
        => dispatch is null
            ? null
            : $"#{dispatch.Id} - {dispatch.Truck?.PlateNumber ?? dispatch.DispatchDate.ToString("yyyy-MM-dd")}";
}
