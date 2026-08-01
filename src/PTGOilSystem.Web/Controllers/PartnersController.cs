using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Partners;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class PartnersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly MasterDataDeleteSafetyService _deleteSafety;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IPartyStatementReadService? _partyStatements;

    public PartnersController(
        ApplicationDbContext db,
        IAuditService audit,
        MasterDataDeleteSafetyService deleteSafety,
        IPurchaseAggregationService? purchaseAggregation = null,
        IPartyStatementReadService? partyStatements = null)
    {
        _db = db;
        _audit = audit;
        _deleteSafety = deleteSafety;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _partyStatements = partyStatements;
    }

    public async Task<IActionResult> Index(string? q, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.Partners.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(p =>
                p.Code.Contains(term) ||
                p.Name.Contains(term) ||
                (p.NamePersian != null && p.NamePersian.Contains(term)) ||
                (p.Country != null && p.Country.Contains(term)) ||
                (p.ContactPerson != null && p.ContactPerson.Contains(term)) ||
                (p.Email != null && p.Email.Contains(term)));
        }

        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        ViewData["q"] = q;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        return View(await query
            .OrderBy(p => p.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync());
    }

    public async Task<IActionResult> Details(int id, int? contractId = null, string? tab = null)
    {
        var item = await BuildPartnerProfileAsync(id, contractId, tab);
        if (item is null) return NotFound();
        if (_partyStatements is not null)
        {
            var statement = await _partyStatements.GetStatementAsync(
                new PartyRef(PartyStatementPartyType.Partner, id),
                new PartyStatementFilter { ContractId = contractId, IncludeOperationalColumns = false },
                HttpContext.RequestAborted);
            ViewData["PartyStatementSummary"] = statement.Summary;
            ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        }
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public IActionResult Create()
        => View(new Partner { IsActive = true });

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,Email,IsActive,Notes")] Partner model, string? returnUrl = null)
    {
        Normalize(model);
        await ValidateAsync(model, model.Id);

        if (!ModelState.IsValid)
            return View(model);

        _db.Partners.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(
            nameof(Partner),
            model.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Code", model.Code),
                ("Name", model.Name),
                ("NamePersian", model.NamePersian),
                ("Country", model.Country),
                ("ContactPerson", model.ContactPerson),
                ("Phone", model.Phone),
                ("Address", model.Address),
                ("Email", model.Email),
                ("IsActive", model.IsActive),
                ("Notes", model.Notes)));

        TempData["ok"] = "شریک با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? NotFound() : View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,Name,NamePersian,Country,ContactPerson,Phone,Address,Email,IsActive,Notes")] Partner model)
    {
        if (id != model.Id)
            return BadRequest();

        Normalize(model);
        await ValidateAsync(model, id);

        if (!ModelState.IsValid)
            return View(model);

        var existing = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        if (existing is null)
            return NotFound();

        var diff = AuditDiffFormatter.ForUpdate(
            ("Code", existing.Code, model.Code),
            ("Name", existing.Name, model.Name),
            ("NamePersian", existing.NamePersian, model.NamePersian),
            ("Country", existing.Country, model.Country),
            ("ContactPerson", existing.ContactPerson, model.ContactPerson),
            ("Phone", existing.Phone, model.Phone),
            ("Address", existing.Address, model.Address),
            ("Email", existing.Email, model.Email),
            ("IsActive", existing.IsActive, model.IsActive),
            ("Notes", existing.Notes, model.Notes));

        existing.Code = model.Code;
        existing.Name = model.Name;
        existing.NamePersian = model.NamePersian;
        existing.Country = model.Country;
        existing.ContactPerson = model.ContactPerson;
        existing.Phone = model.Phone;
        existing.Address = model.Address;
        existing.Email = model.Email;
        existing.IsActive = model.IsActive;
        existing.Notes = model.Notes;

        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Partner), existing.Id, AuditAction.Update, diff: diff);

        TempData["ok"] = "ویرایش شریک انجام شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null)
            return NotFound();

        var safety = await _deleteSafety.EvaluatePartnerAsync(id);
        if (!safety.CanDelete)
        {
            if (safety.ArchiveInsteadOfDelete && item.IsActive)
            {
                var archiveDiff = $"ArchiveInsteadOfDelete: {safety.DependencySummary} | "
                    + AuditDiffFormatter.ForUpdate(("IsActive", item.IsActive, false));
                item.IsActive = false;
                await _db.SaveChangesAsync();
                await _audit.LogAndSaveAsync(nameof(Partner), item.Id, AuditAction.Update, diff: archiveDiff);
                TempData["ok"] = safety.BuildArchivedMessage("شریک");
                return RedirectToAction(nameof(Index));
            }

            TempData["err"] = safety.ArchiveInsteadOfDelete
                ? $"{safety.BuildBlockedMessage("شریک")} این رکورد قبلاً غیرفعال شده است."
                : safety.BuildBlockedMessage("شریک");
            return RedirectToAction(nameof(Index));
        }

        var diff = AuditDiffFormatter.ForDelete(
            ("Code", item.Code),
            ("Name", item.Name),
            ("Country", item.Country));
        _db.Partners.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Partner), id, AuditAction.Delete, diff: diff);
        TempData["ok"] = "شریک حذف شد.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        var wasActive = item.IsActive;
        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAndSaveAsync(nameof(Partner), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(("IsActive", wasActive, item.IsActive)));

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    private async Task<PartnerProfileViewModel?> BuildPartnerProfileAsync(int id, int? contractId, string? tab)
    {
        var partner = await _db.Partners
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        if (partner is null)
        {
            return null;
        }

        var partnerLinks = (await _db.ContractPartners
                .AsNoTracking()
                .Include(cp => cp.Contract)
                    .ThenInclude(c => c!.Product)
                .Include(cp => cp.Contract)
                    .ThenInclude(c => c!.Supplier)
                .Include(cp => cp.Contract)
                    .ThenInclude(c => c!.Customer)
                .Include(cp => cp.Contract)
                    .ThenInclude(c => c!.Company)
                .Where(cp => cp.PartnerId == partner.Id)
                .ToListAsync())
            .Where(cp => cp.Contract is not null)
            .OrderByDescending(cp => cp.Contract!.ContractDate)
            .ThenBy(cp => cp.Contract!.ContractNumber)
            .ToList();

        var contracts = partnerLinks
            .Select(cp => cp.Contract!)
            .DistinctBy(c => c.Id)
            .ToList();
        var contractIds = contracts.Select(c => c.Id).ToHashSet();

        if (contractId.HasValue && !contractIds.Contains(contractId.Value))
        {
            return null;
        }

        var shareByContract = partnerLinks.ToDictionary(cp => cp.ContractId, cp => cp.SharePercent);
        var contractNumberById = contracts.ToDictionary(c => c.Id, c => c.ContractNumber);
        var contractTypeById = contracts.ToDictionary(c => c.Id, c => c.ContractType);
        var finalPriceByContract = contracts.ToDictionary(
            c => c.Id,
            c => ContractPricingAdapter.GetCanonicalFinalPrice(c));

        var purchaseContractIds = contracts
            .Where(c => c.ContractType == ContractType.Purchase)
            .Select(c => c.Id)
            .ToList();
        var purchaseAggregates = await _purchaseAggregation.AggregateForContractsAsync(
            purchaseContractIds,
            finalPriceByContract);

        var transportLegs = purchaseContractIds.Count == 0
            ? new List<PartnerTransportLegRef>()
            : await _db.InventoryTransportLegs
                .AsNoTracking()
                .Where(l => purchaseContractIds.Contains(l.SourcePurchaseContractId)
                    && l.Status != InventoryTransportLegStatus.Cancelled)
                .Select(l => new PartnerTransportLegRef(l.Id, l.SourcePurchaseContractId))
                .ToListAsync();
        var transportPnlByLegId = transportLegs.Count == 0
            ? new Dictionary<int, InventoryTransportPnlSnapshot>()
            : (await new InventoryTransportPnlService(_db).BuildForLegsAsync(
                transportLegs.Select(l => l.TransportLegId).ToList()))
                .ToDictionary(p => p.Key, p => p.Value);
        var purchasePnlByContract = transportLegs
            .GroupBy(l => l.ContractId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var rows = g
                        .Select(l => transportPnlByLegId.GetValueOrDefault(l.TransportLegId))
                        .Where(p => p is not null)
                        .Cast<InventoryTransportPnlSnapshot>()
                        .ToList();

                    return new PartnerPnlTotals(
                        QuantityMt: rows.Sum(p => p.QuantityMt),
                        SalesUsd: rows.Sum(p => p.SalesUsd),
                        PurchaseCostUsd: rows.Sum(p => p.PurchaseCostUsd),
                        OperationalExpensesUsd: rows.Sum(p => p.OperationalExpensesUsd),
                        TotalCostUsd: rows.Sum(p => p.TotalCostUsd),
                        GrossProfitUsd: rows.Sum(p => p.GrossMarginUsd));
                });

        var sales = contractIds.Count == 0
            ? new List<PartnerSaleRef>()
            : await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => s.ContractId.HasValue
                    && contractIds.Contains(s.ContractId.Value)
                    && !s.IsCancelled)
                .Select(s => new PartnerSaleRef(
                    s.Id,
                    s.ContractId!.Value,
                    s.SaleDate,
                    s.QuantityMt,
                    s.TotalUsd))
                .ToListAsync();
        var saleIds = sales.Select(s => s.SaleId).ToHashSet();
        var saleContractById = sales.ToDictionary(s => s.SaleId, s => s.ContractId);
        var salesByContract = sales
            .GroupBy(s => s.ContractId)
            .ToDictionary(
                g => g.Key,
                g => new PartnerSaleTotals(
                    QuantityMt: g.Sum(s => s.QuantityMt),
                    ValueUsd: g.Sum(s => s.AmountUsd),
                    LastSaleDate: g.Max(s => (DateTime?)s.SaleDate)));

        var expenseRows = contractIds.Count == 0
            ? new List<PartnerExpenseRef>()
            : await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.ContractId.HasValue
                    && contractIds.Contains(e.ContractId.Value)
                    && !e.IsCancelled)
                .Select(e => new PartnerExpenseRef(
                    e.ContractId!.Value,
                    e.AmountUsd,
                    e.TransportLegId,
                    e.ShipmentId))
                .ToListAsync();
        var directExpenseByContract = expenseRows
            .Where(e => !e.TransportLegId.HasValue && !e.ShipmentId.HasValue)
            .GroupBy(e => e.ContractId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.AmountUsd));
        var allExpenseByContract = expenseRows
            .GroupBy(e => e.ContractId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.AmountUsd));

        var payments = contractIds.Count == 0
            ? new List<PaymentTransaction>()
            : (await _db.PaymentTransactions
                    .AsNoTracking()
                    .Include(p => p.CashAccount)
                    .Include(p => p.Contract)
                    .Include(p => p.SalesTransaction)
                        .ThenInclude(s => s!.Contract)
                    .Where(p =>
                        (p.ContractId.HasValue && contractIds.Contains(p.ContractId.Value))
                        || (p.SalesTransaction != null
                            && p.SalesTransaction.ContractId.HasValue
                            && contractIds.Contains(p.SalesTransaction.ContractId.Value)))
                    .OrderByDescending(p => p.PaymentDate)
                    .ThenByDescending(p => p.Id)
                    .ToListAsync())
                .DistinctBy(p => p.Id)
                .ToList();

        static int? ResolvePaymentContractId(PaymentTransaction payment)
            => payment.ContractId ?? payment.SalesTransaction?.ContractId;

        static string? ResolvePaymentContractNumber(PaymentTransaction payment)
            => payment.Contract?.ContractNumber ?? payment.SalesTransaction?.Contract?.ContractNumber;

        var paymentSummaries = payments
            .Select(p =>
            {
                var resolvedContractId = ResolvePaymentContractId(p);
                var sharePercent = resolvedContractId.HasValue
                    ? shareByContract.GetValueOrDefault(resolvedContractId.Value)
                    : 0m;
                var shareRatio = sharePercent / 100m;

                return new PartnerPaymentSummaryViewModel
                {
                    PaymentId = p.Id,
                    PaymentDate = p.PaymentDate,
                    Direction = p.Direction,
                    DirectionName = PaymentDirectionLabels.ToPersian(p.Direction),
                    PaymentKind = p.PaymentKind,
                    PaymentKindName = PaymentKindLabels.ToPersian(p.PaymentKind),
                    CashAccount = p.CashAccount?.Name ?? string.Empty,
                    ContractId = resolvedContractId,
                    ContractNumber = ResolvePaymentContractNumber(p),
                    SharePercent = sharePercent,
                    Amount = p.Amount,
                    Currency = p.Currency,
                    AmountUsd = p.AmountUsd,
                    PartnerAmountUsd = RoundMoney(p.AmountUsd * shareRatio),
                    Reference = p.Reference,
                    Description = p.Description,
                    LedgerEntryId = p.LedgerEntryId
                };
            })
            .Where(p => p.ContractId.HasValue)
            .ToList();
        var paymentsByContract = paymentSummaries
            .GroupBy(p => p.ContractId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new PartnerPaymentTotals(
                    CashInUsd: g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd),
                    CashOutUsd: g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd),
                    PartnerCashInUsd: g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.PartnerAmountUsd),
                    PartnerCashOutUsd: g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.PartnerAmountUsd),
                    LastPaymentDate: g.Max(p => (DateTime?)p.PaymentDate)));

        var ledgers = contractIds.Count == 0
            ? new List<LedgerEntry>()
            : (await _db.LedgerEntries
                    .AsNoTracking()
                    .Include(l => l.Contract)
                    .Where(l =>
                        (l.ContractId.HasValue && contractIds.Contains(l.ContractId.Value))
                        || (l.SourceType == "Sale" && saleIds.Contains(l.SourceId)))
                    .OrderBy(l => l.EntryDate)
                    .ThenBy(l => l.Id)
                    .ToListAsync())
                .DistinctBy(l => l.Id)
                .ToList();

        var ledgerBalanceByContract = ledgers
            .Select(l => new { ContractId = ResolveLedgerContractId(l, saleContractById), Ledger = l })
            .Where(x => x.ContractId.HasValue && shareByContract.ContainsKey(x.ContractId.Value))
            .GroupBy(x => x.ContractId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x =>
                {
                    var shareRatio = shareByContract[x.ContractId!.Value] / 100m;
                    var amount = RoundMoney(x.Ledger.AmountUsd * shareRatio);
                    return x.Ledger.Side == LedgerSide.Credit ? amount : -amount;
                }));

        var contractSummaries = partnerLinks
            .Select(cp =>
            {
                var contract = cp.Contract!;
                var shareRatio = cp.SharePercent / 100m;
                var finalPriceUsd = finalPriceByContract[contract.Id];
                var estimatedTotalUsd = finalPriceUsd.HasValue && finalPriceUsd.Value > 0m
                    ? RoundMoney(contract.QuantityMt * finalPriceUsd.Value)
                    : (decimal?)null;

                purchaseAggregates.TryGetValue(contract.Id, out var purchaseAgg);
                purchasePnlByContract.TryGetValue(contract.Id, out var purchasePnl);
                salesByContract.TryGetValue(contract.Id, out var saleTotals);
                paymentsByContract.TryGetValue(contract.Id, out var paymentTotals);
                directExpenseByContract.TryGetValue(contract.Id, out var directExpenseUsd);
                allExpenseByContract.TryGetValue(contract.Id, out var saleExpenseUsd);
                ledgerBalanceByContract.TryGetValue(contract.Id, out var statementBalanceUsd);

                var isPurchase = contract.ContractType == ContractType.Purchase;
                var executedQuantity = isPurchase
                    ? purchaseAgg?.TotalLoadedQuantityMt ?? purchasePnl?.QuantityMt ?? 0m
                    : saleTotals?.QuantityMt ?? 0m;
                var salesUsd = isPurchase
                    ? purchasePnl?.SalesUsd ?? 0m
                    : saleTotals?.ValueUsd ?? 0m;
                var purchaseCostUsd = isPurchase
                    ? ResolvePurchaseCostUsd(purchaseAgg, purchasePnl)
                    : 0m;
                var loadingExpenseUsd = isPurchase && purchaseAgg is not null
                    ? purchaseAgg.LoadingTransportExpenseUsd
                        + purchaseAgg.LoadingWarehouseExpenseUsd
                        + purchaseAgg.LoadingOtherExpenseUsd
                        + purchaseAgg.LoadingRailwayExpenseUsd
                    : 0m;
                var operationalExpensesUsd = isPurchase
                    ? loadingExpenseUsd + (purchasePnl?.OperationalExpensesUsd ?? 0m) + directExpenseUsd
                    : saleExpenseUsd;
                var totalCostUsd = RoundMoney(purchaseCostUsd + operationalExpensesUsd);
                var grossProfitUsd = RoundMoney(salesUsd - totalCostUsd);

                return new PartnerContractSummaryViewModel
                {
                    ContractId = contract.Id,
                    ContractNumber = contract.ContractNumber,
                    ContractType = contract.ContractType,
                    ContractTypeName = GetContractTypeName(contract.ContractType),
                    Product = contract.Product?.Name ?? "-",
                    CounterpartyName = contract.ContractType == ContractType.Purchase
                        ? contract.Supplier?.Name ?? "-"
                        : contract.Customer?.Name ?? "-",
                    ContractDate = contract.ContractDate,
                    Status = contract.Status,
                    StatusName = GetContractStatusName(contract.Status),
                    StatusBadgeClass = GetContractStatusBadgeClass(contract.Status),
                    SharePercent = cp.SharePercent,
                    QuantityMt = contract.QuantityMt,
                    PartnerQuantityMt = RoundQuantity(contract.QuantityMt * shareRatio),
                    Currency = contract.Currency,
                    FinalPriceUsd = finalPriceUsd,
                    EstimatedTotalUsd = estimatedTotalUsd,
                    PartnerEstimatedTotalUsd = RoundMoney((estimatedTotalUsd ?? 0m) * shareRatio),
                    ExecutedQuantityMt = RoundQuantity(executedQuantity),
                    SalesRevenueUsd = RoundMoney(salesUsd),
                    PurchaseCostUsd = RoundMoney(purchaseCostUsd),
                    OperationalExpensesUsd = RoundMoney(operationalExpensesUsd),
                    TotalCostUsd = totalCostUsd,
                    GrossProfitUsd = grossProfitUsd,
                    PartnerGrossProfitUsd = RoundMoney(grossProfitUsd * shareRatio),
                    CashInUsd = paymentTotals?.CashInUsd ?? 0m,
                    CashOutUsd = paymentTotals?.CashOutUsd ?? 0m,
                    PartnerCashInUsd = paymentTotals?.PartnerCashInUsd ?? 0m,
                    PartnerCashOutUsd = paymentTotals?.PartnerCashOutUsd ?? 0m,
                    StatementBalanceUsd = statementBalanceUsd
                };
            })
            .ToList();

        var selectedContract = contractId.HasValue
            ? contractSummaries.First(c => c.ContractId == contractId.Value)
            : null;
        var statementLedgers = contractId.HasValue
            ? ledgers.Where(l => ResolveLedgerContractId(l, saleContractById) == contractId.Value).ToList()
            : ledgers;

        var lastFinancialDate = new[]
            {
                ledgers.Count == 0 ? null : ledgers.Max(l => (DateTime?)l.EntryDate),
                paymentSummaries.Count == 0 ? null : paymentSummaries.Max(p => (DateTime?)p.PaymentDate),
                sales.Count == 0 ? null : sales.Max(s => (DateTime?)s.SaleDate)
            }
            .Where(d => d.HasValue)
            .Max();

        return new PartnerProfileViewModel
        {
            PartnerId = partner.Id,
            Code = partner.Code,
            Name = partner.Name,
            NamePersian = partner.NamePersian,
            Country = partner.Country,
            ContactPerson = partner.ContactPerson,
            Phone = partner.Phone,
            Address = partner.Address,
            Email = partner.Email,
            Notes = partner.Notes,
            IsActive = partner.IsActive,
            CreatedAtUtc = partner.CreatedAtUtc,
            ActiveTab = ResolveActiveTab(tab, contractId),
            SelectedContractId = contractId,
            SelectedContract = selectedContract,
            ContractsCount = contractSummaries.Count,
            ActiveContractsCount = contractSummaries.Count(c => c.Status == ContractStatus.Active),
            PurchaseContractsCount = contractSummaries.Count(c => c.ContractType == ContractType.Purchase),
            SaleContractsCount = contractSummaries.Count(c => c.ContractType == ContractType.Sale),
            AverageSharePercent = contractSummaries.Count == 0 ? 0m : contractSummaries.Average(c => c.SharePercent),
            TotalContractQuantityMt = contractSummaries.Sum(c => c.QuantityMt),
            PartnerContractQuantityMt = contractSummaries.Sum(c => c.PartnerQuantityMt),
            EstimatedContractValueUsd = contractSummaries.Sum(c => c.EstimatedTotalUsd ?? 0m),
            PartnerEstimatedContractValueUsd = contractSummaries.Sum(c => c.PartnerEstimatedTotalUsd),
            SalesRevenueUsd = contractSummaries.Sum(c => c.SalesRevenueUsd),
            PartnerSalesRevenueUsd = contractSummaries.Sum(c => RoundMoney(c.SalesRevenueUsd * c.SharePercent / 100m)),
            PurchaseCostUsd = contractSummaries.Sum(c => c.PurchaseCostUsd),
            OperationalExpensesUsd = contractSummaries.Sum(c => c.OperationalExpensesUsd),
            TotalCostUsd = contractSummaries.Sum(c => c.TotalCostUsd),
            PartnerTotalCostUsd = contractSummaries.Sum(c => c.TotalCostUsd * c.SharePercent / 100m),
            GrossProfitUsd = contractSummaries.Sum(c => c.GrossProfitUsd),
            PartnerGrossProfitUsd = contractSummaries.Sum(c => c.PartnerGrossProfitUsd),
            CashInUsd = contractSummaries.Sum(c => c.CashInUsd),
            CashOutUsd = contractSummaries.Sum(c => c.CashOutUsd),
            PartnerCashInUsd = contractSummaries.Sum(c => c.PartnerCashInUsd),
            PartnerCashOutUsd = contractSummaries.Sum(c => c.PartnerCashOutUsd),
            LastContractDate = contractSummaries.Count == 0 ? null : contractSummaries.Max(c => (DateTime?)c.ContractDate),
            LastFinancialDate = lastFinancialDate,
            StatementContractOptions = contractSummaries
                .OrderBy(c => c.ContractNumber)
                .Select(c => new PartnerStatementContractFilterOptionViewModel
                {
                    ContractId = c.ContractId,
                    ContractNumber = c.ContractNumber
                })
                .ToList(),
            Contracts = contractSummaries,
            Payments = paymentSummaries,
            StatementRows = BuildPartnerStatementRows(
                statementLedgers,
                shareByContract,
                contractNumberById,
                contractTypeById,
                saleContractById)
        };
    }

    private static decimal ResolvePurchaseCostUsd(
        PurchaseAggregationSnapshot? purchaseAgg,
        PartnerPnlTotals? purchasePnl)
    {
        if (purchaseAgg is not null && purchaseAgg.TraceablePurchaseCostUsd > 0m)
        {
            return purchaseAgg.TraceablePurchaseCostUsd;
        }

        return purchasePnl?.PurchaseCostUsd ?? 0m;
    }

    private static IReadOnlyList<PartnerStatementRowViewModel> BuildPartnerStatementRows(
        IEnumerable<LedgerEntry> ledgers,
        IReadOnlyDictionary<int, decimal> shareByContract,
        IReadOnlyDictionary<int, string> contractNumberById,
        IReadOnlyDictionary<int, ContractType> contractTypeById,
        IReadOnlyDictionary<int, int> saleContractById)
    {
        var rows = new List<PartnerStatementRowViewModel>();
        var runningBalance = 0m;

        foreach (var ledger in ledgers
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.Id))
        {
            var resolvedContractId = ResolveLedgerContractId(ledger, saleContractById);
            if (!resolvedContractId.HasValue || !shareByContract.TryGetValue(resolvedContractId.Value, out var sharePercent))
            {
                continue;
            }

            var shareRatio = sharePercent / 100m;
            var partnerAmountUsd = RoundMoney(ledger.AmountUsd * shareRatio);
            var partnerDebitUsd = ledger.Side == LedgerSide.Debit ? partnerAmountUsd : (decimal?)null;
            var partnerCreditUsd = ledger.Side == LedgerSide.Credit ? partnerAmountUsd : (decimal?)null;
            runningBalance += (partnerCreditUsd ?? 0m) - (partnerDebitUsd ?? 0m);
            var sourceAmount = ledger.SourceAmount ?? ledger.AmountUsd;
            var contractTypeName = contractTypeById.TryGetValue(resolvedContractId.Value, out var contractType)
                ? GetContractTypeName(contractType)
                : string.Empty;

            rows.Add(new PartnerStatementRowViewModel
            {
                LedgerEntryId = ledger.Id,
                Date = ledger.EntryDate,
                Type = GetPartnerStatementTypeName(ledger.SourceType),
                Reference = ledger.Reference,
                ContractId = resolvedContractId,
                ContractNumber = contractNumberById.GetValueOrDefault(resolvedContractId.Value),
                ContractTypeName = contractTypeName,
                SharePercent = sharePercent,
                Description = ledger.Description,
                Currency = ledger.SourceCurrencyCode ?? ledger.Currency,
                Debit = ledger.Side == LedgerSide.Debit ? sourceAmount : null,
                Credit = ledger.Side == LedgerSide.Credit ? sourceAmount : null,
                PartnerDebitUsd = partnerDebitUsd,
                PartnerCreditUsd = partnerCreditUsd,
                RunningBalanceUsd = runningBalance
            });
        }

        return rows;
    }

    private static int? ResolveLedgerContractId(
        LedgerEntry ledger,
        IReadOnlyDictionary<int, int> saleContractById)
        => ledger.ContractId
            ?? (ledger.SourceType == "Sale" && saleContractById.TryGetValue(ledger.SourceId, out var saleContractId)
                ? saleContractId
                : null);

    private static string ResolveActiveTab(string? tab, int? contractId)
    {
        if (contractId.HasValue)
        {
            return "account";
        }

        return (tab ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "contracts" or "pnl" or "activity" => "contracts",
            "account" or "statement" or "payments" => "account",
            _ => "overview"
        };
    }

    private static string GetContractTypeName(ContractType type)
        => type == ContractType.Purchase ? "خرید" : "فروش";

    private static string GetContractStatusName(ContractStatus status)
        => status switch
        {
            ContractStatus.Draft => "پیش‌نویس",
            ContractStatus.Active => "فعال",
            ContractStatus.Closed => "بسته‌شده",
            ContractStatus.Cancelled => "لغوشده",
            _ => status.ToString()
        };

    private static string GetContractStatusBadgeClass(ContractStatus status)
        => status switch
        {
            ContractStatus.Active => "status-badge-success",
            ContractStatus.Draft => "status-badge-warning",
            ContractStatus.Closed => "status-badge-info",
            ContractStatus.Cancelled => "status-badge-danger",
            _ => "status-badge-neutral"
        };

    private static string GetPartnerStatementTypeName(string sourceType)
    {
        if (Enum.TryParse<PaymentKind>(sourceType, out var paymentKind))
        {
            return PaymentKindLabels.ToPersian(paymentKind);
        }

        return sourceType switch
        {
            "Sale" => "فروش",
            "Expense" => "مصرف",
            "ContractBalanceTransfer" => "انتقال مانده قرارداد",
            "Adjustment" => "اصلاح حساب",
            _ => sourceType
        };
    }

    private static decimal RoundMoney(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private async Task ValidateAsync(Partner model, int currentId)
    {
        if (string.IsNullOrWhiteSpace(model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد شریک الزامی است.");

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "نام شریک الزامی است.");

        if (await _db.Partners.AnyAsync(p => p.Id != currentId && p.Code == model.Code))
            ModelState.AddModelError(nameof(model.Code), "کد شریک تکراری است.");
    }

    private static void Normalize(Partner model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.NamePersian = string.IsNullOrWhiteSpace(model.NamePersian) ? null : model.NamePersian.Trim();
        model.Country = string.IsNullOrWhiteSpace(model.Country) ? null : model.Country.Trim();
        model.ContactPerson = string.IsNullOrWhiteSpace(model.ContactPerson) ? null : model.ContactPerson.Trim();
        model.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
        model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
        model.Email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    private sealed record PartnerTransportLegRef(int TransportLegId, int ContractId);

    private sealed record PartnerPnlTotals(
        decimal QuantityMt,
        decimal SalesUsd,
        decimal PurchaseCostUsd,
        decimal OperationalExpensesUsd,
        decimal TotalCostUsd,
        decimal GrossProfitUsd);

    private sealed record PartnerSaleRef(
        int SaleId,
        int ContractId,
        DateTime SaleDate,
        decimal QuantityMt,
        decimal AmountUsd);

    private sealed record PartnerSaleTotals(
        decimal QuantityMt,
        decimal ValueUsd,
        DateTime? LastSaleDate);

    private sealed record PartnerExpenseRef(
        int ContractId,
        decimal AmountUsd,
        int? TransportLegId,
        int? ShipmentId);

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.Partners.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Code, p.Name, p.Country, p.ContactPerson, p.Phone, p.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }

    private sealed record PartnerPaymentTotals(
        decimal CashInUsd,
        decimal CashOutUsd,
        decimal PartnerCashInUsd,
        decimal PartnerCashOutUsd,
        DateTime? LastPaymentDate);
}
