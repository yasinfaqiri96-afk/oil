using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IMemoryCache? _cache;

    public ReportsController(
        ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null,
        IMemoryCache? cache = null)
    {
        _db = db;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _cache = cache;
    }

    private sealed record LookupOption(int Id, string Name);
    private sealed record TankLookupOption(int Id, string Display);

    private Task<T> GetCachedLookupAsync<T>(string key, Func<Task<T>> factory)
        where T : class
    {
        if (_cache is null)
        {
            return factory();
        }

        return _cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            entry.SlidingExpiration = TimeSpan.FromSeconds(30);
            return factory();
        })!;
    }

    public async Task<IActionResult> Index()
    {
        var sales = await _db.SalesTransactions
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(x => x.TotalUsd) })
            .FirstOrDefaultAsync();

        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(x => x.AmountUsd) })
            .FirstOrDefaultAsync();

        return View(new ReportHubViewModel
        {
            SalesCount = sales?.Count ?? 0,
            SalesTotalUsd = sales?.Total ?? 0m,
            ExpenseCount = expenses?.Count ?? 0,
            ExpenseTotalUsd = expenses?.Total ?? 0m,
            ContractCount = await _db.Contracts.AsNoTracking().CountAsync(),
            ShipmentCount = await _db.Shipments.AsNoTracking().CountAsync(),
            InventoryMovementCount = await _db.InventoryMovements.AsNoTracking().CountAsync(),
            DispatchCount = await _db.TruckDispatches.AsNoTracking().CountAsync(),
            Cards = BuildReportHubCards()
        });
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> CompanyOverview([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true, includeInventory: true);
        return View(await BuildCompanyFinancialOverviewAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> CashFlow([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true);
        return View(await BuildCashFlowReportAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> ReceivablesPayables([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true);
        return View(await BuildReceivablesPayablesReportAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> InventoryOperations([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeInventory: true);
        return View(await BuildInventoryOperationsReportAsync(filter));
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> Warnings()
    {
        return View(await BuildReportsWarningsAsync());
    }

    private static IReadOnlyList<ReportHubCardViewModel> BuildReportHubCards()
        =>
        [
            new()
            {
                Action = nameof(CompanyOverview),
                TitleFa = "نمای کلی مالی",
                TitleEn = "Company Financial Overview",
                DescriptionFa = "وضعیت کلی فروش، مصارف، سود و مانده‌های مهم شرکت.",
                DescriptionEn = "Company sales, costs, profit and key balances.",
                Icon = "bi-speedometer2",
                ToneClass = "tone-mint"
            },
            new()
            {
                Action = nameof(ContractPnl),
                TitleFa = "سود و زیان قراردادها",
                TitleEn = "Contract P&L",
                DescriptionFa = "درآمد، قیمت خرید، مصارف، ضایعات و سود هر قرارداد.",
                DescriptionEn = "Revenue, purchase cost, expenses, losses and profit by contract.",
                Icon = "bi-graph-up-arrow",
                ToneClass = "tone-lavender"
            },
            new()
            {
                Action = nameof(FxDifference),
                TitleFa = "تفاوت نرخ ارز",
                TitleEn = "FX Difference",
                DescriptionFa = "سود و ضرر تفاوت نرخ خرید ارز شرکت با نرخ قبول تأمین‌کننده در حواله‌های صراف.",
                DescriptionEn = "FX gain and loss between the company purchase rate and the supplier accepted rate on sarraf transfers.",
                Icon = "bi-currency-exchange",
                ToneClass = "tone-amber"
            },
            new()
            {
                Action = nameof(CashFlow),
                TitleFa = "جریان نقدی",
                TitleEn = "Cash Flow",
                DescriptionFa = "پول واقعی ورودی و خروجی بر اساس رزنامچه و حساب‌های نقدی.",
                DescriptionEn = "Actual cash inflow and outflow from payments and cash accounts.",
                Icon = "bi-cash-stack",
                ToneClass = "tone-sky"
            },
            new()
            {
                Action = nameof(ReceivablesPayables),
                TitleFa = "بدهی‌ها و طلب‌ها",
                TitleEn = "Receivables & Payables",
                DescriptionFa = "مانده مشتریان، تأمین‌کنندگان، شرکت‌های خدماتی و صراف‌ها.",
                DescriptionEn = "Customer, supplier, service provider and sarraf balances.",
                Icon = "bi-people",
                ToneClass = "tone-amber"
            },
            new()
            {
                Controller = "Inventory",
                Action = "Index",
                TitleFa = "موجودی",
                TitleEn = "Inventory",
                DescriptionFa = "موجودی انبارها، مخازن و حرکت‌های موجودی.",
                DescriptionEn = "Warehouse and tank inventory and stock movements.",
                Icon = "bi-box-seam-fill",
                ToneClass = "tone-teal"
            },
            new()
            {
                Action = nameof(InventoryOperations),
                TitleFa = "موجودی و عملیات",
                TitleEn = "Inventory & Operations",
                DescriptionFa = "خلاصه موجودی، حرکت‌ها و هشدارهای عملیاتی مهم.",
                DescriptionEn = "Inventory, movements and important operational warnings.",
                Icon = "bi-box-seam",
                ToneClass = "tone-blue"
            },
            new()
            {
                Action = nameof(Warnings),
                TitleFa = "مغایرت‌ها",
                TitleEn = "Reconciliation & Warnings",
                DescriptionFa = "مواردی که برای اصلاح ledger، موجودی یا اسناد نیاز به اقدام دارند.",
                DescriptionEn = "Actionable ledger, inventory and document issues.",
                Icon = "bi-exclamation-triangle",
                ToneClass = "tone-rose"
            }
        ];

    private async Task<CompanyFinancialOverviewViewModel> BuildCompanyFinancialOverviewAsync(ManagementReportFilterViewModel filter)
    {
        var salesQuery = _db.SalesTransactions.AsNoTracking().Where(s => !s.IsCancelled);
        if (filter.FromDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) salesQuery = salesQuery.Where(s => s.SaleDate <= filter.ToDate.Value.Date);
        if (filter.ProductId.HasValue) salesQuery = salesQuery.Where(s => s.ProductId == filter.ProductId.Value);
        if (filter.ContractId.HasValue) salesQuery = salesQuery.Where(s => s.ContractId == filter.ContractId.Value);
        if (filter.CustomerId.HasValue) salesQuery = salesQuery.Where(s => s.CustomerId == filter.CustomerId.Value);

        var revenueUsd = await salesQuery.SumAsync(s => (decimal?)s.TotalUsd) ?? 0m;

        var expenseQuery = _db.ExpenseTransactions.AsNoTracking().Where(e => !e.IsCancelled);
        if (filter.FromDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) expenseQuery = expenseQuery.Where(e => e.ExpenseDate <= filter.ToDate.Value.Date);
        if (filter.ContractId.HasValue) expenseQuery = expenseQuery.Where(e => e.ContractId == filter.ContractId.Value);
        var expenseUsd = await expenseQuery.SumAsync(e => (decimal?)e.AmountUsd) ?? 0m;

        var paymentQuery = ApplyPaymentFilters(_db.PaymentTransactions.AsNoTracking(), filter);
        var cashInUsd = await paymentQuery
            .Where(p => p.Direction == PaymentDirection.In)
            .SumAsync(p => (decimal?)p.AmountUsd) ?? 0m;
        var cashOutUsd = await paymentQuery
            .Where(p => p.Direction == PaymentDirection.Out)
            .SumAsync(p => (decimal?)p.AmountUsd) ?? 0m;

        var pnl = await BuildContractPnlAsync(filter);
        var balances = await BuildReceivablesPayablesReportAsync(filter);
        var warnings = await BuildReportsWarningsAsync();

        var topContracts = pnl.PurchaseRows
            .OrderByDescending(r => Math.Abs(r.GrossMarginUsd))
            .Take(5)
            .ToList();

        return new CompanyFinancialOverviewViewModel
        {
            Filter = filter,
            RevenueUsd = revenueUsd,
            PurchaseCostUsd = pnl.PurchaseRows.Sum(r => r.PurchaseValueUsd),
            ExpenseUsd = expenseUsd,
            LossCostUsd = pnl.PurchaseRows.Sum(r => r.LossCostUsd),
            ExchangeGainUsd = pnl.TotalExchangeGainUsd,
            ExchangeLossUsd = pnl.TotalExchangeLossUsd,
            NetCashMovementUsd = cashInUsd - cashOutUsd,
            CustomerReceivableUsd = balances.CustomerReceivableUsd,
            SupplierPayableUsd = balances.SupplierPayableUsd,
            SarrafNetUsd = balances.SarrafBalanceUsd,
            WarningCount = warnings.TotalIssueCount,
            TopContracts = topContracts,
            Metrics =
            [
                new() { Label = "فروش کل", Value = Money(revenueUsd), Detail = "Sales revenue", Icon = "bi-cart-check", ToneClass = "finance-positive" },
                new() { Label = "قیمت خرید", Value = Money(pnl.PurchaseRows.Sum(r => r.PurchaseValueUsd)), Detail = "Purchase cost", Icon = "bi-box-arrow-in-down", ToneClass = "" },
                new() { Label = "مصارف", Value = Money(expenseUsd), Detail = "Official expenses", Icon = "bi-receipt", ToneClass = "finance-negative" },
                new() { Label = "سود خالص", Value = Money(revenueUsd - pnl.PurchaseRows.Sum(r => r.PurchaseValueUsd) - expenseUsd - pnl.PurchaseRows.Sum(r => r.LossCostUsd) + pnl.TotalExchangeGainUsd - pnl.TotalExchangeLossUsd), Detail = "Net profit", Icon = "bi-graph-up-arrow", ToneClass = revenueUsd - pnl.PurchaseRows.Sum(r => r.PurchaseValueUsd) - expenseUsd - pnl.PurchaseRows.Sum(r => r.LossCostUsd) + pnl.TotalExchangeGainUsd - pnl.TotalExchangeLossUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = "حرکت نقدی", Value = Money(cashInUsd - cashOutUsd), Detail = "Payment inflow - outflow", Icon = "bi-cash-stack", ToneClass = cashInUsd - cashOutUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = "مغایرت‌ها", Value = warnings.TotalIssueCount.ToString("N0"), Detail = "Open warnings", Icon = "bi-exclamation-triangle", ToneClass = warnings.TotalIssueCount == 0 ? "finance-positive" : "finance-negative" }
            ]
        };
    }

    private async Task<CashFlowReportViewModel> BuildCashFlowReportAsync(ManagementReportFilterViewModel filter)
    {
        var payments = await ApplyPaymentFilters(_db.PaymentTransactions.AsNoTracking(), filter)
            .Select(p => new
            {
                p.PaymentKind,
                p.Direction,
                p.AmountUsd,
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : "-",
                p.Currency
            })
            .ToListAsync();

        var rows = payments
            .GroupBy(p => CashFlowGroupName(p.PaymentKind, p.Direction))
            .Select(g => new CashFlowReportRowViewModel
            {
                GroupName = g.Key,
                InflowUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd),
                OutflowUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd),
                Count = g.Count()
            })
            .OrderByDescending(r => Math.Abs(r.NetUsd))
            .ToList();

        var accountRows = payments
            .GroupBy(p => new { p.CashAccountName, p.Currency })
            .Select(g => new CashFlowAccountRowViewModel
            {
                CashAccountName = g.Key.CashAccountName,
                Currency = g.Key.Currency,
                InflowUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd),
                OutflowUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd)
            })
            .OrderByDescending(r => Math.Abs(r.NetUsd))
            .ToList();

        var totalInflowUsd = rows.Sum(r => r.InflowUsd);
        var totalOutflowUsd = rows.Sum(r => r.OutflowUsd);
        var netCashFlowUsd = totalInflowUsd - totalOutflowUsd;

        return new CashFlowReportViewModel
        {
            Filter = filter,
            Rows = rows,
            AccountRows = accountRows,
            Metrics =
            [
                new() { Label = "ورودی نقدی", Value = Money(totalInflowUsd), Detail = "Receipts", Icon = "bi-arrow-down-circle", ToneClass = "finance-positive" },
                new() { Label = "خروجی نقدی", Value = Money(totalOutflowUsd), Detail = "Payments", Icon = "bi-arrow-up-circle", ToneClass = "finance-negative" },
                new() { Label = "خالص جریان نقدی", Value = Money(netCashFlowUsd), Detail = "In - Out", Icon = "bi-cash-stack", ToneClass = netCashFlowUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = "حساب‌های درگیر", Value = accountRows.Count.ToString("N0"), Detail = "Cash / Bank", Icon = "bi-bank", ToneClass = "" }
            ]
        };
    }

    private async Task<ReceivablesPayablesReportViewModel> BuildReceivablesPayablesReportAsync(ManagementReportFilterViewModel filter)
    {
        var ledgerQuery = _db.LedgerEntries.AsNoTracking().AsQueryable();
        // گزارش مانده باید ماندهٔ تجمعی تا تاریخ پایان را نشان دهد؛ FromDate فقط مرز دورهٔ
        // گردش است و نباید Opening Balance را از مانده حذف کند.
        if (filter.ToDate.HasValue) ledgerQuery = ledgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value.Date);
        if (filter.ContractId.HasValue) ledgerQuery = ledgerQuery.Where(l => l.ContractId == filter.ContractId.Value);

        var rows = new List<ReceivablePayableRowViewModel>();

        if (!filter.SupplierId.HasValue)
        {
            var customerRows = await ledgerQuery
                .Where(l => l.CustomerId.HasValue && (!filter.CustomerId.HasValue || l.CustomerId == filter.CustomerId.Value))
                .GroupBy(l => new { l.CustomerId, PartyName = l.Customer != null ? l.Customer.Name : "" })
                .Select(g => new
                {
                    g.Key.CustomerId,
                    g.Key.PartyName,
                    DebitUsd = g.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
                    CreditUsd = g.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd),
                    LastEntryDate = g.Max(l => (DateTime?)l.EntryDate)
                })
                .ToListAsync();

            rows.AddRange(customerRows.Select(r => new ReceivablePayableRowViewModel
            {
                PartyType = "Customer",
                PartyId = r.CustomerId,
                PartyName = string.IsNullOrWhiteSpace(r.PartyName) ? "-" : r.PartyName,
                // Policy مشتری: Credit قدیمی Ledger = فروش/Statement Debit و
                // Debit قدیمی Ledger = دریافت/Statement Credit.
                DebitUsd = r.CreditUsd,
                CreditUsd = r.DebitUsd,
                LastEntryDate = r.LastEntryDate,
                BalanceKind = r.DebitUsd - r.CreditUsd >= 0m ? "اعتبار / پیش‌پرداخت مشتری" : "طلب از مشتری",
                DetailsController = "Customers"
            }));
        }

        if (!filter.CustomerId.HasValue)
        {
            var supplierRows = await ledgerQuery
                .Where(l => l.SupplierId.HasValue && (!filter.SupplierId.HasValue || l.SupplierId == filter.SupplierId.Value))
                .GroupBy(l => new { l.SupplierId, PartyName = l.Supplier != null ? l.Supplier.Name : "" })
                .Select(g => new
                {
                    g.Key.SupplierId,
                    g.Key.PartyName,
                    DebitUsd = g.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
                    CreditUsd = g.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd),
                    LastEntryDate = g.Max(l => (DateTime?)l.EntryDate)
                })
                .ToListAsync();

            rows.AddRange(supplierRows.Select(r => new ReceivablePayableRowViewModel
            {
                PartyType = "Supplier",
                PartyId = r.SupplierId,
                PartyName = string.IsNullOrWhiteSpace(r.PartyName) ? "-" : r.PartyName,
                // Policy تأمین‌کننده: Credit قدیمی Ledger = بار/Statement Debit و
                // Debit قدیمی Ledger = پرداخت/Statement Credit.
                DebitUsd = r.CreditUsd,
                CreditUsd = r.DebitUsd,
                LastEntryDate = r.LastEntryDate,
                BalanceKind = r.DebitUsd - r.CreditUsd >= 0m ? "پیش‌پرداخت تأمین‌کننده" : "بدهی به تأمین‌کننده",
                DetailsController = "Suppliers"
            }));

            var serviceRows = await ledgerQuery
                .Where(l => l.ServiceProviderId.HasValue)
                .GroupBy(l => new { l.ServiceProviderId, PartyName = l.ServiceProvider != null ? l.ServiceProvider.Name : "" })
                .Select(g => new
                {
                    g.Key.ServiceProviderId,
                    g.Key.PartyName,
                    DebitUsd = g.Where(l => l.Side == LedgerSide.Debit).Sum(l => l.AmountUsd),
                    CreditUsd = g.Where(l => l.Side == LedgerSide.Credit).Sum(l => l.AmountUsd),
                    LastEntryDate = g.Max(l => (DateTime?)l.EntryDate)
                })
                .ToListAsync();

            rows.AddRange(serviceRows.Select(r => new ReceivablePayableRowViewModel
            {
                PartyType = "ServiceProvider",
                PartyId = r.ServiceProviderId,
                PartyName = string.IsNullOrWhiteSpace(r.PartyName) ? "-" : r.PartyName,
                // Policy شرکت خدماتی: Credit قدیمی Ledger = خدمت/Statement Debit و
                // Debit قدیمی Ledger = پرداخت/Statement Credit.
                DebitUsd = r.CreditUsd,
                CreditUsd = r.DebitUsd,
                LastEntryDate = r.LastEntryDate,
                BalanceKind = r.DebitUsd - r.CreditUsd >= 0m ? "پیش‌پرداخت خدماتی" : "بدهی خدماتی",
                DetailsController = "ServiceProviders"
            }));
        }

        if (!filter.CustomerId.HasValue && !filter.SupplierId.HasValue)
        {
            var sarrafPaymentQuery = _db.PaymentTransactions.AsNoTracking().Where(p => p.SarrafId.HasValue);
            var sarrafSettlementQuery = _db.SarrafSettlements.AsNoTracking()
                .Where(s => s.Status == SarrafSettlementStatus.Posted);
            var sarrafViaQuery = _db.LedgerEntries.AsNoTracking()
                .Where(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType);
            if (filter.ToDate.HasValue)
            {
                var end = filter.ToDate.Value.Date.AddDays(1);
                sarrafPaymentQuery = sarrafPaymentQuery.Where(p => p.PaymentDate < end);
                sarrafSettlementQuery = sarrafSettlementQuery.Where(s => s.SettlementDate < end);
                sarrafViaQuery = sarrafViaQuery.Where(l => l.EntryDate < end);
            }
            if (filter.ContractId.HasValue)
            {
                sarrafPaymentQuery = sarrafPaymentQuery.Where(p => p.ContractId == filter.ContractId.Value);
                sarrafSettlementQuery = sarrafSettlementQuery.Where(s => s.ContractId == filter.ContractId.Value);
                sarrafViaQuery = sarrafViaQuery.Where(l => l.ContractId == filter.ContractId.Value);
            }

            var sarrafPayments = await sarrafPaymentQuery
                .Where(p => p.SarrafId.HasValue)
                .GroupBy(p => p.SarrafId!.Value)
                .Select(g => new
                {
                    SarrafId = g.Key,
                    DebitUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd),
                    CreditUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd),
                    LastEntryDate = g.Max(p => (DateTime?)p.PaymentDate)
                })
                .ToListAsync();

            var sarrafSettlements = await sarrafSettlementQuery
                .GroupBy(s => s.SarrafId)
                .Select(g => new
                {
                    SarrafId = g.Key,
                    DebitUsd = g.Where(s => s.Direction == SarrafSettlementDirection.In).Sum(s => s.SarrafChargedAmountUsd),
                    CreditUsd = g.Where(s => s.Direction == SarrafSettlementDirection.Out).Sum(s => s.SarrafChargedAmountUsd),
                    LastEntryDate = g.Max(s => (DateTime?)s.SettlementDate)
                })
                .ToListAsync();

            var sarrafVia = await sarrafViaQuery
                .GroupBy(l => l.SourceId)
                .Select(g => new
                {
                    SarrafId = g.Key,
                    CreditUsd = g.Sum(l => l.AmountUsd),
                    LastEntryDate = g.Max(l => (DateTime?)l.EntryDate)
                })
                .ToListAsync();

            var sarrafIds = sarrafPayments.Select(x => x.SarrafId)
                .Concat(sarrafSettlements.Select(x => x.SarrafId))
                .Concat(sarrafVia.Select(x => x.SarrafId))
                .Distinct()
                .ToList();
            var sarrafNames = await _db.Sarrafs.AsNoTracking()
                .Where(s => sarrafIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

            rows.AddRange(sarrafIds.Select(sarrafId =>
            {
                var payment = sarrafPayments.FirstOrDefault(x => x.SarrafId == sarrafId);
                var settlement = sarrafSettlements.FirstOrDefault(x => x.SarrafId == sarrafId);
                var via = sarrafVia.FirstOrDefault(x => x.SarrafId == sarrafId);
                var debit = (payment?.DebitUsd ?? 0m) + (settlement?.DebitUsd ?? 0m);
                var credit = (payment?.CreditUsd ?? 0m) + (settlement?.CreditUsd ?? 0m) + (via?.CreditUsd ?? 0m);
                return new ReceivablePayableRowViewModel
                {
                    PartyType = "Sarraf",
                    PartyId = sarrafId,
                    PartyName = sarrafNames.GetValueOrDefault(sarrafId, "-"),
                    DebitUsd = debit,
                    CreditUsd = credit,
                    LastEntryDate = new[] { payment?.LastEntryDate, settlement?.LastEntryDate, via?.LastEntryDate }.Max(),
                    BalanceKind = credit - debit >= 0m ? "قابل پرداخت به صراف" : "طلب شرکت از صراف",
                    DetailsController = "Sarrafs"
                };
            }));
        }

        rows = rows
            .Where(r => r.DebitUsd != 0m || r.CreditUsd != 0m)
            .OrderByDescending(r => Math.Abs(r.BalanceUsd))
            .ToList();

        var model = new ReceivablesPayablesReportViewModel
        {
            Filter = filter,
            Rows = rows
        };

        return new ReceivablesPayablesReportViewModel
        {
            Filter = filter,
            Rows = rows,
            Metrics =
            [
                new() { Label = "طلب مشتریان", Value = Money(model.CustomerReceivableUsd), Detail = "Customer receivable", Icon = "bi-person-lines-fill", ToneClass = "finance-positive" },
                new() { Label = "بدهی تأمین‌کنندگان", Value = Money(model.SupplierPayableUsd), Detail = "Supplier payable", Icon = "bi-building-check", ToneClass = "finance-negative" },
                new() { Label = "بدهی خدماتی", Value = Money(model.ServiceProviderPayableUsd), Detail = "Service providers", Icon = "bi-building-gear", ToneClass = "finance-negative" },
                new() { Label = "صراف‌ها", Value = Money(model.SarrafBalanceUsd), Detail = "Payment net", Icon = "bi-currency-exchange", ToneClass = model.SarrafBalanceUsd >= 0m ? "finance-negative" : "finance-positive" }
            ]
        };
    }

    private async Task<InventoryOperationsReportViewModel> BuildInventoryOperationsReportAsync(ManagementReportFilterViewModel filter)
    {
        static decimal SignedQuantity(MovementDirection direction, decimal quantityMt) => direction switch
        {
            MovementDirection.In => quantityMt,
            MovementDirection.Adjustment => quantityMt,
            MovementDirection.Out => -quantityMt,
            MovementDirection.Transfer => -quantityMt,
            _ => 0m
        };

        var movementsQuery = _db.InventoryMovements
            .AsNoTracking()
            .AsQueryable();

        if (filter.ToDate.HasValue) movementsQuery = movementsQuery.Where(m => m.MovementDate <= filter.ToDate.Value.Date);
        if (filter.ProductId.HasValue) movementsQuery = movementsQuery.Where(m => m.ProductId == filter.ProductId.Value);
        if (filter.ContractId.HasValue)
        {
            var contractId = filter.ContractId.Value;
            movementsQuery = movementsQuery.Where(m =>
                m.ContractId == contractId
                || (m.ContractId == null
                    && m.LoadingReceipt != null
                    && m.LoadingReceipt.LoadingRegister != null
                    && m.LoadingReceipt.LoadingRegister.ContractId == contractId));
        }
        if (filter.TerminalId.HasValue) movementsQuery = movementsQuery.Where(m => m.TerminalId == filter.TerminalId.Value);
        if (filter.StorageTankId.HasValue) movementsQuery = movementsQuery.Where(m => m.StorageTankId == filter.StorageTankId.Value);

        var movementRows = await movementsQuery
            .Select(m => new
            {
                ProductName = m.Product != null ? m.Product.Name : "",
                TerminalName = m.Terminal != null ? m.Terminal.Name : "",
                StorageTankCode = m.StorageTank == null
                    ? null
                    : m.StorageTank.DisplayName == null || m.StorageTank.DisplayName == ""
                        ? m.StorageTank.TankCode
                        : m.StorageTank.DisplayName,
                ContractId = m.ContractId ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                    ? m.LoadingReceipt.LoadingRegister.ContractId
                    : null),
                m.Direction,
                m.QuantityMt,
                m.MovementDate
            })
            .ToListAsync();

        var scopedRows = movementRows
            .Where(r => !filter.FromDate.HasValue || r.MovementDate >= filter.FromDate.Value.Date)
            .ToList();
        var scopedGroups = scopedRows
            .GroupBy(r => new { r.ProductName, r.TerminalName, r.StorageTankCode, r.ContractId })
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    MovementCount = g.Count(),
                    LastMovementDate = g.Max(r => (DateTime?)r.MovementDate)
                });
        var stockRows = movementRows
            .GroupBy(r => new { r.ProductName, r.TerminalName, r.StorageTankCode, r.ContractId })
            .Where(g => !filter.FromDate.HasValue || scopedGroups.ContainsKey(g.Key))
            .Select(g =>
            {
                var scoped = scopedGroups.GetValueOrDefault(g.Key);
                return new
                {
                    g.Key.ProductName,
                    g.Key.TerminalName,
                    g.Key.StorageTankCode,
                    QuantityMt = g.Sum(r => SignedQuantity(r.Direction, r.QuantityMt)),
                    MovementCount = scoped?.MovementCount ?? 0,
                    LastMovementDate = scoped?.LastMovementDate
                };
            })
            .ToList();

        var productRows = stockRows
            .GroupBy(r => r.ProductName)
            .Select(g => new InventoryOperationsRowViewModel
            {
                GroupName = g.Key,
                QuantityMt = g.Sum(r => r.QuantityMt),
                MovementCount = g.Sum(r => r.MovementCount),
                LastMovementDate = g.Max(r => r.LastMovementDate)
            })
            .OrderByDescending(r => r.QuantityMt)
            .ToList();

        var terminalRows = stockRows
            .GroupBy(r => new { r.TerminalName, r.StorageTankCode })
            .Select(g => new InventoryOperationsRowViewModel
            {
                GroupName = g.Key.TerminalName,
                SecondaryName = g.Key.StorageTankCode,
                QuantityMt = g.Sum(r => r.QuantityMt),
                MovementCount = g.Sum(r => r.MovementCount),
                LastMovementDate = g.Max(r => r.LastMovementDate)
            })
            .OrderByDescending(r => r.QuantityMt)
            .Take(12)
            .ToList();

        var loadingQuery = _db.LoadingRegisters.AsNoTracking().AsQueryable();
        if (filter.FromDate.HasValue) loadingQuery = loadingQuery.Where(l => l.LoadingDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) loadingQuery = loadingQuery.Where(l => l.LoadingDate <= filter.ToDate.Value.Date);
        if (filter.ContractId.HasValue) loadingQuery = loadingQuery.Where(l => l.ContractId == filter.ContractId.Value);

        var unreceiptedLoadingCount = await loadingQuery
            .CountAsync(l => !_db.LoadingReceipts.Any(r => r.LoadingRegisterId == l.Id));
        var activeChargeableLossCount = await _db.LossEvents
            .AsNoTracking()
            .CountAsync(l => !l.IsCancelled && l.ChargeableLossMt > 0m);
        var negativeStockCount = stockRows.Count(r => r.QuantityMt < 0m);
        var totalQuantityMt = productRows.Sum(r => r.QuantityMt);

        var warnings = new List<InventoryOperationsWarningViewModel>();
        if (unreceiptedLoadingCount > 0)
        {
            warnings.Add(new()
            {
                Title = "بارگیری بدون رسید",
                Description = "بارگیری‌هایی که هنوز رسید نهایی ندارند.",
                Count = unreceiptedLoadingCount,
                Controller = "Loading",
                Action = "Index"
            });
        }

        if (activeChargeableLossCount > 0)
        {
            warnings.Add(new()
            {
                Title = "ضایعات قابل شارژ",
                Description = "LossEventهای فعال که مقدار قابل شارژ دارند.",
                Count = activeChargeableLossCount,
                Controller = "LossEvents",
                Action = "Index"
            });
        }

        if (negativeStockCount > 0)
        {
            warnings.Add(new()
            {
                Title = "موجودی منفی",
                Description = "ترکیب محصول/ترمینال/مخزن با موجودی منفی.",
                Count = negativeStockCount,
                Controller = "Inventory",
                Action = "StockCard"
            });
        }

        return new InventoryOperationsReportViewModel
        {
            Filter = filter,
            ProductRows = productRows,
            TerminalRows = terminalRows,
            Warnings = warnings,
            Metrics =
            [
                new() { Label = "موجودی کل", Value = $"{totalQuantityMt:N4} MT", Detail = "Stock balance", Icon = "bi-box-seam", ToneClass = "" },
                new() { Label = "محصولات", Value = productRows.Count.ToString("N0"), Detail = "Products with stock", Icon = "bi-droplet", ToneClass = "" },
                new() { Label = "مخزن/ترمینال", Value = terminalRows.Count.ToString("N0"), Detail = "Storage groups", Icon = "bi-database", ToneClass = "" },
                new() { Label = "هشدار عملیاتی", Value = warnings.Sum(w => w.Count).ToString("N0"), Detail = "Needs review", Icon = "bi-exclamation-triangle", ToneClass = warnings.Any() ? "finance-negative" : "finance-positive" }
            ]
        };
    }

    private async Task<ReportsWarningsViewModel> BuildReportsWarningsAsync()
    {
        var paymentSourceTypes = Enum.GetNames<PaymentKind>();
        var paymentIdsWithLedger = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => paymentSourceTypes.Contains(l.SourceType))
            .Select(l => l.SourceId)
            .Distinct()
            .ToListAsync();

        var salesWithoutLedger = await _db.SalesTransactions
            .AsNoTracking()
            .CountAsync(s => !s.IsCancelled && !_db.LedgerEntries.Any(l => l.SourceType == "Sale" && l.SourceId == s.Id));
        var expensesWithoutLedger = await _db.ExpenseTransactions
            .AsNoTracking()
            .CountAsync(e => !e.IsCancelled && !_db.LedgerEntries.Any(l => l.SourceType == "Expense" && l.SourceId == e.Id));
        var paymentsWithoutLedger = await _db.PaymentTransactions
            .AsNoTracking()
            .CountAsync(p => !p.LedgerEntryId.HasValue && !paymentIdsWithLedger.Contains(p.Id));
        var sarrafWithoutLedger = await _db.SarrafSettlements
            .AsNoTracking()
            .CountAsync(s => s.Status == SarrafSettlementStatus.Posted && !s.LedgerEntryId.HasValue);
        var unvaluedLosses = await _db.LossEvents
            .AsNoTracking()
            .CountAsync(l => !l.IsCancelled
                && l.ChargeableLossMt > 0m
                && l.LoadingRegisterId.HasValue
                && l.LoadingRegister != null
                && !l.LoadingRegister.LoadingPriceUsd.HasValue);

        var items = new List<ReportsWarningItemViewModel>
        {
            new() { Title = "فروش بدون ledger", Description = "فروش‌های قطعی که رکورد دفتر کل متناظر ندارند.", Count = salesWithoutLedger, Severity = "danger", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "مصرف بدون ledger", Description = "مصارف ثبت‌شده که در دفتر کل نیامده‌اند.", Count = expensesWithoutLedger, Severity = "danger", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "پرداخت بدون ledger", Description = "دریافت/پرداخت‌هایی که سند دفتر کل ندارند.", Count = paymentsWithoutLedger, Severity = "warning", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "تسویه صراف بدون ledger", Description = "تسویه‌های ثبت‌شده صراف که ledger تأمین‌کننده ندارند.", Count = sarrafWithoutLedger, Severity = "warning", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "ضایعات بدون قیمت", Description = "ضایعات قابل شارژ که قیمت بارگیری برای ارزش‌گذاری ندارند.", Count = unvaluedLosses, Severity = "warning", Controller = "Reports", Action = "ContractPnl" }
        };

        items = items.Where(i => i.Count > 0).ToList();

        return new ReportsWarningsViewModel
        {
            Items = items,
            Metrics =
            [
                new() { Label = "کل موارد باز", Value = items.Sum(i => i.Count).ToString("N0"), Detail = "Open issues", Icon = "bi-exclamation-triangle", ToneClass = items.Any() ? "finance-negative" : "finance-positive" },
                new() { Label = "ledger", Value = (salesWithoutLedger + expensesWithoutLedger + paymentsWithoutLedger + sarrafWithoutLedger).ToString("N0"), Detail = "Ledger issues", Icon = "bi-journal-x", ToneClass = salesWithoutLedger + expensesWithoutLedger + paymentsWithoutLedger + sarrafWithoutLedger > 0 ? "finance-negative" : "finance-positive" },
                new() { Label = "P&L", Value = unvaluedLosses.ToString("N0"), Detail = "Unvalued losses", Icon = "bi-graph-up", ToneClass = unvaluedLosses > 0 ? "finance-negative" : "finance-positive" }
            ]
        };
    }

    [EnableRateLimiting(RateLimitPolicies.HeavyReport)]
    public async Task<IActionResult> ContractPnl([FromQuery] ManagementReportFilterViewModel? filter = null)
    {
        filter ??= new ManagementReportFilterViewModel();
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true);
        return View(await BuildContractPnlAsync(filter));
    }

    private async Task<ContractPnlReportViewModel> BuildContractPnlAsync(ManagementReportFilterViewModel filter)
    {
        // ── Purchase contracts ────────────────────────────────────────────
        var purchaseQuery = _db.Contracts.AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase);

        if (filter.ProductId.HasValue)  purchaseQuery = purchaseQuery.Where(c => c.ProductId == filter.ProductId.Value);
        if (filter.SupplierId.HasValue) purchaseQuery = purchaseQuery.Where(c => c.SupplierId == filter.SupplierId.Value);
        if (filter.ContractId.HasValue) purchaseQuery = purchaseQuery.Where(c => c.Id == filter.ContractId.Value);
        if (filter.FromDate.HasValue)   purchaseQuery = purchaseQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)     purchaseQuery = purchaseQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var purchaseContracts = await purchaseQuery
            .OrderByDescending(c => c.ContractDate)
            .Select(c => new
            {
                c.Id, c.ContractNumber, c.Status, c.QuantityMt, c.UnitPriceUsd, c.ManualFinalPriceUsd,
                ProductName = c.Product != null ? c.Product.Name : "",
                CounterpartyName = c.Supplier != null ? c.Supplier.Name : null
            })
            .ToListAsync();

        var purchaseIds = purchaseContracts.Select(c => c.Id).ToList();
        var purchaseFinalPriceById = purchaseContracts.ToDictionary(
            c => c.Id,
            c => ResolveContractFinalPrice(c.ManualFinalPriceUsd, c.UnitPriceUsd));

        decimal? ResolveEffectiveLoadingPriceUsd(int contractId, decimal? loadingPriceUsd)
            => HasValidLoadingPrice(loadingPriceUsd)
                ? loadingPriceUsd
                : purchaseFinalPriceById.TryGetValue(contractId, out var finalPriceUsd)
                    ? finalPriceUsd
                    : null;

        var loadingAggById = purchaseIds.Count == 0
            ? new Dictionary<int, PurchaseAggregationSnapshot>()
            : await _purchaseAggregation.AggregateForContractsAsync(purchaseIds, purchaseFinalPriceById);

        var directSaleQuery = _db.LoadingReceiptAllocations.AsNoTracking()
            .Where(a => a.Destination == LoadingReceiptAllocationDestination.DirectSale
                && a.SourcePurchaseContractId.HasValue
                && purchaseIds.Contains(a.SourcePurchaseContractId.Value)
                && a.SalesTransactionId.HasValue
                && a.SalesTransaction != null
                && !a.SalesTransaction.IsCancelled);

        var directSaleAggById = purchaseIds.Count == 0
            ? new Dictionary<int, (decimal TotalSoldMt, decimal TotalRevenueUsd, int QuantityMismatchCount)>()
            : await directSaleQuery
                .GroupBy(a => a.SourcePurchaseContractId!.Value)
                .Select(g => new
                {
                    ContractId = g.Key,
                    TotalSoldMt = g.Sum(a => a.SalesTransaction!.QuantityMt),
                    TotalRevenueUsd = g.Sum(a => a.SalesTransaction!.TotalUsd),
                    QuantityMismatchCount = g.Count(a => a.QuantityMt != a.SalesTransaction!.QuantityMt)
                })
                .ToDictionaryAsync(
                    x => x.ContractId,
                    x => (x.TotalSoldMt, x.TotalRevenueUsd, x.QuantityMismatchCount));

        // TerminalStock sales — sales whose stock-out InventoryMovement is tied to one of these purchase contracts.
        // De-duplicate against DirectSale allocations so a sale never contributes revenue twice.
        var directSaleSaleIds = purchaseIds.Count == 0
            ? []
            : await directSaleQuery
                .Select(a => a.SalesTransactionId!.Value)
                .Distinct()
                .ToArrayAsync();

        var stockMovementQuery = _db.InventoryMovements.AsNoTracking()
            .Where(m => m.Direction == MovementDirection.Out
                && m.SalesTransactionId.HasValue
                && m.ContractId.HasValue
                && purchaseIds.Contains(m.ContractId.Value));

        if (directSaleSaleIds.Length > 0)
        {
            stockMovementQuery = stockMovementQuery
                .Where(m => !directSaleSaleIds.Contains(m.SalesTransactionId!.Value));
        }

        var stockSaleAggById = purchaseIds.Count == 0
            ? new Dictionary<int, (decimal TotalSoldMt, decimal TotalRevenueUsd)>()
            : await stockMovementQuery
                .Select(m => new { ContractId = m.ContractId!.Value, SaleId = m.SalesTransactionId!.Value })
                .Distinct()
                .Join(
                    _db.SalesTransactions.AsNoTracking().Where(s => !s.IsCancelled),
                    link => link.SaleId,
                    sale => sale.Id,
                    (link, sale) => new { link.ContractId, sale.QuantityMt, sale.TotalUsd })
                .GroupBy(row => row.ContractId)
                .Select(g => new
                {
                    ContractId = g.Key,
                    TotalSoldMt = g.Sum(row => row.QuantityMt),
                    TotalRevenueUsd = g.Sum(row => row.TotalUsd)
                })
                .ToDictionaryAsync(
                    x => x.ContractId,
                    x => (x.TotalSoldMt, x.TotalRevenueUsd));

        // ── Loss valuation per purchase contract (read-only) ────────────────
        // Chargeable losses are converted to USD using the originating
        // LoadingRegister.LoadingPriceUsd (the snapshot price for that lot). Losses
        // without a priced loading (LoadingPriceUsd null/<=0) cannot be valued and
        // are reported as UnvaluedLossCount instead — they do NOT inflate cost.
        // No LossUsd column is added to LossEvent; this stays purely derived.
        var lossAggByContract = purchaseIds.Count == 0
            ? new Dictionary<int, (decimal Cost, int UnvaluedCount)>()
            : (await _db.LossEvents.AsNoTracking()
                .Where(le => !le.IsCancelled
                    && le.ChargeableLossMt > 0m
                    && le.ContractId.HasValue
                    && purchaseIds.Contains(le.ContractId!.Value))
                .Select(le => new
                {
                    ContractId = le.ContractId!.Value,
                    le.ChargeableLossMt,
                    le.TransportLegId,
                    LoadingPriceUsd = le.LoadingRegisterId.HasValue && le.LoadingRegister != null
                        ? le.LoadingRegister.LoadingPriceUsd
                        : null
                })
                .ToListAsync())
                .GroupBy(x => x.ContractId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var rows = g
                            .Select(x => new
                            {
                                x.ChargeableLossMt,
                                EffectiveLoadingPriceUsd = x.TransportLegId.HasValue
                                    ? loadingAggById.TryGetValue(x.ContractId, out var agg)
                                        ? agg.WeightedAveragePurchasePriceUsd
                                        : null
                                    : ResolveEffectiveLoadingPriceUsd(x.ContractId, x.LoadingPriceUsd)
                            })
                            .ToList();

                        return (
                            Cost: rows.Where(x => HasValidLoadingPrice(x.EffectiveLoadingPriceUsd))
                                .Sum(x => x.ChargeableLossMt * x.EffectiveLoadingPriceUsd!.Value),
                            UnvaluedCount: rows.Count(x => !HasValidLoadingPrice(x.EffectiveLoadingPriceUsd))
                        );
                    });

        // ── Deferred tank settlement: provisional vs final P&L (read-only) ──
        // A purchase contract's P&L stays "provisional" while any of its receipts
        // recorded as DeferredTankSettlement still hold positive book balance in a
        // tank — i.e. the final loss has not yet been settled. Derived from stock;
        // no stored flag. Once the tank is settled/emptied the balance drops to 0
        // and the contract becomes "final".
        var pendingTankSettlementByContract = new Dictionary<int, decimal>();
        if (purchaseIds.Count > 0)
        {
            var deferredPairs = await _db.LoadingReceipts.AsNoTracking()
                .Where(r => r.LossMode == ReceiptLossMode.DeferredTankSettlement
                    && r.ReceiptDestination == LoadingReceiptDestination.ToInventory
                    && r.StorageTankId != null
                    && r.LoadingRegister != null
                    && purchaseIds.Contains(r.LoadingRegister.ContractId))
                .Select(r => new
                {
                    ContractId = r.LoadingRegister!.ContractId,
                    StorageTankId = r.StorageTankId!.Value
                })
                .Distinct()
                .ToListAsync();

            if (deferredPairs.Count > 0)
            {
                var pairTankIds = deferredPairs.Select(p => p.StorageTankId).Distinct().ToList();
                var tankContractBalances = (await _db.InventoryMovements.AsNoTracking()
                    .Where(m => m.StorageTankId != null && pairTankIds.Contains(m.StorageTankId!.Value))
                    .Select(m => new
                    {
                        StorageTankId = m.StorageTankId!.Value,
                        EffectiveContractId = m.ContractId
                            ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                                ? (int?)m.LoadingReceipt.LoadingRegister.ContractId
                                : null),
                        m.Direction,
                        m.QuantityMt
                    })
                    .ToListAsync())
                    .Where(m => m.EffectiveContractId.HasValue)
                    .GroupBy(m => new { m.StorageTankId, ContractId = m.EffectiveContractId!.Value })
                    .ToDictionary(
                        g => (g.Key.StorageTankId, g.Key.ContractId),
                        g => g.Sum(m =>
                            m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
                                ? m.QuantityMt
                                : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                                    ? -m.QuantityMt
                                    : 0m));

                foreach (var pair in deferredPairs
                    .Select(p => (p.ContractId, p.StorageTankId))
                    .Distinct())
                {
                    if (tankContractBalances.TryGetValue((pair.StorageTankId, pair.ContractId), out var balance)
                        && balance > 0m)
                    {
                        pendingTankSettlementByContract.TryGetValue(pair.ContractId, out var existing);
                        pendingTankSettlementByContract[pair.ContractId] = existing + balance;
                    }
                }
            }
        }

        // ── ExpenseTransaction totals per purchase contract ─────────────────
        // Generic expenses recorded against a Purchase contract (ContractId == purchase.Id)
        // — e.g. commission, customs batch entries, ad-hoc shipment-or-dispatch costs.
        // LoadingRegister inline expenses (Transport/Warehouse/Other/Railway) are stored on
        // the LoadingRegister entity and are NOT also written as ExpenseTransaction rows
        // (verified across LoadingController), so summing both is not double-counting.
        // CustomsDeclaration likewise has its own table; ExpenseTransaction never mirrors it.
        // Cancelled rows and rows tied to Sales contracts are excluded.
        var expenseRows = purchaseIds.Count == 0
            ? new List<ContractPnlExpenseRow>()
            : await _db.ExpenseTransactions.AsNoTracking()
                .Where(e => !e.IsCancelled
                    && e.ContractId.HasValue
                    && purchaseIds.Contains(e.ContractId.Value))
                .Select(e => new ContractPnlExpenseRow(
                    e.ContractId!.Value,
                    e.AmountUsd,
                    e.Description,
                    e.ExpenseType != null ? e.ExpenseType.Code : null,
                    e.ExpenseType != null ? e.ExpenseType.Name : null,
                    e.ExpenseType != null ? e.ExpenseType.NamePersian : null))
                .ToListAsync();

        var generalExpenseByContract = purchaseIds.Count == 0
            ? new Dictionary<int, decimal>()
            : expenseRows
                .GroupBy(e => e.ContractId)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.AmountUsd));

        var contractsWithOfficialWagonRent = expenseRows
            .Where(e => ExpenseClassification.IsWagonRent(
                e.ExpenseTypeCode,
                e.ExpenseTypeName,
                e.ExpenseTypeNamePersian,
                e.Description))
            .Select(e => e.ContractId)
            .ToHashSet();

        var sarrafDifferenceRows = purchaseIds.Count == 0
            ? []
            : await _db.SarrafSettlements.AsNoTracking()
                .Where(s => s.Status == SarrafSettlementStatus.Posted
                    && s.ContractId.HasValue
                    && purchaseIds.Contains(s.ContractId.Value))
                .Select(s => new
                {
                    ContractId = s.ContractId!.Value,
                    s.DifferenceType,
                    s.DifferenceAmountUsd
                })
                .ToListAsync();

        var sarrafDifferenceByContract = sarrafDifferenceRows
            .GroupBy(s => s.ContractId)
            .ToDictionary(
                g => g.Key,
                g => (
                    SupplierShortfallUsd: g
                        .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.SupplierShortfall)
                        .Sum(s => Math.Abs(s.DifferenceAmountUsd)),
                    ExchangeGainUsd: g
                        .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Gain)
                        .Sum(s => Math.Abs(s.DifferenceAmountUsd)),
                    ExchangeLossUsd: g
                        .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Loss)
                        .Sum(s => Math.Abs(s.DifferenceAmountUsd))));

        // Customs totals per purchase contract via loading registers
        Dictionary<int, decimal> customsByContract = new();
        if (purchaseIds.Count > 0)
        {
            var lrMap = await _db.LoadingRegisters.AsNoTracking()
                .Where(lr => purchaseIds.Contains(lr.ContractId))
                .Select(lr => new { lr.Id, lr.ContractId })
                .ToListAsync();

            var lrIdToContract = lrMap.ToDictionary(x => x.Id, x => x.ContractId);
            var lrIdList = lrMap.Select(x => x.Id).ToList();

            var legMap = await _db.InventoryTransportLegs.AsNoTracking()
                .Where(l => purchaseIds.Contains(l.SourcePurchaseContractId))
                .Select(l => new { l.Id, ContractId = l.SourcePurchaseContractId })
                .ToListAsync();
            var legIdToContract = legMap.ToDictionary(x => x.Id, x => x.ContractId);
            var legIdList = legMap.Select(x => x.Id).ToList();

            if (lrIdList.Count > 0 || legIdList.Count > 0)
            {
                var customsRows = await _db.CustomsDeclarations.AsNoTracking()
                    .Where(cd =>
                        (cd.LoadingRegisterId.HasValue && lrIdList.Contains(cd.LoadingRegisterId.Value))
                        || (cd.TransportLegId.HasValue && legIdList.Contains(cd.TransportLegId.Value)))
                    .Select(cd => new { cd.LoadingRegisterId, cd.TransportLegId, cd.TotalUsd })
                    .ToListAsync();

                foreach (var row in customsRows)
                {
                    if (row.LoadingRegisterId.HasValue
                        && lrIdToContract.TryGetValue(row.LoadingRegisterId.Value, out var loadingContractId))
                    {
                        customsByContract[loadingContractId] = customsByContract.GetValueOrDefault(loadingContractId) + row.TotalUsd;
                    }

                    if (row.TransportLegId.HasValue
                        && legIdToContract.TryGetValue(row.TransportLegId.Value, out var transportContractId))
                    {
                        customsByContract[transportContractId] = customsByContract.GetValueOrDefault(transportContractId) + row.TotalUsd;
                    }
                }
            }
        }

        var purchaseRows = purchaseContracts.Select(c =>
        {
            loadingAggById.TryGetValue(c.Id, out var agg);
            customsByContract.TryGetValue(c.Id, out var customs);
            generalExpenseByContract.TryGetValue(c.Id, out var generalExpense);
            lossAggByContract.TryGetValue(c.Id, out var lossAgg);
            pendingTankSettlementByContract.TryGetValue(c.Id, out var pendingSettlementMt);
            sarrafDifferenceByContract.TryGetValue(c.Id, out var sarrafDifference);
            var hasDirectSaleAgg = directSaleAggById.TryGetValue(c.Id, out var directSaleAgg);
            var hasStockSaleAgg = stockSaleAggById.TryGetValue(c.Id, out var stockSaleAgg);
            // Official wagon rent (ServiceProvider) is counted via ExpenseTransactions
            // (generalExpense). For LEGACY loadings the inline railway field mirrors that
            // same amount, so it must be dropped to avoid double counting. For row-based
            // loadings the inline railway field only mirrors "None" expense lines, which
            // never overlap with the official wagon rent — so that portion must be KEPT.
            var inlineRailwayCostUsd = contractsWithOfficialWagonRent.Contains(c.Id)
                ? agg?.LoadingRailwayExpenseUsdFromLines ?? 0m
                : agg?.LoadingRailwayExpenseUsd ?? 0m;
            var directSoldMt = hasDirectSaleAgg ? directSaleAgg.TotalSoldMt : 0m;
            var directRevenueUsd = hasDirectSaleAgg ? directSaleAgg.TotalRevenueUsd : 0m;
            var stockSoldMt = hasStockSaleAgg ? stockSaleAgg.TotalSoldMt : 0m;
            var stockRevenueUsd = hasStockSaleAgg ? stockSaleAgg.TotalRevenueUsd : 0m;
            return new ContractPnlRowViewModel
            {
                ContractId = c.Id,
                ContractNumber = c.ContractNumber,
                ContractType = ContractType.Purchase,
                Status = c.Status,
                ProductName = c.ProductName,
                CounterpartyName = c.CounterpartyName,
                ContractQuantityMt = c.QuantityMt,
                ContractUnitPriceUsd = ResolveContractFinalPrice(c.ManualFinalPriceUsd, c.UnitPriceUsd),
                TotalLoadedMt    = agg?.TotalLoadedQuantityMt ?? 0m,
                PricedLoadedMt   = agg?.PricedPurchaseQuantityMt ?? 0m,
                PendingLoadedMt  = agg?.PendingPurchaseQuantityMt ?? 0m,
                PendingLoadingCount = agg?.PendingLoadingCount ?? 0,
                PurchaseValueUsd = agg?.TraceablePurchaseCostUsd ?? 0m,
                TransportCostUsd = agg?.LoadingTransportExpenseUsd ?? 0m,
                WarehouseCostUsd = agg?.LoadingWarehouseExpenseUsd  ?? 0m,
                OtherCostUsd     = agg?.LoadingOtherExpenseUsd      ?? 0m,
                RailwayCostUsd   = inlineRailwayCostUsd,
                CustomsCostUsd   = customs,
                GeneralExpenseCostUsd = generalExpense,
                LossCostUsd = lossAgg.Cost,
                UnvaluedLossCount = lossAgg.UnvaluedCount,
                PendingSettlementQuantityMt = pendingSettlementMt,
                SarrafSupplierShortfallUsd = sarrafDifference.SupplierShortfallUsd,
                ExchangeGainUsd = sarrafDifference.ExchangeGainUsd,
                ExchangeLossUsd = sarrafDifference.ExchangeLossUsd,
                TotalSoldMt = directSoldMt + stockSoldMt,
                TotalRevenueUsd = directRevenueUsd + stockRevenueUsd,
                DirectSaleQuantityMismatchCount = hasDirectSaleAgg ? directSaleAgg.QuantityMismatchCount : 0
            };
        }).ToList();

        // ── Sale contracts ────────────────────────────────────────────────
        var saleQuery = _db.Contracts.AsNoTracking()
            .Where(c => c.ContractType == ContractType.Sale);

        if (filter.ProductId.HasValue)  saleQuery = saleQuery.Where(c => c.ProductId == filter.ProductId.Value);
        if (filter.CustomerId.HasValue) saleQuery = saleQuery.Where(c => c.CustomerId == filter.CustomerId.Value);
        if (filter.ContractId.HasValue) saleQuery = saleQuery.Where(c => c.Id == filter.ContractId.Value);
        if (filter.FromDate.HasValue)   saleQuery = saleQuery.Where(c => c.ContractDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)     saleQuery = saleQuery.Where(c => c.ContractDate <= filter.ToDate.Value);

        var saleContracts = await saleQuery
            .OrderByDescending(c => c.ContractDate)
            .Select(c => new
            {
                c.Id, c.ContractNumber, c.Status, c.QuantityMt, c.UnitPriceUsd,
                ProductName = c.Product != null ? c.Product.Name : "",
                CounterpartyName = c.Customer != null ? c.Customer.Name : null
            })
            .ToListAsync();

        var saleIds = saleContracts.Select(c => c.Id).ToList();

        var salesAgg = saleIds.Count == 0
            ? []
            : await _db.SalesTransactions.AsNoTracking()
                .Where(s => !s.IsCancelled && s.ContractId.HasValue && saleIds.Contains(s.ContractId.Value))
                .GroupBy(s => s.ContractId!.Value)
                .Select(g => new { ContractId = g.Key, TotalSoldMt = g.Sum(s => s.QuantityMt), TotalRevenue = g.Sum(s => s.TotalUsd) })
                .ToListAsync();

        var salesAggById = salesAgg.ToDictionary(x => x.ContractId);

        var saleRows = saleContracts.Select(c =>
        {
            salesAggById.TryGetValue(c.Id, out var agg);
            return new ContractPnlRowViewModel
            {
                ContractId = c.Id,
                ContractNumber = c.ContractNumber,
                ContractType = ContractType.Sale,
                Status = c.Status,
                ProductName = c.ProductName,
                CounterpartyName = c.CounterpartyName,
                ContractQuantityMt = c.QuantityMt,
                ContractUnitPriceUsd = c.UnitPriceUsd,
                TotalSoldMt      = agg?.TotalSoldMt  ?? 0m,
                TotalRevenueUsd  = agg?.TotalRevenue ?? 0m
            };
        }).ToList();

        return new ContractPnlReportViewModel
        {
            Filter = filter,
            PurchaseRows = purchaseRows,
            SaleRows = saleRows
        };
    }

    private async Task PopulateLookupsAsync(
        ManagementReportFilterViewModel filter,
        bool includeCustomers = false,
        bool includeSuppliers = false,
        bool includeInventory = false)
    {
        var productLookups = await GetCachedLookupAsync(
            "reports:lookups:products:v1",
            () => _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new LookupOption(p.Id, p.Name))
                .ToListAsync());
        ViewBag.Products = new SelectList(
            productLookups,
            "Id",
            "Name",
            filter.ProductId);

        var contractLookupRows = await _db.Contracts
            .AsNoTracking()
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Select(c => new
            {
                c.Id,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        ViewBag.Contracts = new SelectList(
            contractLookupRows
                .Select(c => new ContractLookupOption(
                    c.Id,
                    ContractUiText.FormatLookup(
                        c.ContractNumber,
                        c.ContractType,
                        c.ProductName,
                        ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))))
                .ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            filter.ContractId);

        if (includeCustomers)
        {
            var customerLookups = await GetCachedLookupAsync(
                "reports:lookups:customers:v1",
                () => _db.Customers.AsNoTracking()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.Name)
                    .Select(c => new LookupOption(c.Id, c.Name))
                    .ToListAsync());
            ViewBag.Customers = new SelectList(
                customerLookups,
                "Id",
                "Name",
                filter.CustomerId);
        }

        if (includeSuppliers)
        {
            var supplierLookups = await GetCachedLookupAsync(
                "reports:lookups:suppliers:v1",
                () => _db.Suppliers.AsNoTracking()
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.Name)
                    .Select(s => new LookupOption(s.Id, s.Name))
                    .ToListAsync());
            ViewBag.Suppliers = new SelectList(
                supplierLookups,
                "Id",
                "Name",
                filter.SupplierId);
        }

        if (includeInventory)
        {
            var terminalLookups = await GetCachedLookupAsync(
                "reports:lookups:terminals:v1",
                () => _db.Terminals.AsNoTracking()
                    .Where(t => t.IsActive)
                    .OrderBy(t => t.Code)
                    .Select(t => new LookupOption(t.Id, t.Name))
                    .ToListAsync());
            ViewBag.Terminals = new SelectList(
                terminalLookups,
                "Id",
                "Name",
                filter.TerminalId);
            var tankLookups = await GetCachedLookupAsync(
                "reports:lookups:storage-tanks:v2",
                async () => (await StorageTankDisplay.LoadOptionsAsync(_db.StorageTanks.AsNoTracking()
                        .OrderBy(t => t.DisplayName ?? t.TankCode)))
                    .Select(t => new TankLookupOption(t.Id, t.Display))
                    .ToList());
            ViewBag.StorageTanks = new SelectList(
                tankLookups,
                "Id",
                "Display",
                filter.StorageTankId);
        }
    }

    private static string Money(decimal value) => $"{value:N2} USD";

    private static IQueryable<PaymentTransaction> ApplyPaymentFilters(
        IQueryable<PaymentTransaction> query,
        ManagementReportFilterViewModel filter)
    {
        if (filter.FromDate.HasValue) query = query.Where(p => p.PaymentDate >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(p => p.PaymentDate <= filter.ToDate.Value.Date);
        if (filter.ContractId.HasValue) query = query.Where(p => p.ContractId == filter.ContractId.Value);
        if (filter.CustomerId.HasValue) query = query.Where(p => p.CustomerId == filter.CustomerId.Value);
        if (filter.SupplierId.HasValue) query = query.Where(p => p.SupplierId == filter.SupplierId.Value);
        if (filter.ProductId.HasValue) query = query.Where(p => p.Contract != null && p.Contract.ProductId == filter.ProductId.Value);

        return query;
    }

    private static string CashFlowGroupName(PaymentKind paymentKind, PaymentDirection direction)
        => paymentKind switch
        {
            PaymentKind.ManualReceipt when direction == PaymentDirection.In => "دریافت دستی",
            PaymentKind.ManualPayment when direction == PaymentDirection.Out => "پرداخت دستی",
            _ => PaymentKindLabels.ToPersian(paymentKind)
        };

    private static bool HasValidLoadingPrice(decimal? loadingPriceUsd)
        => loadingPriceUsd.HasValue && loadingPriceUsd.Value > 0m;

    private static decimal? ResolveContractFinalPrice(decimal? manualFinalPriceUsd, decimal? unitPriceUsd)
        => manualFinalPriceUsd.HasValue && manualFinalPriceUsd.Value > 0m
            ? manualFinalPriceUsd.Value
            : unitPriceUsd.HasValue && unitPriceUsd.Value > 0m
                ? unitPriceUsd.Value
                : null;

    /// <summary>
    /// Pair of (PurchaseContractId, SalesTransactionId) used by ContractPnl to
    /// aggregate TerminalStock sale revenue back onto the originating purchase contract.
    /// </summary>
    private sealed record ContractPnlExpenseRow(
        int ContractId,
        decimal AmountUsd,
        string? Description,
        string? ExpenseTypeCode,
        string? ExpenseTypeName,
        string? ExpenseTypeNamePersian);

}
