using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Balance;
using PTGOilSystem.Web.Models.ContractJourney;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class BalanceController : Controller
{
    private const int IndexPageSize = 20;
    private readonly ApplicationDbContext _db;
    private readonly IPartyBalanceReadService _partyBalances;

    public BalanceController(ApplicationDbContext db, IPartyBalanceReadService? partyBalances = null)
    {
        _db = db;
        _partyBalances = partyBalances ?? new PartyBalanceReadService(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService());
    }

    public IActionResult Index()
    {
        return RedirectToAction(nameof(Contracts));
    }

    private async Task PopulateContractsLookupsAsync(ContractsBalanceFilterViewModel filter)
    {
        ViewBag.ContractTypes = new SelectList(
            Enum.GetValues<ContractType>()
                .Select(value => new { Value = value, Text = GetContractTypeName(value) })
                .ToList(),
            "Value",
            "Text",
            filter.ContractType);

        ViewBag.ContractStatuses = new SelectList(
            Enum.GetValues<ContractStatus>()
                .Select(value => new { Value = value, Text = GetContractStatusName(value) })
                .ToList(),
            "Value",
            "Text",
            filter.Status);

        ViewBag.Products = new SelectList(
            await _db.Products
                .AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.ProductId);

        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.CustomerId);

        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.SupplierId);
    }

    private async Task PopulateCustomersLookupsAsync(CustomersBalanceFilterViewModel filter)
    {
        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.CustomerId);

        ViewBag.Countries = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .Where(c => c.Country != null && c.Country != string.Empty)
                .Select(c => c.Country!)
                .Distinct()
                .OrderBy(country => country)
                .ToListAsync(),
            filter.Country);
    }

    private async Task PopulateSuppliersLookupsAsync(SuppliersBalanceFilterViewModel filter)
    {
        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            filter.SupplierId);

        ViewBag.Countries = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .Where(s => s.Country != null && s.Country != string.Empty)
                .Select(s => s.Country!)
                .Distinct()
                .OrderBy(country => country)
                .ToListAsync(),
            filter.Country);
    }

    private static IQueryable<Contract> ApplyContractsFilter(
        IQueryable<Contract> query,
        ContractsBalanceFilterViewModel filter)
    {
        if (filter.FromDate.HasValue)
        {
            query = query.Where(c => c.ContractDate >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(c => c.ContractDate <= filter.ToDate.Value);
        }

        if (filter.ContractType.HasValue)
        {
            query = query.Where(c => c.ContractType == filter.ContractType.Value);
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(c => c.Status == filter.Status.Value);
        }

        if (filter.ProductId.HasValue)
        {
            query = query.Where(c => c.ProductId == filter.ProductId.Value);
        }

        if (filter.CustomerId.HasValue)
        {
            query = query.Where(c => c.CustomerId == filter.CustomerId.Value);
        }

        if (filter.SupplierId.HasValue)
        {
            query = query.Where(c => c.SupplierId == filter.SupplierId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(c =>
                c.ContractName.Contains(search)
                || c.ContractNumber.Contains(search)
                || (c.Customer != null && c.Customer.Name.Contains(search))
                || (c.Supplier != null && c.Supplier.Name.Contains(search))
                || (c.Product != null && c.Product.Name.Contains(search)));
        }

        return query;
    }

    private static IQueryable<Customer> ApplyCustomersFilter(
        IQueryable<Customer> query,
        CustomersBalanceFilterViewModel filter)
    {
        if (filter.CustomerId.HasValue)
        {
            query = query.Where(c => c.Id == filter.CustomerId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Country))
        {
            var country = filter.Country.Trim();
            query = query.Where(c => c.Country == country);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(c =>
                c.Name.Contains(search)
                || (c.Country != null && c.Country.Contains(search))
                || (c.ContactPerson != null && c.ContactPerson.Contains(search))
                || (c.Code != null && c.Code.Contains(search)));
        }

        return query;
    }

    private static IQueryable<Supplier> ApplySuppliersFilter(
        IQueryable<Supplier> query,
        SuppliersBalanceFilterViewModel filter)
    {
        if (filter.SupplierId.HasValue)
        {
            query = query.Where(s => s.Id == filter.SupplierId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Country))
        {
            var country = filter.Country.Trim();
            query = query.Where(s => s.Country == country);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(s =>
                s.Name.Contains(search)
                || (s.Country != null && s.Country.Contains(search))
                || (s.ContactPerson != null && s.ContactPerson.Contains(search))
                || (s.Code != null && s.Code.Contains(search)));
        }

        return query;
    }

    public async Task<IActionResult> Contracts([FromQuery] ContractsBalanceFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new ContractsBalanceFilterViewModel();
        await PopulateContractsLookupsAsync(filter);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var contractQuery = ApplyContractsFilter(_db.Contracts.AsNoTracking(), filter);
        var totalCount = await contractQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var orderedContracts = contractQuery
            .OrderByDescending(c => c.ContractDate)
            .ThenByDescending(c => c.Id);

        var contracts = await (page <= 0
                ? orderedContracts
                : orderedContracts
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                c.ContractType,
                c.Status,
                CustomerName = c.Customer != null ? c.Customer.Name : null,
                SupplierName = c.Supplier != null ? c.Supplier.Name : null,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null,
                c.QuantityMt
            })
            .ToListAsync();

        var contractIds = contracts.Select(contract => contract.Id).ToList();

        var salesQuery = _db.SalesTransactions
            .AsNoTracking()
            .Where(s => s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value));
        if (filter.FromDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate <= filter.ToDate.Value);
        var salesSummary = contractIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await salesQuery
            .GroupBy(s => s.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                TotalUsd = g.Sum(x => x.TotalUsd)
            })
            .ToDictionaryAsync(x => x.ContractId, x => x.TotalUsd);

        var expensesQuery = _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ContractId.HasValue && contractIds.Contains(e.ContractId.Value));
        if (filter.FromDate.HasValue) expensesQuery = expensesQuery.Where(e => e.ExpenseDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) expensesQuery = expensesQuery.Where(e => e.ExpenseDate <= filter.ToDate.Value);
        var expensesSummary = contractIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await expensesQuery
            .GroupBy(e => e.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                TotalUsd = g.Sum(x => x.AmountUsd)
            })
            .ToDictionaryAsync(x => x.ContractId, x => x.TotalUsd);

        var ledgerQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId.HasValue && contractIds.Contains(l.ContractId.Value));
        if (filter.FromDate.HasValue) ledgerQuery = ledgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) ledgerQuery = ledgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value);
        var ledgerSummary = contractIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await ledgerQuery
            .GroupBy(l => l.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                Summary = new LedgerAggregate(
                    g.Count(),
                    g.Sum(x => x.Side == LedgerSide.Debit ? x.AmountUsd : 0m),
                    g.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : 0m))
            })
            .ToDictionaryAsync(x => x.ContractId, x => x.Summary);

        var shipmentSummary = contractIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.Shipments
            .AsNoTracking()
            .Where(s => s.ContractId.HasValue && contractIds.Contains(s.ContractId.Value))
            .GroupBy(s => s.ContractId!.Value)
            .Select(g => new
            {
                ContractId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.ContractId, x => x.Count);

        var items = contracts.Select(contract =>
        {
            salesSummary.TryGetValue(contract.Id, out var salesTotal);
            expensesSummary.TryGetValue(contract.Id, out var expensesTotal);
            ledgerSummary.TryGetValue(contract.Id, out var ledger);
            shipmentSummary.TryGetValue(contract.Id, out var shipmentCount);

            var debitTotal = ledger?.DebitTotal ?? 0m;
            var creditTotal = ledger?.CreditTotal ?? 0m;

            return new ContractBalanceListItemViewModel
            {
                Id = contract.Id,
                ContractNumber = contract.ContractNumber,
                ContractTypeName = GetContractTypeName(contract.ContractType),
                StatusName = GetContractStatusName(contract.Status),
                CustomerName = contract.CustomerName,
                SupplierName = contract.SupplierName,
                ProductName = contract.ProductName,
                ContractUnitText = ContractUiText.ResolveUnitText(
                    contract.UnitSymbol,
                    contract.UnitCode,
                    contract.UnitNamePersian,
                    contract.UnitName),
                QuantityMt = contract.QuantityMt,
                ShipmentCount = shipmentCount,
                TotalSalesUsd = salesTotal,
                TotalExpensesUsd = expensesTotal,
                RelatedLedgerCount = ledger?.Count ?? 0,
                // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته) = پرداخت‌ها − بار.
                // مثبت یعنی روی این قرارداد پیش‌پرداخت داریم؛ منفی یعنی بدهکاریم.
                BaseBalanceUsd = debitTotal - creditTotal
            };
        }).ToList();

        return View(new ContractsBalanceViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> ContractDetails(int id)
    {
        var exists = await _db.Contracts
            .AsNoTracking()
            .AnyAsync(c => c.Id == id);
        if (!exists)
        {
            return NotFound();
        }
        return RedirectToAction(
            "Details",
            "ContractJourney",
            new { contractId = id, tab = ContractJourneyTabs.Details.Finance });
    }

    public async Task<IActionResult> Customers([FromQuery] CustomersBalanceFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new CustomersBalanceFilterViewModel();
        await PopulateCustomersLookupsAsync(filter);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var customerQuery = ApplyCustomersFilter(_db.Customers.AsNoTracking(), filter);
        var totalCount = await customerQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var orderedCustomers = customerQuery.OrderBy(c => c.Name);

        var customers = await (page <= 0
                ? orderedCustomers
                : orderedCustomers
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Country
            })
            .ToListAsync();

        var customerIds = customers.Select(customer => customer.Id).ToList();
        var officialCustomerBalances = (await _partyBalances.GetBalancesAsync(
                new ManagementReportFilterViewModel
                {
                    FromDate = filter.FromDate,
                    ToDate = filter.ToDate
                }))
            .Where(row => row.PartyType == PartyStatementPartyType.Customer
                && customerIds.Contains(row.PartyId))
            .ToDictionary(row => row.PartyId);

        var filteredContractsQuery = _db.Contracts
            .AsNoTracking()
            .Where(c => c.CustomerId.HasValue && customerIds.Contains(c.CustomerId.Value));
        if (filter.FromDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var contractSummary = customerIds.Count == 0
            ? new Dictionary<int, CountAggregate>()
            : await filteredContractsQuery
                .GroupBy(c => c.CustomerId!.Value)
                .Select(g => new
                {
                    CustomerId = g.Key,
                    Summary = new CountAggregate(
                        g.Count(),
                        g.Count(c => c.Status == ContractStatus.Active),
                        g.Count(c => c.Status == ContractStatus.Closed))
                })
                .ToDictionaryAsync(x => x.CustomerId, x => x.Summary);

        var salesQuery = _db.SalesTransactions
            .AsNoTracking()
            .Where(s => customerIds.Contains(s.CustomerId));
        if (filter.FromDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate <= filter.ToDate.Value);
        var salesSummary = customerIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await salesQuery
            .GroupBy(s => s.CustomerId)
            .Select(g => new
            {
                CustomerId = g.Key,
                TotalUsd = g.Sum(x => x.TotalUsd)
            })
            .ToDictionaryAsync(x => x.CustomerId, x => x.TotalUsd);

        var expensesSummary = customerIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await (
                from e in _db.ExpenseTransactions.AsNoTracking()
                join c in filteredContractsQuery on e.ContractId equals c.Id
                where (!filter.FromDate.HasValue || e.ExpenseDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || e.ExpenseDate <= filter.ToDate.Value)
                group e by c.CustomerId!.Value into g
                select new
                {
                    CustomerId = g.Key,
                    TotalUsd = g.Sum(x => x.AmountUsd)
                })
            .ToDictionaryAsync(x => x.CustomerId, x => x.TotalUsd);

        var directLedgerQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.CustomerId.HasValue && customerIds.Contains(l.CustomerId.Value));
        if (filter.FromDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value);
        var directLedgerSummary = customerIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await directLedgerQuery
            .GroupBy(l => l.CustomerId!.Value)
            .Select(g => new
            {
                CustomerId = g.Key,
                Summary = new LedgerAggregate(
                    g.Count(),
                    g.Sum(x => x.Side == LedgerSide.Debit ? x.AmountUsd : 0m),
                    g.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : 0m))
            })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Summary);

        var contractLedgerSummary = customerIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await (
                from l in _db.LedgerEntries.AsNoTracking()
                join c in filteredContractsQuery on l.ContractId equals c.Id
                where !l.CustomerId.HasValue
                    && (!filter.FromDate.HasValue || l.EntryDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || l.EntryDate <= filter.ToDate.Value)
                group l by c.CustomerId!.Value into g
                select new
                {
                    CustomerId = g.Key,
                    Summary = new LedgerAggregate(
                        g.Count(),
                        g.Sum(x => x.Side == LedgerSide.Debit ? x.AmountUsd : 0m),
                        g.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : 0m))
                })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Summary);

        var items = customers.Select(customer =>
        {
            contractSummary.TryGetValue(customer.Id, out var contracts);
            salesSummary.TryGetValue(customer.Id, out var salesTotal);
            expensesSummary.TryGetValue(customer.Id, out var expensesTotal);
            directLedgerSummary.TryGetValue(customer.Id, out var directLedger);
            contractLedgerSummary.TryGetValue(customer.Id, out var contractLedger);

            var debitTotal = (directLedger?.DebitTotal ?? 0m) + (contractLedger?.DebitTotal ?? 0m);
            var creditTotal = (directLedger?.CreditTotal ?? 0m) + (contractLedger?.CreditTotal ?? 0m);

            return new CustomerBalanceListItemViewModel
            {
                Id = customer.Id,
                Name = customer.Name,
                Country = customer.Country,
                RelatedContractsCount = contracts?.Count ?? 0,
                ActiveContractsCount = contracts?.ActiveCount ?? 0,
                ClosedContractsCount = contracts?.ClosedCount ?? 0,
                TotalSalesUsd = salesTotal,
                TotalExpensesUsd = expensesTotal,
                RelatedLedgerCount = (directLedger?.Count ?? 0) + (contractLedger?.Count ?? 0),
                // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته).
                BaseBalanceUsd = officialCustomerBalances.TryGetValue(customer.Id, out var officialBalance)
                    ? officialBalance.ClosingBalanceUsd
                    : 0m
            };
        }).ToList();

        return View(new CustomersBalanceViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> CustomerDetails(int id)
    {
        var exists = await _db.Customers
            .AsNoTracking()
            .AnyAsync(c => c.Id == id);
        if (!exists)
        {
            return NotFound();
        }
        return RedirectToAction("Details", "Customers", new { id });
    }

    public async Task<IActionResult> Suppliers([FromQuery] SuppliersBalanceFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new SuppliersBalanceFilterViewModel();
        await PopulateSuppliersLookupsAsync(filter);

        var pageSize = PTGOilSystem.Web.Helpers.ListPageSize.Resolve(perPage, IndexPageSize);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = IndexPageSize;

        var supplierQuery = ApplySuppliersFilter(_db.Suppliers.AsNoTracking(), filter);
        var totalCount = await supplierQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var orderedSuppliers = supplierQuery.OrderBy(s => s.Name);

        var suppliers = await (page <= 0
                ? orderedSuppliers
                : orderedSuppliers
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize))
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Country
            })
            .ToListAsync();

        var supplierIds = suppliers.Select(supplier => supplier.Id).ToList();
        var officialSupplierBalances = (await _partyBalances.GetBalancesAsync(
                new ManagementReportFilterViewModel
                {
                    FromDate = filter.FromDate,
                    ToDate = filter.ToDate
                }))
            .Where(row => row.PartyType == PartyStatementPartyType.Supplier
                && supplierIds.Contains(row.PartyId))
            .ToDictionary(row => row.PartyId);

        var filteredContractsQuery = _db.Contracts
            .AsNoTracking()
            .Where(c => c.SupplierId.HasValue && supplierIds.Contains(c.SupplierId.Value));
        if (filter.FromDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) filteredContractsQuery = filteredContractsQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var contractSummary = supplierIds.Count == 0
            ? new Dictionary<int, CountAggregate>()
            : await filteredContractsQuery
                .GroupBy(c => c.SupplierId!.Value)
                .Select(g => new
                {
                    SupplierId = g.Key,
                    Summary = new CountAggregate(
                        g.Count(),
                        g.Count(c => c.Status == ContractStatus.Active),
                        g.Count(c => c.Status == ContractStatus.Closed))
                })
                .ToDictionaryAsync(x => x.SupplierId, x => x.Summary);

        var salesSummary = supplierIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await (
                from s in _db.SalesTransactions.AsNoTracking()
                join c in filteredContractsQuery on s.ContractId equals c.Id
                where (!filter.FromDate.HasValue || s.SaleDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || s.SaleDate <= filter.ToDate.Value)
                group s by c.SupplierId!.Value into g
                select new
                {
                    SupplierId = g.Key,
                    TotalUsd = g.Sum(x => x.TotalUsd)
                })
            .ToDictionaryAsync(x => x.SupplierId, x => x.TotalUsd);

        var expensesSummary = supplierIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await (
                from e in _db.ExpenseTransactions.AsNoTracking()
                join c in filteredContractsQuery on e.ContractId equals c.Id
                where (!filter.FromDate.HasValue || e.ExpenseDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || e.ExpenseDate <= filter.ToDate.Value)
                group e by c.SupplierId!.Value into g
                select new
                {
                    SupplierId = g.Key,
                    TotalUsd = g.Sum(x => x.AmountUsd)
                })
            .ToDictionaryAsync(x => x.SupplierId, x => x.TotalUsd);

        var directLedgerQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SupplierId.HasValue && supplierIds.Contains(l.SupplierId.Value));
        if (filter.FromDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue) directLedgerQuery = directLedgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value);
        var directLedgerSummary = supplierIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await directLedgerQuery
            .GroupBy(l => l.SupplierId!.Value)
            .Select(g => new
            {
                SupplierId = g.Key,
                Summary = new LedgerAggregate(
                    g.Count(),
                    g.Sum(x => x.Side == LedgerSide.Debit ? x.AmountUsd : 0m),
                    g.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : 0m))
            })
            .ToDictionaryAsync(x => x.SupplierId, x => x.Summary);

        var contractLedgerSummary = supplierIds.Count == 0
            ? new Dictionary<int, LedgerAggregate>()
            : await (
                from l in _db.LedgerEntries.AsNoTracking()
                join c in filteredContractsQuery on l.ContractId equals c.Id
                where !l.SupplierId.HasValue
                    && (!filter.FromDate.HasValue || l.EntryDate >= filter.FromDate.Value)
                    && (!filter.ToDate.HasValue || l.EntryDate <= filter.ToDate.Value)
                group l by c.SupplierId!.Value into g
                select new
                {
                    SupplierId = g.Key,
                    Summary = new LedgerAggregate(
                        g.Count(),
                        g.Sum(x => x.Side == LedgerSide.Debit ? x.AmountUsd : 0m),
                        g.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : 0m))
                })
            .ToDictionaryAsync(x => x.SupplierId, x => x.Summary);

        var items = suppliers.Select(supplier =>
        {
            contractSummary.TryGetValue(supplier.Id, out var contracts);
            salesSummary.TryGetValue(supplier.Id, out var salesTotal);
            expensesSummary.TryGetValue(supplier.Id, out var expensesTotal);
            directLedgerSummary.TryGetValue(supplier.Id, out var directLedger);
            contractLedgerSummary.TryGetValue(supplier.Id, out var contractLedger);

            var debitTotal = (directLedger?.DebitTotal ?? 0m) + (contractLedger?.DebitTotal ?? 0m);
            var creditTotal = (directLedger?.CreditTotal ?? 0m) + (contractLedger?.CreditTotal ?? 0m);

            return new SupplierBalanceListItemViewModel
            {
                Id = supplier.Id,
                Name = supplier.Name,
                Country = supplier.Country,
                RelatedContractsCount = contracts?.Count ?? 0,
                ActiveContractsCount = contracts?.ActiveCount ?? 0,
                ClosedContractsCount = contracts?.ClosedCount ?? 0,
                TotalSalesUsd = salesTotal,
                TotalExpensesUsd = expensesTotal,
                RelatedLedgerCount = (directLedger?.Count ?? 0) + (contractLedger?.Count ?? 0),
                // مانده نمایشی مطابق قرارداد صورت‌حساب: Σ(داده − گرفته) = پرداخت‌ها − بار.
                BaseBalanceUsd = officialSupplierBalances.TryGetValue(supplier.Id, out var officialBalance)
                    ? officialBalance.ClosingBalanceUsd
                    : 0m
            };
        }).ToList();

        return View(new SuppliersBalanceViewModel
        {
            Filter = filter,
            Items = items,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount
        });
    }

    public async Task<IActionResult> SupplierDetails(int id)
    {
        var exists = await _db.Suppliers
            .AsNoTracking()
            .AnyAsync(s => s.Id == id);
        if (!exists)
        {
            return NotFound();
        }
        return RedirectToAction("Details", "Suppliers", new { id });
    }


    private sealed record CountAggregate(int Count, int ActiveCount, int ClosedCount);

    private sealed record LedgerAggregate(int Count, decimal DebitTotal, decimal CreditTotal);

    private static string GetContractTypeName(ContractType contractType)
        => contractType == ContractType.Purchase ? "خرید" : "فروش";

    private static string GetContractStatusName(ContractStatus status)
        => status switch
        {
            ContractStatus.Draft => "پیش‌نویس",
            ContractStatus.Active => "فعال",
            ContractStatus.Closed => "بسته",
            ContractStatus.Cancelled => "لغو‌شده",
            _ => status.ToString()
        };
}
