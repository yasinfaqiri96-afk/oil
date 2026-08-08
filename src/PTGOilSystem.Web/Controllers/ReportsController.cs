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
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IProfitAndLossService _profitAndLoss;
    private readonly IPartyBalanceReadService _partyBalances;
    private readonly IStockService _stock;
    private readonly IPreSaleReservationService _preSaleReservations;
    private readonly INegativeStockAnalysisService _negativeStock;
    private readonly Services.Accounting.ISystemCompanyProvider _systemCompany;
    private readonly IMemoryCache? _cache;

    private readonly IAfghanistanBusinessClock _businessClock;

    public ReportsController(
        ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null,
        IProfitAndLossService? profitAndLoss = null,
        IPartyBalanceReadService? partyBalances = null,
        IStockService? stock = null,
        IPreSaleReservationService? preSaleReservations = null,
        INegativeStockAnalysisService? negativeStock = null,
        IAfghanistanBusinessClock? clock = null,
        IMemoryCache? cache = null,
        Services.Accounting.ISystemCompanyProvider? systemCompany = null)
    {
        _db = db;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _profitAndLoss = profitAndLoss ?? new ProfitAndLossService(db);
        _partyBalances = partyBalances ?? new PartyBalanceReadService(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService());
        _stock = stock ?? new StockService(db);
        // یک مرجع واحد ساعت کابل برای همهٔ گزارش‌ها و خروجی‌های همین کنترلر.
        _businessClock = clock ?? new AfghanistanBusinessClock(TimeProvider.System);
        _preSaleReservations = preSaleReservations ?? new PreSaleReservationService(db, _businessClock);
        _negativeStock = negativeStock ?? new NegativeStockAnalysisService(db);
        // مرجع واحدِ «شرکت مالک سیستم» — گزارش کشتی‌ها سال‌های مالی را فقط از همین شرکت می‌خواند.
        _systemCompany = systemCompany ?? new Services.Accounting.SystemCompanyProvider(db);
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

    public IActionResult Index()
    {
        return View(new ReportHubViewModel
        {
            Groups = BuildReportHubGroups()
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

    /// <summary>
    /// مرکز گزارشات: هشت دستهٔ اصلی، هر دسته فقط چند گزارش کلیدی. هیچ گزارشی دو بار
    /// در دو دسته تکرار نمی‌شود و هیچ محاسبه‌ای اینجا انجام نمی‌گیرد — فقط مسیر.
    /// Routeهای قدیمی (InventoryOperations، Warnings و غیره) دست‌نخورده باقی می‌مانند؛
    /// این فهرست فقط تعیین می‌کند چه چیزی در مرکز گزارشات دیده شود.
    /// </summary>
    private static IReadOnlyList<ReportHubGroupViewModel> BuildReportHubGroups()
        =>
        [
            new()
            {
                TitleFa = "امروز و مدیریت",
                TitleEn = "Today & Management",
                Icon = "bi-speedometer2",
                Cards =
                [
                    new()
                    {
                        Controller = "Home", Action = "Index",
                        TitleFa = "خلاصهٔ امروز", TitleEn = "Today at a glance",
                        DescriptionFa = "کارهای امروز، هشدارها و ارقام کلیدی در یک صفحه.",
                        DescriptionEn = "Today's work, warnings and key figures on one page.",
                        Icon = "bi-house-door", ToneClass = "tone-mint"
                    },
                    new()
                    {
                        Action = nameof(CompanyOverview),
                        TitleFa = "نمای کلی مالی", TitleEn = "Company Overview",
                        DescriptionFa = "فروش، بهای تمام‌شده، مصارف و سود خالص شرکت.",
                        DescriptionEn = "Company revenue, COGS, expenses and net profit.",
                        Icon = "bi-clipboard-data", ToneClass = "tone-mint"
                    },
                    new()
                    {
                        Controller = "PeriodActivity", Action = "Index",
                        TitleFa = "فعالیت دوره", TitleEn = "Period Activity",
                        DescriptionFa = "حجم ثبت‌ها و فعالیت سیستم در هر دورهٔ مالی.",
                        DescriptionEn = "Entry volume and system activity per fiscal period.",
                        Icon = "bi-calendar-range", ToneClass = "tone-sky"
                    }
                ]
            },
            new()
            {
                TitleFa = "قراردادها و محموله‌ها",
                TitleEn = "Contracts & Shipments",
                Icon = "bi-file-earmark-text",
                Cards =
                [
                    new()
                    {
                        Controller = "ContractJourney", Action = "Index",
                        TitleFa = "مسیر قرارداد", TitleEn = "Contract Journey",
                        DescriptionFa = "از عقد قرارداد تا بارگیری، حمل، فروش و تسویه.",
                        DescriptionEn = "From contract to loading, transport, sale and settlement.",
                        Icon = "bi-signpost-split", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        Action = nameof(ContractPnl),
                        TitleFa = "سود و زیان قراردادها", TitleEn = "Contract P&L",
                        DescriptionFa = "درآمد، بهای تمام‌شده، مصارف و سود هر قرارداد.",
                        DescriptionEn = "Revenue, COGS, expenses and profit by contract.",
                        Icon = "bi-graph-up-arrow", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        Controller = "ShipmentPnl", Action = "Index",
                        TitleFa = "سود و زیان محموله‌ها", TitleEn = "Shipment P&L",
                        DescriptionFa = "درآمد و هزینهٔ هر محموله به‌تفکیک.",
                        DescriptionEn = "Revenue and cost per shipment.",
                        Icon = "bi-truck", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Action = nameof(VesselVoyages),
                        TitleFa = "گزارش کشتی‌ها", TitleEn = "Vessel Voyages",
                        DescriptionFa = "هر سفر کشتی با محصول، مقدار، Shipperها، مقصد و کرایهٔ آن.",
                        DescriptionEn = "Each vessel voyage with product, quantity, shippers, destination and freight.",
                        Icon = "bi-water", ToneClass = "tone-sky"
                    }
                ]
            },
            new()
            {
                TitleFa = "موجودی و مسیر بار",
                TitleEn = "Inventory & In-transit",
                Icon = "bi-box-seam",
                Cards =
                [
                    new()
                    {
                        Action = nameof(InventoryOperations),
                        TitleFa = "موجودی و عملیات", TitleEn = "Inventory & Operations",
                        DescriptionFa = "مقدار موجودی، گردش‌ها و مواردی که بررسی لازم دارند.",
                        DescriptionEn = "Stock quantities, movements and items needing review.",
                        Icon = "bi-box-seam", ToneClass = "tone-teal"
                    },
                    new()
                    {
                        Controller = "Inventory", Action = "StockCard",
                        TitleFa = "کارت موجودی", TitleEn = "Stock Card",
                        DescriptionFa = "ورود و خروج هر جنس با مانده در هر تاریخ.",
                        DescriptionEn = "In/out per product with running balance.",
                        Icon = "bi-card-list", ToneClass = "tone-teal"
                    },
                    new()
                    {
                        Controller = "InventoryTransportLegs", Action = "Index",
                        TitleFa = "بار در مسیر", TitleEn = "Goods in Transit",
                        DescriptionFa = "باری که از مخزن خارج شده اما هنوز تحویل نشده است.",
                        DescriptionEn = "Stock that left the tank but has not been received yet.",
                        Icon = "bi-arrow-left-right", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Action = nameof(NegativeStock),
                        TitleFa = "موجودی منفی", TitleEn = "Negative Stock",
                        DescriptionFa = "جاهایی که مانده زیر صفر رفته، با علت و سند ایجادکننده.",
                        DescriptionEn = "Scopes that went below zero, with cause and source document.",
                        Icon = "bi-exclamation-octagon", ToneClass = "tone-rose"
                    }
                ]
            },
            new()
            {
                TitleFa = "فروش و تعهدات",
                TitleEn = "Sales & Commitments",
                Icon = "bi-cart-check",
                Cards =
                [
                    new()
                    {
                        Controller = "Sales", Action = "Index",
                        TitleFa = "فروش‌ها", TitleEn = "Sales",
                        DescriptionFa = "فهرست فروش‌ها با مقدار، قیمت و مشتری.",
                        DescriptionEn = "Sales list with quantity, price and customer.",
                        Icon = "bi-cart-check", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Controller = "Sales", Action = "PreSales",
                        TitleFa = "پیش‌فروش‌ها", TitleEn = "Pre-sales",
                        DescriptionFa = "تعهدهای فروش آینده و مقدار باقی‌ماندهٔ تحویل.",
                        DescriptionEn = "Future sale commitments and remaining delivery.",
                        Icon = "bi-bookmark-check", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(SellableStock),
                        TitleFa = "موجودی قابل فروش", TitleEn = "Sellable Stock",
                        DescriptionFa = "موجودی فیزیکی منهای رزرو پیش‌فروش.",
                        DescriptionEn = "Physical stock minus active pre-sale reservation.",
                        Icon = "bi-box-arrow-up-right", ToneClass = "tone-teal"
                    },
                    new()
                    {
                        Action = nameof(PreSaleDiscrepancies),
                        TitleFa = "ناهماهنگی‌های پیش‌فروش", TitleEn = "Pre-sale Discrepancies",
                        DescriptionFa = "تحویل بیشتر از تعهد، تعهد سررسیدشده و پیش‌پرداخت مصرف‌نشده.",
                        DescriptionEn = "Over-delivery, overdue commitments and unconsumed advances.",
                        Icon = "bi-exclamation-triangle", ToneClass = "tone-rose"
                    }
                ]
            },
            new()
            {
                TitleFa = "پول و طرف‌حساب‌ها",
                TitleEn = "Money & Parties",
                Icon = "bi-wallet2",
                Cards =
                [
                    new()
                    {
                        Action = nameof(ReceivablesPayables),
                        TitleFa = "طلب و بدهی", TitleEn = "Receivables & Payables",
                        DescriptionFa = "مانده مشتریان، تأمین‌کنندگان، خدماتی‌ها و صراف‌ها.",
                        DescriptionEn = "Customer, supplier, service provider and sarraf balances.",
                        Icon = "bi-people", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(CashFlow),
                        TitleFa = "جریان پول", TitleEn = "Cash Flow",
                        DescriptionFa = "پول واقعی وارد و خارج‌شده از حساب‌های نقدی.",
                        DescriptionEn = "Actual cash in and out of the cash accounts.",
                        Icon = "bi-cash-stack", ToneClass = "tone-sky"
                    },
                    new()
                    {
                        Controller = "AccountStatements", Action = "Index",
                        TitleFa = "صورت‌حساب طرف‌حساب", TitleEn = "Party Statement",
                        DescriptionFa = "اول دوره، رسید، برد و مانده هر طرف‌حساب.",
                        DescriptionEn = "Opening, received, given and closing per party.",
                        Icon = "bi-journal-text", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        Controller = "Balance", Action = "Contracts",
                        TitleFa = "مانده قراردادها", TitleEn = "Contract Balances",
                        DescriptionFa = "مانده هر قرارداد با مشتری و تأمین‌کنندهٔ آن.",
                        DescriptionEn = "Balance per contract with its customer and supplier.",
                        Icon = "bi-scales", ToneClass = "tone-blue"
                    }
                ]
            },
            new()
            {
                TitleFa = "مصارف، کسری و سود",
                TitleEn = "Expenses, Losses & Margin",
                Icon = "bi-receipt",
                Cards =
                [
                    new()
                    {
                        Controller = "Expenses", Action = "Index",
                        TitleFa = "مصارف", TitleEn = "Expenses",
                        DescriptionFa = "مصارف ثبت‌شده به‌تفکیک نوع، قرارداد و تاریخ.",
                        DescriptionEn = "Recorded expenses by type, contract and date.",
                        Icon = "bi-receipt", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(TransportVariance),
                        TitleFa = "راپور کسری و اضافه‌بار حمل", TitleEn = "Transport Shortage & Surplus",
                        DescriptionFa = "تفاوت وزن بارگیری و تخلیهٔ هر حمل، با مجموع جداگانهٔ کسری و اضافه‌بار.",
                        DescriptionEn = "Loaded vs unloaded weight per transport, with separate shortage and surplus totals.",
                        Icon = "bi-truck", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Controller = "LossEvents", Action = "Index",
                        TitleFa = "کسری و ضایعات", TitleEn = "Shortage & Loss",
                        DescriptionFa = "کسری بار، ضایعات و مبلغ قابل مطالبهٔ هر مورد.",
                        DescriptionEn = "Shortage, loss and the chargeable amount of each case.",
                        Icon = "bi-droplet-half", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(FxDifference),
                        TitleFa = "تفاوت نرخ ارز", TitleEn = "FX Difference",
                        DescriptionFa = "سود و ضرر تفاوت نرخ ارز در حواله‌های صراف.",
                        DescriptionEn = "FX gain and loss on sarraf transfers.",
                        Icon = "bi-currency-exchange", ToneClass = "tone-sky"
                    }
                ]
            },
            new()
            {
                TitleFa = "گمرک، اسناد و کیفیت",
                TitleEn = "Customs, Documents & Quality",
                Icon = "bi-shield-check",
                Cards =
                [
                    new()
                    {
                        Controller = "CustomsDeclarations", Action = "Index",
                        TitleFa = "اظهارنامه‌های گمرکی", TitleEn = "Customs Declarations",
                        DescriptionFa = "اظهارنامه‌ها، محصولات و مصارف گمرکی هر محموله.",
                        DescriptionEn = "Declarations, products and customs costs per shipment.",
                        Icon = "bi-file-earmark-check", ToneClass = "tone-teal"
                    },
                    new()
                    {
                        Controller = "CustomsPermitTurnover", Action = "Index",
                        TitleFa = "گردش جواز گمرکی", TitleEn = "Permit Turnover",
                        DescriptionFa = "مقدار مصرف‌شده و باقی‌ماندهٔ هر جواز گمرکی.",
                        DescriptionEn = "Consumed and remaining quantity per customs permit.",
                        Icon = "bi-card-checklist", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Controller = "QualityInspections", Action = "Index",
                        TitleFa = "کیفیت و لابراتوار", TitleEn = "Quality & Laboratory",
                        DescriptionFa = "نتیجهٔ آزمایش هر بار: در انتظار، قبول یا رد.",
                        DescriptionEn = "Inspection result per load: pending, accepted or rejected.",
                        Icon = "bi-clipboard-check", ToneClass = "tone-mint"
                    }
                ]
            },
            new()
            {
                TitleFa = "حساب‌داری و تاریخچه",
                TitleEn = "Accounting & History",
                Icon = "bi-journal-text",
                Cards =
                [
                    new()
                    {
                        Controller = "Ledger", Action = "Index",
                        TitleFa = "دفتر کل", TitleEn = "Ledger",
                        DescriptionFa = "تمام اسناد مالی ثبت‌شده با منبع و مبلغ.",
                        DescriptionEn = "All posted financial entries with source and amount.",
                        Icon = "bi-journals", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        // Summary فقط endpoint‌ـی JSON برای شمارنده‌های AJAX است و صفحه ندارد؛
                        // ورودی کاربر باید Index باشد.
                        Controller = "Reconciliation", Action = "Index",
                        TitleFa = "بررسی ناهماهنگی‌ها", TitleEn = "Reconciliation",
                        DescriptionFa = "مواردی که بین عملیات، موجودی و حساب‌ها جور نیستند.",
                        DescriptionEn = "Items where operations, stock and accounts disagree.",
                        Icon = "bi-clipboard-check", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Controller = "AuditLogs", Action = "Index",
                        TitleFa = "تاریخچهٔ تغییرات", TitleEn = "Change History",
                        DescriptionFa = "چه کسی، چه وقت و چه چیزی را تغییر داده یا لغو کرده است.",
                        DescriptionEn = "Who changed or cancelled what, and when.",
                        Icon = "bi-clock-history", ToneClass = "tone-sky"
                    }
                ]
            }
        ];

    private async Task<CompanyFinancialOverviewViewModel> BuildCompanyFinancialOverviewAsync(ManagementReportFilterViewModel filter)
    {
        var companyPnl = await _profitAndLoss.BuildCompanyAsync(filter);
        var revenueUsd = companyPnl.Sales.RevenueUsd;
        var cogsUsd = companyPnl.Sales.CostOfGoodsSoldUsd;
        var expenseUsd = companyPnl.OperatingExpenseUsd;

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
            PurchaseCostUsd = cogsUsd,
            ExpenseUsd = expenseUsd,
            LossCostUsd = 0m,
            ExchangeGainUsd = companyPnl.ExchangeGainUsd,
            ExchangeLossUsd = companyPnl.ExchangeLossUsd,
            NetCashMovementUsd = cashInUsd - cashOutUsd,
            CustomerReceivableUsd = balances.CustomerReceivableUsd,
            SupplierPayableUsd = balances.SupplierPayableUsd,
            SarrafNetUsd = balances.SarrafBalanceUsd,
            WarningCount = warnings.TotalIssueCount,
            UncostedSaleCount = companyPnl.Sales.UncostedSaleCount,
            PnlConfidence = companyPnl.Sales.Confidence,
            TopContracts = topContracts,
            Metrics =
            [
                new() { Label = "فروش کل", Value = Money(revenueUsd), Detail = "Sales revenue", Icon = "bi-cart-check", ToneClass = "finance-positive" },
                new() { Label = "بهای تمام‌شده فروش", Value = Money(cogsUsd), Detail = "Realised COGS", Icon = "bi-box-arrow-in-down", ToneClass = "" },
                new() { Label = "مصارف", Value = Money(expenseUsd), Detail = "Official expenses", Icon = "bi-receipt", ToneClass = "finance-negative" },
                new() { Label = "سود خالص", Value = Money(companyPnl.NetProfitUsd), Detail = companyPnl.Sales.UncostedSaleCount == 0 ? "Net profit" : $"{companyPnl.Sales.UncostedSaleCount:N0} sale(s) need COGS review", Icon = "bi-graph-up-arrow", ToneClass = companyPnl.NetProfitUsd >= 0m ? "finance-positive" : "finance-negative" },
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
        var balanceRows = await _partyBalances.GetBalancesAsync(filter);
        var rows = balanceRows.Select(balance => new ReceivablePayableRowViewModel
        {
            PartyType = balance.PartyType.ToString(),
            PartyId = balance.PartyId,
            PartyName = balance.PartyName,
            OpeningBalanceUsd = balance.OpeningBalanceUsd,
            // The report keeps its historical Debit/Credit column names for route/UI
            // compatibility. Their values now come from the official statement
            // convention: Debit = received, Credit = given.
            DebitUsd = balance.TotalReceiptUsd,
            CreditUsd = balance.TotalOutflowUsd,
            LastEntryDate = balance.LastEntryDate,
            BalanceKind = balance.BalanceMeaning,
            DetailsController = balance.DetailsController
        }).ToList();

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
                new() { Label = "صراف‌ها", Value = Money(model.SarrafBalanceUsd), Detail = "Official statement balance", Icon = "bi-currency-exchange", ToneClass = model.SarrafBalanceUsd >= 0m ? "finance-positive" : "finance-negative" }
            ]
        };
    }

    private async Task<InventoryOperationsReportViewModel> BuildInventoryOperationsReportAsync(ManagementReportFilterViewModel filter)
    {
        var movementRows = await _stock.GetMovementSummaryAsync(
            productId: filter.ProductId,
            contractId: filter.ContractId,
            terminalId: filter.TerminalId,
            storageTankId: filter.StorageTankId,
            fromUtc: filter.FromDate?.Date,
            toUtc: filter.ToDate?.Date);
        var stockRows = movementRows
            .Where(r => !filter.FromDate.HasValue || r.MovementCount > 0)
            .Select(r => new
            {
                r.ProductName,
                r.TerminalName,
                r.StorageTankCode,
                QuantityMt = r.ClosingQuantityMt,
                r.MovementCount,
                r.LastMovementDate
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
            .CountAsync(l => !_db.LoadingReceipts.Any(r => r.LoadingRegisterId == l.Id && !r.IsCancelled));
        var activeChargeableLossCount = await _db.LossEvents
            .AsNoTracking()
            .CountAsync(l => !l.IsCancelled && l.ChargeableLossMt > 0m);
        var negativeStockCount = stockRows.Count(r => r.QuantityMt < 0m);
        var totalQuantityMt = productRows.Sum(r => r.QuantityMt);
        var canAttributeReservation = !filter.ContractId.HasValue
            && !filter.TerminalId.HasValue
            && !filter.StorageTankId.HasValue;
        decimal? reservedPreSaleMt = null;
        if (canAttributeReservation)
        {
            var activePreSales = _db.PreSaleOrders.AsNoTracking()
                .Where(o => o.Status == PreSaleOrderStatus.Confirmed
                    || o.Status == PreSaleOrderStatus.PartiallyDelivered);
            if (filter.ProductId.HasValue)
            {
                activePreSales = activePreSales.Where(o => o.ProductId == filter.ProductId.Value);
            }

            reservedPreSaleMt = await activePreSales
                .Select(o => o.QuantityMt
                    - _db.SalesTransactions
                        .Where(s => s.PreSaleOrderId == o.Id && !s.IsCancelled)
                        .Sum(s => (decimal?)s.QuantityMt).GetValueOrDefault())
                .SumAsync();
            reservedPreSaleMt = Math.Max(0m, reservedPreSaleMt.Value);
        }

        var warnings = new List<InventoryOperationsWarningViewModel>();
        if (unreceiptedLoadingCount > 0)
        {
            warnings.Add(new()
            {
                Title = "بارگیری بدون رسید",
                Description = "بارگیری‌هایی که هنوز رسید نهایی ندارند.",
                Count = unreceiptedLoadingCount,
                Controller = "Loading",
                Action = "Index",
                RouteValues = new
                {
                    contractId = filter.ContractId,
                    productId = filter.ProductId,
                    fromDate = filter.FromDate?.ToString("yyyy-MM-dd"),
                    toDate = filter.ToDate?.ToString("yyyy-MM-dd"),
                    withoutReceipt = true
                }
            });
        }

        if (activeChargeableLossCount > 0)
        {
            warnings.Add(new()
            {
                Title = "ضایعات قابل شارژ",
                Description = "رویدادهای فعال که مقدار قابل شارژ دارند.",
                Count = activeChargeableLossCount,
                Controller = "LossEvents",
                Action = "Index",
                RouteValues = new Dictionary<string, string?>
                {
                    ["Filter.ProductId"] = filter.ProductId?.ToString(),
                    ["Filter.ContractId"] = filter.ContractId?.ToString(),
                    ["Filter.FromDate"] = filter.FromDate?.ToString("yyyy-MM-dd"),
                    ["Filter.ToDate"] = filter.ToDate?.ToString("yyyy-MM-dd"),
                    ["Filter.ChargeableOnly"] = "true"
                }
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
                Action = "StockCard",
                RouteValues = new Dictionary<string, string?>
                {
                    ["Filter.ProductId"] = filter.ProductId?.ToString(),
                    ["Filter.ContractId"] = filter.ContractId?.ToString(),
                    ["Filter.TerminalId"] = filter.TerminalId?.ToString(),
                    ["Filter.StorageTankId"] = filter.StorageTankId?.ToString(),
                    ["Filter.FromDate"] = filter.FromDate?.ToString("yyyy-MM-dd"),
                    ["Filter.ToDate"] = filter.ToDate?.ToString("yyyy-MM-dd")
                }
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
                new() { Label = "موجودی کل", Value = $"{totalQuantityMt:N4} MT", Detail = UiText.T(HttpContext, "مانده فیزیکی موجودی", "Stock balance"), Icon = "bi-box-seam", ToneClass = "" },
                new() { Label = "تعهد پیش‌فروش", Value = reservedPreSaleMt.HasValue ? $"{reservedPreSaleMt:N4} MT" : "—", Detail = reservedPreSaleMt.HasValue ? UiText.T(HttpContext, "تعهد فعال تحویل‌نشده", "Active undelivered commitment") : UiText.T(HttpContext, "تعهد به قرارداد/ترمینال/مخزن قابل انتساب نیست", "Reservation cannot be attributed to contract/terminal/tank"), Icon = "bi-bookmark-check", ToneClass = "" },
                new() { Label = "موجودی قابل فروش", Value = reservedPreSaleMt.HasValue ? $"{totalQuantityMt - reservedPreSaleMt.Value:N4} MT" : "—", Detail = UiText.T(HttpContext, "موجودی فیزیکی منهای تعهد فعال پیش‌فروش", "Physical stock - active pre-sale commitment"), Icon = "bi-box-arrow-up-right", ToneClass = reservedPreSaleMt.HasValue && totalQuantityMt - reservedPreSaleMt.Value < 0m ? "finance-negative" : "" },
                new() { Label = "محصولات", Value = productRows.Count.ToString("N0"), Detail = UiText.T(HttpContext, "محصولات دارای موجودی", "Products with stock"), Icon = "bi-droplet", ToneClass = "" },
                new() { Label = "مخزن/ترمینال", Value = terminalRows.Count.ToString("N0"), Detail = UiText.T(HttpContext, "گروه‌های ذخیره‌سازی", "Storage groups"), Icon = "bi-database", ToneClass = "" },
                new() { Label = "هشدار عملیاتی", Value = warnings.Sum(w => w.Count).ToString("N0"), Detail = UiText.T(HttpContext, "نیازمند بررسی", "Needs review"), Icon = "bi-exclamation-triangle", ToneClass = warnings.Any() ? "finance-negative" : "finance-positive" }
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
                c.Id, c.ContractName, c.ContractNumber, c.Status, c.QuantityMt, c.UnitPriceUsd, c.ManualFinalPriceUsd,
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

        // ── In-transit direct sales (truck / internal transport receipt) ────
        // این فروش‌ها عمداً هیچ InventoryMovement نمی‌سازند (بار هنگام بارگیریِ موتر یا حمل قبلاً از
        // موجودی خارج شده) و LoadingReceiptAllocation نوع DirectSale هم ندارند، پس در هیچ‌کدام از دو
        // منبعِ بالا دیده نمی‌شدند و کل عایدشان — شامل عایدِ اضافه‌بارِ تخلیه — از سود و زیان قرارداد
        // خرید بیرون می‌ماند. قرارداد از Lineage واقعی گرفته می‌شود: موتر → TruckDispatch.ContractId،
        // رسید انتقال → InventoryTransportLeg.SourcePurchaseContractId. هیچ قراردادی حدس زده نمی‌شود.
        var inTransitSaleLinks = purchaseIds.Count == 0
            ? new List<(int ContractId, int SaleId)>()
            : await BuildInTransitDirectSaleLinksAsync(purchaseIds);

        var countedSaleIds = directSaleSaleIds.ToHashSet();
        if (purchaseIds.Count > 0)
        {
            foreach (var saleId in await stockMovementQuery
                .Select(m => m.SalesTransactionId!.Value)
                .Distinct()
                .ToListAsync())
            {
                countedSaleIds.Add(saleId);
            }
        }

        var inTransitSaleAggById = new Dictionary<int, (decimal TotalSoldMt, decimal TotalRevenueUsd)>();
        var newInTransitLinks = inTransitSaleLinks
            .Where(link => countedSaleIds.Add(link.SaleId))
            .ToList();
        if (newInTransitLinks.Count > 0)
        {
            var inTransitSaleIds = newInTransitLinks.Select(l => l.SaleId).ToList();
            var inTransitSales = await _db.SalesTransactions.AsNoTracking()
                .Where(s => !s.IsCancelled && inTransitSaleIds.Contains(s.Id))
                .Select(s => new { s.Id, s.QuantityMt, s.TotalUsd })
                .ToDictionaryAsync(s => s.Id);

            foreach (var link in newInTransitLinks)
            {
                if (!inTransitSales.TryGetValue(link.SaleId, out var row))
                {
                    continue;
                }

                inTransitSaleAggById.TryGetValue(link.ContractId, out var current);
                inTransitSaleAggById[link.ContractId] = (
                    current.TotalSoldMt + row.QuantityMt,
                    current.TotalRevenueUsd + row.TotalUsd);
            }
        }

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
                .Where(r => !r.IsCancelled
                    && r.LossMode == ReceiptLossMode.DeferredTankSettlement
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
            inTransitSaleAggById.TryGetValue(c.Id, out var inTransitSaleAgg);
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
                ContractName = c.ContractName,
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
                TotalSoldMt = directSoldMt + stockSoldMt + inTransitSaleAgg.TotalSoldMt,
                TotalRevenueUsd = directRevenueUsd + stockRevenueUsd + inTransitSaleAgg.TotalRevenueUsd,
                DirectSaleQuantityMismatchCount = hasDirectSaleAgg ? directSaleAgg.QuantityMismatchCount : 0,
                PnlConfidence = (agg?.PendingLoadingCount ?? 0) > 0
                    || (hasDirectSaleAgg && directSaleAgg.QuantityMismatchCount > 0)
                    || lossAgg.UnvaluedCount > 0
                        ? PnlConfidence.NeedsReview
                        : PnlConfidence.Estimated
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
                c.Id, c.ContractName, c.ContractNumber, c.Status, c.QuantityMt, c.UnitPriceUsd,
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
                .Select(g => new { ContractId = g.Key, TotalSoldMt = g.Sum(s => s.QuantityMt) })
                .ToListAsync();
        var salesAggById = salesAgg.ToDictionary(x => x.ContractId);
        var realisedPnlByContract = await _profitAndLoss.BuildForSaleContractsAsync(saleIds);

        var saleRows = saleContracts.Select(c =>
        {
            salesAggById.TryGetValue(c.Id, out var agg);
            realisedPnlByContract.TryGetValue(c.Id, out var realisedPnl);
            return new ContractPnlRowViewModel
            {
                ContractId = c.Id,
                ContractName = c.ContractName,
                ContractNumber = c.ContractNumber,
                ContractType = ContractType.Sale,
                Status = c.Status,
                ProductName = c.ProductName,
                CounterpartyName = c.CounterpartyName,
                ContractQuantityMt = c.QuantityMt,
                ContractUnitPriceUsd = c.UnitPriceUsd,
                TotalSoldMt      = agg?.TotalSoldMt  ?? 0m,
                TotalRevenueUsd  = realisedPnl?.RevenueUsd ?? 0m,
                PurchaseValueUsd = realisedPnl?.CostOfGoodsSoldUsd ?? 0m,
                UncostedSaleCount = realisedPnl?.UncostedSaleCount ?? 0,
                PnlConfidence = realisedPnl?.Confidence ?? PnlConfidence.Verified
            };
        }).ToList();

        return new ContractPnlReportViewModel
        {
            Filter = filter,
            PurchaseRows = purchaseRows,
            SaleRows = saleRows
        };
    }

    /// <summary>
    /// جفت‌های (قرارداد خرید، فروش) برای فروش‌های «در جریان» که هیچ حرکت موجودی و هیچ تخصیصِ
    /// DirectSale ندارند. هر جفت فقط از Lineage واقعیِ همان عملیات ساخته می‌شود:
    /// <list type="bullet">
    /// <item>موتر: <c>SalesTransaction.TruckDispatchId</c> (فروش قسمتی را هم پوشش می‌دهد) یا
    /// <c>TruckDispatch.SalesTransactionId</c> برای رکوردهای قدیمی → <c>TruckDispatch.ContractId</c>.</item>
    /// <item>رسید انتقال داخلی: <c>InventoryTransportReceipt.SalesTransactionId</c> →
    /// <c>InventoryTransportLeg.SourcePurchaseContractId</c>.</item>
    /// </list>
    /// خروجی می‌تواند برای یک فروش چند ردیف داشته باشد؛ فراخوان با dedupe روی شناسهٔ فروش
    /// تضمین می‌کند هیچ عایدی دو بار شمرده نشود.
    /// </summary>
    private async Task<List<(int ContractId, int SaleId)>> BuildInTransitDirectSaleLinksAsync(
        List<int> purchaseIds)
    {
        var links = new List<(int ContractId, int SaleId)>();

        var dispatchLinks = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled
                && s.TruckDispatchId.HasValue
                && s.TruckDispatch != null
                && s.TruckDispatch.Status != DispatchStatus.Cancelled
                && purchaseIds.Contains(s.TruckDispatch.ContractId))
            .Select(s => new { ContractId = s.TruckDispatch!.ContractId, SaleId = s.Id })
            .ToListAsync();
        links.AddRange(dispatchLinks.Select(x => (x.ContractId, x.SaleId)));

        var legacyDispatchLinks = await _db.TruckDispatches.AsNoTracking()
            .Where(d => d.Status != DispatchStatus.Cancelled
                && d.SalesTransactionId.HasValue
                && d.SalesTransaction != null
                && !d.SalesTransaction.IsCancelled
                && purchaseIds.Contains(d.ContractId))
            .Select(d => new { d.ContractId, SaleId = d.SalesTransactionId!.Value })
            .ToListAsync();
        links.AddRange(legacyDispatchLinks.Select(x => (x.ContractId, x.SaleId)));

        var transportReceiptLinks = await _db.InventoryTransportReceipts.AsNoTracking()
            .Where(r => !r.IsCancelled
                && r.SalesTransactionId.HasValue
                && r.SalesTransaction != null
                && !r.SalesTransaction.IsCancelled
                && r.InventoryTransportLeg != null
                && purchaseIds.Contains(r.InventoryTransportLeg.SourcePurchaseContractId))
            .Select(r => new
            {
                ContractId = r.InventoryTransportLeg!.SourcePurchaseContractId,
                SaleId = r.SalesTransactionId!.Value
            })
            .ToListAsync();
        links.AddRange(transportReceiptLinks.Select(x => (x.ContractId, x.SaleId)));

        return links;
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
                c.ContractName,
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
                        c.ContractName,
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
