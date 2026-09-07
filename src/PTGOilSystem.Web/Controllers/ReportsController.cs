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
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Reporting;
using PTGOilSystem.Web.Services.Time;
using PTGOilSystem.Web.Services.Parties;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class ReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IProfitAndLossService _profitAndLoss;
    private readonly ISaleContractAttributionReader _saleAttribution;
    private readonly IPartyBalanceReadService _partyBalances;
    private readonly ISupplierFxSettlementService _supplierFxSettlements;
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
        Services.Accounting.ISystemCompanyProvider? systemCompany = null,
        ISaleContractAttributionReader? saleAttribution = null,
        ISupplierFxSettlementService? supplierFxSettlements = null)
    {
        _db = db;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _profitAndLoss = profitAndLoss ?? new ProfitAndLossService(db);
        // انتساب فروش به قرارداد خرید فقط از همین مرجع خوانده می‌شود.
        _saleAttribution = saleAttribution ?? new SaleContractAttributionReader(db);
        _partyBalances = partyBalances ?? new PartyBalanceReadService(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            new PartyDirectory(db));
        _supplierFxSettlements = supplierFxSettlements ?? new SupplierFxSettlementService(db);
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
        await PopulateLookupsAsync(filter, includeCustomers: true, includeSuppliers: true, includeCashAccounts: true);
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
                TitleFa = "وضعیت شرکت",
                TitleEn = "Company status",
                Icon = "bi-speedometer2",
                Cards =
                [
                    new()
                    {
                        Action = nameof(CompanyOverview),
                        TitleFa = "وضعیت مالی شرکت", TitleEn = "Company Financial Status",
                        DescriptionFa = "پول نقد، طلبات، بدهی‌ها و مفاد شرکت در یک صفحه.",
                        DescriptionEn = "Cash, receivables, payables and profit on one page.",
                        Icon = "bi-clipboard-data", ToneClass = "tone-mint"
                    },
                    new()
                    {
                        Action = nameof(ContractPnl),
                        TitleFa = "مفاد قراردادها", TitleEn = "Contract Profit",
                        DescriptionFa = "مصارف، فروش و مفاد هر قرارداد خرید.",
                        DescriptionEn = "Cost, revenue and profit per purchase contract.",
                        Icon = "bi-graph-up-arrow", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        Controller = "ShipmentPnl", Action = "Index",
                        TitleFa = "مفاد محموله‌ها", TitleEn = "Shipment Profit",
                        DescriptionFa = "فروش و مصارف هر محموله به‌تفکیک.",
                        DescriptionEn = "Revenue and cost per shipment.",
                        Icon = "bi-truck", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Controller = "ContractJourney", Action = "Index",
                        TitleFa = "مسیر قرارداد", TitleEn = "Contract Journey",
                        DescriptionFa = "از عقد قرارداد تا بارگیری، حمل، فروش و تصفیه.",
                        DescriptionEn = "From contract to loading, transport, sale and settlement.",
                        Icon = "bi-signpost-split", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        // پیش‌تر این صفحه ساخته شده بود ولی هیچ لینکی به آن نمی‌رفت.
                        Action = nameof(Warnings),
                        TitleFa = "کارهای نیمه‌تمام", TitleEn = "Unfinished Work",
                        DescriptionFa = "ثبت‌هایی که ناقص مانده‌اند و تا تکمیل نشوند ارقام را ناقص نگه می‌دارند.",
                        DescriptionEn = "Incomplete entries that keep the figures incomplete until they are finished.",
                        Icon = "bi-exclamation-triangle", ToneClass = "tone-rose"
                    }
                ]
            },
            new()
            {
                TitleFa = "پول، طلبات و بدهی‌ها",
                TitleEn = "Money, Receivables & Payables",
                Icon = "bi-wallet2",
                Cards =
                [
                    new()
                    {
                        Action = nameof(ReceivablesPayables),
                        TitleFa = "طلبات و بدهی‌ها", TitleEn = "Receivables & Payables",
                        DescriptionFa = "مانده مشتری، تأمین‌کننده، شرکت خدماتی و صراف در یک جدول.",
                        DescriptionEn = "Customer, supplier, service provider and sarraf balances in one table.",
                        Icon = "bi-people", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Action = nameof(PartyAging),
                        TitleFa = "سررسید طلبات و بدهی‌ها", TitleEn = "Receivable & Payable Aging",
                        DescriptionFa = "هر حساب چند روز است بی‌حرکت مانده: تا ۳۰، ۳۱ تا ۶۰، ۶۱ تا ۹۰ و بیشتر از ۹۰ روز.",
                        DescriptionEn = "How long each account has been idle: up to 30, 31-60, 61-90 and over 90 days.",
                        Icon = "bi-hourglass-split", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Action = nameof(CashFlow),
                        TitleFa = "گردش پول", TitleEn = "Cash Movement",
                        DescriptionFa = "پول واقعی که از صندوق و بانک وارد یا خارج شده است.",
                        DescriptionEn = "Actual cash in and out of the cash and bank accounts.",
                        Icon = "bi-cash-stack", ToneClass = "tone-sky"
                    },
                    new()
                    {
                        Controller = "Balance", Action = "Customers",
                        TitleFa = "گردش حساب مشتریان", TitleEn = "Customer Accounts",
                        DescriptionFa = "فروش، مصارف و مانده هر مشتری، با لینک به گردش حساب او.",
                        DescriptionEn = "Sales, expenses and balance per customer, linked to the statement.",
                        Icon = "bi-person-lines-fill", ToneClass = "tone-amber"
                    },
                    new()
                    {
                        Controller = "Balance", Action = "Suppliers",
                        TitleFa = "گردش حساب تأمین‌کننده‌ها", TitleEn = "Supplier Accounts",
                        DescriptionFa = "خرید، پرداخت و مانده هر تأمین‌کننده، با لینک به گردش حساب او.",
                        DescriptionEn = "Purchases, payments and balance per supplier, linked to the statement.",
                        Icon = "bi-building-check", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Controller = "Balance", Action = "Contracts",
                        TitleFa = "مانده قراردادها", TitleEn = "Contract Balances",
                        DescriptionFa = "مانده هر قرارداد با مشتری و تأمین‌کنندهٔ آن.",
                        DescriptionEn = "Balance per contract with its customer and supplier.",
                        Icon = "bi-scales", ToneClass = "tone-blue"
                    },
                    new()
                    {
                        Action = nameof(FxDifference),
                        TitleFa = "حساب صراف و تفاوت نرخ", TitleEn = "Sarraf & FX Difference",
                        DescriptionFa = "مفاد و ضرر تفاوت نرخ در حواله‌های صراف، با کمیشن.",
                        DescriptionEn = "Gain and loss on the FX rate of sarraf transfers, with commission.",
                        Icon = "bi-currency-exchange", ToneClass = "tone-sky"
                    },
                    new()
                    {
                        Controller = "Expenses", Action = "Index",
                        TitleFa = "مصارف", TitleEn = "Expenses",
                        DescriptionFa = "مصارف ثبت‌شده به‌تفکیک نوع، قرارداد و تاریخ.",
                        DescriptionEn = "Recorded expenses by type, contract and date.",
                        Icon = "bi-receipt", ToneClass = "tone-amber"
                    }
                ]
            },
            new()
            {
                TitleFa = "مال، فروش و حمل",
                TitleEn = "Stock, Sales & Transport",
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
                        Action = nameof(SellableStock),
                        TitleFa = "موجودی قابل فروش", TitleEn = "Sellable Stock",
                        DescriptionFa = "موجودی فزیکی منهای رزرو پیش‌فروش.",
                        DescriptionEn = "Physical stock minus active pre-sale reservation.",
                        Icon = "bi-box-arrow-up-right", ToneClass = "tone-teal"
                    },
                    new()
                    {
                        Action = nameof(NegativeStock),
                        TitleFa = "موجودی منفی", TitleEn = "Negative Stock",
                        DescriptionFa = "جاهایی که مانده زیر صفر رفته، با علت و سند ایجادکننده.",
                        DescriptionEn = "Scopes that went below zero, with cause and source document.",
                        Icon = "bi-exclamation-octagon", ToneClass = "tone-rose"
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
                        Action = nameof(PreSaleDiscrepancies),
                        TitleFa = "ناهماهنگی‌های پیش‌فروش", TitleEn = "Pre-sale Discrepancies",
                        DescriptionFa = "تحویل بیشتر از تعهد، تعهد سررسیدشده و پیش‌پرداخت مصرف‌نشده.",
                        DescriptionEn = "Over-delivery, overdue commitments and unconsumed advances.",
                        Icon = "bi-exclamation-triangle", ToneClass = "tone-rose"
                    },
                    new()
                    {
                        Action = nameof(GoodsInTransit),
                        TitleFa = "بارهای در مسیر", TitleEn = "Goods In Transit",
                        DescriptionFa = "هر باری که حرکت کرده و هنوز نرسیده، با مسیر، مقدار و وضعیت آن.",
                        DescriptionEn = "Every load that has departed and not yet arrived, with route, quantity and status.",
                        Icon = "bi-truck", ToneClass = "tone-sky"
                    },
                    new()
                    {
                        Action = nameof(TransportVariance),
                        TitleFa = "کسری و اضافه‌بار حمل", TitleEn = "Transport Shortage & Surplus",
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
                TitleFa = "کنترول و اسناد",
                TitleEn = "Control & Documents",
                Icon = "bi-shield-check",
                Cards =
                [
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
                    },
                    new()
                    {
                        Controller = "Ledger", Action = "Index",
                        TitleFa = "دفتر کل", TitleEn = "Ledger",
                        DescriptionFa = "تمام اسناد مالی ثبت‌شده با منبع و مبلغ. مخصوص حسابدار.",
                        DescriptionEn = "All posted financial entries with source and amount. For the accountant.",
                        Icon = "bi-journals", ToneClass = "tone-lavender"
                    },
                    new()
                    {
                        // این صفحه صورت‌حساب طرف‌حساب نیست؛ اسناد Opening و Adjustment ارزی است.
                        // نام کارت با محتوای واقعی صفحه یکی شد تا با «گردش حساب» اشتباه نشود.
                        Controller = "AccountStatements", Action = "Index",
                        TitleFa = "اسناد اول دوره و اصلاح ارزی", TitleEn = "Opening & FX Adjustment Documents",
                        DescriptionFa = "ثبت مانده اول دوره و اصلاح ارزی. مخصوص حسابدار.",
                        DescriptionEn = "Opening balance and FX adjustment entries. For the accountant.",
                        Icon = "bi-journal-text", ToneClass = "tone-lavender"
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

        // مانده واقعی صندوق و بانک از همان مرجعی خوانده می‌شود که صفحهٔ دفتر کل و
        // حساب‌های نقدی می‌خوانند؛ محاسبهٔ موازی ساخته نمی‌شود.
        var cashCards = await FinanceMetricCardsQuery.BuildAsync(_db, _cache, businessClock: _businessClock);

        var topContracts = pnl.PurchaseRows
            .OrderByDescending(r => Math.Abs(r.GrossMarginUsd))
            .Take(5)
            .ToList();

        // طلب و بدهی از همان ردیف‌های گزارش «طلبات و بدهی‌ها» می‌آید؛ علامت مانده
        // همان قرارداد نمایشی سیستم است: مثبت = شرکت طلبکار، منفی = شرکت بدهکار.
        //
        // حساب شریک همچنان در گزارش کامل «طلبات و بدهی‌ها» و صورت‌حساب
        // شریک می‌ماند، اما ماندهٔ سهم/سرمایه نباید با طلب و بدهی عملیاتی شرکت
        // در کارت‌های مدیریتی یکجا شود. هیچ ردیف Ledger یا محاسبهٔ شریک حذف نمی‌شود.
        var operationalPartyBalances = balances.Rows
            .Where(r => r.PartyType != nameof(PartyStatementPartyType.Partner))
            .ToList();
        var topReceivables = operationalPartyBalances
            .Where(r => r.BalanceUsd > 0m)
            .OrderByDescending(r => r.BalanceUsd)
            .Take(5)
            .ToList();
        var topPayables = operationalPartyBalances
            .Where(r => r.BalanceUsd < 0m)
            .OrderBy(r => r.BalanceUsd)
            .Take(5)
            .ToList();

        // تا وقتی فروشِ بدون بهای تمام‌شده وجود دارد، COGS آن فروش‌ها صفر خوانده می‌شود و
        // «سود» بیش از واقع درمی‌آید. در آن حالت عدد سود منتشر نمی‌شود و به‌جایش دلیلش
        // نوشته می‌شود؛ هیچ COGS حدسی (مثلاً بهای خرید) جایگزین نمی‌گردد.
        var isProfitPublishable = companyPnl.Sales.UncostedSaleCount == 0
            && companyPnl.Sales.Confidence == PnlConfidence.Verified;

        // ── مصارفی که در ExpenseTransaction نیستند و تا حالا از سود شرکت می‌افتادند ──
        // مصارفِ درون‌خطیِ بارگیری (حمل/گدام/سایر/خط‌آهن) روی خودِ LoadingRegister ذخیره
        // می‌شوند و برای سطرهای «بدون طرف‌حساب» هیچ ExpenseTransaction نمی‌سازند؛ ارزشِ
        // ضایعاتِ قابلِ شارژ هم هیچ‌وقت سطرِ مصرف ندارد. هر دو از همان ردیف‌هایی خوانده
        // می‌شوند که صفحهٔ «مفاد قراردادها» می‌سازد (pnl.PurchaseRows) تا دو صفحه یک عدد
        // بدهند و هیچ فرمول موازی ساخته نشود. این‌ها با مصارفِ ثبت‌شده همپوشانی ندارند،
        // پس دوباره‌شماری نمی‌شود.
        var loadingOperationalCostUsd = pnl.PurchaseRows.Sum(r =>
            r.TransportCostUsd + r.WarehouseCostUsd + r.OtherCostUsd + r.RailwayCostUsd);
        var lossCostUsd = pnl.PurchaseRows.Sum(r => r.LossCostUsd);
        var unvaluedLossCount = pnl.PurchaseRows.Sum(r => r.UnvaluedLossCount);

        return new CompanyFinancialOverviewViewModel
        {
            Filter = filter,
            RevenueUsd = revenueUsd,
            PurchaseCostUsd = cogsUsd,
            ExpenseUsd = expenseUsd,
            LoadingOperationalCostUsd = loadingOperationalCostUsd,
            LossCostUsd = lossCostUsd,
            UnvaluedLossCount = unvaluedLossCount,
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
            CashOnHandUsd = cashCards.CashAccountsBalanceUsd,
            TopReceivables = topReceivables,
            TopPayables = topPayables,
            TotalReceivableUsd = operationalPartyBalances.Where(r => r.BalanceUsd > 0m).Sum(r => r.BalanceUsd),
            TotalPayableUsd = -operationalPartyBalances.Where(r => r.BalanceUsd < 0m).Sum(r => r.BalanceUsd),
            AsOfDate = _businessClock.Today,
            Metrics =
            [
                new() { Label = "فروش کل", Value = Money(revenueUsd), Detail = "Sales revenue", Icon = "bi-cart-check", ToneClass = "finance-positive" },
                new() { Label = "بهای تمام‌شده فروش", Value = Money(cogsUsd), Detail = "Realised COGS", Icon = "bi-box-arrow-in-down", ToneClass = "" },
                new() { Label = "مصارف", Value = Money(expenseUsd), Detail = "Official expenses", Icon = "bi-receipt", ToneClass = "finance-negative" },
                new() { Label = "مصارف بارگیری", Value = Money(loadingOperationalCostUsd), Detail = "Loading transport / warehouse / railway / other", Icon = "bi-truck", ToneClass = "finance-negative" },
                new() { Label = "ارزش ضایعات", Value = Money(lossCostUsd), Detail = unvaluedLossCount > 0 ? $"{unvaluedLossCount:N0} loss event(s) cannot be valued" : "Chargeable loss valued at purchase price", Icon = "bi-droplet-half", ToneClass = "finance-negative" },
                new() { Label = "سود خالص", Value = isProfitPublishable ? Money(companyPnl.NetProfitUsd) : "—", Detail = isProfitPublishable ? "Net profit" : $"COGS incomplete — {companyPnl.Sales.UncostedSaleCount:N0} sale(s) need COGS", Icon = "bi-graph-up-arrow", ToneClass = !isProfitPublishable ? "" : companyPnl.NetProfitUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = "حرکت نقدی", Value = Money(cashInUsd - cashOutUsd), Detail = "Payment inflow - outflow", Icon = "bi-cash-stack", ToneClass = cashInUsd - cashOutUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = "مغایرت‌ها", Value = warnings.TotalIssueCount.ToString("N0"), Detail = "Open warnings", Icon = "bi-exclamation-triangle", ToneClass = warnings.TotalIssueCount == 0 ? "finance-positive" : "finance-negative" }
            ]
        };
    }

    /// <summary>
    /// گردشِ صندوق و بانک. دقیقاً همان دو منبعی خوانده می‌شود که صفحهٔ
    /// «حساب‌های نقدی» می‌خواند:
    /// <list type="number">
    ///   <item>روزنامچه (<see cref="PaymentTransaction"/>)، به‌جز پرداختی که شریک از جیب خودش
    ///   داده (<see cref="PaymentFundingSource.Partner"/>). آن پول اصلاً وارد یا خارجِ صندوقِ شرکت
    ///   نشده و <c>CashAccountId</c> هم ندارد؛ جدا و فقط برای اطلاع نشان داده می‌شود.</item>
    ///   <item>مصرفی که «نقد پرداخت شد» ثبت شده و سندِ روزنامچه ندارد. اگر همان مصرف
    ///   یک <see cref="PaymentTransaction"/> مرتبط داشته باشد (کمیسیونِ نقدی)، حرکتِ پول از
    ///   منبعِ ۱ شمرده می‌شود و اینجا کنار می‌رود تا دو بار شمرده نشود.</item>
    /// </list>
    /// هیچ سندی ساخته یا تغییر داده نمی‌شود و هیچ منطقِ دفتری عوض نمی‌شود؛ فقط خواندن است.
    /// </summary>
    private async Task<CashFlowReportViewModel> BuildCashFlowReportAsync(ManagementReportFilterViewModel filter)
    {
        // مصرفِ نقدی نه مشتری/تأمین‌کننده دارد و نه «بابتِ پرداخت»؛ با این
        // فیلترها هیچ سطرِ مصرفی مطابقت نمی‌کند، پس کنار می‌رود و کاربر در هشدار می‌بیند.
        var includeCashExpenses = !filter.CustomerId.HasValue
            && !filter.SupplierId.HasValue
            && !filter.PaymentKind.HasValue;

        // فیلترهای غیرتاریخی برای ماندهٔ اول دوره هم لازم‌اند، پس تاریخ جدا اعمال می‌شود.
        var undated = new ManagementReportFilterViewModel
        {
            ProductId = filter.ProductId,
            ContractId = filter.ContractId,
            CustomerId = filter.CustomerId,
            SupplierId = filter.SupplierId,
            CashAccountId = filter.CashAccountId,
            CompanyId = filter.CompanyId,
            PaymentKind = filter.PaymentKind
        };

        var companyPayments = ApplyPaymentFilters(
            _db.PaymentTransactions.AsNoTracking().Where(p => p.FundingSource != PaymentFundingSource.Partner),
            undated);

        var cashExpenses = includeCashExpenses
            ? ApplyCashExpenseFilters(_db.ExpenseTransactions.AsNoTracking(), undated)
            : _db.ExpenseTransactions.AsNoTracking().Where(e => false);

        var fromDate = filter.FromDate?.Date;
        var toDate = filter.ToDate?.Date;

        var periodPayments = companyPayments;
        if (fromDate.HasValue) periodPayments = periodPayments.Where(p => p.PaymentDate >= fromDate.Value);
        if (toDate.HasValue) periodPayments = periodPayments.Where(p => p.PaymentDate <= toDate.Value);

        var periodExpenses = cashExpenses;
        if (fromDate.HasValue) periodExpenses = periodExpenses.Where(e => e.ExpenseDate >= fromDate.Value);
        if (toDate.HasValue) periodExpenses = periodExpenses.Where(e => e.ExpenseDate <= toDate.Value);

        // ---- «بابت»: تجمیع در دیتابیس، نام‌گذاری در حافظه ----
        var paymentGroups = await periodPayments
            .GroupBy(p => new { p.PaymentKind, p.Direction })
            .Select(g => new
            {
                g.Key.PaymentKind,
                g.Key.Direction,
                AmountUsd = g.Sum(p => p.AmountUsd),
                Count = g.Count()
            })
            .ToListAsync();

        var expenseGroups = await periodExpenses
            .GroupBy(e => e.ExpenseType != null ? (e.ExpenseType.NamePersian ?? e.ExpenseType.Name) : "")
            .Select(g => new { TypeName = g.Key, AmountUsd = g.Sum(e => e.AmountUsd), Count = g.Count() })
            .ToListAsync();

        var rows = paymentGroups
            .GroupBy(g => CashFlowGroupName(g.PaymentKind, g.Direction))
            .Select(g => new CashFlowReportRowViewModel
            {
                GroupName = g.Key,
                InflowUsd = g.Where(x => x.Direction == PaymentDirection.In).Sum(x => x.AmountUsd),
                OutflowUsd = g.Where(x => x.Direction == PaymentDirection.Out).Sum(x => x.AmountUsd),
                Count = g.Sum(x => x.Count)
            })
            .Concat(expenseGroups.Select(g => new CashFlowReportRowViewModel
            {
                GroupName = string.IsNullOrWhiteSpace(g.TypeName)
                    ? CashExpenseGroupLabel
                    : $"{CashExpenseGroupLabel} \u2014 {g.TypeName}",
                OutflowUsd = g.AmountUsd,
                Count = g.Count
            }))
            .OrderByDescending(r => Math.Abs(r.NetUsd))
            .ToList();

        // ---- تفکیک حساب. کلیدِ گروه شناسهٔ حساب است نه نام، پس دو
        // حسابِ هم‌نام روی هم نمی‌افتند؛ ارز هم از خودِ حساب می‌آید نه از ارزِ سند.
        var paymentByAccount = await periodPayments
            .GroupBy(p => p.CashAccountId)
            .Select(g => new
            {
                CashAccountId = g.Key,
                InflowUsd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : 0m),
                OutflowUsd = g.Sum(p => p.Direction == PaymentDirection.Out ? p.AmountUsd : 0m)
            })
            .ToListAsync();

        var expenseByAccount = await periodExpenses
            .GroupBy(e => e.CashAccountId)
            .Select(g => new { CashAccountId = g.Key, OutflowUsd = g.Sum(e => e.AmountUsd) })
            .ToListAsync();

        // ---- ماندهٔ اول دوره: همان دو منبع، فقط پیش از شروعِ بازه ----
        var openingByAccount = new Dictionary<int, decimal>();
        var openingNoAccountUsd = 0m;
        if (fromDate.HasValue)
        {
            var openingPayments = await companyPayments
                .Where(p => p.PaymentDate < fromDate.Value)
                .GroupBy(p => p.CashAccountId)
                .Select(g => new
                {
                    CashAccountId = g.Key,
                    NetUsd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : -p.AmountUsd)
                })
                .ToListAsync();

            var openingExpenses = await cashExpenses
                .Where(e => e.ExpenseDate < fromDate.Value)
                .GroupBy(e => e.CashAccountId)
                .Select(g => new { CashAccountId = g.Key, NetUsd = -g.Sum(e => e.AmountUsd) })
                .ToListAsync();

            foreach (var opening in openingPayments.Concat(openingExpenses))
            {
                if (opening.CashAccountId is int openingAccountId)
                {
                    openingByAccount[openingAccountId] =
                        openingByAccount.GetValueOrDefault(openingAccountId) + opening.NetUsd;
                }
                else
                {
                    openingNoAccountUsd += opening.NetUsd;
                }
            }
        }

        var accountIds = paymentByAccount.Select(a => a.CashAccountId)
            .Concat(expenseByAccount.Select(a => a.CashAccountId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Concat(openingByAccount.Keys)
            .Distinct()
            .ToList();

        var accountMeta = (await _db.CashAccounts
                .AsNoTracking()
                .Where(a => accountIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Name, a.Currency })
                .ToListAsync())
            .ToDictionary(a => a.Id);

        var accountKeys = accountIds.Select(id => (int?)id).ToList();
        if (paymentByAccount.Any(a => a.CashAccountId is null)
            || expenseByAccount.Any(a => a.CashAccountId is null)
            || openingNoAccountUsd != 0m)
        {
            accountKeys.Add(null);
        }

        var accountRows = accountKeys
            .Select(key => new CashFlowAccountRowViewModel
            {
                CashAccountName = key is int nameId && accountMeta.TryGetValue(nameId, out var named)
                    ? named.Name
                    : UnlinkedCashAccountLabel,
                Currency = key is int currencyId && accountMeta.TryGetValue(currencyId, out var priced)
                    ? priced.Currency
                    : "-",
                OpeningUsd = key is int openingId ? openingByAccount.GetValueOrDefault(openingId) : openingNoAccountUsd,
                InflowUsd = paymentByAccount.Where(a => a.CashAccountId == key).Sum(a => a.InflowUsd),
                OutflowUsd = paymentByAccount.Where(a => a.CashAccountId == key).Sum(a => a.OutflowUsd)
                    + expenseByAccount.Where(a => a.CashAccountId == key).Sum(a => a.OutflowUsd)
            })
            .OrderByDescending(r => Math.Abs(r.NetUsd))
            .ToList();

        // ---- روندِ ماهانه ----
        var paymentMonths = await periodPayments
            .GroupBy(p => new { p.PaymentDate.Year, p.PaymentDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                InflowUsd = g.Sum(p => p.Direction == PaymentDirection.In ? p.AmountUsd : 0m),
                OutflowUsd = g.Sum(p => p.Direction == PaymentDirection.Out ? p.AmountUsd : 0m),
                Count = g.Count()
            })
            .ToListAsync();

        var expenseMonths = await periodExpenses
            .GroupBy(e => new { e.ExpenseDate.Year, e.ExpenseDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                InflowUsd = 0m,
                OutflowUsd = g.Sum(e => e.AmountUsd),
                Count = g.Count()
            })
            .ToListAsync();

        var openingBalanceUsd = openingByAccount.Values.Sum() + openingNoAccountUsd;

        var monthGroups = paymentMonths.Concat(expenseMonths)
            .GroupBy(m => new { m.Year, m.Month })
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .ToList();

        var periodRows = new List<CashFlowPeriodRowViewModel>(monthGroups.Count);
        var runningUsd = openingBalanceUsd;
        foreach (var monthGroup in monthGroups)
        {
            var monthInflowUsd = monthGroup.Sum(m => m.InflowUsd);
            var monthOutflowUsd = monthGroup.Sum(m => m.OutflowUsd);
            runningUsd += monthInflowUsd - monthOutflowUsd;
            periodRows.Add(new CashFlowPeriodRowViewModel
            {
                Year = monthGroup.Key.Year,
                Month = monthGroup.Key.Month,
                InflowUsd = monthInflowUsd,
                OutflowUsd = monthOutflowUsd,
                Count = monthGroup.Sum(m => m.Count),
                ClosingUsd = runningUsd
            });
        }

        // ---- پرداختِ شریک. خارج از صندوق است و برای یک حسابِ نقدیِ مشخص نمایش داده
        // نمی‌شود. در نمای شرکت، مالکیت از CompanyId اثبات‌شده یا قرارداد همان پرداخت
        // خوانده می‌شود تا پرداخت شریکِ شرکت‌های دیگر وارد این scope نشود. ----
        var partnerFundedFilter = new ManagementReportFilterViewModel
        {
            FromDate = filter.FromDate,
            ToDate = filter.ToDate,
            ProductId = filter.ProductId,
            ContractId = filter.ContractId,
            CustomerId = filter.CustomerId,
            SupplierId = filter.SupplierId,
            PaymentKind = filter.PaymentKind
        };

        var partnerFundedQuery = ApplyPaymentFilters(
            _db.PaymentTransactions.AsNoTracking().Where(p => p.FundingSource == PaymentFundingSource.Partner),
            partnerFundedFilter);

        if (filter.CompanyId.HasValue)
        {
            var companyId = filter.CompanyId.Value;
            partnerFundedQuery = partnerFundedQuery.Where(p => p.CompanyId == companyId
                || (p.CompanyId == null && p.Contract != null && p.Contract.CompanyId == companyId));
        }

        var partnerFunded = filter.CashAccountId.HasValue
            ? null
            : await partnerFundedQuery
                .GroupBy(_ => 1)
                .Select(g => new { AmountUsd = g.Sum(p => p.AmountUsd), Count = g.Count() })
                .FirstOrDefaultAsync();

        var totalInflowUsd = rows.Sum(r => r.InflowUsd);
        var totalOutflowUsd = rows.Sum(r => r.OutflowUsd);
        var netCashFlowUsd = totalInflowUsd - totalOutflowUsd;
        var closingBalanceUsd = openingBalanceUsd + netCashFlowUsd;

        var scopeNotes = new List<string>();
        if (filter.ContractId.HasValue || filter.ProductId.HasValue)
        {
            scopeNotes.Add("فیلترِ قرارداد/جنس فقط گردشِ متصل به قرارداد را نگه می‌دارد؛ معاش، دریافت دستی و مصرفِ عمومی در این نما نیستند.");
        }

        if (!includeCashExpenses)
        {
            scopeNotes.Add("با فیلترِ مشتری، تأمین‌کننده یا بابتِ پرداخت، مصرفِ نقدی قابل انتساب نیست و در این نما شمرده نشده است.");
        }

        var partnerFundedUsd = partnerFunded?.AmountUsd ?? 0m;
        var partnerFundedCount = partnerFunded?.Count ?? 0;

        // چهار کارت، به ترتیبِ همان جمله‌ای که کاربر می‌خواند:
        // «اول دوره این‌قدر داشتیم، این‌قدر آمد، این‌قدر رفت، آخر دوره این‌قدر ماند.»
        // خالص، تعداد حساب‌ها و پرداختِ شریک از کارت‌ها برداشته شد؛ خالص در جمعِ
        // جدول‌ها، تعداد حساب‌ها در جدولِ تفکیک حساب و پرداختِ شریک در هشدارِ بالای
        // صفحه دیده می‌شود، پس ردیف کارت‌ها یک سطر تمیز می‌ماند.
        var metrics = new List<ReportMetricViewModel>
        {
            new() { Label = "مانده اول دوره", Value = Money(openingBalanceUsd), Detail = fromDate.HasValue ? "Opening balance" : "From the beginning", Icon = "bi-hourglass-top", ToneClass = openingBalanceUsd >= 0m ? "finance-positive" : "finance-negative" },
            new() { Label = "رسیدکی", Value = Money(totalInflowUsd), Detail = "Money in", Icon = "bi-arrow-down-circle", ToneClass = "finance-positive" },
            new() { Label = "بردکی", Value = Money(totalOutflowUsd), Detail = "Money out", Icon = "bi-arrow-up-circle", ToneClass = "finance-negative" },
            new() { Label = "مانده آخر دوره", Value = Money(closingBalanceUsd), Detail = "Opening + In - Out", Icon = "bi-hourglass-bottom", ToneClass = closingBalanceUsd >= 0m ? "finance-positive" : "finance-negative" }
        };

        return new CashFlowReportViewModel
        {
            Filter = filter,
            Rows = rows,
            AccountRows = accountRows,
            PeriodRows = periodRows,
            OpeningBalanceUsd = openingBalanceUsd,
            PartnerFundedOutflowUsd = partnerFundedUsd,
            PartnerFundedCount = partnerFundedCount,
            ScopeWarning = scopeNotes.Count == 0 ? null : string.Join(" ", scopeNotes),
            Metrics = metrics
        };
    }

    private async Task<ReceivablesPayablesReportViewModel> BuildReceivablesPayablesReportAsync(ManagementReportFilterViewModel filter)
    {
        // «طلبات و بدهی‌ها» فقط طرف‌حساب بیرونی را می‌شمارد؛ حساب جاریِ خودِ جوازها
        // طرف معامله نیست. رجوع: PartyBalanceSnapshotFilters.ExternalPartiesOnly.
        var balanceRows = (await _partyBalances.GetBalancesAsync(filter)).ExternalPartiesOnly();
        var supplierIds = balanceRows
            .Where(row => row.PartyType == PartyStatementPartyType.Supplier)
            .Select(row => row.PartyId)
            .Distinct()
            .ToArray();
        var cancellationToken = HttpContext?.RequestAborted ?? CancellationToken.None;
        var fxSettlements = await _supplierFxSettlements.GetManyAsync(
            supplierIds,
            filter.ContractId,
            filter.ToDate,
            cancellationToken);

        // در نمای همهٔ قراردادها، تعدیل ثبت‌شدهٔ SupplierFxDifference از قبل داخل ماندهٔ
        // رسمی است؛ فقط بخشِ ثبت‌نشده را به‌صورت read-only اصلاح می‌کنیم تا دوباره‌شماری نشود.
        // در فیلتر یک قرارداد، سطر شناسایی عمداً ContractId ندارد و در ماندهٔ خام هم نیست؛
        // بنابراین کل تفاوت همان قرارداد باید در ستون تعدیل گزارش دیده شود.
        var recognizedFxEffects = filter.ContractId.HasValue
            ? new Dictionary<int, decimal>()
            : await LoadRecognizedSupplierFxEffectsAsync(supplierIds, filter.ToDate, cancellationToken);
        var policies = new PartyStatementPolicyResolver();
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
            FxAdjustmentUsd = balance.PartyType == PartyStatementPartyType.Supplier
                && fxSettlements.TryGetValue(balance.PartyId, out var fx)
                ? decimal.Round(
                    -(fx.RealizedFxDifferenceUsd + recognizedFxEffects.GetValueOrDefault(balance.PartyId)),
                    4,
                    MidpointRounding.AwayFromZero)
                : 0m,
            LastEntryDate = balance.LastEntryDate,
            BalanceKind = balance.PartyType == PartyStatementPartyType.Supplier
                && fxSettlements.TryGetValue(balance.PartyId, out var supplierFx)
                    ? policies.Resolve(balance.PartyType).BalanceMeaning(
                        balance.ClosingBalanceUsd + decimal.Round(
                            -(supplierFx.RealizedFxDifferenceUsd
                                + recognizedFxEffects.GetValueOrDefault(balance.PartyId)),
                            4,
                            MidpointRounding.AwayFromZero),
                        isEnglish: false)
                    : balance.BalanceMeaning,
            DetailsController = balance.DetailsController
        }).ToList();

        // تاریخ مبنای «راکد بودن» حساب: انتهای بازهٔ فیلتر، وگرنه امروز.
        var asOfDate = filter.ToDate?.Date ?? _businessClock.Today;
        var model = new ReceivablesPayablesReportViewModel
        {
            Filter = filter,
            Rows = rows,
            AsOfDate = asOfDate
        };

        return new ReceivablesPayablesReportViewModel
        {
            Filter = filter,
            Rows = rows,
            AsOfDate = asOfDate,
            Metrics =
            [
                new() { Label = "مجموع طلبات", Value = Money(model.TotalReceivableUsd), Detail = "Total receivables", Icon = "bi-arrow-down-circle", ToneClass = "finance-positive" },
                new() { Label = "مجموع بدهی‌ها", Value = Money(model.TotalPayableUsd), Detail = "Total payables", Icon = "bi-arrow-up-circle", ToneClass = "finance-negative" },
                new() { Label = "خالص وضعیت", Value = Money(model.NetBalanceUsd), Detail = "Net position", Icon = "bi-graph-up-arrow", ToneClass = model.NetBalanceUsd >= 0m ? "finance-positive" : "finance-negative" },
                new() { Label = $"طلبات راکد بیش از {ReceivablesPayablesReportViewModel.StaleAfterDays} روز", Value = Money(model.StaleReceivableUsd), Detail = "Idle receivables", Icon = "bi-exclamation-triangle", ToneClass = "finance-negative" }
            ]
        };
    }

    private async Task<Dictionary<int, decimal>> LoadRecognizedSupplierFxEffectsAsync(
        IReadOnlyCollection<int> supplierIds,
        DateTime? toDate,
        CancellationToken ct)
    {
        if (supplierIds.Count == 0)
        {
            return [];
        }

        var query = _db.LedgerEntries.AsNoTracking()
            .Where(l => l.SupplierId.HasValue
                && supplierIds.Contains(l.SupplierId.Value)
                && l.SourceType == SupplierFxRecognitionService.PartyLedgerSourceType);

        if (toDate.HasValue)
        {
            var end = toDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < end);
        }

        var rows = await query
            .Select(l => new { SupplierId = l.SupplierId!.Value, l.Side, l.AmountUsd })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.SupplierId)
            // ماندهٔ گزارش برای طرف‌حساب: Debit/برد مثبت و Credit/رسید منفی است.
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.Side == LedgerSide.Debit ? row.AmountUsd : -row.AmountUsd));
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

        // PTG-P1-04 — ردیف‌های «طبقه‌بندی‌نشده». هویتِ تسویه‌شان هیچ‌وقت حدس زده نمی‌شود،
        // پس باید دیده شوند تا کسی برایشان تصمیم بگیرد — نه اینکه خاموش در گزارش‌ها بمانند.
        var unclassifiedExpenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .CountAsync(e => !e.IsCancelled && e.SettlementMode == ExpenseSettlementMode.Unknown);

        // اظهارنامهٔ گمرکی‌ای که هنوز به دفتر کل نرسیده: مبلغش در گزارش‌ها هست، ولی سطرِ
        // مالی ندارد و در حساب هیچ طرف‌حسابی دیده نمی‌شود.
        var customsWithoutSettlement = await _db.CustomsDeclarations
            .AsNoTracking()
            .CountAsync(c => c.TotalUsd > 0m
                && !c.ExpenseTransactions.Any(e => !e.IsCancelled));

        var items = new List<ReportsWarningItemViewModel>
        {
            new() { Title = "فروش بدون سند دفتر", Description = "فروش‌های قطعی که سند دفتر کل ندارند.", Count = salesWithoutLedger, Severity = "danger", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "مصارف بدون سند دفتر", Description = "مصارف ثبت‌شده که در دفتر کل نیامده‌اند.", Count = expensesWithoutLedger, Severity = "danger", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "رسیدکی و بردکی بدون سند دفتر", Description = "دریافت و پرداخت‌هایی که سند دفتر کل ندارند.", Count = paymentsWithoutLedger, Severity = "warning", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "تصفیهٔ صراف بدون سند دفتر", Description = "تصفیه‌های ثبت‌شدهٔ صراف که سند تأمین‌کننده ندارند.", Count = sarrafWithoutLedger, Severity = "warning", Controller = "Reconciliation", Action = "MissingLedger" },
            new() { Title = "ضایعات بدون قیمت", Description = "ضایعات قابل شارژ که قیمت بارگیری برای ارزش‌گذاری ندارند.", Count = unvaluedLosses, Severity = "warning", Controller = "Reports", Action = "ContractPnl" },
            new() { Title = "مصارف با تسویهٔ طبقه‌بندی‌نشده", Description = "مصرف‌هایی که معلوم نیست بدهی مانده‌اند یا پرداخت شده‌اند. حدس زده نمی‌شوند و باید یک‌یک مشخص شوند.", Count = unclassifiedExpenses, Severity = "warning", Controller = "Expenses", Action = "Index" },
            new() { Title = "گمرک بدون تسویه", Description = "اظهارنامه‌های گمرکی که وضعیت تسویه‌شان انتخاب نشده، پس هزینه‌شان به دفتر کل نرسیده است.", Count = customsWithoutSettlement, Severity = "warning", Controller = "CustomsDeclarations", Action = "Index" }
        };

        items = items.Where(i => i.Count > 0).ToList();

        return new ReportsWarningsViewModel
        {
            Items = items,
            Metrics =
            [
                new() { Label = "کل کارهای نیمه‌تمام", Value = items.Sum(i => i.Count).ToString("N0"), Detail = "Open items", Icon = "bi-exclamation-triangle", ToneClass = items.Any() ? "finance-negative" : "finance-positive" },
                new() { Label = "بدون سند دفتر", Value = (salesWithoutLedger + expensesWithoutLedger + paymentsWithoutLedger + sarrafWithoutLedger).ToString("N0"), Detail = "Ledger issues", Icon = "bi-journal-x", ToneClass = salesWithoutLedger + expensesWithoutLedger + paymentsWithoutLedger + sarrafWithoutLedger > 0 ? "finance-negative" : "finance-positive" },
                new() { Label = "ضایعات بدون قیمت", Value = unvaluedLosses.ToString("N0"), Detail = "Unvalued losses", Icon = "bi-graph-up", ToneClass = unvaluedLosses > 0 ? "finance-negative" : "finance-positive" }
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

        // فقط پیوندِ (قرارداد، فروش) خوانده می‌شود، نه جمعِ کاملِ فروش. عددِ عاید هر قرارداد
        // پایین‌تر و یکجا از روی سهم اثبات‌شده ساخته می‌شود تا یک فروش چند-قراردادی کل
        // TotalUsd خود را به هر قرارداد ندهد.
        var directSaleLinks = purchaseIds.Count == 0
            ? []
            : await directSaleQuery
                .Select(a => new SaleContractLink(a.SourcePurchaseContractId!.Value, a.SalesTransactionId!.Value))
                .Distinct()
                .ToListAsync();

        var directSaleMismatchById = purchaseIds.Count == 0
            ? new Dictionary<int, int>()
            : await directSaleQuery
                .Where(a => a.QuantityMt != a.SalesTransaction!.QuantityMt)
                .GroupBy(a => a.SourcePurchaseContractId!.Value)
                .Select(g => new { ContractId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ContractId, x => x.Count);

        // TerminalStock sales — sales whose stock-out InventoryMovement is tied to one of these purchase contracts.
        // De-duplicate against DirectSale allocations so a sale never contributes revenue twice.
        var directSaleSaleIds = directSaleLinks.Select(l => l.SaleId).Distinct().ToArray();

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

        var stockSaleLinks = purchaseIds.Count == 0
            ? []
            : await stockMovementQuery
                .Select(m => new SaleContractLink(m.ContractId!.Value, m.SalesTransactionId!.Value))
                .Distinct()
                .ToListAsync();

        // ── In-transit direct sales (truck / internal transport receipt) ────
        // این فروش‌ها عمداً هیچ InventoryMovement نمی‌سازند (بار هنگام بارگیریِ موتر یا حمل قبلاً از
        // موجودی خارج شده) و LoadingReceiptAllocation نوع DirectSale هم ندارند، پس در هیچ‌کدام از دو
        // منبعِ بالا دیده نمی‌شدند و کل عایدشان — شامل عایدِ اضافه‌بارِ تخلیه — از سود و زیان قرارداد
        // خرید بیرون می‌ماند. قرارداد از Lineage واقعی گرفته می‌شود: موتر → TruckDispatch.ContractId،
        // رسید انتقال → InventoryTransportLeg.SourcePurchaseContractId. هیچ قراردادی حدس زده نمی‌شود.
        var inTransitSaleLinks = purchaseIds.Count == 0
            ? new List<(int ContractId, int SaleId)>()
            : await BuildInTransitDirectSaleLinksAsync(purchaseIds);

        // ── Revenue per purchase contract — one place, one rule ─────────────
        // ترتیب مسیرها مثل قبل است و هر فروش فقط از نخستین مسیری که آن را دیده شمرده
        // می‌شود. سهم دالریِ هر قرارداد از SalesTransactionSourceAllocations خوانده
        // می‌شود (منبعِ واحدِ انتساب فروش→قرارداد خرید) و جمع سهم‌ها دقیقاً برابر
        // SalesTransaction.TotalUsd است؛ پس یک فروشِ چند-قراردادی دیگر عایدش را در دو
        // قرارداد تکرار نمی‌کند. فروشِ بدون allocation «اثبات‌نشده» است و رفتار قدیمی
        // همان صفحه نگه داشته می‌شود — هیچ قراردادی حدس زده نمی‌شود.
        var saleAggById = await BuildContractSaleRevenueAsync(
            purchaseIds,
            directSaleLinks,
            stockSaleLinks,
            inTransitSaleLinks.Select(l => new SaleContractLink(l.ContractId, l.SaleId)).ToList());

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
                    e.ExpenseType != null ? e.ExpenseType.NamePersian : null,
                    e.CustomsDeclarationId))
                .ToListAsync();

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
        var countedCustomsDeclarationIds = new HashSet<int>();
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
                    .Select(cd => new { cd.Id, cd.LoadingRegisterId, cd.TransportLegId, cd.TotalUsd })
                    .ToListAsync();

                foreach (var row in customsRows)
                {
                    // یک اظهارنامه فقط یک بار شمرده می‌شود. ترتیب اولویت همان ترتیبِ
                    // CustomsDeclarationExpenseSync.ResolveContractIdAsync است (اول مسیر
                    // حمل، بعد بارگیری) تا ستون «گمرک» و ContractId مصرفِ ساخته‌شده از
                    // همان اظهارنامه به یک قرارداد اشاره کنند.
                    int? attributedContractId = null;
                    if (row.TransportLegId.HasValue
                        && legIdToContract.TryGetValue(row.TransportLegId.Value, out var transportContractId))
                    {
                        attributedContractId = transportContractId;
                    }
                    else if (row.LoadingRegisterId.HasValue
                        && lrIdToContract.TryGetValue(row.LoadingRegisterId.Value, out var loadingContractId))
                    {
                        attributedContractId = loadingContractId;
                    }

                    if (attributedContractId is null)
                    {
                        continue;
                    }

                    customsByContract[attributedContractId.Value] =
                        customsByContract.GetValueOrDefault(attributedContractId.Value) + row.TotalUsd;
                    countedCustomsDeclarationIds.Add(row.Id);
                }
            }
        }

        // ستون «مصارف عمومی» نباید همان پولی را دوباره بشمارد که ستون «گمرک» شمرده است.
        // هر اظهارنامه از راه CustomsDeclarationExpenseSync یک ExpenseTransaction با همان
        // ContractId می‌سازد؛ پس مصرفِ اظهارنامه‌هایی که بالا شمرده شدند اینجا کنار می‌رود.
        var generalExpenseByContract = purchaseIds.Count == 0
            ? new Dictionary<int, decimal>()
            : expenseRows
                .Where(e => !e.CustomsDeclarationId.HasValue
                    || !countedCustomsDeclarationIds.Contains(e.CustomsDeclarationId.Value))
                .GroupBy(e => e.ContractId)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.AmountUsd));

        var purchaseRows = purchaseContracts.Select(c =>
        {
            loadingAggById.TryGetValue(c.Id, out var agg);
            customsByContract.TryGetValue(c.Id, out var customs);
            generalExpenseByContract.TryGetValue(c.Id, out var generalExpense);
            lossAggByContract.TryGetValue(c.Id, out var lossAgg);
            pendingTankSettlementByContract.TryGetValue(c.Id, out var pendingSettlementMt);
            sarrafDifferenceByContract.TryGetValue(c.Id, out var sarrafDifference);
            saleAggById.TryGetValue(c.Id, out var saleAgg);
            var directSaleQuantityMismatchCount = directSaleMismatchById.GetValueOrDefault(c.Id);
            // Official wagon rent (ServiceProvider) is counted via ExpenseTransactions
            // (generalExpense). For LEGACY loadings the inline railway field mirrors that
            // same amount, so it must be dropped to avoid double counting. For row-based
            // loadings the inline railway field only mirrors "None" expense lines, which
            // never overlap with the official wagon rent — so that portion must be KEPT.
            var inlineRailwayCostUsd = contractsWithOfficialWagonRent.Contains(c.Id)
                ? agg?.LoadingRailwayExpenseUsdFromLines ?? 0m
                : agg?.LoadingRailwayExpenseUsd ?? 0m;
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
                TotalSoldMt = saleAgg.TotalSoldMt,
                TotalRevenueUsd = saleAgg.TotalRevenueUsd,
                DirectSaleQuantityMismatchCount = directSaleQuantityMismatchCount,
                PnlConfidence = (agg?.PendingLoadingCount ?? 0) > 0
                    || directSaleQuantityMismatchCount > 0
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
        bool includeInventory = false,
        bool includeCashAccounts = false)
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

        if (includeCashAccounts)
        {
            var cashAccountLookups = await GetCachedLookupAsync(
                "reports:lookups:cash-accounts:v1",
                () => _db.CashAccounts.AsNoTracking()
                    .Where(a => a.IsActive)
                    .OrderBy(a => a.Name)
                    .Select(a => new LookupOption(a.Id, a.Name))
                    .ToListAsync());
            ViewBag.CashAccounts = new SelectList(
                cashAccountLookups,
                "Id",
                "Name",
                filter.CashAccountId);

            var companyLookups = await GetCachedLookupAsync(
                "reports:lookups:companies:v1",
                () => _db.Companies.AsNoTracking()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.Name)
                    .Select(c => new LookupOption(c.Id, c.NamePersian ?? c.Name))
                    .ToListAsync());
            ViewBag.Companies = new SelectList(
                companyLookups,
                "Id",
                "Name",
                filter.CompanyId);

            ViewBag.PaymentKinds = new SelectList(
                Enum.GetValues<PaymentKind>()
                    .Select(kind => new LookupOption((int)kind, PaymentKindLabels.ToPersian(kind)))
                    .OrderBy(kind => kind.Name)
                    .ToList(),
                "Id",
                "Name",
                filter.PaymentKind.HasValue ? (int)filter.PaymentKind.Value : null);
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
        if (filter.CashAccountId.HasValue) query = query.Where(p => p.CashAccountId == filter.CashAccountId.Value);
        if (filter.PaymentKind.HasValue) query = query.Where(p => p.PaymentKind == filter.PaymentKind.Value);

        // شرکت از روی صاحبِ صندوق/بانک تفکیک می‌شود، نه از PaymentTransaction.CompanyId که
        // برای رکوردهای مبهم عمداً null مانده و فیلترِ مستقیم روی آن پول را خاموش گم می‌کند.
        if (filter.CompanyId.HasValue)
        {
            query = query.Where(p => p.CashAccount != null && p.CashAccount.CompanyId == filter.CompanyId.Value);
        }

        return query;
    }

    /// <summary>عنوانِ گروهِ مصرفِ نقدی در ستون «بابت».</summary>
    private const string CashExpenseGroupLabel = "مصرف نقدی";

    /// <summary>سطرِ پرداختی که صندوق/بانکِ معتبر ندارد. مغایرت است، پس پنهان نمی‌شود.</summary>
    private const string UnlinkedCashAccountLabel = "بدون حساب نقدی";

    /// <summary>
    /// مصرفی که از صندوق/بانک پرداخت شده و حرکتِ پولش در روزنامچه سند ندارد.
    /// مصرفِ لغوشده و مصرفی که <see cref="PaymentTransaction"/> مرتبط دارد (کمیسیونِ نقدی)
    /// بیرون می‌ماند تا همان خروجِ پول دو بار شمرده نشود.
    /// </summary>
    private IQueryable<ExpenseTransaction> ApplyCashExpenseFilters(
        IQueryable<ExpenseTransaction> query,
        ManagementReportFilterViewModel filter)
    {
        query = query.Where(e => e.SettlementMode == ExpenseSettlementMode.PaidImmediately
            && !e.IsCancelled
            && e.CashAccountId != null
            && !_db.PaymentTransactions.Any(p => p.ExpenseTransactionId == e.Id));

        if (filter.ContractId.HasValue) query = query.Where(e => e.ContractId == filter.ContractId.Value);
        if (filter.ProductId.HasValue) query = query.Where(e => e.Contract != null && e.Contract.ProductId == filter.ProductId.Value);
        if (filter.CashAccountId.HasValue) query = query.Where(e => e.CashAccountId == filter.CashAccountId.Value);
        if (filter.CompanyId.HasValue) query = query.Where(e => e.CashAccount != null && e.CashAccount.CompanyId == filter.CompanyId.Value);

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
    private sealed record SaleContractLink(int ContractId, int SaleId);

    /// <summary>
    /// عاید و مقدار فروخته‌شدهٔ هر قرارداد خرید، از روی پیوندهای سه مسیر فروش.
    /// <para>
    /// هر فروش فقط از نخستین مسیری که آن را دیده شمرده می‌شود (DirectSale، سپس
    /// TerminalStock، سپس فروش در راه) تا عاید تکرار نشود. برای فروشی که سهم
    /// اثبات‌شده دارد، مبلغ هر قرارداد از <c>SalesTransactionSourceAllocations</c>
    /// می‌آید و جمع سهم‌ها برابر <c>SalesTransaction.TotalUsd</c> است. فروش
    /// اثبات‌نشده (بدون allocation) مثل قبل کامل به قرارداد پیوندخورده‌اش می‌نشیند؛
    /// قراردادی حدس زده نمی‌شود.
    /// </para>
    /// </summary>
    private async Task<Dictionary<int, (decimal TotalSoldMt, decimal TotalRevenueUsd)>> BuildContractSaleRevenueAsync(
        IReadOnlyCollection<int> purchaseIds,
        IReadOnlyList<SaleContractLink> directSaleLinks,
        IReadOnlyList<SaleContractLink> stockSaleLinks,
        IReadOnlyList<SaleContractLink> inTransitSaleLinks)
    {
        var aggregate = new Dictionary<int, (decimal TotalSoldMt, decimal TotalRevenueUsd)>();
        if (purchaseIds.Count == 0)
        {
            return aggregate;
        }

        var claimedSaleIds = new HashSet<int>();
        var effectiveLinks = new List<SaleContractLink>();
        foreach (var path in new[] { directSaleLinks, stockSaleLinks, inTransitSaleLinks })
        {
            // یک مسیر می‌تواند یک جفت (قرارداد، فروش) را از دو راهِ lineage بدهد
            // (مثلاً موترِ امروز و لینکِ قدیمیِ همان دیسپچ)؛ آن یک رویداد است، نه دو تا.
            var pathLinks = path
                .Distinct()
                .OrderBy(link => link.ContractId)
                .ToList();
            var newSaleIds = pathLinks
                .Select(link => link.SaleId)
                .Where(saleId => !claimedSaleIds.Contains(saleId))
                .ToHashSet();
            if (newSaleIds.Count == 0)
            {
                continue;
            }

            effectiveLinks.AddRange(pathLinks.Where(link => newSaleIds.Contains(link.SaleId)));
            claimedSaleIds.UnionWith(newSaleIds);
        }

        if (effectiveLinks.Count == 0)
        {
            return aggregate;
        }

        var saleIds = claimedSaleIds.ToList();
        var saleRows = await _db.SalesTransactions.AsNoTracking()
            .Where(s => !s.IsCancelled && saleIds.Contains(s.Id))
            .Select(s => new { s.Id, s.QuantityMt, s.TotalUsd })
            .ToDictionaryAsync(s => s.Id, s => (s.QuantityMt, s.TotalUsd));
        var attribution = await _saleAttribution.LoadForSalesAsync(saleIds);
        var purchaseIdSet = purchaseIds.ToHashSet();

        void Accumulate(int contractId, decimal quantityMt, decimal revenueUsd)
        {
            aggregate.TryGetValue(contractId, out var current);
            aggregate[contractId] = (
                current.TotalSoldMt + quantityMt,
                current.TotalRevenueUsd + revenueUsd);
        }

        foreach (var group in effectiveLinks.GroupBy(link => link.SaleId))
        {
            if (!saleRows.TryGetValue(group.Key, out var sale))
            {
                continue;
            }

            if (attribution.HasProvenAllocation(group.Key))
            {
                foreach (var share in attribution.SharesFor(group.Key))
                {
                    if (purchaseIdSet.Contains(share.SourcePurchaseContractId))
                    {
                        Accumulate(share.SourcePurchaseContractId, share.QuantityMt, share.AmountUsd);
                    }
                }

                continue;
            }

            // فروشِ اثبات‌نشده سهم قابل اثبات ندارد، پس شکسته نمی‌شود؛ اما دو بار هم شمرده
            // نمی‌شود. کلِ مبلغ یک بار روی نخستین قرارداد پیوندخورده (به ترتیب مسیر و شمارهٔ
            // قرارداد) می‌نشیند تا جمع قراردادها از مبلغ فروش بیشتر نشود.
            Accumulate(group.First().ContractId, sale.QuantityMt, sale.TotalUsd);
        }

        return aggregate;
    }

    private sealed record ContractPnlExpenseRow(
        int ContractId,
        decimal AmountUsd,
        string? Description,
        string? ExpenseTypeCode,
        string? ExpenseTypeName,
        string? ExpenseTypeNamePersian,
        int? CustomsDeclarationId);

}
