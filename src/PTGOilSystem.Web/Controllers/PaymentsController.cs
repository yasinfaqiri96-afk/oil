using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Infrastructure.RateLimiting;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.PartyStatements;
using ServiceProviderEntity = PTGOilSystem.Web.Models.Entities.ServiceProvider;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class PaymentsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly ISupplierPaymentAllocationService _allocations;
    private readonly IPaymentCorrectionService _correction;
    private readonly ISarrafSettlementService _sarrafSettlements;
    private readonly IAuditService _audit;
    private readonly ILogger<PaymentsController> _logger;
    private readonly IPurchaseAggregationService _purchaseAggregation;
    private readonly IMemoryCache? _summaryCache;
    private readonly IFormTokenGuard _formTokens;
    private readonly IPartyStatementReadService? _partyStatements;
    // مرحله ۴ — Dual-write اختیاری به دفتر کل جدید. پشت Feature Flag و null-safe: اگر تزریق
    // نشود یا خاموش باشد، مسیر قدیمی هیچ تغییری نمی‌کند.
    private readonly Services.Accounting.IPaymentAccountingAdapter? _paymentAccounting;
    private readonly Services.Accounting.IViaSarrafAccountingAdapter? _viaSarrafAccounting;
    private readonly Services.Accounting.IExpenseAccountingAdapter? _expenseAccounting;
    private const int IndexPageSize = 20;
    private const int LookupLimit = 200;
    public const string ViaSarrafSupplierLedgerSourceType = "SupplierViaSarrafPayment";
    public const string ViaSarrafPayableLedgerSourceType = "SupplierViaSarrafPayable";
    // Display-only KPI cards (today totals + cash balances). Aggregates the full
    // PaymentTransactions table, so cache briefly: same numbers, no per-open scan.
    private const string SummaryCacheKey = "payments-index-summary-v1";
    private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromSeconds(45);

    [ActivatorUtilitiesConstructor]
    public PaymentsController(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        ISupplierPaymentAllocationService allocations,
        ISarrafSettlementService sarrafSettlements,
        IAuditService audit,
        ILogger<PaymentsController> logger,
        IPurchaseAggregationService? purchaseAggregation = null,
        IMemoryCache? summaryCache = null,
        IFormTokenGuard? formTokens = null,
        Services.Accounting.IPaymentAccountingAdapter? paymentAccounting = null,
        Services.Accounting.IViaSarrafAccountingAdapter? viaSarrafAccounting = null,
        Services.Accounting.IExpenseAccountingAdapter? expenseAccounting = null,
        IPartyStatementReadService? partyStatements = null,
        IPaymentCorrectionService? paymentCorrection = null)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _allocations = allocations;
        // Central reverse-and-repost for corrections. Built from the already-injected pieces when
        // DI does not supply one, so the lighter test constructor keeps working unchanged.
        _correction = paymentCorrection ?? new PaymentCorrectionService(db, allocations, paymentAccounting);
        _sarrafSettlements = sarrafSettlements;
        _audit = audit;
        _logger = logger;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
        _summaryCache = summaryCache;
        _formTokens = formTokens ?? new FormTokenGuard(db);
        _paymentAccounting = paymentAccounting;
        _viaSarrafAccounting = viaSarrafAccounting;
        _expenseAccounting = expenseAccounting;
        _partyStatements = partyStatements;
    }

    public PaymentsController(
        ApplicationDbContext db,
        IPricingService pricing,
        IAuditService audit,
        ILogger<PaymentsController> logger)
        : this(
            db,
            new CurrencyConversionService(pricing),
            new SupplierPaymentAllocationService(db),
            new SarrafSettlementService(db),
            audit,
            logger)
    {
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

    public async Task<IActionResult> Index([FromQuery] PaymentIndexFilterViewModel? filter = null, int page = 1)
    {
        filter ??= new PaymentIndexFilterViewModel();
        NormalizeFilter(filter);
        await PopulateLookupsAsync(filter: filter);
        var (rows, totalCount, currentPage, pageCount) = await BuildRowsAsync(filter, page);
        var summary = await BuildSummaryAsync();

        return View(new PaymentIndexViewModel
        {
            Filter = filter,
            Items = rows,
            CurrentPage = currentPage,
            PageCount = pageCount,
            TotalCount = totalCount,
            TodayReceiptUsd = summary.TodayReceiptUsd,
            TodayPaymentUsd = summary.TodayPaymentUsd,
            CashAccountsBalanceUsd = summary.CashAccountsBalanceUsd,
            LastDocumentReference = summary.LastDocumentReference,
            LastDocumentDate = summary.LastDocumentDate,
            CashAccountBalances = summary.CashAccountBalances
        });
    }

    // مرکز رزنامچه و تسویه‌ها — صفحه‌ٔ کاملاً read-only برای navigation و خلاصه.
    // هیچ PaymentTransaction/Ledger/CashAccount ساخته یا تغییر داده نمی‌شود؛ فقط queryهای AsNoTracking.
    [HttpGet]
    public async Task<IActionResult> Hub()
    {
        var today = DateTime.UtcNow.Date;

        var todayTotals = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.PaymentDate == today)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ReceiptUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => (decimal?)p.AmountUsd) ?? 0m,
                PaymentUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => (decimal?)p.AmountUsd) ?? 0m
            })
            .FirstOrDefaultAsync();

        // پول معلق: پرداختی که هیچ طرف‌حسابی ندارد (همان معیار critical رزنامچه در مرکز تطبیق).
        var suspenseCount = await _db.PaymentTransactions
            .AsNoTracking()
            .CountAsync(p => p.CustomerId == null && p.SupplierId == null
                && p.ServiceProviderId == null && p.SarrafId == null
                && p.EmployeeId == null && p.DriverId == null);

        var postedHawalaCount = await _db.ThreeWaySettlements
            .AsNoTracking()
            .CountAsync(s => s.Status == ThreeWaySettlementStatus.Posted);

        // نیازمند بررسی: حواله ثبت‌شده با تأمین‌کننده نامشخص یا Ledger ناقص.
        var needsReviewCount = await _db.ThreeWaySettlements
            .AsNoTracking()
            .CountAsync(s => s.Status == ThreeWaySettlementStatus.Posted
                && (s.SupplierId == null || s.CustomerLedgerEntryId == null || s.SupplierLedgerEntryId == null));

        return View(new TreasuryHubViewModel
        {
            TodayReceiptUsd = todayTotals?.ReceiptUsd ?? 0m,
            TodayPaymentUsd = todayTotals?.PaymentUsd ?? 0m,
            SuspenseCount = suspenseCount,
            PostedHawalaCount = postedHawalaCount,
            NeedsReviewCount = needsReviewCount
        });
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Csv([FromQuery] PaymentIndexFilterViewModel? filter = null)
    {
        filter ??= new PaymentIndexFilterViewModel();

        // بازهٔ تاریخ الزامی است تا خروجی کل روزنامچه گرفته نشود.
        if (!filter.FromDate.HasValue || !filter.ToDate.HasValue)
        {
            TempData["error"] = "برای خروجی CSV، تعیین «از تاریخ» و «تا تاریخ» الزامی است.";
            return RedirectToAction(nameof(Index), filter);
        }

        var (rows, _, _, _) = await BuildRowsAsync(filter, page: 0);

        // روزنامچه از ادغام دو منبع (پرداخت‌ها + حواله‌های صراف) در حافظه ساخته می‌شود، پس
        // شمارش فقط پس از ادغام ممکن است؛ بازهٔ تاریخ بالا حجم را از قبل محدود می‌کند.
        if (rows.Count > CsvExportSupport.MaxRows)
        {
            TempData["error"] =
                $"این خروجی {rows.Count:N0} ردیف دارد و از سقف مجاز {CsvExportSupport.MaxRows:N0} ردیف بیشتر است. " +
                "بازهٔ تاریخ را کوتاه‌تر کنید یا فیلتر بیشتری اعمال کنید.";
            return RedirectToAction(nameof(Index), filter);
        }

        return CsvExportSupport.File(this, "roznamcha.csv",
            ["Date", "Reference", "Direction", "Kind", "CounterpartyType", "Counterparty", "CashAccount", "Amount", "Currency", "AmountUsd", "RelatedTo", "Description", "CreatedBy", "LedgerEntryId"],
            rows.Select(r => new[]
            {
                CsvExportSupport.Date(r.PaymentDate),
                r.Reference,
                r.DirectionName,
                r.PaymentKindName,
                r.CounterpartyTypeName,
                r.CounterpartyName,
                r.CashAccountName,
                CsvExportSupport.Decimal(r.Amount),
                r.Currency,
                CsvExportSupport.Decimal(r.AmountUsd),
                r.RelatedTo,
                r.Description,
                r.CreatedByDisplay,
                r.LedgerEntryId?.ToString()
            }));
    }

    [EnableRateLimiting(RateLimitPolicies.CsvExport)]
    public async Task<IActionResult> Export(string? format, [FromQuery] PaymentIndexFilterViewModel? filter = null)
    {
        filter ??= new PaymentIndexFilterViewModel();
        NormalizeFilter(filter);
        if (!filter.FromDate.HasValue || !filter.ToDate.HasValue)
        {
            TempData["error"] = UiText.T(HttpContext,
                "برای خروجی، تعیین «از تاریخ» و «تا تاریخ» الزامی است.",
                "From date and to date are required for export.");
            return RedirectToAction(nameof(Index), filter);
        }

        var (rows, _, _, _) = await BuildRowsAsync(filter, page: 0);
        var document = new TabularExportDocument
        {
            FileNameStem = "PTG_Roznamcha",
            TitleFa = "روزنامچه دریافت و پرداخت",
            TitleEn = "Receipts and Payments Journal",
            KnownRowCount = rows.Count,
            ForceLandscape = true,
            Filters =
            [
                new("از تاریخ", "From date", filter.FromDate?.ToString("yyyy-MM-dd")),
                new("تا تاریخ", "To date", filter.ToDate?.ToString("yyyy-MM-dd")),
                new("جستجو", "Search", filter.Search),
                new("مشتری", "Customer", filter.CustomerId?.ToString()),
                new("تأمین‌کننده", "Supplier", filter.SupplierId?.ToString()),
                new("حساب نقدی", "Cash account", filter.CashAccountId?.ToString())
            ],
            Columns =
            [
                new("تاریخ", "Date", TabularExportValueType.Date, 13), new("شماره سند", "Reference", Width: 18),
                new("جهت", "Direction", Width: 11), new("نوع", "Kind", Width: 16), new("نوع طرف حساب", "Counterparty type", Width: 16),
                new("طرف حساب", "Counterparty", Width: 20), new("حساب نقدی/بانکی", "Cash account", Width: 20),
                new("مبلغ", "Amount", TabularExportValueType.Number, 16), new("ارز", "Currency", Width: 10),
                new("مبلغ USD", "Amount USD", TabularExportValueType.Number, 16), new("مرتبط به", "Related to", Width: 20),
                new("شرح", "Description", Width: 30, Wrap: true), new("ثبت‌کننده", "Created by", Width: 18)
            ],
            Rows = rows.Select(row => new TabularExportRow(
            [
                TabularExportCell.Date(row.PaymentDate), TabularExportCell.Text(row.Reference), TabularExportCell.Text(row.DirectionName),
                TabularExportCell.Text(row.PaymentKindName), TabularExportCell.Text(row.CounterpartyTypeName), TabularExportCell.Text(row.CounterpartyName),
                TabularExportCell.Text(row.CashAccountName), TabularExportCell.Number(row.Amount), TabularExportCell.Text(row.Currency),
                TabularExportCell.Number(row.AmountUsd), TabularExportCell.Text(row.RelatedTo), TabularExportCell.Text(row.Description),
                TabularExportCell.Text(row.CreatedByDisplay)
            ]))
        };

        return TabularExportSupport.File(this, format, document);
    }

    public async Task<IActionResult> Details(int id, string? returnUrl = null)
    {
        var payment = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id,
                p.PaymentDate,
                p.Direction,
                p.PaymentKind,
                CashAccountCode = p.CashAccount != null ? p.CashAccount.Code : string.Empty,
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : string.Empty,
                CashAccountType = p.CashAccount != null ? (CashAccountType?)p.CashAccount.AccountType : null,
                p.Currency,
                p.Amount,
                p.AmountUsd,
                p.AppliedFxRateToUsd,
                p.CustomerId,
                CustomerName = p.Customer != null ? p.Customer.Name : null,
                p.SupplierId,
                SupplierName = p.Supplier != null ? p.Supplier.Name : null,
                p.ServiceProviderId,
                ServiceProviderName = p.ServiceProvider != null ? p.ServiceProvider.Name : null,
                p.SarrafId,
                SarrafName = p.Sarraf != null ? p.Sarraf.Name : null,
                p.DriverId,
                DriverName = p.Driver != null ? p.Driver.FullName : null,
                p.EmployeeId,
                EmployeeName = p.Employee != null ? p.Employee.FullName : null,
                p.ContractId,
                ContractNumber = p.Contract != null ? p.Contract.ContractNumber : null,
                p.ShipmentId,
                ShipmentCode = p.Shipment != null ? p.Shipment.ShipmentCode : null,
                p.SalesTransactionId,
                SalesInvoiceNumber = p.SalesTransaction != null ? p.SalesTransaction.InvoiceNumber : null,
                p.ExpenseTransactionId,
                ExpenseDescription = p.ExpenseTransaction != null ? p.ExpenseTransaction.Description : null,
                p.TruckDispatchId,
                TruckDispatchLabel = p.TruckDispatch == null
                    ? null
                    : $"#{p.TruckDispatch.Id} - {(p.TruckDispatch.Truck != null ? p.TruckDispatch.Truck.PlateNumber : "Ø¨Ø¯ÙˆÙ† Ù¾Ù„Ø§Ú©")}",
                p.Reference,
                p.Description,
                p.LedgerEntryId,
                p.RelatedExpenseTransactionId,
                LedgerSourceType = p.LedgerEntry != null ? p.LedgerEntry.SourceType : null,
                LedgerReference = p.LedgerEntry != null ? p.LedgerEntry.Reference : null,
                LedgerSide = p.LedgerEntry != null ? (LedgerSide?)p.LedgerEntry.Side : null,
                p.CreatedAtUtc,
                p.CreatedByUserId,
                p.UpdatedAtUtc,
                p.UpdatedByUserId
            })
            .FirstOrDefaultAsync();

        if (payment is null)
        {
            return NotFound();
        }

        ViewBag.ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null;

        // کمیسیون مرتبط (اگر باشد) — فقط نمایشی.
        decimal? commissionAmount = null;
        string? commissionCurrency = null;
        decimal? commissionAmountUsd = null;
        if (payment.RelatedExpenseTransactionId.HasValue)
        {
            var commission = await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.Id == payment.RelatedExpenseTransactionId.Value)
                .Select(e => new { e.Amount, e.Currency, e.AmountUsd })
                .FirstOrDefaultAsync();
            if (commission is not null)
            {
                commissionAmount = commission.Amount;
                commissionCurrency = commission.Currency;
                commissionAmountUsd = commission.AmountUsd;
            }
        }

        var supportsAllocation = payment.PaymentKind == PaymentKind.SupplierPayment && payment.SupplierId.HasValue;
        var allocations = new List<SupplierPaymentAllocationListItemViewModel>();
        var allocatedBookUsd = 0m;
        var activeAllocationCount = 0;
        var allocatableBalanceUsd = 0m;
        var allocatedPaymentAmount = 0m;
        var allocatableBalanceAmount = 0m;
        var allocationExchangeDifferenceUsd = 0m;
        if (supportsAllocation)
        {
            allocations = await _db.SupplierPaymentAllocations
                .AsNoTracking()
                .Where(a => a.PaymentTransactionId == payment.Id)
                .OrderByDescending(a => a.AllocationDate)
                .ThenByDescending(a => a.Id)
                .Select(a => new SupplierPaymentAllocationListItemViewModel
                {
                    Id = a.Id,
                    AllocationDate = a.AllocationDate,
                    ContractId = a.ContractId,
                    ContractNumber = a.Contract != null ? a.Contract.ContractNumber : string.Empty,
                    AllocatedPaymentAmount = a.AllocatedPaymentAmount,
                    PaymentCurrencyCode = a.PaymentCurrencyCode,
                    AllocatedBookAmountUsd = a.AllocatedBookAmountUsd,
                    PaymentCurrencyPerUsdRateAtAllocation = a.PaymentCurrencyPerUsdRateAtAllocation,
                    AllocatedValueUsdAtAllocation = a.AllocatedValueUsdAtAllocation,
                    ExchangeDifferenceUsd = a.ExchangeDifferenceUsd,
                    ExchangeDifferenceType = a.ExchangeDifferenceType,
                    ContractCurrencyCode = a.ContractCurrencyCode,
                    ContractCurrencyPerUsdRate = a.ContractCurrencyPerUsdRate,
                    AllocatedContractCurrencyAmount = a.AllocatedContractCurrencyAmount,
                    ReferenceNumber = a.ReferenceNumber,
                    Status = a.Status,
                    ReversedAtUtc = a.ReversedAtUtc,
                    ReversalReason = a.ReversalReason,
                    CreatedByUserName = a.CreatedByUserName
                })
                .ToListAsync();

            allocatedBookUsd = allocations
                .Where(a => a.IsActive)
                .Sum(a => a.AllocatedBookAmountUsd);
            activeAllocationCount = allocations.Count(a => a.IsActive);
            allocatableBalanceUsd = decimal.Round(payment.AmountUsd - allocatedBookUsd, 4, MidpointRounding.AwayFromZero);

            // مانده واقعی به ارز پرداخت — سقف تخصیص همین است، نه معادل دلاری آن.
            allocatedPaymentAmount = allocations.Where(a => a.IsActive).Sum(a => a.AllocatedPaymentAmount);
            allocatableBalanceAmount = decimal.Round(payment.Amount - allocatedPaymentAmount, 4, MidpointRounding.AwayFromZero);
            allocationExchangeDifferenceUsd = allocations.Where(a => a.IsActive).Sum(a => a.ExchangeDifferenceUsd);
        }

        return View(new PaymentDetailsViewModel
        {
            CommissionExpenseTransactionId = payment.RelatedExpenseTransactionId,
            CommissionAmount = commissionAmount,
            CommissionCurrency = commissionCurrency,
            CommissionAmountUsd = commissionAmountUsd,
            SupportsAdvanceAllocation = supportsAllocation,
            AllocatedBookAmountUsd = allocatedBookUsd,
            AllocatableBalanceUsd = allocatableBalanceUsd,
            AllocatedPaymentAmountTotal = allocatedPaymentAmount,
            AllocatableBalanceAmount = allocatableBalanceAmount,
            AllocationExchangeDifferenceUsd = allocationExchangeDifferenceUsd,
            ActiveAllocationCount = activeAllocationCount,
            Allocations = allocations,
            Id = payment.Id,
            PaymentDate = payment.PaymentDate,
            Direction = payment.Direction,
            DirectionName = PaymentDirectionLabels.ToPersian(payment.Direction),
            PaymentKind = payment.PaymentKind,
            PaymentKindName = PaymentKindLabels.ToPersian(payment.PaymentKind),
            CashAccountCode = payment.CashAccountCode,
            CashAccountName = payment.CashAccountName,
            CashAccountTypeName = payment.CashAccountType.HasValue ? CashAccountTypeLabels.ToPersian(payment.CashAccountType.Value) : string.Empty,
            Currency = payment.Currency,
            Amount = payment.Amount,
            AmountUsd = payment.AmountUsd,
            AppliedFxRateToUsd = payment.AppliedFxRateToUsd,
            CustomerId = payment.CustomerId,
            CustomerName = payment.CustomerName,
            SupplierId = payment.SupplierId,
            SupplierName = payment.SupplierName,
            ServiceProviderId = payment.ServiceProviderId,
            ServiceProviderName = payment.ServiceProviderName,
            SarrafId = payment.SarrafId,
            SarrafName = payment.SarrafName,
            DriverId = payment.DriverId,
            DriverName = payment.DriverName,
            EmployeeId = payment.EmployeeId,
            EmployeeName = payment.EmployeeName,
            ContractId = payment.ContractId,
            ContractNumber = payment.ContractNumber,
            ShipmentId = payment.ShipmentId,
            ShipmentCode = payment.ShipmentCode,
            SalesTransactionId = payment.SalesTransactionId,
            SalesInvoiceNumber = payment.SalesInvoiceNumber,
            ExpenseTransactionId = payment.ExpenseTransactionId,
            ExpenseDescription = payment.ExpenseDescription,
            TruckDispatchId = payment.TruckDispatchId,
            TruckDispatchLabel = payment.TruckDispatchLabel,
            Reference = payment.Reference,
            Description = payment.Description,
            LedgerEntryId = payment.LedgerEntryId,
            LedgerSourceType = payment.LedgerSourceType,
            LedgerReference = payment.LedgerReference,
            LedgerSideName = payment.LedgerSide.HasValue ? GetSideName(payment.LedgerSide.Value) : null,
            CreatedAtUtc = payment.CreatedAtUtc,
            CreatedByUserId = payment.CreatedByUserId,
            UpdatedAtUtc = payment.UpdatedAtUtc,
            UpdatedByUserId = payment.UpdatedByUserId
        });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create(
        int? contractId = null,
        int? customerId = null,
        int? supplierId = null,
        int? serviceProviderId = null,
        int? sarrafId = null,
        int? employeeId = null,
        int? driverId = null,
        int? expenseTransactionId = null,
        int? salesTransactionId = null,
        int? shipmentId = null,
        string? kind = null,
        string? method = null,
        string? returnUrl = null)
    {
        var model = new PaymentCreateViewModel
        {
            PaymentDate = DateTime.UtcNow.Date,
            Currency = SystemCurrency.BaseCurrencyCode,
            ContractId = contractId,
            CustomerId = customerId,
            SupplierId = supplierId,
            ServiceProviderId = serviceProviderId,
            SarrafId = sarrafId,
            EmployeeId = employeeId,
            DriverId = driverId,
            ExpenseTransactionId = expenseTransactionId,
            ShipmentId = shipmentId,
            ReturnUrl = returnUrl
        };

        // پیش‌انتخاب «پرداخت از طریق صراف» فقط وقتی method=sarraf صریح آمده باشد.
        // sarrafId به‌تنهایی مسیر عادی پرداخت/تسویه با صراف است.
        if (string.Equals(method?.Trim(), "sarraf", StringComparison.OrdinalIgnoreCase))
        {
            model.PaymentMethod = PaymentMethod.ViaSarraf;
            // پرداختِ صراف در عمل روبلی است؛ پیش‌فرض RUB تا مبلغ روبلی به‌اشتباه USD ثبت نشود.
            model.SarrafSupplierCurrency = "RUB";
        }

        if (supplierId.HasValue)
        {
            model.Direction = PaymentDirection.Out;
            model.PaymentKind = PaymentKind.SupplierPayment;
        }
        else if (serviceProviderId.HasValue)
        {
            model.Direction = PaymentDirection.Out;
            model.PaymentKind = PaymentKind.ServiceProviderPayment;
        }
        else if (sarrafId.HasValue)
        {
            model.Direction = PaymentDirection.Out;
            model.PaymentKind = PaymentKind.SarrafSettlement;
        }
        else if (customerId.HasValue)
        {
            model.Direction = PaymentDirection.In;
            model.PaymentKind = PaymentKind.CustomerReceipt;
        }
        else if (employeeId.HasValue)
        {
            model.Direction = PaymentDirection.Out;
            model.PaymentKind = PaymentKind.EmployeeSalaryPayment;
        }
        else if (driverId.HasValue)
        {
            model.Direction = PaymentDirection.Out;
            model.PaymentKind = PaymentKind.TruckPayment;
        }
        else if (expenseTransactionId.HasValue)
        {
            model.Direction = PaymentDirection.Out;
            model.PaymentKind = PaymentKind.ExpensePayment;
        }

        if (salesTransactionId.HasValue)
        {
            var sale = await _db.SalesTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == salesTransactionId.Value && !s.IsCancelled);
            if (sale is not null)
            {
                model.PaymentDate = sale.SaleDate;
                model.Direction = PaymentDirection.In;
                model.PaymentKind = PaymentKind.CustomerReceipt;
                model.SalesTransactionId = sale.Id;
                model.CustomerId = sale.CustomerId;
                model.Currency = sale.Currency;
                model.Amount = sale.TotalInCurrency;
                model.Reference = sale.InvoiceNumber;
            }
        }

        if (contractId.HasValue && !supplierId.HasValue && !customerId.HasValue && !salesTransactionId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId.Value);

            if (contract?.ContractType == ContractType.Purchase && contract.SupplierId.HasValue)
            {
                model.SupplierId = contract.SupplierId.Value;
                model.Direction = PaymentDirection.Out;
                model.PaymentKind = PaymentKind.SupplierPayment;
            }
            else if (contract?.ContractType == ContractType.Sale && contract.CustomerId.HasValue)
            {
                model.CustomerId = contract.CustomerId.Value;
                model.Direction = PaymentDirection.In;
                model.PaymentKind = PaymentKind.CustomerReceipt;
            }
        }

        // پیش‌فرض نوع سند از «مرکز رزنامچه» وقتی هیچ طرف‌حساب/سند خاصی از قبل تعیین نشده.
        // فقط مقدار پیش‌فرض فرم را تنظیم می‌کند؛ هیچ منطق posting/Ledger/CashAccount تغییر نمی‌کند.
        var noPreselectedParty = !supplierId.HasValue && !customerId.HasValue && !serviceProviderId.HasValue
            && !sarrafId.HasValue && !employeeId.HasValue && !driverId.HasValue
            && !expenseTransactionId.HasValue && !salesTransactionId.HasValue && !contractId.HasValue;
        if (noPreselectedParty && !string.IsNullOrWhiteSpace(kind))
        {
            switch (kind.Trim().ToLowerInvariant())
            {
                case "customer-receipt":
                    model.Direction = PaymentDirection.In;
                    model.PaymentKind = PaymentKind.CustomerReceipt;
                    break;
                case "supplier-payment":
                    model.Direction = PaymentDirection.Out;
                    model.PaymentKind = PaymentKind.SupplierPayment;
                    break;
                case "supplier-advance":
                    model.Direction = PaymentDirection.Out;
                    model.PaymentKind = PaymentKind.SupplierPayment;
                    model.IsAdvancePayment = true;
                    break;
                case "customer-advance":
                    model.Direction = PaymentDirection.In;
                    model.PaymentKind = PaymentKind.CustomerReceipt;
                    model.IsCustomerAdvance = true;
                    break;
            }
        }

        model.CounterpartyType = InferCounterpartyType(model);
        await PopulateLookupsAsync(createModel: model);
        // فقط نمایشی: همان خلاصهٔ آماری صفحه فهرست برای نمایش کارت‌ها بالای فرم.
        var formSummary = await BuildSummaryAsync();
        ViewBag.FormHasSummary = true;
        ViewBag.FormTodayReceiptUsd = formSummary.TodayReceiptUsd;
        ViewBag.FormTodayPaymentUsd = formSummary.TodayPaymentUsd;
        ViewBag.FormCashBalanceUsd = formSummary.CashAccountsBalanceUsd;
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        PaymentCreateViewModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        // روش پرداخت «از طریق صراف» یک جریان غیرنقدی ساده است:
        // تأمین‌کننده کم می‌شود، صراف طلبکار می‌شود، صندوق/بانک دست نمی‌خورد.
        if (model.PaymentMethod == PaymentMethod.ViaSarraf)
        {
            return await CreateViaSarrafAsync(model);
        }

        NormalizeCreateModel(model);
        var context = await ValidateAndResolveAsync(model);

        if (!ModelState.IsValid || context is null)
        {
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.PaymentDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.DocumentCurrencyPerUsdRate), ex.Message);
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        var amountUsd = conversion.ConvertToBase(model.Amount);

        // کمیسیون (اختیاری) — اعتبارسنجی و محاسبه قبل از هر نوشتنی روی DB تا در صورت خطا
        // فرم با مقادیر کمیسیون حفظ شود.
        CommissionComputation? commission = null;
        var commissionCashAccountId = context.CashAccount.Id;
        ExpenseType? commissionType = null;
        if (model.CommissionEnabled)
        {
            commission = await ValidateAndComputeCommissionAsync(
                model, model.Amount, conversion.SourceCurrencyCode, model.PaymentDate.Date,
                conversion.AppliedRateToBase, conversion.EffectiveDate.Date, conversion.SourceDescription);
            if (commission is not null)
            {
                commissionCashAccountId = await ValidateCommissionCashAccountAsync(model, context.CashAccount.Id, commission.Currency);
            }
            if (!ModelState.IsValid || commission is null)
            {
                await PopulateLookupsAsync(createModel: model);
                return View(model);
            }
            commissionType = await EnsureCommissionExpenseTypeAsync();
        }

        var payment = new PaymentTransaction
        {
            PaymentDate = model.PaymentDate.Date,
            Direction = model.Direction,
            PaymentKind = model.PaymentKind,
            CashAccountId = context.CashAccount.Id,
            CustomerId = context.CustomerId,
            SupplierId = context.SupplierId,
            ServiceProviderId = context.ServiceProviderId,
            SarrafId = context.SarrafId,
            DriverId = context.DriverId,
            EmployeeId = context.EmployeeId,
            ContractId = context.ContractId,
            ShipmentId = context.ShipmentId,
            SalesTransactionId = context.SalesTransactionId,
            ExpenseTransactionId = context.ExpenseTransactionId,
            TruckDispatchId = context.TruckDispatchId,
            Amount = model.Amount,
            Currency = conversion.SourceCurrencyCode,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            AmountUsd = amountUsd,
            Reference = model.Reference,
            Description = model.Description,
            // Phase 1 — نشانهٔ پیش‌پرداخت (فقط ثبت/نمایش، به Ledger اثر ندارد).
            IsAdvancePayment = model.IsAdvancePayment,
            // مرحله ۴ — نشانهٔ پیش‌دریافت مشتری (فقط ثبت/نمایش، به Ledger اثر ندارد).
            IsCustomerAdvance = model.IsCustomerAdvance
        };

        var ledgerEntry = new LedgerEntry
        {
            EntryDate = payment.PaymentDate,
            Side = GetLedgerSide(payment.PaymentKind),
            AmountUsd = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = payment.Amount,
            SourceCurrencyCode = payment.Currency,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            AppliedFxRateDate = conversion.EffectiveDate.Date,
            AppliedFxRateSource = conversion.SourceDescription,
            Description = BuildLedgerDescription(payment, context.CashAccount),
            SourceType = payment.PaymentKind.ToString(),
            Reference = payment.Reference,
            ContractId = payment.ContractId,
            CustomerId = payment.CustomerId,
            SupplierId = payment.SupplierId,
            ServiceProviderId = payment.ServiceProviderId,
            EmployeeId = payment.EmployeeId,
            ShipmentId = payment.ShipmentId
        };

        // Duplicate-submit guard: token persists atomically with the payment.
        _formTokens.Stamp(formToken, "Payment.Create", nameof(PaymentTransaction));

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            _db.PaymentTransactions.Add(payment);
            await _db.SaveChangesAsync();

            ledgerEntry.SourceId = payment.Id;
            _db.LedgerEntries.Add(ledgerEntry);
            await _db.SaveChangesAsync();

            payment.LedgerEntryId = ledgerEntry.Id;
            await _db.SaveChangesAsync();

            if (commission is not null && commissionType is not null)
            {
                await PostCashCommissionAsync(payment, commission, commissionCashAccountId, commissionType);
            }

            // مرحله ۴ — Dual-write به دفتر کل جدید داخل همان Transaction قدیمی. خاموش/Skip
            // بودن Pilot هیچ اثری روی مسیر قدیمی ندارد؛ خطای واقعی همان Rollback را می‌گیرد.
            if (_paymentAccounting is not null)
            {
                await _paymentAccounting.TryPostPaymentAsync(payment);
            }

            await _audit.LogAsync(
                nameof(PaymentTransaction),
                payment.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("PaymentDate", payment.PaymentDate),
                    ("Direction", payment.Direction),
                    ("PaymentKind", payment.PaymentKind),
                    ("CashAccountId", payment.CashAccountId),
                    ("CustomerId", payment.CustomerId),
                    ("SupplierId", payment.SupplierId),
                    ("ServiceProviderId", payment.ServiceProviderId),
                    ("SarrafId", payment.SarrafId),
                    ("DriverId", payment.DriverId),
                    ("EmployeeId", payment.EmployeeId),
                    ("ContractId", payment.ContractId),
                    ("ShipmentId", payment.ShipmentId),
                    ("SalesTransactionId", payment.SalesTransactionId),
                    ("ExpenseTransactionId", payment.ExpenseTransactionId),
                    ("TruckDispatchId", payment.TruckDispatchId),
                    ("Amount", payment.Amount),
                    ("Currency", payment.Currency),
                    ("AppliedFxRateToUsd", payment.AppliedFxRateToUsd),
                    ("AmountUsd", payment.AmountUsd),
                    ("Reference", payment.Reference),
                    ("LedgerEntryId", payment.LedgerEntryId)));

            await _audit.LogAsync(
                nameof(LedgerEntry),
                ledgerEntry.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("EntryDate", ledgerEntry.EntryDate),
                    ("Side", ledgerEntry.Side),
                    ("AmountUsd", ledgerEntry.AmountUsd),
                    ("SourceAmount", ledgerEntry.SourceAmount),
                    ("SourceCurrencyCode", ledgerEntry.SourceCurrencyCode),
                    ("AppliedFxRateToUsd", ledgerEntry.AppliedFxRateToUsd),
                    ("SourceType", ledgerEntry.SourceType),
                    ("SourceId", ledgerEntry.SourceId),
                    ("Reference", ledgerEntry.Reference)));

            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (DbUpdateException dup) when (_formTokens.IsDuplicate(dup))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            TempData["err"] = "این عملیات قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to create payment transaction.");
            ModelState.AddModelError(string.Empty, "ثبت پرداخت / دریافت انجام نشد. دوباره تلاش کنید.");
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        TempData["ok"] = "پرداخت / دریافت با موفقیت ثبت شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = payment.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var payment = await _db.PaymentTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (payment is null)
        {
            return NotFound();
        }

        var model = new PaymentCreateViewModel
        {
            Id = payment.Id,
            PaymentDate = payment.PaymentDate,
            Direction = payment.Direction,
            PaymentKind = payment.PaymentKind,
            CounterpartyType = InferCounterpartyType(payment),
            CashAccountId = payment.CashAccountId,
            CustomerId = payment.CustomerId,
            SupplierId = payment.SupplierId,
            ServiceProviderId = payment.ServiceProviderId,
            SarrafId = payment.SarrafId,
            DriverId = payment.DriverId,
            EmployeeId = payment.EmployeeId,
            ContractId = payment.ContractId,
            ShipmentId = payment.ShipmentId,
            SalesTransactionId = payment.SalesTransactionId,
            ExpenseTransactionId = payment.ExpenseTransactionId,
            TruckDispatchId = payment.TruckDispatchId,
            Amount = payment.Amount,
            Currency = payment.Currency,
            AppliedFxRateToUsd = payment.AppliedFxRateToUsd,
            // فیلد سادهٔ «نرخ دالر به ارز» (مثلاً ۷۷ برای روبل) از نرخ داخلی بازسازی می‌شود.
            DocumentCurrencyPerUsdRate =
                (!SystemCurrency.IsBaseCurrency(payment.Currency) && payment.AppliedFxRateToUsd is > 0m)
                    ? 1m / payment.AppliedFxRateToUsd.Value
                    : null,
            Reference = payment.Reference,
            Description = payment.Description,
            IsAdvancePayment = payment.IsAdvancePayment,
            IsCustomerAdvance = payment.IsCustomerAdvance,
            ReturnUrl = returnUrl
        };

        // کمیسیونِ موجود را در فرم نمایش بده. چون نوع درصدی/ثابت ذخیره نمی‌شود،
        // به‌صورت «مبلغ ثابت» بارگذاری می‌شود (ویرایش، بازسازی امن بدون duplicate).
        if (payment.RelatedExpenseTransactionId.HasValue)
        {
            var commission = await _db.ExpenseTransactions
                .AsNoTracking()
                .Where(e => e.Id == payment.RelatedExpenseTransactionId.Value)
                .Select(e => new { e.Amount, e.Currency })
                .FirstOrDefaultAsync();
            if (commission is not null)
            {
                model.CommissionEnabled = true;
                model.CommissionType = PaymentCommissionType.Fixed;
                model.CommissionFixedAmount = commission.Amount;
                model.CommissionCurrency = commission.Currency;
                model.CommissionCashAccountId = payment.CashAccountId;
            }
        }

        await PopulateLookupsAsync(createModel: model);
        ViewData["PaymentFormMode"] = "Edit";
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, PaymentCreateViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        NormalizeCreateModel(model);
        var context = await ValidateAndResolveAsync(model);

        if (!ModelState.IsValid || context is null)
        {
            await PopulateLookupsAsync(createModel: model);
            ViewData["PaymentFormMode"] = "Edit";
            return View("Create", model);
        }

        var payment = await _db.PaymentTransactions
            .Include(p => p.LedgerEntry)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (payment is null)
        {
            return NotFound();
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.Currency,
                model.PaymentDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            await PopulateLookupsAsync(createModel: model);
            ViewData["PaymentFormMode"] = "Edit";
            return View("Create", model);
        }

        // اگر این پرداخت تخصیص فعال به قرارداد دارد و یکی از فیلدهای مالی کلیدی تغییر کرده،
        // به‌جای قفل‌کردن ویرایش، تخصیص‌های قبلی در همین تراکنش برگشت داده می‌شوند تا مانده قرارداد
        // با مبلغ اشتباه باقی نماند. کاربر پس از اصلاح، در صورت نیاز دوباره تخصیص می‌دهد.
        var hasActiveAllocations = await _db.SupplierPaymentAllocations
            .AsNoTracking()
            .AnyAsync(a => a.PaymentTransactionId == payment.Id && a.Status == SupplierPaymentAllocationStatus.Active);
        var protectedFieldsChanged =
            payment.Amount != model.Amount
            || !string.Equals(payment.Currency, conversion.SourceCurrencyCode, StringComparison.OrdinalIgnoreCase)
            || payment.AppliedFxRateToUsd != conversion.AppliedRateToBase
            || payment.SupplierId != context.SupplierId
            || payment.PaymentDate.Date != model.PaymentDate.Date;
        var reverseAllocations = hasActiveAllocations && protectedFieldsChanged;

        var previousPayment = new
        {
            payment.PaymentDate,
            payment.Direction,
            payment.PaymentKind,
            payment.CashAccountId,
            payment.CustomerId,
            payment.SupplierId,
            payment.ServiceProviderId,
            payment.SarrafId,
            payment.DriverId,
            payment.EmployeeId,
            payment.ContractId,
            payment.ShipmentId,
            payment.SalesTransactionId,
            payment.ExpenseTransactionId,
            payment.TruckDispatchId,
            payment.Amount,
            payment.Currency,
            payment.AppliedFxRateToUsd,
            payment.AmountUsd,
            payment.Reference,
            payment.Description,
            payment.LedgerEntryId
        };

        var ledgerEntry = payment.LedgerEntry;
        var ledgerIsNew = ledgerEntry is null;
        var previousLedger = ledgerEntry is null
            ? null
            : new
            {
                ledgerEntry.EntryDate,
                ledgerEntry.Side,
                ledgerEntry.AmountUsd,
                ledgerEntry.SourceAmount,
                ledgerEntry.SourceCurrencyCode,
                ledgerEntry.AppliedFxRateToUsd,
                ledgerEntry.AppliedFxRateDate,
                ledgerEntry.AppliedFxRateSource,
                ledgerEntry.Description,
                ledgerEntry.SourceType,
                ledgerEntry.SourceId,
                ledgerEntry.Reference,
                ledgerEntry.ContractId,
                ledgerEntry.CustomerId,
                ledgerEntry.SupplierId,
                ledgerEntry.ServiceProviderId,
                ledgerEntry.EmployeeId,
                ledgerEntry.ShipmentId
            };

        if (ledgerEntry is null)
        {
            ledgerEntry = new LedgerEntry();
            _db.LedgerEntries.Add(ledgerEntry);
        }

        var amountUsd = conversion.ConvertToBase(model.Amount);

        // کمیسیون (اختیاری) — اعتبارسنجی قبل از نوشتن. رفتار ویرایش: رکوردهای کمیسیونِ قبلی
        // حذف امن و در صورت فعال‌بودن دوباره ساخته می‌شوند (بدون duplicate).
        CommissionComputation? commission = null;
        var commissionCashAccountId = context.CashAccount.Id;
        ExpenseType? commissionType = null;
        if (model.CommissionEnabled)
        {
            commission = await ValidateAndComputeCommissionAsync(
                model, model.Amount, conversion.SourceCurrencyCode, model.PaymentDate.Date,
                conversion.AppliedRateToBase, conversion.EffectiveDate.Date, conversion.SourceDescription);
            if (commission is not null)
            {
                commissionCashAccountId = await ValidateCommissionCashAccountAsync(model, context.CashAccount.Id, commission.Currency);
            }
            if (!ModelState.IsValid || commission is null)
            {
                await PopulateLookupsAsync(createModel: model);
                ViewData["PaymentFormMode"] = "Edit";
                return View("Create", model);
            }
            commissionType = await EnsureCommissionExpenseTypeAsync();
        }

        payment.PaymentDate = model.PaymentDate.Date;
        payment.Direction = model.Direction;
        payment.PaymentKind = model.PaymentKind;
        payment.CashAccountId = context.CashAccount.Id;
        payment.CustomerId = context.CustomerId;
        payment.SupplierId = context.SupplierId;
        payment.ServiceProviderId = context.ServiceProviderId;
        payment.SarrafId = context.SarrafId;
        payment.DriverId = context.DriverId;
        payment.EmployeeId = context.EmployeeId;
        payment.ContractId = context.ContractId;
        payment.ShipmentId = context.ShipmentId;
        payment.SalesTransactionId = context.SalesTransactionId;
        payment.ExpenseTransactionId = context.ExpenseTransactionId;
        payment.TruckDispatchId = context.TruckDispatchId;
        payment.Amount = model.Amount;
        payment.Currency = conversion.SourceCurrencyCode;
        payment.AppliedFxRateToUsd = conversion.AppliedRateToBase;
        payment.AmountUsd = amountUsd;
        payment.Reference = model.Reference;
        payment.Description = model.Description;
        payment.IsAdvancePayment = model.IsAdvancePayment;
        payment.IsCustomerAdvance = model.IsCustomerAdvance;

        ledgerEntry.EntryDate = payment.PaymentDate;
        ledgerEntry.Side = GetLedgerSide(payment.PaymentKind);
        ledgerEntry.AmountUsd = amountUsd;
        ledgerEntry.Currency = SystemCurrency.BaseCurrencyCode;
        ledgerEntry.SourceAmount = payment.Amount;
        ledgerEntry.SourceCurrencyCode = payment.Currency;
        ledgerEntry.AppliedFxRateToUsd = conversion.AppliedRateToBase;
        ledgerEntry.AppliedFxRateDate = conversion.EffectiveDate.Date;
        ledgerEntry.AppliedFxRateSource = conversion.SourceDescription;
        ledgerEntry.Description = BuildLedgerDescription(payment, context.CashAccount);
        ledgerEntry.SourceType = payment.PaymentKind.ToString();
        ledgerEntry.SourceId = payment.Id;
        ledgerEntry.Reference = payment.Reference;
        ledgerEntry.ContractId = payment.ContractId;
        ledgerEntry.CustomerId = payment.CustomerId;
        ledgerEntry.SupplierId = payment.SupplierId;
        ledgerEntry.ServiceProviderId = payment.ServiceProviderId;
        ledgerEntry.EmployeeId = payment.EmployeeId;
        ledgerEntry.ShipmentId = payment.ShipmentId;

        var correctionResult = new PaymentCorrectionResult(0, false);
        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            await _db.SaveChangesAsync();

            if (payment.LedgerEntryId != ledgerEntry.Id)
            {
                payment.LedgerEntryId = ledgerEntry.Id;
                await _db.SaveChangesAsync();
            }

            // اصلاح مرکزی و اتمیک: تخصیص‌های وابسته را برگشت بده و سند دوبل را با مقادیر جدید
            // هم‌گام کن. وقتی هستهٔ حسابداری خاموش است، بخش Journal بی‌اثر (Skip) می‌ماند و رفتار
            // قدیمی دست‌نخورده باقی می‌ماند.
            var journalRelevantChanged =
                previousPayment.Direction != payment.Direction
                || previousPayment.PaymentKind != payment.PaymentKind
                || previousPayment.CashAccountId != payment.CashAccountId
                || previousPayment.SupplierId != payment.SupplierId
                || previousPayment.CustomerId != payment.CustomerId
                || previousPayment.SarrafId != payment.SarrafId
                || previousPayment.ContractId != payment.ContractId
                || previousPayment.ExpenseTransactionId != payment.ExpenseTransactionId
                || previousPayment.Amount != payment.Amount
                || !string.Equals(previousPayment.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)
                || previousPayment.AppliedFxRateToUsd != payment.AppliedFxRateToUsd
                || previousPayment.PaymentDate.Date != payment.PaymentDate.Date;

            correctionResult = await _correction.ApplyAsync(
                payment,
                reverseAllocations,
                journalRelevantChanged,
                "اصلاح تراکنش پرداخت/دریافت",
                User?.Identity?.Name);

            // کمیسیون: حذف رکوردهای قبلی و ساخت دوباره در صورت فعال‌بودن (بدون duplicate).
            await RemoveCashCommissionAsync(payment);
            if (commission is not null && commissionType is not null)
            {
                await PostCashCommissionAsync(payment, commission, commissionCashAccountId, commissionType);
            }

            await _audit.LogAsync(
                nameof(PaymentTransaction),
                payment.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("PaymentDate", previousPayment.PaymentDate, payment.PaymentDate),
                    ("Direction", previousPayment.Direction, payment.Direction),
                    ("PaymentKind", previousPayment.PaymentKind, payment.PaymentKind),
                    ("CashAccountId", previousPayment.CashAccountId, payment.CashAccountId),
                    ("CustomerId", previousPayment.CustomerId, payment.CustomerId),
                    ("SupplierId", previousPayment.SupplierId, payment.SupplierId),
                    ("ServiceProviderId", previousPayment.ServiceProviderId, payment.ServiceProviderId),
                    ("SarrafId", previousPayment.SarrafId, payment.SarrafId),
                    ("DriverId", previousPayment.DriverId, payment.DriverId),
                    ("EmployeeId", previousPayment.EmployeeId, payment.EmployeeId),
                    ("ContractId", previousPayment.ContractId, payment.ContractId),
                    ("ShipmentId", previousPayment.ShipmentId, payment.ShipmentId),
                    ("SalesTransactionId", previousPayment.SalesTransactionId, payment.SalesTransactionId),
                    ("ExpenseTransactionId", previousPayment.ExpenseTransactionId, payment.ExpenseTransactionId),
                    ("TruckDispatchId", previousPayment.TruckDispatchId, payment.TruckDispatchId),
                    ("Amount", previousPayment.Amount, payment.Amount),
                    ("Currency", previousPayment.Currency, payment.Currency),
                    ("AppliedFxRateToUsd", previousPayment.AppliedFxRateToUsd, payment.AppliedFxRateToUsd),
                    ("AmountUsd", previousPayment.AmountUsd, payment.AmountUsd),
                    ("Reference", previousPayment.Reference, payment.Reference),
                    ("Description", previousPayment.Description, payment.Description),
                    ("LedgerEntryId", previousPayment.LedgerEntryId, payment.LedgerEntryId)));

            await _audit.LogAsync(
                nameof(LedgerEntry),
                ledgerEntry.Id,
                ledgerIsNew ? AuditAction.Insert : AuditAction.Update,
                diff: ledgerIsNew || previousLedger is null
                    ? AuditDiffFormatter.ForCreate(
                        ("EntryDate", ledgerEntry.EntryDate),
                        ("Side", ledgerEntry.Side),
                        ("AmountUsd", ledgerEntry.AmountUsd),
                        ("SourceAmount", ledgerEntry.SourceAmount),
                        ("SourceCurrencyCode", ledgerEntry.SourceCurrencyCode),
                        ("AppliedFxRateToUsd", ledgerEntry.AppliedFxRateToUsd),
                        ("SourceType", ledgerEntry.SourceType),
                        ("SourceId", ledgerEntry.SourceId),
                        ("Reference", ledgerEntry.Reference))
                    : AuditDiffFormatter.ForUpdate(
                        ("EntryDate", previousLedger.EntryDate, ledgerEntry.EntryDate),
                        ("Side", previousLedger.Side, ledgerEntry.Side),
                        ("AmountUsd", previousLedger.AmountUsd, ledgerEntry.AmountUsd),
                        ("SourceAmount", previousLedger.SourceAmount, ledgerEntry.SourceAmount),
                        ("SourceCurrencyCode", previousLedger.SourceCurrencyCode, ledgerEntry.SourceCurrencyCode),
                        ("AppliedFxRateToUsd", previousLedger.AppliedFxRateToUsd, ledgerEntry.AppliedFxRateToUsd),
                        ("AppliedFxRateDate", previousLedger.AppliedFxRateDate, ledgerEntry.AppliedFxRateDate),
                        ("AppliedFxRateSource", previousLedger.AppliedFxRateSource, ledgerEntry.AppliedFxRateSource),
                        ("Description", previousLedger.Description, ledgerEntry.Description),
                        ("SourceType", previousLedger.SourceType, ledgerEntry.SourceType),
                        ("SourceId", previousLedger.SourceId, ledgerEntry.SourceId),
                        ("Reference", previousLedger.Reference, ledgerEntry.Reference),
                        ("ContractId", previousLedger.ContractId, ledgerEntry.ContractId),
                        ("CustomerId", previousLedger.CustomerId, ledgerEntry.CustomerId),
                        ("SupplierId", previousLedger.SupplierId, ledgerEntry.SupplierId),
                        ("ServiceProviderId", previousLedger.ServiceProviderId, ledgerEntry.ServiceProviderId),
                        ("EmployeeId", previousLedger.EmployeeId, ledgerEntry.EmployeeId),
                        ("ShipmentId", previousLedger.ShipmentId, ledgerEntry.ShipmentId)));

            await _db.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to update payment transaction {PaymentTransactionId}.", id);
            ModelState.AddModelError(string.Empty, "ویرایش پرداخت / دریافت انجام نشد. دوباره تلاش کنید.");
            await PopulateLookupsAsync(createModel: model);
            ViewData["PaymentFormMode"] = "Edit";
            return View("Create", model);
        }

        TempData["ok"] = correctionResult.ReversedAllocationCount > 0
            ? $"پرداخت / دریافت ویرایش شد. {correctionResult.ReversedAllocationCount} تخصیص قبلی قرارداد به‌صورت خودکار برگشت داده شد؛ در صورت نیاز دوباره تخصیص دهید."
            : "پرداخت / دریافت با موفقیت ویرایش شد.";
        if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = payment.Id });
    }

    // ===== پیش‌پرداخت تأمین‌کننده: مصرف برای قرارداد و برگشت تخصیص =====

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> AllocateToContract(int paymentId, string? returnUrl = null)
    {
        var payment = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Supplier)
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment is null)
        {
            return NotFound();
        }

        if (payment.PaymentKind != PaymentKind.SupplierPayment || !payment.SupplierId.HasValue)
        {
            TempData["error"] = "فقط «پرداخت به تأمین‌کننده» قابل تخصیص به قرارداد است.";
            return RedirectToAction(nameof(Details), new { id = paymentId });
        }

        var model = new SupplierPaymentAllocationCreateViewModel
        {
            PaymentTransactionId = payment.Id,
            SupplierId = payment.SupplierId.Value,
            SupplierName = payment.Supplier?.Name ?? string.Empty,
            PaymentReference = string.IsNullOrWhiteSpace(payment.Reference) ? $"#{payment.Id}" : payment.Reference!,
            PaymentDate = payment.PaymentDate,
            PaymentAmount = payment.Amount,
            PaymentCurrencyCode = payment.Currency,
            PaymentAmountUsd = payment.AmountUsd,
            AllocatableBalanceUsd = await _allocations.GetAllocatableBalanceUsdAsync(payment.Id),
            AllocatableBalanceAmount = await _allocations.GetAllocatablePaymentAmountAsync(payment.Id),
            AllocationDate = DateTime.UtcNow.Date,
            ContractCurrencyPerUsdRate = 1m,
            // پیش‌فرض = نرخ روز پرداخت؛ کاربر آن را با نرخ روز تخصیص جایگزین می‌کند.
            PaymentCurrencyPerUsdRateAtAllocation = PaymentPerUsdRate(payment),
            PaymentCurrencyPerUsdRateAtPayment = PaymentPerUsdRate(payment),
            ReturnUrl = TryGetLocalReturnUrl(returnUrl, out var localReturnUrl) ? localReturnUrl : null
        };

        await PopulateAllocationFormAsync(model, payment.SupplierId.Value);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AllocateToContract(SupplierPaymentAllocationCreateViewModel model)
    {
        model.ReferenceNumber = string.IsNullOrWhiteSpace(model.ReferenceNumber) ? null : model.ReferenceNumber.Trim();
        model.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        if (!ModelState.IsValid)
        {
            await RefreshAllocationFormAsync(model);
            return View(model);
        }

        try
        {
            var allocation = await _allocations.CreateAsync(new SupplierPaymentAllocationCreateRequest(
                model.PaymentTransactionId,
                model.ContractId,
                model.AllocationDate,
                model.AllocatedPaymentAmount,
                model.ContractCurrencyPerUsdRate,
                model.ReferenceNumber,
                model.Notes,
                CurrentUserName(),
                model.PaymentCurrencyPerUsdRateAtAllocation));

            await _audit.LogAndSaveAsync(
                nameof(SupplierPaymentAllocation),
                allocation.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("PaymentTransactionId", allocation.PaymentTransactionId),
                    ("ContractId", allocation.ContractId),
                    ("AllocationDate", allocation.AllocationDate),
                    ("AllocatedPaymentAmount", allocation.AllocatedPaymentAmount),
                    ("PaymentCurrencyCode", allocation.PaymentCurrencyCode),
                    ("AllocatedBookAmountUsd", allocation.AllocatedBookAmountUsd),
                    ("PaymentCurrencyPerUsdRateAtAllocation", allocation.PaymentCurrencyPerUsdRateAtAllocation),
                    ("AllocatedValueUsdAtAllocation", allocation.AllocatedValueUsdAtAllocation),
                    ("ExchangeDifferenceUsd", allocation.ExchangeDifferenceUsd),
                    ("ExchangeDifferenceType", allocation.ExchangeDifferenceType),
                    ("ContractCurrencyCode", allocation.ContractCurrencyCode),
                    ("ContractCurrencyPerUsdRate", allocation.ContractCurrencyPerUsdRate),
                    ("ContractCurrencyFxRateToUsd", allocation.ContractCurrencyFxRateToUsd),
                    ("AllocatedContractCurrencyAmount", allocation.AllocatedContractCurrencyAmount)));

            TempData["ok"] = "مصرف پیش‌پرداخت برای قرارداد ثبت شد.";
            if (TryGetLocalReturnUrl(model.ReturnUrl, out var localReturnUrl))
            {
                return Redirect(localReturnUrl);
            }

            return RedirectToAction(nameof(Details), new { id = model.PaymentTransactionId });
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await RefreshAllocationFormAsync(model);
            return View(model);
        }
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReverseAllocation(int allocationId, string? reason, string? returnUrl = null)
    {
        var paymentId = await _db.SupplierPaymentAllocations
            .AsNoTracking()
            .Where(a => a.Id == allocationId)
            .Select(a => (int?)a.PaymentTransactionId)
            .FirstOrDefaultAsync();

        if (paymentId is null)
        {
            return NotFound();
        }

        try
        {
            var allocation = await _allocations.ReverseAsync(new SupplierPaymentAllocationReverseRequest(
                allocationId,
                reason ?? string.Empty,
                CurrentUserName()));

            await _audit.LogAndSaveAsync(
                nameof(SupplierPaymentAllocation),
                allocation.Id,
                AuditAction.Reverse,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", SupplierPaymentAllocationStatus.Active, allocation.Status),
                    ("ReversalReason", null, allocation.ReversalReason)));

            TempData["ok"] = "تخصیص پیش‌پرداخت برگشت داده شد.";
        }
        catch (BusinessRuleException ex)
        {
            TempData["error"] = ex.Message;
        }

        if (TryGetLocalReturnUrl(returnUrl, out var localReturnUrl))
        {
            return Redirect(localReturnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = paymentId.Value });
    }

    private string? CurrentUserName()
        => User?.FindFirstValue(AppClaimTypes.Username) ?? User?.Identity?.Name;

    private async Task RefreshAllocationFormAsync(SupplierPaymentAllocationCreateViewModel model)
    {
        var payment = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.Supplier)
            .FirstOrDefaultAsync(p => p.Id == model.PaymentTransactionId);

        if (payment is not null)
        {
            model.SupplierId = payment.SupplierId ?? 0;
            model.SupplierName = payment.Supplier?.Name ?? string.Empty;
            model.PaymentReference = string.IsNullOrWhiteSpace(payment.Reference) ? $"#{payment.Id}" : payment.Reference!;
            model.PaymentDate = payment.PaymentDate;
            model.PaymentAmount = payment.Amount;
            model.PaymentCurrencyCode = payment.Currency;
            model.PaymentAmountUsd = payment.AmountUsd;
            model.AllocatableBalanceUsd = await _allocations.GetAllocatableBalanceUsdAsync(payment.Id);
            model.AllocatableBalanceAmount = await _allocations.GetAllocatablePaymentAmountAsync(payment.Id);
            model.PaymentCurrencyPerUsdRateAtPayment = PaymentPerUsdRate(payment);
        }

        await PopulateAllocationFormAsync(model, model.SupplierId);
    }

    // نرخ روز پرداخت به شکل خوانا «۱ دلار = ؟ ارز پرداخت»؛ پیش‌فرض فرم تخصیص از همین می‌آید.
    private static decimal PaymentPerUsdRate(PaymentTransaction payment)
    {
        var fxRateToUsd = payment.AppliedFxRateToUsd ?? 1m;
        return fxRateToUsd <= 0m
            ? 1m
            : decimal.Round(1m / fxRateToUsd, 6, MidpointRounding.AwayFromZero);
    }

    private async Task PopulateAllocationFormAsync(SupplierPaymentAllocationCreateViewModel model, int supplierId)
    {
        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.ContractType == ContractType.Purchase && c.SupplierId == supplierId)
            .OrderByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new { c.Id, c.ContractNumber, c.Currency })
            .ToListAsync();

        ViewBag.AllocationContracts = new SelectList(
            contracts.Select(c => new { c.Id, Display = $"{c.ContractNumber} ({SystemCurrency.Normalize(c.Currency)})" }),
            "Id",
            "Display",
            model.ContractId);
        ViewBag.AllocationContractCurrencies = contracts
            .ToDictionary(c => c.Id, c => SystemCurrency.Normalize(c.Currency));
    }

    private async Task<(IReadOnlyList<PaymentListItemViewModel> Items, int TotalCount, int CurrentPage, int PageCount)> BuildRowsAsync(
        PaymentIndexFilterViewModel filter,
        int page = 1)
    {
        NormalizeFilter(filter);

        var query = _db.PaymentTransactions
            .AsNoTracking()
            .AsQueryable();

        if (filter.FromDate.HasValue)
        {
            query = query.Where(p => p.PaymentDate >= filter.FromDate.Value.Date);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(p => p.PaymentDate <= filter.ToDate.Value.Date);
        }

        if (filter.Direction.HasValue)
        {
            query = query.Where(p => p.Direction == filter.Direction.Value);
        }

        if (filter.PaymentKind.HasValue)
        {
            query = query.Where(p => p.PaymentKind == filter.PaymentKind.Value);
        }

        if (filter.CounterpartyType.HasValue)
        {
            query = ApplyCounterpartyTypeFilter(query, filter.CounterpartyType.Value);
        }

        if (filter.CashAccountId.HasValue)
        {
            query = query.Where(p => p.CashAccountId == filter.CashAccountId.Value);
        }

        if (filter.CustomerId.HasValue)
        {
            query = query.Where(p => p.CustomerId == filter.CustomerId.Value);
        }

        if (filter.SupplierId.HasValue)
        {
            query = query.Where(p => p.SupplierId == filter.SupplierId.Value);
        }

        if (filter.ServiceProviderId.HasValue)
        {
            query = query.Where(p => p.ServiceProviderId == filter.ServiceProviderId.Value);
        }

        if (filter.SarrafId.HasValue)
        {
            query = query.Where(p => p.SarrafId == filter.SarrafId.Value);
        }

        if (filter.EmployeeId.HasValue)
        {
            query = query.Where(p => p.EmployeeId == filter.EmployeeId.Value);
        }

        if (filter.DriverId.HasValue)
        {
            query = query.Where(p => p.DriverId == filter.DriverId.Value);
        }

        if (filter.ContractId.HasValue)
        {
            query = query.Where(p => p.ContractId == filter.ContractId.Value);
        }

        if (filter.ShipmentId.HasValue)
        {
            query = query.Where(p => p.ShipmentId == filter.ShipmentId.Value);
        }

        if (filter.SalesTransactionId.HasValue)
        {
            query = query.Where(p => p.SalesTransactionId == filter.SalesTransactionId.Value);
        }

        if (filter.ExpenseTransactionId.HasValue)
        {
            query = query.Where(p => p.ExpenseTransactionId == filter.ExpenseTransactionId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            query = query.Where(p => p.Currency == filter.Currency);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            query = query.Where(p => p.Reference != null && p.Reference.Contains(filter.Reference));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(p =>
                (p.Reference != null && p.Reference.Contains(search))
                || (p.Description != null && p.Description.Contains(search))
                || (p.Customer != null && p.Customer.Name.Contains(search))
                || (p.Supplier != null && p.Supplier.Name.Contains(search))
                || (p.ServiceProvider != null && p.ServiceProvider.Name.Contains(search))
                || (p.Sarraf != null && p.Sarraf.Name.Contains(search))
                || (p.Employee != null && p.Employee.FullName.Contains(search))
                || (p.Driver != null && p.Driver.FullName.Contains(search))
                || (p.Contract != null && p.Contract.ContractNumber.Contains(search))
                || (p.Shipment != null && p.Shipment.ShipmentCode.Contains(search))
                || (p.SalesTransaction != null && p.SalesTransaction.InvoiceNumber.Contains(search))
                || (p.ExpenseTransaction != null && p.ExpenseTransaction.Description != null && p.ExpenseTransaction.Description.Contains(search)));
        }

        // پرداخت‌های نقدی/بانکی (PaymentTransaction) — کل ردیف‌های مطابق فیلتر؛ صفحه‌بندی پایین‌تر
        // در حافظه انجام می‌شود تا با حواله‌های صراف به‌صورت یک رزنامچهٔ زمانی واحد ادغام شوند.
        var payments = await query
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new PaymentListProjection
            {
                Id = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                PaymentKind = p.PaymentKind,
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : "",
                CashAccountCurrency = p.CashAccount != null ? p.CashAccount.Currency : p.Currency,
                CustomerId = p.CustomerId,
                CustomerName = p.Customer != null ? p.Customer.Name : null,
                SupplierId = p.SupplierId,
                SupplierName = p.Supplier != null ? p.Supplier.Name : null,
                ServiceProviderId = p.ServiceProviderId,
                ServiceProviderName = p.ServiceProvider != null ? p.ServiceProvider.Name : null,
                SarrafId = p.SarrafId,
                SarrafName = p.Sarraf != null ? p.Sarraf.Name : null,
                EmployeeId = p.EmployeeId,
                EmployeeName = p.Employee != null ? p.Employee.FullName : null,
                DriverId = p.DriverId,
                DriverName = p.Driver != null ? p.Driver.FullName : null,
                ContractId = p.ContractId,
                ContractNumber = p.Contract != null ? p.Contract.ContractNumber : null,
                ShipmentId = p.ShipmentId,
                ShipmentCode = p.Shipment != null ? p.Shipment.ShipmentCode : null,
                SalesTransactionId = p.SalesTransactionId,
                SalesInvoiceNumber = p.SalesTransaction != null ? p.SalesTransaction.InvoiceNumber : null,
                ExpenseTransactionId = p.ExpenseTransactionId,
                ExpenseDescription = p.ExpenseTransaction != null ? p.ExpenseTransaction.Description : null,
                TruckDispatchId = p.TruckDispatchId,
                TruckPlateNumber = p.TruckDispatch != null && p.TruckDispatch.Truck != null
                    ? p.TruckDispatch.Truck.PlateNumber
                    : null,
                Description = p.Description,
                CreatedByUserId = p.CreatedByUserId,
                Amount = p.Amount,
                Currency = p.Currency,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                LedgerEntryId = p.LedgerEntryId,
                CommissionAmount = p.RelatedExpenseTransactionId != null
                    ? _db.ExpenseTransactions.Where(e => e.Id == p.RelatedExpenseTransactionId).Select(e => (decimal?)e.Amount).FirstOrDefault()
                    : null,
                CommissionCurrency = p.RelatedExpenseTransactionId != null
                    ? _db.ExpenseTransactions.Where(e => e.Id == p.RelatedExpenseTransactionId).Select(e => e.Currency).FirstOrDefault()
                    : null
            })
            .ToListAsync();

        // حواله‌های صراف به تأمین‌کننده (SarrafSettlement) — فقط نمایشی. این‌ها PaymentTransaction
        // ندارند و صندوق را تکان نداده‌اند؛ صرفاً برای دیده‌شدن جریان پول از طریق صراف در رزنامچه
        // به‌صورت ردیف read-only ادغام می‌شوند.
        var settlements = await BuildSarrafHawalaRowsAsync(filter);
        var viaSarrafLedgers = await BuildViaSarrafLedgerRowsAsync(filter);

        var userIds = payments
            .Select(p => p.CreatedByUserId)
            .Concat(settlements.Select(s => s.CreatedByUserId))
            .Concat(viaSarrafLedgers.Select(l => l.CreatedByUserId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var users = userIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName);

        var allRows = payments
            .Select(payment => ToPaymentListItem(payment, users))
            .Concat(settlements.Select(settlement => ToSarrafHawalaListItem(settlement, users)))
            .Concat(viaSarrafLedgers.Select(ledger => ToViaSarrafLedgerListItem(ledger, users)))
            .OrderByDescending(r => r.PaymentDate)
            .ThenBy(r => r.IsLedgerOnlyViaSarraf)
            .ThenBy(r => r.IsSarrafHawala)
            .ThenByDescending(r => r.IsSarrafHawala ? r.SarrafSettlementId ?? 0 : r.Id)
            .ToList();

        var totalCount = allRows.Count;
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)IndexPageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);

        var items = page > 0
            ? allRows.Skip((currentPage - 1) * IndexPageSize).Take(IndexPageSize).ToList()
            : allRows;

        return (items, totalCount, currentPage, pageCount);
    }

    // ردیف‌های مجازیِ «حواله صراف به تأمین‌کننده» برای نمایش در رزنامچه.
    // فقط تسویه‌های Posted که به PaymentTransaction وصل نیستند (یعنی همان حواله‌هایی که صندوق را
    // تکان نداده‌اند). فیلترهای مخصوص پول نقد/بانک (حساب نقدی، مشتری، کارمند، …) شامل حال حواله‌ها
    // نمی‌شوند، پس اگر چنین فیلتری فعال باشد هیچ حواله‌ای برنمی‌گردد. هیچ نوشتنی روی DB نیست.
    private async Task<List<SarrafHawalaRowProjection>> BuildSarrafHawalaRowsAsync(PaymentIndexFilterViewModel filter)
    {
        var incompatibleFilterSet =
            filter.CashAccountId.HasValue
            || filter.CustomerId.HasValue
            || filter.ServiceProviderId.HasValue
            || filter.EmployeeId.HasValue
            || filter.DriverId.HasValue
            || filter.ShipmentId.HasValue
            || filter.SalesTransactionId.HasValue
            || filter.ExpenseTransactionId.HasValue
            || filter.Direction == PaymentDirection.In
            || (filter.PaymentKind.HasValue && filter.PaymentKind.Value != PaymentKind.SarrafSettlement)
            || (filter.CounterpartyType.HasValue && filter.CounterpartyType.Value != PaymentCounterpartyType.Sarraf);

        if (incompatibleFilterSet)
        {
            return [];
        }

        var settlementQuery = _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted && s.PaymentTransactionId == null);

        if (filter.FromDate.HasValue)
        {
            settlementQuery = settlementQuery.Where(s => s.SettlementDate >= filter.FromDate.Value.Date);
        }

        if (filter.ToDate.HasValue)
        {
            settlementQuery = settlementQuery.Where(s => s.SettlementDate <= filter.ToDate.Value.Date);
        }

        if (filter.SupplierId.HasValue)
        {
            settlementQuery = settlementQuery.Where(s => s.SupplierId == filter.SupplierId.Value);
        }

        if (filter.SarrafId.HasValue)
        {
            settlementQuery = settlementQuery.Where(s => s.SarrafId == filter.SarrafId.Value);
        }

        if (filter.ContractId.HasValue)
        {
            settlementQuery = settlementQuery.Where(s => s.ContractId == filter.ContractId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            settlementQuery = settlementQuery.Where(s => s.SarrafCurrency == filter.Currency);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            settlementQuery = settlementQuery.Where(s => s.ReferenceNumber != null && s.ReferenceNumber.Contains(filter.Reference));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            settlementQuery = settlementQuery.Where(s =>
                (s.ReferenceNumber != null && s.ReferenceNumber.Contains(search))
                || (s.Description != null && s.Description.Contains(search))
                || (s.Sarraf != null && s.Sarraf.Name.Contains(search))
                || (s.Supplier != null && s.Supplier.Name.Contains(search))
                || (s.Contract != null && s.Contract.ContractNumber.Contains(search)));
        }

        return await settlementQuery
            .Select(s => new SarrafHawalaRowProjection
            {
                Id = s.Id,
                SettlementDate = s.SettlementDate,
                SarrafName = s.Sarraf != null ? s.Sarraf.Name : null,
                SupplierName = s.Supplier != null ? s.Supplier.Name : null,
                ContractNumber = s.Contract != null ? s.Contract.ContractNumber : null,
                Currency = s.SarrafCurrency,
                Amount = s.SarrafChargedAmount,
                AmountUsd = s.SarrafChargedAmountUsd,
                Reference = s.ReferenceNumber,
                Description = s.Description,
                LedgerEntryId = s.LedgerEntryId,
                CreatedByUserId = s.CreatedByUserId
            })
            .ToListAsync();
    }

    private static PaymentListItemViewModel ToSarrafHawalaListItem(
        SarrafHawalaRowProjection row,
        IReadOnlyDictionary<int, string> users)
    {
        var sarrafName = string.IsNullOrWhiteSpace(row.SarrafName) ? "صراف" : row.SarrafName;
        var relatedParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.SupplierName))
        {
            relatedParts.Add($"تأمین‌کننده: {row.SupplierName}");
        }
        if (!string.IsNullOrWhiteSpace(row.ContractNumber))
        {
            relatedParts.Add($"قرارداد: {row.ContractNumber}");
        }

        return new PaymentListItemViewModel
        {
            Id = row.Id,
            PaymentDate = row.SettlementDate,
            Direction = PaymentDirection.Out,
            DirectionName = "حواله صراف",
            PaymentKind = PaymentKind.SarrafSettlement,
            PaymentKindName = "حواله صراف به تأمین‌کننده",
            CashAccountName = "—",
            CashAccountCurrency = row.Currency,
            CounterpartyTypeName = PaymentCounterpartyTypeLabels.ToPersian(PaymentCounterpartyType.Sarraf),
            CounterpartyName = sarrafName,
            SupplierName = row.SupplierName,
            SarrafName = row.SarrafName,
            ContractNumber = row.ContractNumber,
            RelatedTo = relatedParts.Count == 0 ? "—" : string.Join(" • ", relatedParts),
            Description = row.Description,
            CreatedByDisplay = row.CreatedByUserId.HasValue && users.TryGetValue(row.CreatedByUserId.Value, out var userName)
                ? userName
                : null,
            Amount = row.Amount,
            Currency = row.Currency,
            AmountUsd = row.AmountUsd,
            Reference = row.Reference,
            LedgerEntryId = row.LedgerEntryId,
            IsSarrafHawala = true,
            SarrafSettlementId = row.Id
        };
    }

    private sealed class SarrafHawalaRowProjection
    {
        public int Id { get; init; }
        public DateTime SettlementDate { get; init; }
        public string? SarrafName { get; init; }
        public string? SupplierName { get; init; }
        public string? ContractNumber { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal Amount { get; init; }
        public decimal AmountUsd { get; init; }
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public int? LedgerEntryId { get; init; }
        public int? CreatedByUserId { get; init; }
    }

    private async Task<List<ViaSarrafLedgerRowProjection>> BuildViaSarrafLedgerRowsAsync(PaymentIndexFilterViewModel filter)
    {
        var incompatibleFilterSet =
            filter.CashAccountId.HasValue
            || filter.CustomerId.HasValue
            || filter.ServiceProviderId.HasValue
            || filter.EmployeeId.HasValue
            || filter.DriverId.HasValue
            || filter.ShipmentId.HasValue
            || filter.SalesTransactionId.HasValue
            || filter.ExpenseTransactionId.HasValue
            || filter.Direction == PaymentDirection.In
            || (filter.PaymentKind.HasValue && filter.PaymentKind.Value != PaymentKind.SupplierPayment)
            || (filter.CounterpartyType.HasValue
                && filter.CounterpartyType.Value != PaymentCounterpartyType.Supplier
                && filter.CounterpartyType.Value != PaymentCounterpartyType.Sarraf);

        if (incompatibleFilterSet)
        {
            return [];
        }

        var ledgerQuery = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == ViaSarrafSupplierLedgerSourceType);

        if (filter.FromDate.HasValue)
        {
            ledgerQuery = ledgerQuery.Where(l => l.EntryDate >= filter.FromDate.Value.Date);
        }

        if (filter.ToDate.HasValue)
        {
            ledgerQuery = ledgerQuery.Where(l => l.EntryDate <= filter.ToDate.Value.Date);
        }

        if (filter.SupplierId.HasValue)
        {
            ledgerQuery = ledgerQuery.Where(l => l.SupplierId == filter.SupplierId.Value);
        }

        if (filter.SarrafId.HasValue)
        {
            ledgerQuery = ledgerQuery.Where(l => l.SourceId == filter.SarrafId.Value);
        }

        if (filter.ContractId.HasValue)
        {
            ledgerQuery = ledgerQuery.Where(l => l.ContractId == filter.ContractId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            ledgerQuery = ledgerQuery.Where(l => l.SourceCurrencyCode == filter.Currency || l.Currency == filter.Currency);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            ledgerQuery = ledgerQuery.Where(l => l.Reference != null && l.Reference.Contains(filter.Reference));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            ledgerQuery = ledgerQuery.Where(l =>
                (l.Reference != null && l.Reference.Contains(search))
                || l.Description.Contains(search)
                || (l.Supplier != null && l.Supplier.Name.Contains(search))
                || (l.Contract != null && l.Contract.ContractNumber.Contains(search))
                || _db.Sarrafs.Any(s => s.Id == l.SourceId && s.Name.Contains(search)));
        }

        return await ledgerQuery
            .Select(l => new ViaSarrafLedgerRowProjection
            {
                Id = l.Id,
                EntryDate = l.EntryDate,
                SupplierName = l.Supplier != null ? l.Supplier.Name : null,
                SarrafName = _db.Sarrafs
                    .Where(s => s.Id == l.SourceId)
                    .Select(s => s.Name)
                    .FirstOrDefault(),
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                Currency = l.SourceCurrencyCode ?? l.Currency,
                Amount = l.SourceAmount ?? l.AmountUsd,
                AmountUsd = l.AmountUsd,
                Reference = l.Reference,
                Description = l.Description,
                LedgerEntryId = l.Id,
                CreatedByUserId = l.CreatedByUserId
            })
            .ToListAsync();
    }

    private static PaymentListItemViewModel ToViaSarrafLedgerListItem(
        ViaSarrafLedgerRowProjection row,
        IReadOnlyDictionary<int, string> users)
    {
        var relatedParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.SarrafName))
        {
            relatedParts.Add($"صراف: {row.SarrafName}");
        }
        if (!string.IsNullOrWhiteSpace(row.ContractNumber))
        {
            relatedParts.Add($"قرارداد: {row.ContractNumber}");
        }

        return new PaymentListItemViewModel
        {
            Id = row.Id,
            PaymentDate = row.EntryDate,
            Direction = PaymentDirection.Out,
            DirectionName = "پرداخت",
            PaymentKind = PaymentKind.SupplierPayment,
            PaymentKindName = "پرداخت تأمین‌کننده از طریق صراف",
            CashAccountName = "—",
            CashAccountCurrency = row.Currency,
            CounterpartyTypeName = PaymentCounterpartyTypeLabels.ToPersian(PaymentCounterpartyType.Supplier),
            CounterpartyName = string.IsNullOrWhiteSpace(row.SupplierName) ? "تأمین‌کننده" : row.SupplierName,
            SupplierName = row.SupplierName,
            SarrafName = row.SarrafName,
            ContractNumber = row.ContractNumber,
            RelatedTo = relatedParts.Count == 0 ? "—" : string.Join(" • ", relatedParts),
            Description = row.Description,
            CreatedByDisplay = row.CreatedByUserId.HasValue && users.TryGetValue(row.CreatedByUserId.Value, out var userName)
                ? userName
                : null,
            Amount = row.Amount,
            Currency = row.Currency,
            AmountUsd = row.AmountUsd,
            Reference = row.Reference,
            LedgerEntryId = row.LedgerEntryId,
            IsLedgerOnlyViaSarraf = true
        };
    }

    private sealed class ViaSarrafLedgerRowProjection
    {
        public int Id { get; init; }
        public DateTime EntryDate { get; init; }
        public string? SupplierName { get; init; }
        public string? SarrafName { get; init; }
        public string? ContractNumber { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal Amount { get; init; }
        public decimal AmountUsd { get; init; }
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public int? LedgerEntryId { get; init; }
        public int? CreatedByUserId { get; init; }
    }

    private async Task<PaymentIndexSummary> BuildSummaryAsync()
    {
        // Cache the display-only summary for a few seconds so opening the Create
        // form (and the Index list) does not re-run the full-table aggregate on
        // every request. No posting/Ledger/CashAccount logic touched.
        if (_summaryCache is null)
        {
            return await BuildSummaryCoreAsync();
        }

        return await _summaryCache.GetOrCreateAsync(SummaryCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SummaryCacheTtl;
            return await BuildSummaryCoreAsync();
        }) ?? await BuildSummaryCoreAsync();
    }

    private async Task<PaymentIndexSummary> BuildSummaryCoreAsync()
    {
        var today = DateTime.UtcNow.Date;
        var todayTotals = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.PaymentDate == today)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ReceiptUsd = g
                    .Where(p => p.Direction == PaymentDirection.In)
                    .Sum(p => (decimal?)p.AmountUsd) ?? 0m,
                PaymentUsd = g
                    .Where(p => p.Direction == PaymentDirection.Out)
                    .Sum(p => (decimal?)p.AmountUsd) ?? 0m
            })
            .FirstOrDefaultAsync();

        var cashTotals = await _db.PaymentTransactions
            .AsNoTracking()
            .GroupBy(p => p.CashAccountId)
            .Select(g => new
            {
                CashAccountId = g.Key,
                TotalIn = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.Amount),
                TotalOut = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.Amount),
                TotalInUsd = g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd),
                TotalOutUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd)
            })
            .ToListAsync();

        var totalsByAccount = cashTotals.ToDictionary(t => t.CashAccountId);
        var accounts = await _db.CashAccounts
            .AsNoTracking()
            .OrderBy(a => a.Code)
            .Select(a => new { a.Id, a.Code, a.Name, a.Currency })
            .ToListAsync();
        var balances = accounts
            .Select(account =>
            {
                totalsByAccount.TryGetValue(account.Id, out var total);
                return new CashAccountBalanceSummaryViewModel
                {
                    CashAccountId = account.Id,
                    Code = account.Code,
                    Name = account.Name,
                    Currency = account.Currency,
                    TotalIn = total?.TotalIn ?? 0m,
                    TotalOut = total?.TotalOut ?? 0m
                };
            })
            .ToList();

        var lastDocument = await _db.PaymentTransactions
            .AsNoTracking()
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new { p.PaymentDate, p.Reference, p.Id })
            .FirstOrDefaultAsync();

        return new PaymentIndexSummary(
            TodayReceiptUsd: todayTotals?.ReceiptUsd ?? 0m,
            TodayPaymentUsd: todayTotals?.PaymentUsd ?? 0m,
            CashAccountsBalanceUsd: cashTotals.Sum(p => p.TotalInUsd - p.TotalOutUsd),
            LastDocumentReference: lastDocument is null
                ? null
                : string.IsNullOrWhiteSpace(lastDocument.Reference) ? $"#{lastDocument.Id}" : lastDocument.Reference,
            LastDocumentDate: lastDocument?.PaymentDate,
            CashAccountBalances: balances);
    }

    private static IQueryable<PaymentTransaction> ApplyCounterpartyTypeFilter(
        IQueryable<PaymentTransaction> query,
        PaymentCounterpartyType counterpartyType)
        => counterpartyType switch
        {
            PaymentCounterpartyType.Supplier => query.Where(p => p.SupplierId.HasValue || p.PaymentKind == PaymentKind.SupplierPayment || p.PaymentKind == PaymentKind.SupplierReceipt),
            PaymentCounterpartyType.Customer => query.Where(p => p.CustomerId.HasValue || p.PaymentKind == PaymentKind.CustomerReceipt || p.PaymentKind == PaymentKind.CustomerPayment),
            PaymentCounterpartyType.ServiceProvider => query.Where(p => p.ServiceProviderId.HasValue || p.PaymentKind == PaymentKind.ServiceProviderPayment),
            PaymentCounterpartyType.Sarraf => query.Where(p => p.SarrafId.HasValue || p.PaymentKind == PaymentKind.SarrafSettlement),
            PaymentCounterpartyType.Employee => query.Where(p => p.EmployeeId.HasValue || p.PaymentKind == PaymentKind.EmployeeSalaryPayment || p.PaymentKind == PaymentKind.EmployeeSalaryAdvance || p.PaymentKind == PaymentKind.EmployeeReturn),
            PaymentCounterpartyType.Driver => query.Where(p => p.DriverId.HasValue || p.TruckDispatchId.HasValue || p.PaymentKind == PaymentKind.TruckPayment),
            PaymentCounterpartyType.OfficeExpense => query.Where(p => p.ExpenseTransactionId.HasValue || p.PaymentKind == PaymentKind.ExpensePayment || p.PaymentKind == PaymentKind.CommissionPayment),
            PaymentCounterpartyType.Contract => query.Where(p => p.ContractId.HasValue),
            PaymentCounterpartyType.Sales => query.Where(p => p.SalesTransactionId.HasValue),
            PaymentCounterpartyType.Shipment => query.Where(p => p.ShipmentId.HasValue),
            PaymentCounterpartyType.Other => query.Where(p =>
                !p.CustomerId.HasValue
                && !p.SupplierId.HasValue
                && !p.ServiceProviderId.HasValue
                && !p.SarrafId.HasValue
                && !p.EmployeeId.HasValue
                && !p.DriverId.HasValue
                && !p.ContractId.HasValue
                && !p.ShipmentId.HasValue
                && !p.SalesTransactionId.HasValue
                && !p.ExpenseTransactionId.HasValue
                && !p.TruckDispatchId.HasValue),
            _ => query
        };

    private static PaymentListItemViewModel ToPaymentListItem(
        PaymentListProjection payment,
        IReadOnlyDictionary<int, string> users)
    {
        var projected = new PaymentTransaction
        {
            Id = payment.Id,
            PaymentDate = payment.PaymentDate,
            Direction = payment.Direction,
            PaymentKind = payment.PaymentKind,
            CashAccount = new CashAccount
            {
                Name = payment.CashAccountName,
                Currency = payment.CashAccountCurrency
            },
            CustomerId = payment.CustomerId,
            Customer = payment.CustomerName is null ? null : new Customer { Name = payment.CustomerName },
            SupplierId = payment.SupplierId,
            Supplier = payment.SupplierName is null ? null : new Supplier { Name = payment.SupplierName },
            ServiceProviderId = payment.ServiceProviderId,
            ServiceProvider = payment.ServiceProviderName is null ? null : new ServiceProviderEntity { Name = payment.ServiceProviderName },
            SarrafId = payment.SarrafId,
            Sarraf = payment.SarrafName is null ? null : new Sarraf { Name = payment.SarrafName },
            EmployeeId = payment.EmployeeId,
            Employee = payment.EmployeeName is null ? null : new Employee { FullName = payment.EmployeeName },
            DriverId = payment.DriverId,
            Driver = payment.DriverName is null ? null : new Driver { FullName = payment.DriverName },
            ContractId = payment.ContractId,
            Contract = payment.ContractNumber is null ? null : new Contract { ContractNumber = payment.ContractNumber },
            ShipmentId = payment.ShipmentId,
            Shipment = payment.ShipmentCode is null ? null : new Shipment { ShipmentCode = payment.ShipmentCode },
            SalesTransactionId = payment.SalesTransactionId,
            SalesTransaction = payment.SalesInvoiceNumber is null ? null : new SalesTransaction { InvoiceNumber = payment.SalesInvoiceNumber },
            ExpenseTransactionId = payment.ExpenseTransactionId,
            ExpenseTransaction = payment.ExpenseTransactionId.HasValue
                ? new ExpenseTransaction { Description = payment.ExpenseDescription }
                : null,
            TruckDispatchId = payment.TruckDispatchId,
            TruckDispatch = payment.TruckDispatchId.HasValue
                ? new TruckDispatch
                {
                    Id = payment.TruckDispatchId.Value,
                    Truck = payment.TruckPlateNumber is null
                        ? null
                        : new Truck { PlateNumber = payment.TruckPlateNumber }
                }
                : null,
            Description = payment.Description,
            CreatedByUserId = payment.CreatedByUserId,
            Amount = payment.Amount,
            Currency = payment.Currency,
            AmountUsd = payment.AmountUsd,
            Reference = payment.Reference,
            LedgerEntryId = payment.LedgerEntryId
        };

        var item = ToPaymentListItem(projected, users);
        item.CommissionAmount = payment.CommissionAmount;
        item.CommissionCurrency = payment.CommissionCurrency;
        return item;
    }

    private static PaymentListItemViewModel ToPaymentListItem(
        PaymentTransaction payment,
        IReadOnlyDictionary<int, string> users)
    {
        var counterpartyType = InferCounterpartyType(payment);
        return new PaymentListItemViewModel
        {
            Id = payment.Id,
            PaymentDate = payment.PaymentDate,
            Direction = payment.Direction,
            DirectionName = PaymentDirectionLabels.ToPersian(payment.Direction),
            PaymentKind = payment.PaymentKind,
            PaymentKindName = PaymentKindLabels.ToPersian(payment.PaymentKind),
            CashAccountName = payment.CashAccount?.Name ?? string.Empty,
            CashAccountCurrency = payment.CashAccount?.Currency ?? payment.Currency,
            CounterpartyTypeName = PaymentCounterpartyTypeLabels.ToPersian(counterpartyType),
            CounterpartyName = BuildCounterpartyName(payment, counterpartyType),
            CustomerName = payment.Customer?.Name,
            SupplierName = payment.Supplier?.Name,
            ServiceProviderName = payment.ServiceProvider?.Name,
            SarrafName = payment.Sarraf?.Name,
            EmployeeName = payment.Employee?.FullName,
            DriverName = payment.Driver?.FullName,
            ContractNumber = payment.Contract?.ContractNumber,
            ShipmentCode = payment.Shipment?.ShipmentCode,
            SalesInvoiceNumber = payment.SalesTransaction?.InvoiceNumber,
            ExpenseDescription = payment.ExpenseTransaction?.Description,
            RelatedTo = BuildRelatedTo(payment),
            Description = payment.Description,
            CreatedByDisplay = payment.CreatedByUserId.HasValue && users.TryGetValue(payment.CreatedByUserId.Value, out var userName)
                ? userName
                : null,
            Amount = payment.Amount,
            Currency = payment.Currency,
            AmountUsd = payment.AmountUsd,
            Reference = payment.Reference,
            LedgerEntryId = payment.LedgerEntryId
        };
    }

    private static string BuildCounterpartyName(PaymentTransaction payment, PaymentCounterpartyType counterpartyType)
        => counterpartyType switch
        {
            PaymentCounterpartyType.Supplier => payment.Supplier?.Name ?? "تأمین‌کننده مشخص نشده",
            PaymentCounterpartyType.Customer => payment.Customer?.Name ?? "مشتری مشخص نشده",
            PaymentCounterpartyType.Employee => payment.Employee?.FullName ?? "کارمند مشخص نشده",
            PaymentCounterpartyType.ServiceProvider => payment.ServiceProvider?.Name ?? "Service provider not selected",
            PaymentCounterpartyType.Sarraf => payment.Sarraf?.Name ?? "صراف مشخص نشده",
            PaymentCounterpartyType.Driver => payment.Driver?.FullName
                ?? payment.TruckDispatch?.Truck?.PlateNumber
                ?? "راننده / موتر مشخص نشده",
            PaymentCounterpartyType.OfficeExpense => payment.ExpenseTransaction?.Description ?? "مصرف دفتری",
            PaymentCounterpartyType.Contract => payment.Contract?.ContractNumber ?? "قرارداد",
            PaymentCounterpartyType.Sales => payment.SalesTransaction?.InvoiceNumber ?? "فروش",
            PaymentCounterpartyType.Shipment => payment.Shipment?.ShipmentCode ?? "Shipment",
            _ => "متفرقه"
        };

    private static string BuildRelatedTo(PaymentTransaction payment)
    {
        var parts = new List<string>();
        if (payment.Contract is not null)
        {
            parts.Add($"قرارداد {payment.Contract.ContractNumber}");
        }

        if (payment.SalesTransaction is not null)
        {
            parts.Add($"فاکتور {payment.SalesTransaction.InvoiceNumber}");
        }

        if (payment.ExpenseTransaction is not null)
        {
            parts.Add("مصرف");
        }

        if (payment.Shipment is not null)
        {
            parts.Add($"Shipment {payment.Shipment.ShipmentCode}");
        }

        if (payment.TruckDispatch is not null)
        {
            parts.Add($"Dispatch #{payment.TruckDispatch.Id}");
        }

        if (payment.Sarraf is not null)
        {
            parts.Add($"صراف {payment.Sarraf.Name}");
        }

        return parts.Count == 0 ? "—" : string.Join(" | ", parts);
    }

    // مانده‌ی فقط-خواندنی طرف حساب برای نمایش در فرم روزنامچه.
    // عدد و عبارتِ طلب/بدهی دقیقاً همان منطق و علامت‌گذاریِ صفحه‌ی صورت‌حساب رسمیِ هر طرف است
    // (مشتری/تأمین‌کننده از Ledger، صراف از تسویه‌های Posted منهای پرداخت‌ها). هیچ نوشتنی انجام نمی‌شود.
    [HttpGet]
    public async Task<IActionResult> PartyBalance(int type, int id)
    {
        if (id <= 0)
        {
            return Json(new { available = false });
        }

        var counterparty = (PaymentCounterpartyType)type;

        if (_partyStatements is not null && TryMapStatementPartyType(counterparty, out var statementPartyType))
        {
            try
            {
                var statement = await _partyStatements.GetStatementAsync(
                    new PartyRef(statementPartyType, id),
                    new PartyStatementFilter { IncludeOperationalColumns = false },
                    HttpContext.RequestAborted);
                var closing = statement.Summary.ClosingBalance;
                return Json(new
                {
                    available = true,
                    name = statement.PartyInfo.Name,
                    amountText = statement.Summary.ClosingBalanceAbsolute.ToString("N2") + " " + statement.Summary.BaseCurrencyCode,
                    label = statement.Summary.ClosingBalanceMeaning,
                    tone = closing > 0m ? "positive" : closing < 0m ? "negative" : "zero"
                });
            }
            catch (KeyNotFoundException)
            {
                return Json(new { available = false });
            }
        }

        decimal net;
        string? name;
        string label;
        string tone;

        switch (counterparty)
        {
            case PaymentCounterpartyType.Customer:
            {
                var saleIds = (await _db.SalesTransactions
                    .AsNoTracking()
                    .Where(s => s.CustomerId == id
                        || (s.Contract != null
                            && s.Contract.ContractType == ContractType.Sale
                            && s.Contract.CustomerId == id))
                    .Select(s => s.Id)
                    .ToListAsync())
                    .ToHashSet();

                var ledgers = (await _db.LedgerEntries
                    .AsNoTracking()
                    .Include(l => l.Contract)
                    .Where(l => l.CustomerId == id
                        || (l.Contract != null
                            && l.Contract.ContractType == ContractType.Sale
                            && l.Contract.CustomerId == id)
                        || (l.SourceType == "Sale" && saleIds.Contains(l.SourceId)))
                    .ToListAsync())
                    .DistinctBy(l => l.Id)
                    .ToList();

                net = ledgers
                    .Where(l => CustomersController.IsCustomerAccountLedger(l, id, saleIds))
                    .Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
                name = await _db.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => c.Name).FirstOrDefaultAsync();
                label = net > 0 ? "مشتری بدهکار شرکت است" : net < 0 ? "شرکت بدهکار مشتری است" : "تسویه";
                break;
            }

            case PaymentCounterpartyType.Supplier:
            {
                // «طلب باقی‌مانده» تأمین‌کننده = ارزش جنس بارگیری‌شده − (پرداخت مستقیم + کاهش از طریق صراف).
                // عیناً همان فرمول و منبعِ صفحه‌ی پروفایل تأمین‌کننده (SupplierRemainingClaimUsd)؛
                // تعهد خرید در Ledger حساب تأمین‌کننده Credit نمی‌خورد، پس مانده‌ی Ledger معیار درست نیست.
                var contracts = await _db.Contracts
                    .AsNoTracking()
                    .Where(c => c.ContractType == ContractType.Purchase && c.SupplierId == id)
                    .ToListAsync();
                name = await _db.Suppliers.AsNoTracking().Where(s => s.Id == id).Select(s => s.Name).FirstOrDefaultAsync();

                if (name is null)
                {
                    return Json(new { available = false });
                }

                var contractIds = contracts.Select(c => c.Id).ToList();
                var loadedValueUsd = 0m;
                if (contractIds.Count > 0)
                {
                    var finalPriceByContract = contracts.ToDictionary(
                        c => c.Id,
                        c => ContractPricingAdapter.GetCanonicalFinalPrice(c));
                    var purchaseAggregates = await _purchaseAggregation.AggregateForContractsAsync(contractIds, finalPriceByContract);
                    loadedValueUsd = purchaseAggregates.Values.Sum(a => a.TraceablePurchaseCostUsd);
                }

                var directPaidUsd = (await _db.PaymentTransactions
                    .AsNoTracking()
                    .Where(p => p.PaymentKind == PaymentKind.SupplierPayment
                        && (p.SupplierId == id
                            || (p.Contract != null
                                && p.Contract.ContractType == ContractType.Purchase
                                && p.Contract.SupplierId == id)))
                    .Select(p => p.AmountUsd)
                    .ToListAsync())
                    .Sum();

                var sarrafReductionUsd = (await _db.SarrafSettlements
                    .AsNoTracking()
                    .Where(s => s.Status == SarrafSettlementStatus.Posted
                        && (s.SupplierId == id
                            || (s.Contract != null
                                && s.Contract.ContractType == ContractType.Purchase
                                && s.Contract.SupplierId == id)))
                    .ToListAsync())
                    .Sum(SuppliersController.SupplierReductionAmountUsd);

                net = loadedValueUsd - (directPaidUsd + sarrafReductionUsd);
                label = net > 0 ? "قابل پرداخت به تأمین‌کننده" : net < 0 ? "طلب شرکت از تأمین‌کننده" : "تسویه شده";
                break;
            }

            case PaymentCounterpartyType.ServiceProvider:
            {
                // مانده‌ی شرکت خدماتی = جمع Ledger حساب آن (Credit−Debit)؛ عیناً صفحه‌ی پروفایلِ شرکت خدماتی.
                var ledgers = (await _db.LedgerEntries
                    .AsNoTracking()
                    .Where(l => l.ServiceProviderId == id)
                    .ToListAsync())
                    .DistinctBy(l => l.Id)
                    .ToList();
                net = ledgers.Sum(l => l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd);
                name = await _db.ServiceProviders.AsNoTracking().Where(s => s.Id == id).Select(s => s.Name).FirstOrDefaultAsync();
                label = net > 0 ? "قابل پرداخت به شرکت خدماتی" : net < 0 ? "پیش‌پرداخت به شرکت خدماتی" : "تسویه";
                break;
            }

            case PaymentCounterpartyType.Employee:
            {
                // مانده‌ی کارمند = (حقوق/پاداش/تعدیلِ تعهدشده) − (پرداخت/مساعده/کسر)؛ عیناً منطقِ صفحه‌ی کارمندان.
                var txns = await _db.EmployeeSalaryTransactions
                    .AsNoTracking()
                    .Where(t => t.EmployeeId == id && !t.IsCancelled)
                    .Select(t => new { t.TransactionType, t.AmountUsd })
                    .ToListAsync();
                net = txns.Sum(t =>
                    t.TransactionType == EmployeeSalaryTransactionType.SalaryAccrual
                    || t.TransactionType == EmployeeSalaryTransactionType.Bonus
                    || t.TransactionType == EmployeeSalaryTransactionType.Adjustment
                        ? t.AmountUsd
                        : t.TransactionType == EmployeeSalaryTransactionType.SalaryPayment
                          || t.TransactionType == EmployeeSalaryTransactionType.SalaryAdvance
                          || t.TransactionType == EmployeeSalaryTransactionType.SalaryDeduction
                            ? -t.AmountUsd
                            : 0m);
                name = await _db.Employees.AsNoTracking().Where(e => e.Id == id).Select(e => e.FullName).FirstOrDefaultAsync();
                label = net > 0 ? "قابل پرداخت به کارمند" : net < 0 ? "طلب شرکت از کارمند" : "تسویه";
                break;
            }

            case PaymentCounterpartyType.Sarraf:
            {
                var chargedUsd = await _db.SarrafSettlements
                    .AsNoTracking()
                    .Where(s => s.SarrafId == id && s.Status == SarrafSettlementStatus.Posted)
                    .SumAsync(s => (decimal?)s.SarrafChargedAmountUsd) ?? 0m;

                var paidUsd = (await _db.PaymentTransactions
                    .AsNoTracking()
                    .Where(p => p.SarrafId == id)
                    .Select(p => new { p.Direction, p.AmountUsd })
                    .ToListAsync())
                    .Sum(p => p.Direction == PaymentDirection.Out ? p.AmountUsd : -p.AmountUsd);

                net = chargedUsd - paidUsd;
                name = await _db.Sarrafs.AsNoTracking().Where(s => s.Id == id).Select(s => s.Name).FirstOrDefaultAsync();
                label = net > 0 ? "قابل پرداخت به صراف" : net < 0 ? "طلب شرکت از صراف" : "تسویه";
                break;
            }

            default:
                return Json(new { available = false });
        }

        if (name is null)
        {
            return Json(new { available = false });
        }

        tone = net > 0 ? "positive" : net < 0 ? "negative" : "zero";
        return Json(new
        {
            available = true,
            name,
            amountText = Math.Abs(decimal.Round(net, 2)).ToString("N2") + " USD",
            label,
            tone
        });
    }

    private static bool TryMapStatementPartyType(
        PaymentCounterpartyType counterparty,
        out PartyStatementPartyType partyType)
    {
        partyType = counterparty switch
        {
            PaymentCounterpartyType.Customer => PartyStatementPartyType.Customer,
            PaymentCounterpartyType.Supplier => PartyStatementPartyType.Supplier,
            PaymentCounterpartyType.ServiceProvider => PartyStatementPartyType.ServiceProvider,
            PaymentCounterpartyType.Employee => PartyStatementPartyType.Employee,
            PaymentCounterpartyType.Driver => PartyStatementPartyType.Driver,
            PaymentCounterpartyType.Sarraf => PartyStatementPartyType.Sarraf,
            _ => default
        };
        return counterparty is PaymentCounterpartyType.Customer
            or PaymentCounterpartyType.Supplier
            or PaymentCounterpartyType.ServiceProvider
            or PaymentCounterpartyType.Employee
            or PaymentCounterpartyType.Driver
            or PaymentCounterpartyType.Sarraf;
    }

    private async Task PopulateLookupsAsync(
        PaymentCreateViewModel? createModel = null,
        PaymentIndexFilterViewModel? filter = null)
    {
        var selectedContractId = createModel?.ContractId ?? filter?.ContractId;
        var selectedShipmentId = createModel?.ShipmentId ?? filter?.ShipmentId;
        var selectedSalesTransactionId = createModel?.SalesTransactionId ?? filter?.SalesTransactionId;
        var selectedExpenseTransactionId = createModel?.ExpenseTransactionId ?? filter?.ExpenseTransactionId;
        var selectedTruckDispatchId = createModel?.TruckDispatchId;

        var cashAccounts = await _db.CashAccounts
            .AsNoTracking()
            .OrderBy(a => a.Code)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Currency,
                a.IsActive,
                a.AccountType
            })
            .ToListAsync();

        ViewBag.CashAccounts = new SelectList(
            cashAccounts,
            "Id",
            "Name",
            createModel?.CashAccountId ?? filter?.CashAccountId);

        if (createModel is not null)
        {
            ViewBag.CashAccountCatalog = cashAccounts
                .Select(a => new
                {
                    a.Id,
                    a.Name,
                    a.Currency,
                    a.IsActive,
                    IsMixed = a.AccountType == CashAccountType.Mixed
                })
                .ToList();
        }

        ViewBag.Customers = new SelectList(
            await _db.Customers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(),
            "Id",
            "Name",
            createModel?.CustomerId ?? filter?.CustomerId);

        var selectedSupplierId = createModel?.SupplierId ?? filter?.SupplierId;
        var suppliers = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync();

        ViewBag.Suppliers = new SelectList(
            suppliers,
            "Id",
            "Name",
            selectedSupplierId);
        if (createModel is not null)
        {
            ViewBag.SelectedSupplierName = suppliers.FirstOrDefault(s => s.Id == selectedSupplierId)?.Name;
        }

        var selectedServiceProviderId = createModel?.ServiceProviderId ?? filter?.ServiceProviderId;
        var serviceProviders = await _db.ServiceProviders
            .AsNoTracking()
            .Where(p => p.IsActive || (selectedServiceProviderId.HasValue && p.Id == selectedServiceProviderId.Value))
            .OrderBy(p => selectedServiceProviderId.HasValue && p.Id == selectedServiceProviderId.Value ? 0 : 1)
            .ThenBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Code })
            .Take(LookupLimit)
            .ToListAsync();

        ViewBag.ServiceProviders = serviceProviders
            .Select(p => new SelectListItem
            {
                Value = p.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(p.Code) ? p.Name : $"{p.Name} ({p.Code})",
                Selected = selectedServiceProviderId == p.Id
            })
            .ToList();

        var selectedSarrafId = createModel?.SarrafId ?? filter?.SarrafId;
        ViewBag.Sarrafs = new SelectList(
            await _db.Sarrafs
                .AsNoTracking()
                .Where(s => s.IsActive || (selectedSarrafId.HasValue && s.Id == selectedSarrafId.Value))
                .OrderBy(s => selectedSarrafId.HasValue && s.Id == selectedSarrafId.Value ? 0 : 1)
                .ThenBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            selectedSarrafId);

        ViewBag.Drivers = new SelectList(
            await _db.Drivers
                .AsNoTracking()
                .Where(d => d.IsActive)
                .OrderBy(d => d.FullName)
                .Select(d => new { d.Id, d.FullName })
                .ToListAsync(),
            "Id",
            "FullName",
            createModel?.DriverId ?? filter?.DriverId);

        ViewBag.Employees = new SelectList(
            await _db.Employees
                .AsNoTracking()
                .Where(e => e.IsActive)
                .OrderBy(e => e.FullName)
                .Select(e => new { e.Id, Label = e.EmployeeCode + " - " + e.FullName })
                .ToListAsync(),
            "Id",
            "Label",
            createModel?.EmployeeId ?? filter?.EmployeeId);

        var filterContractsToSupplier = createModel is not null
            && selectedSupplierId.HasValue
            && (createModel.CounterpartyType == PaymentCounterpartyType.Supplier || IsSupplierKind(createModel.PaymentKind));

        var contractQuery = _db.Contracts.AsNoTracking();
        if (filterContractsToSupplier && selectedSupplierId is int supplierContractFilterId)
        {
            contractQuery = contractQuery.Where(c =>
                c.ContractType == ContractType.Purchase
                && c.SupplierId == supplierContractFilterId);
        }

        var contracts = await contractQuery
            .AsNoTracking()
            .OrderBy(c => selectedContractId.HasValue && c.Id == selectedContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
            .Select(c => new
            {
                c.Id,
                c.SupplierId,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                UnitSymbol = c.Unit != null ? c.Unit.Symbol : null,
                UnitCode = c.Unit != null ? c.Unit.Code : null,
                UnitNamePersian = c.Unit != null ? c.Unit.NamePersian : null,
                UnitName = c.Unit != null ? c.Unit.Name : null
            })
            .ToListAsync();

        var contractLookup = contracts
            .Select(c => new PaymentContractLookupItemViewModel
            {
                Id = c.Id,
                SupplierId = c.SupplierId,
                ContractType = c.ContractType,
                Display = ContractUiText.FormatLookup(
                    c.ContractNumber,
                    c.ContractType,
                    c.ProductName,
                    ContractUiText.ResolveUnitText(c.UnitSymbol, c.UnitCode, c.UnitNamePersian, c.UnitName))
            })
            .ToList();

        if (createModel is not null)
        {
            ViewBag.ContractCatalog = contractLookup;
            ViewBag.SelectedContractNumber = contractLookup.FirstOrDefault(c => c.Id == selectedContractId)?.Display;
        }

        ViewBag.Contracts = new SelectList(
            contractLookup.Select(c => new ContractLookupOption(c.Id, c.Display)).ToList(),
            nameof(ContractLookupOption.Id),
            nameof(ContractLookupOption.Display),
            selectedContractId);

        ViewBag.Shipments = new SelectList(
            await _db.Shipments
                .AsNoTracking()
                .OrderBy(s => selectedShipmentId.HasValue && s.Id == selectedShipmentId.Value ? 0 : 1)
                .ThenByDescending(s => s.DepartureDate)
                .ThenBy(s => s.ShipmentCode)
                .Take(LookupLimit)
                .Select(s => new { s.Id, s.ShipmentCode })
                .ToListAsync(),
            "Id",
            "ShipmentCode",
            selectedShipmentId);

        ViewBag.SalesTransactions = new SelectList(
            await _db.SalesTransactions
                .AsNoTracking()
                .OrderBy(s => selectedSalesTransactionId.HasValue && s.Id == selectedSalesTransactionId.Value ? 0 : 1)
                .ThenByDescending(s => s.SaleDate)
                .ThenByDescending(s => s.Id)
                .Take(LookupLimit)
                .Select(s => new { s.Id, Label = s.InvoiceNumber })
                .ToListAsync(),
            "Id",
            "Label",
            selectedSalesTransactionId);

        ViewBag.ExpenseTransactions = new SelectList(
            await _db.ExpenseTransactions
                .AsNoTracking()
                .OrderBy(e => selectedExpenseTransactionId.HasValue && e.Id == selectedExpenseTransactionId.Value ? 0 : 1)
                .ThenByDescending(e => e.ExpenseDate)
                .ThenByDescending(e => e.Id)
                .Take(LookupLimit)
                .Select(e => new { e.Id, Label = e.Description ?? ("Expense #" + e.Id) })
                .ToListAsync(),
            "Id",
            "Label",
            selectedExpenseTransactionId);

        if (createModel is not null)
        {
            ViewBag.TruckDispatches = new SelectList(
                await _db.TruckDispatches
                    .AsNoTracking()
                    .OrderBy(d => selectedTruckDispatchId.HasValue && d.Id == selectedTruckDispatchId.Value ? 0 : 1)
                    .ThenByDescending(d => d.DispatchDate)
                    .ThenByDescending(d => d.Id)
                    .Take(LookupLimit)
                    .Select(d => new { d.Id, Label = d.Truck != null ? $"#{d.Id} - {d.Truck.PlateNumber}" : ("#" + d.Id) })
                    .ToListAsync(),
                "Id",
                "Label",
                selectedTruckDispatchId);
        }

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            createModel?.Currency ?? filter?.Currency);

        ViewBag.Directions = Enum.GetValues<PaymentDirection>()
            .Select(direction => new SelectListItem
            {
                Value = ((int)direction).ToString(),
                Text = PaymentDirectionLabels.ToPersian(direction),
                Selected = (createModel?.Direction ?? filter?.Direction) == direction
            })
            .ToList();

        ViewBag.PaymentKinds = Enum.GetValues<PaymentKind>()
            .Select(paymentKind => new SelectListItem
            {
                Value = ((int)paymentKind).ToString(),
                Text = PaymentKindLabels.ToPersian(paymentKind),
                Selected = (createModel?.PaymentKind ?? filter?.PaymentKind) == paymentKind
            })
            .ToList();

        ViewBag.CounterpartyTypes = Enum.GetValues<PaymentCounterpartyType>()
            .Select(type => new SelectListItem
            {
                Value = ((int)type).ToString(),
                Text = PaymentCounterpartyTypeLabels.ToPersian(type),
                Selected = (createModel?.CounterpartyType ?? filter?.CounterpartyType) == type
            })
            .ToList();
    }

    private async Task<ResolvedPaymentContext?> ValidateAndResolveAsync(PaymentCreateViewModel model)
    {
        if (!MatchesExpectedDirection(model.PaymentKind, model.Direction))
        {
            ModelState.AddModelError(nameof(model.Direction), "جهت انتخاب‌شده با نوع پرداخت / دریافت سازگار نیست.");
        }

        var cashAccount = await _db.CashAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == model.CashAccountId);
        if (cashAccount is null || !cashAccount.IsActive)
        {
            ModelState.AddModelError(nameof(model.CashAccountId), "حساب نقد / بانک انتخاب‌شده معتبر و فعال نیست.");
        }

        var hasCurrenciesConfigured = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasCurrenciesConfigured
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.Currency && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز انتخاب‌شده معتبر نیست.");
        }

        Customer? customer = null;
        if (model.CustomerId.HasValue)
        {
            customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.CustomerId.Value && c.IsActive);
            if (customer is null)
            {
                ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
            }
        }

        Supplier? supplier = null;
        if (model.SupplierId.HasValue)
        {
            supplier = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.SupplierId.Value && s.IsActive);
            if (supplier is null)
            {
                ModelState.AddModelError(nameof(model.SupplierId), "تأمین‌کننده انتخاب‌شده معتبر نیست.");
            }
        }

        ServiceProviderEntity? serviceProvider = null;
        if (model.ServiceProviderId.HasValue)
        {
            serviceProvider = await _db.ServiceProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ServiceProviderId.Value && p.IsActive);
            if (serviceProvider is null)
            {
                ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider selection is invalid.");
            }
        }

        Sarraf? sarraf = null;
        if (model.SarrafId.HasValue)
        {
            sarraf = await _db.Sarrafs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.SarrafId.Value && s.IsActive);
            if (sarraf is null)
            {
                ModelState.AddModelError(nameof(model.SarrafId), "صراف انتخاب‌شده معتبر نیست.");
            }
        }

        Driver? driver = null;
        if (model.DriverId.HasValue)
        {
            driver = await _db.Drivers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == model.DriverId.Value && d.IsActive);
            if (driver is null)
            {
                ModelState.AddModelError(nameof(model.DriverId), "راننده انتخاب‌شده معتبر نیست.");
            }
        }

        Employee? employee = null;
        if (model.EmployeeId.HasValue)
        {
            employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == model.EmployeeId.Value && e.IsActive);
            if (employee is null)
            {
                ModelState.AddModelError(nameof(model.EmployeeId), "کارمند انتخاب‌شده معتبر نیست.");
            }
        }

        Contract? contract = null;
        if (model.ContractId.HasValue)
        {
            contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
        }

        Shipment? shipment = null;
        if (model.ShipmentId.HasValue)
        {
            shipment = await _db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.ShipmentId.Value);
            if (shipment is null)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment انتخاب‌شده معتبر نیست.");
            }
        }

        SalesTransaction? sale = null;
        if (model.SalesTransactionId.HasValue)
        {
            sale = await _db.SalesTransactions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == model.SalesTransactionId.Value);
            if (sale is null)
            {
                ModelState.AddModelError(nameof(model.SalesTransactionId), "فروش انتخاب‌شده معتبر نیست.");
            }
        }

        ExpenseTransaction? expense = null;
        if (model.ExpenseTransactionId.HasValue)
        {
            expense = await _db.ExpenseTransactions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == model.ExpenseTransactionId.Value);
            if (expense is null)
            {
                ModelState.AddModelError(nameof(model.ExpenseTransactionId), "هزینه انتخاب‌شده معتبر نیست.");
            }
        }

        TruckDispatch? dispatch = null;
        if (model.TruckDispatchId.HasValue)
        {
            dispatch = await _db.TruckDispatches.AsNoTracking().FirstOrDefaultAsync(d => d.Id == model.TruckDispatchId.Value);
            if (dispatch is null)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "Truck Dispatch انتخاب‌شده معتبر نیست.");
            }
        }

        // حساب «مختلط» همه ارزها را می‌پذیرد؛ فقط حساب‌های تک‌ارزی باید با ارز پرداخت یکسان باشند.
        if (cashAccount is not null
            && cashAccount.AccountType != CashAccountType.Mixed
            && !string.Equals(cashAccount.Currency, model.Currency, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.Currency), "ارز پرداخت باید با ارز حساب نقد / بانک یکسان باشد.");
        }

        if (sale is not null)
        {
            if (model.CustomerId.HasValue && sale.CustomerId != model.CustomerId.Value)
            {
                ModelState.AddModelError(nameof(model.CustomerId), "مشتری فروش انتخاب‌شده با این payment سازگار نیست.");
            }

            if (model.ContractId.HasValue && !await SaleMatchesContractAsync(sale.Id, model.ContractId.Value))
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد فروش انتخاب‌شده با این payment سازگار نیست.");
            }

            if (model.ShipmentId.HasValue && sale.ShipmentId != model.ShipmentId)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment فروش انتخاب‌شده با این payment سازگار نیست.");
            }
        }

        if (expense is not null)
        {
            if (model.ContractId.HasValue && expense.ContractId != model.ContractId)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد هزینه انتخاب‌شده با این payment سازگار نیست.");
            }

            if (model.ShipmentId.HasValue && expense.ShipmentId != model.ShipmentId)
            {
                ModelState.AddModelError(nameof(model.ShipmentId), "Shipment هزینه انتخاب‌شده با این payment سازگار نیست.");
            }

            if (model.TruckDispatchId.HasValue && expense.TruckDispatchId != model.TruckDispatchId)
            {
                ModelState.AddModelError(nameof(model.TruckDispatchId), "Truck Dispatch هزینه انتخاب‌شده با این payment سازگار نیست.");
            }
        }

        if (dispatch is not null)
        {
            if (model.ContractId.HasValue && dispatch.ContractId != model.ContractId.Value)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد dispatch انتخاب‌شده با این payment سازگار نیست.");
            }

            if (model.DriverId.HasValue && dispatch.DriverId != model.DriverId)
            {
                ModelState.AddModelError(nameof(model.DriverId), "راننده dispatch انتخاب‌شده با این payment سازگار نیست.");
            }
        }

        var resolvedCustomerId = model.CustomerId ?? sale?.CustomerId ?? contract?.CustomerId;
        var resolvedSupplierId = model.SupplierId ?? contract?.SupplierId;
        var resolvedServiceProviderId = model.ServiceProviderId;
        var resolvedSarrafId = model.SarrafId;
        var resolvedDriverId = model.DriverId ?? dispatch?.DriverId;
        var resolvedEmployeeId = model.EmployeeId;
        var resolvedContractId = model.ContractId ?? sale?.ContractId ?? expense?.ContractId ?? shipment?.ContractId ?? dispatch?.ContractId;
        var resolvedShipmentId = model.ShipmentId ?? sale?.ShipmentId ?? expense?.ShipmentId;
        var resolvedTruckDispatchId = model.TruckDispatchId ?? expense?.TruckDispatchId;

        if (shipment is not null && shipment.ContractId.HasValue && resolvedContractId.HasValue && shipment.ContractId.Value != resolvedContractId.Value)
        {
            ModelState.AddModelError(nameof(model.ContractId), "Shipment انتخاب‌شده با قرارداد payment سازگار نیست.");
        }

        if (contract is not null && IsCustomerKind(model.PaymentKind) && contract.CustomerId.HasValue && resolvedCustomerId.HasValue && contract.CustomerId.Value != resolvedCustomerId.Value)
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری payment با مشتری قرارداد هم‌خوان نیست.");
        }

        if (contract is not null && IsSupplierKind(model.PaymentKind) && contract.ContractType != ContractType.Purchase)
        {
            ModelState.AddModelError(nameof(model.ContractId), "برای پرداخت تأمین‌کننده فقط قرارداد خرید معتبر است.");
        }

        if (contract is not null && IsSupplierKind(model.PaymentKind) && contract.SupplierId.HasValue && resolvedSupplierId.HasValue && contract.SupplierId.Value != resolvedSupplierId.Value)
        {
            ModelState.AddModelError(nameof(model.SupplierId), "تأمین‌کننده payment با تأمین‌کننده قرارداد هم‌خوان نیست.");
        }

        if (IsCustomerKind(model.PaymentKind) && !resolvedCustomerId.HasValue)
        {
            ModelState.AddModelError(nameof(model.CustomerId), "برای دریافت از مشتری باید مشتری یا relation واقعی مشتری مشخص باشد.");
        }

        if (IsSupplierKind(model.PaymentKind) && !resolvedSupplierId.HasValue)
        {
            ModelState.AddModelError(nameof(model.SupplierId), "برای پرداخت به تأمین‌کننده باید تأمین‌کننده یا relation واقعی تأمین‌کننده مشخص باشد.");
        }

        if (model.PaymentKind == PaymentKind.ServiceProviderPayment && !resolvedServiceProviderId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ServiceProviderId), "Service provider payment requires a service provider.");
        }

        if (model.PaymentKind == PaymentKind.SarrafSettlement && !resolvedSarrafId.HasValue)
        {
            ModelState.AddModelError(nameof(model.SarrafId), "برای پرداخت به صراف باید صراف مشخص باشد.");
        }

        if (model.PaymentKind == PaymentKind.TruckPayment && !resolvedTruckDispatchId.HasValue && !resolvedDriverId.HasValue)
        {
            ModelState.AddModelError(nameof(model.TruckDispatchId), "برای پرداخت کرایه / موتر باید dispatch یا راننده واقعی مشخص باشد.");
        }

        if ((model.PaymentKind == PaymentKind.EmployeeSalaryPayment
                || model.PaymentKind == PaymentKind.EmployeeSalaryAdvance
                || model.PaymentKind == PaymentKind.EmployeeReturn)
            && !resolvedEmployeeId.HasValue)
        {
            ModelState.AddModelError(nameof(model.EmployeeId), "برای پرداخت یا برداشت معاش باید کارمند مشخص باشد.");
        }

        if (!ModelState.IsValid || cashAccount is null)
        {
            return null;
        }

        return new ResolvedPaymentContext(
            cashAccount,
            resolvedCustomerId,
            resolvedSupplierId,
            resolvedServiceProviderId,
            resolvedSarrafId,
            resolvedDriverId,
            resolvedEmployeeId,
            resolvedContractId,
            resolvedShipmentId,
            model.SalesTransactionId,
            model.ExpenseTransactionId,
            resolvedTruckDispatchId);
    }

    private static bool MatchesExpectedDirection(PaymentKind paymentKind, PaymentDirection direction)
        => paymentKind switch
        {
            PaymentKind.CustomerReceipt => direction == PaymentDirection.In,
            PaymentKind.SupplierPayment => direction == PaymentDirection.Out,
            PaymentKind.ServiceProviderPayment => direction == PaymentDirection.Out,
            PaymentKind.SarrafSettlement => direction == PaymentDirection.Out,
            PaymentKind.SupplierReceipt => direction == PaymentDirection.In,
            PaymentKind.CustomerPayment => direction == PaymentDirection.Out,
            PaymentKind.ExpensePayment => direction == PaymentDirection.Out,
            PaymentKind.TruckPayment => direction == PaymentDirection.Out,
            PaymentKind.ManualPayment => direction == PaymentDirection.Out,
            PaymentKind.ManualReceipt => direction == PaymentDirection.In,
            PaymentKind.EmployeeSalaryPayment => direction == PaymentDirection.Out,
            PaymentKind.EmployeeSalaryAdvance => direction == PaymentDirection.Out,
            PaymentKind.EmployeeReturn => direction == PaymentDirection.In,
            PaymentKind.CommissionPayment => direction == PaymentDirection.Out,
            _ => false
        };

    private static bool IsCustomerKind(PaymentKind paymentKind)
        => paymentKind is PaymentKind.CustomerReceipt or PaymentKind.CustomerPayment;

    private static bool IsSupplierKind(PaymentKind paymentKind)
        => paymentKind is PaymentKind.SupplierPayment or PaymentKind.SupplierReceipt;

    private static bool IsSarrafKind(PaymentKind paymentKind)
        => paymentKind is PaymentKind.SarrafSettlement;

    private async Task<bool> SaleMatchesContractAsync(int saleId, int contractId)
        => await _db.SalesTransactions.AsNoTracking().AnyAsync(s => s.Id == saleId && s.ContractId == contractId)
            || await _db.InventoryMovements.AsNoTracking().AnyAsync(m =>
                m.Direction == MovementDirection.Out
                && m.ContractId == contractId
                && m.SalesTransactionId == saleId)
            || await _db.LoadingReceiptAllocations.AsNoTracking().AnyAsync(a =>
                a.SourcePurchaseContractId == contractId
                && a.SalesTransactionId == saleId)
            || await _db.TruckDispatches.AsNoTracking().AnyAsync(d =>
                d.ContractId == contractId
                && d.SalesTransactionId == saleId)
            || await _db.InventoryTransportReceipts.AsNoTracking().AnyAsync(r =>
                r.SalesTransactionId == saleId
                && !r.IsCancelled
                && _db.InventoryTransportLegs.Any(l =>
                    l.Id == r.InventoryTransportLegId
                    && l.SourcePurchaseContractId == contractId));

    private static LedgerSide GetLedgerSide(PaymentKind paymentKind)
        => paymentKind switch
        {
            PaymentKind.ManualReceipt => LedgerSide.Credit,
            PaymentKind.SupplierReceipt => LedgerSide.Credit,
            PaymentKind.CustomerPayment => LedgerSide.Credit,
            PaymentKind.EmployeeReturn => LedgerSide.Credit,
            PaymentKind.CustomerReceipt => LedgerSide.Debit,
            PaymentKind.SupplierPayment => LedgerSide.Debit,
            PaymentKind.ServiceProviderPayment => LedgerSide.Debit,
            PaymentKind.SarrafSettlement => LedgerSide.Debit,
            PaymentKind.ExpensePayment => LedgerSide.Debit,
            PaymentKind.TruckPayment => LedgerSide.Debit,
            PaymentKind.ManualPayment => LedgerSide.Debit,
            PaymentKind.EmployeeSalaryPayment => LedgerSide.Debit,
            PaymentKind.EmployeeSalaryAdvance => LedgerSide.Debit,
            PaymentKind.CommissionPayment => LedgerSide.Debit,
            _ => LedgerSide.Debit
        };

    private static void NormalizeFilter(PaymentIndexFilterViewModel filter)
    {
        filter.Currency = string.IsNullOrWhiteSpace(filter.Currency) ? null : SystemCurrency.Normalize(filter.Currency);
        filter.Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();
        filter.Reference = string.IsNullOrWhiteSpace(filter.Reference) ? null : filter.Reference.Trim();
    }

    private async Task<IActionResult> CreateViaSarrafAsync(PaymentCreateViewModel model)
    {
        // تأمین‌کنندهٔ «پرداخت» (Out) با یک نرخ: مسیر خام و آزمودهٔ فعلی (دو LedgerEntry مستقیم)
        // دست‌نخورده می‌ماند تا صورت‌حساب‌هایی که SourceType=ViaSarrafSupplier* را می‌خوانند نشکنند.
        // بقیهٔ حالت‌ها — مشتری/شرکت خدماتی/راننده/کارمند، تأمین‌کنندهٔ «دریافت/برگشت» (In)، و
        // حالت دو‌نرخی (نرخ خرید شرکت ≠ نرخ قبول تأمین‌کننده) — از موتور عمومی SarrafSettlement
        // عبور می‌کنند که هر دو نرخ و تفاوت آن‌ها را از قبل می‌شناسد.
        var counterpartyType = model.SarrafCounterpartyType ?? SarrafSettlementCounterpartyType.Supplier;
        var directionIsOut = (model.SarrafDirection ?? SarrafSettlementDirection.Out) == SarrafSettlementDirection.Out;
        if (counterpartyType != SarrafSettlementCounterpartyType.Supplier
            || !directionIsOut
            || HasDistinctSarrafCompanyRate(model))
        {
            return await CreateViaSarrafGeneralAsync(model);
        }

        var context = await ValidateViaSarrafSupplierAsync(model);
        if (!ModelState.IsValid || context is null)
        {
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        var description = string.IsNullOrWhiteSpace(model.Description)
            ? $"پرداخت از طریق صراف برای تأمین‌کننده"
            : model.Description.Trim();
        var reference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim();
        var amount = model.SarrafSupplierAmount!.Value;

        // شناسهٔ گروهِ این عملیات — هر دو LedgerEntry مکمل زیر همین یک شناسه می‌نشینند تا بعداً
        // اتصال قطعی آن‌ها برای «ثبت/تغییر قرارداد» ممکن باشد (بدون حدسِ مبلغ/تاریخ).
        var viaSarrafGroupId = Guid.NewGuid();

        // کمیسیون ViaSarraf (اختیاری) — صندوق دست نمی‌خورد؛ به بدهی صراف اضافه می‌شود.
        CommissionComputation? commission = null;
        ExpenseType? commissionType = null;
        if (model.CommissionEnabled)
        {
            commission = await ValidateAndComputeCommissionAsync(
                model, amount, context.Currency, model.PaymentDate.Date,
                context.FxRateToUsd, model.PaymentDate.Date, context.FxRateSource);
            if (!ModelState.IsValid || commission is null)
            {
                await PopulateLookupsAsync(createModel: model);
                return View(model);
            }
            commissionType = await EnsureCommissionExpenseTypeAsync();
        }

        var supplierLedger = new LedgerEntry
        {
            EntryDate = model.PaymentDate.Date,
            Side = LedgerSide.Debit,
            AmountUsd = context.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = amount,
            SourceCurrencyCode = context.Currency,
            AppliedFxRateToUsd = context.FxRateToUsd,
            AppliedFxRateDate = model.PaymentDate.Date,
            AppliedFxRateSource = context.FxRateSource,
            Description = description,
            SourceType = ViaSarrafSupplierLedgerSourceType,
            SourceId = context.SarrafId,
            Reference = reference,
            SupplierId = context.SupplierId,
            ContractId = context.ContractId,
            ViaSarrafGroupId = viaSarrafGroupId
        };

        var sarrafPayableLedger = new LedgerEntry
        {
            EntryDate = model.PaymentDate.Date,
            Side = LedgerSide.Credit,
            AmountUsd = context.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = amount,
            SourceCurrencyCode = context.Currency,
            AppliedFxRateToUsd = context.FxRateToUsd,
            AppliedFxRateDate = model.PaymentDate.Date,
            AppliedFxRateSource = context.FxRateSource,
            Description = $"بدهی شرکت به صراف بابت پرداخت به تأمین‌کننده: {description}",
            SourceType = ViaSarrafPayableLedgerSourceType,
            SourceId = context.SarrafId,
            Reference = reference,
            ContractId = context.ContractId,
            ViaSarrafGroupId = viaSarrafGroupId
        };

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            _db.LedgerEntries.AddRange(supplierLedger, sarrafPayableLedger);
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                nameof(LedgerEntry),
                supplierLedger.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("EntryDate", supplierLedger.EntryDate),
                    ("Side", supplierLedger.Side),
                    ("Amount", supplierLedger.SourceAmount),
                    ("Currency", supplierLedger.SourceCurrencyCode),
                    ("SourceType", supplierLedger.SourceType),
                    ("SupplierId", supplierLedger.SupplierId),
                    ("SarrafId", context.SarrafId),
                    ("ContractId", supplierLedger.ContractId),
                    ("Reference", supplierLedger.Reference)));

            await _audit.LogAsync(
                nameof(LedgerEntry),
                sarrafPayableLedger.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("EntryDate", sarrafPayableLedger.EntryDate),
                    ("Side", sarrafPayableLedger.Side),
                    ("Amount", sarrafPayableLedger.SourceAmount),
                    ("Currency", sarrafPayableLedger.SourceCurrencyCode),
                    ("SourceType", sarrafPayableLedger.SourceType),
                    ("SarrafId", context.SarrafId),
                    ("ContractId", sarrafPayableLedger.ContractId),
                    ("Reference", sarrafPayableLedger.Reference)));

            if (commission is not null && commissionType is not null)
            {
                await PostViaSarrafCommissionAsync(
                    commission, context.SarrafId, context.SupplierId, context.ContractId,
                    model.PaymentDate.Date, reference, commissionType);
            }

            // مرحله ۴ — Dual-write به دفتر کل جدید داخل همان Transaction قدیمی. این جریان
            // PaymentTransaction نمی‌سازد، بنابراین هویت رویداد همان سطر Ledger تأمین‌کننده است
            // (بعد از SaveChanges بالا Id گرفته است).
            if (_viaSarrafAccounting is not null)
            {
                await _viaSarrafAccounting.TryPostSupplierPaymentAsync(
                    new Services.Accounting.ViaSarrafSupplierPaymentEvent(
                        supplierLedger.Id,
                        context.SupplierId,
                        context.SarrafId,
                        context.ContractId,
                        model.PaymentDate.Date,
                        context.Currency,
                        amount,
                        context.AmountUsd,
                        context.FxRateToUsd));
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            TempData["ok"] = commission is not null
                ? "پرداخت از طریق صراف ثبت شد؛ کمیسیون به بدهی صراف اضافه شد. صندوق/بانک تغییر نکرد."
                : "پرداخت از طریق صراف ثبت شد؛ حساب تأمین‌کننده کم و بدهی به صراف ثبت شد. صندوق/بانک تغییر نکرد.";

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogError(ex, "Failed to create via-sarraf supplier payment.");
            ModelState.AddModelError(string.Empty, "ثبت پرداخت از طریق صراف انجام نشد. دوباره تلاش کنید.");
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }
    }

    // پرداخت/دریافت از طریق صراف برای طرف‌حساب‌های غیر تأمین‌کننده (مشتری/شرکت خدماتی/راننده/کارمند).
    // از موتور عمومی SarrafSettlementService استفاده می‌کند. برای هم‌سانی با مسیر تأمین‌کننده،
    // نرخ صراف با شرکت را برابر نرخ طرف مقابل می‌گذاریم تا «یک نرخ، بدون تفاوت» بماند (DifferenceType=None).
    private async Task<IActionResult> CreateViaSarrafGeneralAsync(PaymentCreateViewModel model)
    {
        var currency = string.IsNullOrWhiteSpace(model.SarrafSupplierCurrency)
            ? "RUB"
            : SystemCurrency.Normalize(model.SarrafSupplierCurrency);
        model.SarrafSupplierCurrency = currency;
        // نرخ خرید ارز توسط شرکت (سمت صراف) از نرخ قبول طرف مقابل جداست. اگر کاربر آن را
        // وارد نکرده باشد، همان نرخ طرف مقابل به‌کار می‌رود و تفاوت صفر می‌شود — یعنی رفتار
        // تک‌نرخیِ قبلی بدون تغییر. وارد کردن نرخ متفاوت، سناریوی تفاوت نرخ ارز را فعال می‌کند.
        model.SarrafCompanyPerUsdRate = SystemCurrency.IsBaseCurrency(currency)
            ? 1m
            : (model.SarrafCompanyPerUsdRate is > 0m
                ? model.SarrafCompanyPerUsdRate
                : model.SarrafSupplierPerUsdRate);

        // کمیسیون ViaSarraf (اختیاری) برای طرف‌های عمومی — مانند مسیر تأمین‌کننده صندوق دست نمی‌خورد؛
        // به‌عنوان مصرف در P&L ثبت و به بدهی صراف اضافه می‌شود. جهت (پرداخت/دریافت) اثری بر کمیسیون ندارد.
        var isUsd = SystemCurrency.IsBaseCurrency(currency);
        var documentRate = isUsd ? 1m : (model.SarrafSupplierPerUsdRate ?? 0m);
        CommissionComputation? commission = null;
        ExpenseType? commissionType = null;
        if (model.CommissionEnabled)
        {
            var commFxRateToUsd = isUsd
                ? 1m
                : (documentRate > 0m ? decimal.Round(1m / documentRate, 6, MidpointRounding.AwayFromZero) : 0m);
            var commFxSource = isUsd
                ? $"Identity {SystemCurrency.BaseCurrencyCode}/{SystemCurrency.BaseCurrencyCode}"
                : $"Via sarraf manual rate: 1 USD = {documentRate:N4} {currency}";
            commission = await ValidateAndComputeCommissionAsync(
                model, model.SarrafSupplierAmount ?? 0m, currency, model.PaymentDate.Date,
                commFxRateToUsd, model.PaymentDate.Date, commFxSource);
            if (!ModelState.IsValid || commission is null)
            {
                await PopulateLookupsAsync(createModel: model);
                return View(model);
            }
            commissionType = await EnsureCommissionExpenseTypeAsync();
        }

        var command = BuildSarrafCommandFromModel(model);
        if (command is null)
        {
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        try
        {
            var settlement = await _sarrafSettlements.CreatePostedAsync(command);

            await _audit.LogAndSaveAsync(
                nameof(SarrafSettlement),
                settlement.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("SettlementDate", settlement.SettlementDate),
                    ("Direction", settlement.Direction),
                    ("CounterpartyType", settlement.CounterpartyType),
                    ("SarrafId", settlement.SarrafId),
                    ("CustomerId", settlement.CustomerId),
                    ("ServiceProviderId", settlement.ServiceProviderId),
                    ("DriverId", settlement.DriverId),
                    ("EmployeeId", settlement.EmployeeId),
                    ("SarrafChargedAmount", settlement.SarrafChargedAmount),
                    ("SarrafCurrency", settlement.SarrafCurrency),
                    ("ReferenceNumber", settlement.ReferenceNumber),
                    ("LedgerEntryId", settlement.LedgerEntryId)));

            // تفاوت نرخ ارز (نرخ خرید شرکت در برابر نرخ قبول طرف مقابل) — مصرف واقعی، بدون خروج نقدی.
            // مستقل از کمیسیون است و اگر هر دو نرخ یکسان باشند اصلاً ساخته نمی‌شود.
            await PostSarrafFxDifferenceAsync(settlement);

            if (commission is not null && commissionType is not null)
            {
                var commissionReference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim();
                await using var commissionTx = _db.Database.IsRelational()
                    ? await _db.Database.BeginTransactionAsync()
                    : null;
                await PostViaSarrafCommissionAsync(
                    commission, settlement.SarrafId, settlement.SupplierId, settlement.ContractId,
                    model.PaymentDate.Date, commissionReference, commissionType);
                if (commissionTx is not null)
                {
                    await commissionTx.CommitAsync();
                }
            }

            TempData["ok"] = commission is not null
                ? "پرداخت / دریافت از طریق صراف ثبت شد؛ کمیسیون به‌عنوان مصرف و به بدهی صراف ثبت شد. صندوق/بانک تغییر نکرد."
                : "پرداخت / دریافت از طریق صراف ثبت شد؛ حساب طرف مقابل کم و بدهی/طلب صراف ثبت شد. صندوق/بانک تغییر نکرد.";

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }
    }

    private async Task<ViaSarrafSupplierContext?> ValidateViaSarrafSupplierAsync(PaymentCreateViewModel model)
    {
        ModelState.Clear();

        model.PaymentDate = model.PaymentDate.Date;
        model.PaymentKind = PaymentKind.SupplierPayment;
        model.Direction = PaymentDirection.Out;
        model.SarrafCounterpartyType = SarrafSettlementCounterpartyType.Supplier;
        var currency = string.IsNullOrWhiteSpace(model.SarrafSupplierCurrency)
            ? "USD"
            : SystemCurrency.Normalize(model.SarrafSupplierCurrency);
        model.SarrafSupplierCurrency = currency;

        if (!currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            && !currency.Equals("RUB", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.SarrafSupplierCurrency), "برای پرداخت از طریق صراف فقط USD یا RUB مجاز است.");
        }

        if (!model.SupplierId.HasValue || model.SupplierId.Value <= 0)
        {
            ModelState.AddModelError(nameof(model.SupplierId), "انتخاب تأمین‌کننده الزامی است.");
        }

        if (!model.SarrafId.HasValue || model.SarrafId.Value <= 0)
        {
            ModelState.AddModelError(nameof(model.SarrafId), "انتخاب صراف الزامی است.");
        }

        if (!model.SarrafSupplierAmount.HasValue || model.SarrafSupplierAmount.Value <= 0m)
        {
            ModelState.AddModelError(nameof(model.SarrafSupplierAmount), "مبلغ باید بزرگ‌تر از صفر باشد.");
        }

        var isUsd = SystemCurrency.IsBaseCurrency(currency);
        var documentRate = isUsd ? 1m : (model.SarrafSupplierPerUsdRate ?? 0m);
        if (!isUsd && documentRate <= 0m)
        {
            ModelState.AddModelError(nameof(model.SarrafSupplierPerUsdRate), "نرخ دالر را وارد کنید (مثال: ۹۲ یعنی ۱ دالر = ۹۲ روبل).");
        }

        var supplierId = model.SupplierId ?? 0;
        var sarrafId = model.SarrafId ?? 0;

        if (supplierId > 0
            && !await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == supplierId && s.IsActive))
        {
            ModelState.AddModelError(nameof(model.SupplierId), "تأمین‌کننده انتخاب‌شده معتبر نیست.");
        }

        if (sarrafId > 0
            && !await _db.Sarrafs.AsNoTracking().AnyAsync(s => s.Id == sarrafId && s.IsActive))
        {
            ModelState.AddModelError(nameof(model.SarrafId), "صراف انتخاب‌شده معتبر نیست.");
        }

        int? contractId = null;
        if (model.ContractId.HasValue)
        {
            var contract = await _db.Contracts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == model.ContractId.Value);
            if (contract is null)
            {
                ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
            }
            else if (contract.ContractType != ContractType.Purchase)
            {
                ModelState.AddModelError(nameof(model.ContractId), "برای پرداخت تأمین‌کننده فقط قرارداد خرید معتبر است.");
            }
            else if (contract.SupplierId.HasValue && contract.SupplierId.Value != supplierId)
            {
                ModelState.AddModelError(nameof(model.SupplierId), "تأمین‌کننده payment با تأمین‌کننده قرارداد هم‌خوان نیست.");
            }
            else
            {
                contractId = contract.Id;
            }
        }

        if (!ModelState.IsValid)
        {
            return null;
        }

        var fxRateToUsd = isUsd ? 1m : decimal.Round(1m / documentRate, 6, MidpointRounding.AwayFromZero);
        var amountUsd = isUsd
            ? decimal.Round(model.SarrafSupplierAmount!.Value, 4, MidpointRounding.AwayFromZero)
            : decimal.Round(model.SarrafSupplierAmount!.Value / documentRate, 4, MidpointRounding.AwayFromZero);
        var fxRateSource = isUsd
            ? $"Identity {SystemCurrency.BaseCurrencyCode}/{SystemCurrency.BaseCurrencyCode}"
            : $"Via sarraf manual rate: 1 USD = {documentRate:N4} {currency}";

        return new ViaSarrafSupplierContext(
            supplierId,
            sarrafId,
            contractId,
            currency,
            amountUsd,
            fxRateToUsd,
            fxRateSource);
    }

    // ویرایش «حواله صراف» از همان فرم روزنامچه‌ای که با آن ثبت شده است.
    // فرم اضافیِ ماژول صراف (SarrafSettlements/Edit) حذف شده و این مسیر جایگزین آن است.
    // منطق مالی جدیدی ساخته نمی‌شود؛ از همان EditPostedAsync موجود استفاده می‌شود.
    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> EditSarrafHawala(int id, string? returnUrl = null)
    {
        var settlement = await _db.SarrafSettlements
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (settlement is null)
        {
            return NotFound();
        }

        if (settlement.Status == SarrafSettlementStatus.Cancelled)
        {
            TempData["error"] = "تسویهٔ لغو‌شده قابل ویرایش نیست.";
            return RedirectToAction(nameof(Index));
        }

        // اگر این تسویه به یک پرداخت نقدی/بانکی وصل است، از مسیر استاندارد ویرایش پرداخت برود.
        if (settlement.PaymentTransactionId.HasValue)
        {
            return RedirectToAction(nameof(Edit), new { id = settlement.PaymentTransactionId.Value, returnUrl });
        }

        var model = new PaymentCreateViewModel
        {
            Id = settlement.Id,
            PaymentMethod = PaymentMethod.ViaSarraf,
            PaymentKind = PaymentKind.SarrafSettlement,
            PaymentDate = settlement.SettlementDate,
            SarrafId = settlement.SarrafId,
            SarrafDirection = settlement.Direction,
            SarrafCounterpartyType = settlement.CounterpartyType,
            SupplierId = settlement.CounterpartyType == SarrafSettlementCounterpartyType.Supplier ? settlement.SupplierId : null,
            SarrafCustomerId = settlement.CounterpartyType == SarrafSettlementCounterpartyType.Customer ? settlement.CustomerId : null,
            SarrafServiceProviderId = settlement.CounterpartyType == SarrafSettlementCounterpartyType.ServiceProvider ? settlement.ServiceProviderId : null,
            SarrafDriverId = settlement.CounterpartyType == SarrafSettlementCounterpartyType.Driver ? settlement.DriverId : null,
            SarrafEmployeeId = settlement.CounterpartyType == SarrafSettlementCounterpartyType.Employee ? settlement.EmployeeId : null,
            ContractId = settlement.ContractId,
            SarrafSupplierAmount = settlement.SarrafChargedAmount,
            SarrafSupplierCurrency = settlement.SarrafCurrency,
            SarrafSupplierPerUsdRate = settlement.SupplierRate,
            SarrafCompanyPerUsdRate = settlement.SarrafRate,
            Reference = settlement.ReferenceNumber,
            Description = settlement.Description,
            ReturnUrl = returnUrl
        };

        await PopulateLookupsAsync(createModel: model);
        ViewData["PaymentFormMode"] = "EditSarrafHawala";
        return View("Create", model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSarrafHawala(int id, PaymentCreateViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        model.PaymentMethod = PaymentMethod.ViaSarraf;
        var command = BuildSarrafCommandFromModel(model);
        if (command is null)
        {
            await PopulateLookupsAsync(createModel: model);
            ViewData["PaymentFormMode"] = "EditSarrafHawala";
            return View("Create", model);
        }

        try
        {
            await _sarrafSettlements.EditPostedAsync(id, command, null);
            TempData["ok"] = "حواله صراف ویرایش و دفتر کل آن به‌روزرسانی شد.";

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateLookupsAsync(createModel: model);
            ViewData["PaymentFormMode"] = "EditSarrafHawala";
            return View("Create", model);
        }
    }

    // نگاشت فیلدهای سادهٔ فرم روزنامچه (حالت «از طریق صرافی») به SarrafSettlementCommand.
    // مشترک بین ثبت و ویرایش. اگر داده نامعتبر باشد، خطاها به ModelState افزوده و null برمی‌گردد.
    private SarrafSettlementCommand? BuildSarrafCommandFromModel(PaymentCreateViewModel model)
    {
        // فقط فیلدهای صراف اعتبارسنجی می‌شوند؛ خطاهای فیلدهای فرم نقد/بانک بی‌ربط‌اند.
        ModelState.Clear();

        // پرداختِ صراف پیش‌فرض روبلی است؛ اگر ارز خالی بماند RUB در نظر گرفته می‌شود
        // تا مبلغِ روبلی به‌اشتباه به‌عنوان USD ثبت نشود. انتخابِ صریح USD همچنان محترم است.
        var currency = string.IsNullOrWhiteSpace(model.SarrafSupplierCurrency)
            ? "RUB"
            : SystemCurrency.Normalize(model.SarrafSupplierCurrency);
        model.SarrafSupplierCurrency = currency;
        var isUsd = SystemCurrency.IsBaseCurrency(currency);

        if (!model.SarrafId.HasValue || model.SarrafId.Value <= 0)
        {
            ModelState.AddModelError(nameof(model.SarrafId), "انتخاب صراف الزامی است.");
        }

        // نوع طرف‌حساب و جهت. backward-compatible: اگر فرم قدیمی فقط SupplierId بفرستد،
        // پیش‌فرض «تأمین‌کننده / پرداخت (Out)» تعبیر می‌شود.
        var counterpartyType = model.SarrafCounterpartyType ?? SarrafSettlementCounterpartyType.Supplier;
        // جهت (پرداخت/دریافت) آزاد است و از فرم می‌آید. اگر نیامد، جهتِ متعارفِ نوع را پیش‌فرض بگیر:
        // مشتری = دریافت (In)، بقیه = پرداخت (Out).
        var direction = model.SarrafDirection
            ?? (counterpartyType == SarrafSettlementCounterpartyType.Customer
                ? SarrafSettlementDirection.In
                : SarrafSettlementDirection.Out);

        int? supplierId = null;
        int? customerId = null;
        int? serviceProviderId = null;
        int? driverId = null;
        int? employeeId = null;
        switch (counterpartyType)
        {
            case SarrafSettlementCounterpartyType.Supplier:
                supplierId = model.SupplierId;
                if (!supplierId.HasValue || supplierId.Value <= 0)
                {
                    ModelState.AddModelError(nameof(model.SupplierId), "انتخاب تأمین‌کننده الزامی است.");
                }
                break;
            case SarrafSettlementCounterpartyType.Customer:
                customerId = model.SarrafCustomerId;
                if (!customerId.HasValue || customerId.Value <= 0)
                {
                    ModelState.AddModelError(nameof(model.SarrafCustomerId), "انتخاب مشتری الزامی است.");
                }
                break;
            case SarrafSettlementCounterpartyType.ServiceProvider:
                serviceProviderId = model.SarrafServiceProviderId;
                if (!serviceProviderId.HasValue || serviceProviderId.Value <= 0)
                {
                    ModelState.AddModelError(nameof(model.SarrafServiceProviderId), "انتخاب شرکت خدماتی الزامی است.");
                }
                break;
            case SarrafSettlementCounterpartyType.Driver:
                driverId = model.SarrafDriverId;
                if (!driverId.HasValue || driverId.Value <= 0)
                {
                    ModelState.AddModelError(nameof(model.SarrafDriverId), "انتخاب راننده الزامی است.");
                }
                break;
            case SarrafSettlementCounterpartyType.Employee:
                employeeId = model.SarrafEmployeeId;
                if (!employeeId.HasValue || employeeId.Value <= 0)
                {
                    ModelState.AddModelError(nameof(model.SarrafEmployeeId), "انتخاب کارمند الزامی است.");
                }
                break;
        }

        var amountLabel = direction == SarrafSettlementDirection.In ? "مبلغ دریافت‌شده" : "مبلغ فرستاده‌شده";
        if (!model.SarrafSupplierAmount.HasValue || model.SarrafSupplierAmount.Value <= 0m)
        {
            ModelState.AddModelError(nameof(model.SarrafSupplierAmount), $"{amountLabel} باید بزرگ‌تر از صفر باشد.");
        }

        // نرخ ساده «۱ دالر = چند واحد ارز». برای دالر نرخ ۱ است و لازم نیست.
        var supplierRate = isUsd ? 1m : (model.SarrafSupplierPerUsdRate ?? 0m);
        if (!isUsd && supplierRate <= 0m)
        {
            ModelState.AddModelError(nameof(model.SarrafSupplierPerUsdRate), "نرخ حساب طرف مقابل را وارد کنید (مثال: ۷۷ یعنی ۱ دالر = ۷۷ روبل).");
        }

        var companyRate = model.SarrafCompanyPerUsdRate ?? 0m;
        if (companyRate <= 0m)
        {
            ModelState.AddModelError(nameof(model.SarrafCompanyPerUsdRate), "نرخ حساب صراف با شرکت را وارد کنید (مثال: ۷۷ یعنی ۱ دالر = ۷۷ روبل).");
        }

        if (!ModelState.IsValid)
        {
            return null;
        }

        var amount = model.SarrafSupplierAmount!.Value;
        var supplierFx = 1m / supplierRate;   // نرخ داخلی USD-per-unit (کاربر آن را نمی‌بیند)
        var companyFx = 1m / companyRate;

        return new SarrafSettlementCommand(
            SettlementDate: model.PaymentDate.Date,
            SarrafId: model.SarrafId!.Value,
            SupplierId: supplierId,
            // قرارداد فقط برای تأمین‌کننده مجاز است (Phase 1)؛ برای مشتری/شرکت خدماتی نادیده گرفته می‌شود.
            ContractId: counterpartyType == SarrafSettlementCounterpartyType.Supplier ? model.ContractId : null,
            PaymentTransactionId: null,
            CashAccountId: null,
            ReferenceNumber: model.Reference,
            Description: model.Description,
            // دو نرخ جدا: حساب طرف مقابل با نرخ طرف مقابل کم می‌شود (SupplierAccepted*)،
            // و حساب صراف با شرکت با نرخ صراف ثبت می‌شود (SarrafCharged*).
            // «درخواستی» = همان سمت صراف؛ بدین‌گونه تفاوت = (سمت صراف − سمت طرف مقابل)
            // به‌عنوان مصرف/کمیشن حواله ثبت می‌شود (SupplierShortfall) و صندوق دست‌نخورده می‌ماند.
            RequestedAmount: amount,
            RequestedCurrency: currency,
            RequestedFxRateToUsd: companyFx,
            SarrafCurrency: currency,
            SarrafRate: companyRate,
            SarrafChargedAmount: amount,
            SarrafFxRateToUsd: companyFx,
            SupplierAcceptedAmount: amount,
            SupplierAcceptedCurrency: currency,
            SupplierAcceptedFxRateToUsd: supplierFx,
            SupplierRate: supplierRate,
            DifferenceTreatment: SarrafSettlementDifferenceTreatment.AcceptedAmountOnly,
            Direction: direction,
            CounterpartyType: counterpartyType,
            CustomerId: customerId,
            ServiceProviderId: serviceProviderId,
            DriverId: driverId,
            EmployeeId: employeeId);
    }

    private static void NormalizeCreateModel(PaymentCreateViewModel model)
    {
        model.PaymentDate = model.PaymentDate.Date;
        model.Currency = SystemCurrency.Normalize(model.Currency);
        model.CounterpartyType = InferCounterpartyType(model);
        model.Reference = string.IsNullOrWhiteSpace(model.Reference) ? null : model.Reference.Trim();
        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();

        if (model.DocumentCurrencyPerUsdRate.HasValue
            && model.DocumentCurrencyPerUsdRate.Value > 0m
            && !SystemCurrency.IsBaseCurrency(model.Currency))
        {
            model.AppliedFxRateToUsd = 1m / model.DocumentCurrencyPerUsdRate.Value;
        }
    }

    private static PaymentCounterpartyType InferCounterpartyType(PaymentCreateViewModel model)
        => model.PaymentKind switch
        {
            PaymentKind.CustomerReceipt or PaymentKind.CustomerPayment => PaymentCounterpartyType.Customer,
            PaymentKind.SupplierPayment or PaymentKind.SupplierReceipt => PaymentCounterpartyType.Supplier,
            PaymentKind.ServiceProviderPayment => PaymentCounterpartyType.ServiceProvider,
            PaymentKind.SarrafSettlement => PaymentCounterpartyType.Sarraf,
            PaymentKind.EmployeeSalaryPayment or PaymentKind.EmployeeSalaryAdvance or PaymentKind.EmployeeReturn => PaymentCounterpartyType.Employee,
            PaymentKind.TruckPayment => PaymentCounterpartyType.Driver,
            PaymentKind.ExpensePayment or PaymentKind.CommissionPayment => PaymentCounterpartyType.OfficeExpense,
            _ => InferCounterpartyType(
                model.CustomerId,
                model.SupplierId,
                model.ServiceProviderId,
                model.SarrafId,
                model.EmployeeId,
                model.DriverId,
                model.ContractId,
                model.ShipmentId,
                model.SalesTransactionId,
                model.ExpenseTransactionId)
        };

    private static PaymentCounterpartyType InferCounterpartyType(PaymentTransaction payment)
        => payment.PaymentKind switch
        {
            PaymentKind.CustomerReceipt or PaymentKind.CustomerPayment => PaymentCounterpartyType.Customer,
            PaymentKind.SupplierPayment or PaymentKind.SupplierReceipt => PaymentCounterpartyType.Supplier,
            PaymentKind.ServiceProviderPayment => PaymentCounterpartyType.ServiceProvider,
            PaymentKind.SarrafSettlement => PaymentCounterpartyType.Sarraf,
            PaymentKind.EmployeeSalaryPayment or PaymentKind.EmployeeSalaryAdvance or PaymentKind.EmployeeReturn => PaymentCounterpartyType.Employee,
            PaymentKind.TruckPayment => PaymentCounterpartyType.Driver,
            PaymentKind.ExpensePayment or PaymentKind.CommissionPayment => PaymentCounterpartyType.OfficeExpense,
            _ => InferCounterpartyType(
                payment.CustomerId,
                payment.SupplierId,
                payment.ServiceProviderId,
                payment.SarrafId,
                payment.EmployeeId,
                payment.DriverId,
                payment.ContractId,
                payment.ShipmentId,
                payment.SalesTransactionId,
                payment.ExpenseTransactionId)
        };

    private static PaymentCounterpartyType InferCounterpartyType(
        int? customerId,
        int? supplierId,
        int? serviceProviderId,
        int? sarrafId,
        int? employeeId,
        int? driverId,
        int? contractId,
        int? shipmentId,
        int? salesTransactionId,
        int? expenseTransactionId)
    {
        if (customerId.HasValue) return PaymentCounterpartyType.Customer;
        if (supplierId.HasValue) return PaymentCounterpartyType.Supplier;
        if (serviceProviderId.HasValue) return PaymentCounterpartyType.ServiceProvider;
        if (sarrafId.HasValue) return PaymentCounterpartyType.Sarraf;
        if (employeeId.HasValue) return PaymentCounterpartyType.Employee;
        if (driverId.HasValue) return PaymentCounterpartyType.Driver;
        if (expenseTransactionId.HasValue) return PaymentCounterpartyType.OfficeExpense;
        if (salesTransactionId.HasValue) return PaymentCounterpartyType.Sales;
        if (shipmentId.HasValue) return PaymentCounterpartyType.Shipment;
        if (contractId.HasValue) return PaymentCounterpartyType.Contract;
        return PaymentCounterpartyType.Other;
    }

    private static string BuildLedgerDescription(PaymentTransaction payment, CashAccount cashAccount)
    {
        var baseText = $"{PaymentDirectionLabels.ToPersian(payment.Direction)} - {PaymentKindLabels.ToPersian(payment.PaymentKind)}";
        var reference = string.IsNullOrWhiteSpace(payment.Reference) ? string.Empty : $" / {payment.Reference}";
        var description = string.IsNullOrWhiteSpace(payment.Description) ? string.Empty : $" / {payment.Description}";
        return $"{baseText} / {cashAccount.Name}{reference}{description}";
    }

    // ===================== کمیسیون (رزنامچه) =====================
    // مدل «دو رکورد مرتبط»:
    //  • نقد/بانک: ExpenseTransaction (مصرف واقعی، SourceType="Expense" → P&L) + یک
    //    PaymentTransaction خروج نقدی (CommissionPayment, Out → مانده صندوق کم می‌شود).
    //    برای جلوگیری از دوباره‌شماری، فقط لِجرِ SourceType="Expense" در P&L شمرده می‌شود؛
    //    لِجرِ خروج نقدی SourceType="CommissionPayment" فقط cash movement است.
    //  • ViaSarraf: صندوق دست نمی‌خورد؛ ExpenseTransaction (SourceType="Expense" → P&L) +
    //    یک LedgerEntry بستانکارِ بدهی صراف (همان SourceType بدهی صراف) که با مصرف متوازن است.

    private sealed record CommissionComputation(
        decimal Amount,
        string Currency,
        decimal FxRateToUsd,
        decimal AmountUsd,
        DateTime EffectiveDate,
        string FxSource,
        string Description,
        PaymentCommissionType Type,
        decimal? Percent);

    // اعتبارسنجی و محاسبهٔ کمیسیون. اگر غیرفعال بود null برمی‌گرداند بدون خطا.
    // در صورت خطا ModelState پر می‌شود و null برمی‌گردد. اثر مالی ندارد (فقط محاسبه).
    private async Task<CommissionComputation?> ValidateAndComputeCommissionAsync(
        PaymentCreateViewModel model,
        decimal mainAmount,
        string mainCurrency,
        DateTime mainDate,
        decimal mainFxRateToUsd,
        DateTime mainFxDate,
        string mainFxSource)
    {
        if (!model.CommissionEnabled)
        {
            return null;
        }

        if (model.CommissionType is null)
        {
            ModelState.AddModelError(nameof(model.CommissionType), "نوع کمیسیون را انتخاب کنید.");
        }

        decimal commissionAmount = 0m;
        var commissionCurrency = mainCurrency;
        decimal? percentForTrace = null;

        if (model.CommissionType == PaymentCommissionType.Percent)
        {
            percentForTrace = model.CommissionPercent;
            if (!model.CommissionPercent.HasValue)
            {
                ModelState.AddModelError(nameof(model.CommissionPercent), "درصد کمیسیون را وارد کنید.");
            }
            else if (model.CommissionPercent.Value < 0m)
            {
                ModelState.AddModelError(nameof(model.CommissionPercent), "درصد کمیسیون نمی‌تواند منفی باشد.");
            }
            else if (model.CommissionPercent.Value > 100m)
            {
                ModelState.AddModelError(nameof(model.CommissionPercent), "درصد کمیسیون نمی‌تواند بیشتر از ۱۰۰ باشد.");
            }
            else
            {
                commissionAmount = decimal.Round(mainAmount * model.CommissionPercent.Value / 100m, 4, MidpointRounding.AwayFromZero);
            }
            commissionCurrency = mainCurrency;
        }
        else if (model.CommissionType == PaymentCommissionType.Fixed)
        {
            commissionCurrency = string.IsNullOrWhiteSpace(model.CommissionCurrency)
                ? mainCurrency
                : SystemCurrency.Normalize(model.CommissionCurrency);
            if (!model.CommissionFixedAmount.HasValue)
            {
                ModelState.AddModelError(nameof(model.CommissionFixedAmount), "مبلغ ثابت کمیسیون را وارد کنید.");
            }
            else if (model.CommissionFixedAmount.Value <= 0m)
            {
                ModelState.AddModelError(nameof(model.CommissionFixedAmount), "مبلغ کمیسیون باید بزرگ‌تر از صفر باشد.");
            }
            else
            {
                commissionAmount = model.CommissionFixedAmount.Value;
            }
        }

        if (!ModelState.IsValid || commissionAmount <= 0m)
        {
            return null;
        }

        decimal fxRateToUsd;
        decimal amountUsd;
        DateTime effectiveDate;
        string fxSource;

        // درصدی، یا هم‌ارز با پرداخت اصلی: همان نرخ پرداخت اصلی استفاده می‌شود.
        if (model.CommissionType == PaymentCommissionType.Percent
            || string.Equals(commissionCurrency, mainCurrency, StringComparison.OrdinalIgnoreCase))
        {
            fxRateToUsd = mainFxRateToUsd;
            effectiveDate = mainFxDate;
            fxSource = mainFxSource;
            amountUsd = decimal.Round(commissionAmount * fxRateToUsd, 4, MidpointRounding.AwayFromZero);
        }
        else
        {
            decimal? appliedRate = SystemCurrency.IsBaseCurrency(commissionCurrency)
                ? 1m
                : (model.CommissionPerUsdRate.HasValue && model.CommissionPerUsdRate.Value > 0m
                    ? 1m / model.CommissionPerUsdRate.Value
                    : (decimal?)null);
            CurrencyConversionResult conv;
            try
            {
                conv = await _currencyConversion.ResolveToBaseAsync(commissionCurrency, mainDate.Date, appliedRate);
            }
            catch (BusinessRuleException ex)
            {
                ModelState.AddModelError(nameof(model.CommissionPerUsdRate), ex.Message);
                return null;
            }
            fxRateToUsd = conv.AppliedRateToBase;
            effectiveDate = conv.EffectiveDate.Date;
            fxSource = conv.SourceDescription;
            commissionCurrency = conv.SourceCurrencyCode;
            amountUsd = conv.ConvertToBase(commissionAmount);
        }

        var description = string.IsNullOrWhiteSpace(model.CommissionDescription)
            ? "کمیسیون سند روزنامچه"
            : model.CommissionDescription.Trim();

        return new CommissionComputation(
            commissionAmount,
            commissionCurrency,
            fxRateToUsd,
            amountUsd,
            effectiveDate,
            fxSource,
            description,
            model.CommissionType!.Value,
            percentForTrace);
    }

    // ExpenseTypeِ کمیسیون را پیدا یا (یک‌بار) می‌سازد. بدون hardcode کردن Id.
    private async Task<ExpenseType> EnsureCommissionExpenseTypeAsync()
    {
        var existing = await _db.ExpenseTypes
            .FirstOrDefaultAsync(e => e.IsActive && (e.Category == "Commission" || e.Code == "COMMISSION"));
        if (existing is not null)
        {
            return existing;
        }

        var created = new ExpenseType
        {
            Code = "COMMISSION",
            Name = "Commission",
            NamePersian = "کمیسیون",
            Category = "Commission",
            IsActive = true
        };
        _db.ExpenseTypes.Add(created);
        await _db.SaveChangesAsync();
        return created;
    }

    // نقد/بانک: مصرف کمیسیون + خروج نقدی کمیسیون. mainPayment.RelatedExpenseTransactionId ست می‌شود.
    // فرض: داخل ترنزکشن باز صدا زده می‌شود.
    private async Task PostCashCommissionAsync(
        PaymentTransaction mainPayment,
        CommissionComputation c,
        int commissionCashAccountId,
        ExpenseType commissionType)
    {
        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = commissionType.Id,
            ContractId = mainPayment.ContractId,
            ShipmentId = mainPayment.ShipmentId,
            ExpenseDate = mainPayment.PaymentDate,
            Amount = c.Amount,
            Currency = c.Currency,
            AppliedFxRateToUsd = c.FxRateToUsd,
            AmountUsd = c.AmountUsd,
            Description = c.Description,
            RelatedPaymentTransactionId = mainPayment.Id
        };
        _db.ExpenseTransactions.Add(expense);
        await _db.SaveChangesAsync();

        var expenseLedger = new LedgerEntry
        {
            EntryDate = mainPayment.PaymentDate,
            Side = LedgerSide.Debit,
            AmountUsd = c.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = c.Amount,
            SourceCurrencyCode = c.Currency,
            AppliedFxRateToUsd = c.FxRateToUsd,
            AppliedFxRateDate = c.EffectiveDate,
            AppliedFxRateSource = c.FxSource,
            Description = $"کمیسیون — {c.Description}",
            SourceType = "Expense",
            SourceId = expense.Id,
            Reference = mainPayment.Reference,
            ContractId = expense.ContractId,
            ShipmentId = expense.ShipmentId
        };
        _db.LedgerEntries.Add(expenseLedger);

        var cashOut = new PaymentTransaction
        {
            PaymentDate = mainPayment.PaymentDate,
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.CommissionPayment,
            CashAccountId = commissionCashAccountId,
            ExpenseTransactionId = expense.Id,
            ContractId = mainPayment.ContractId,
            ShipmentId = mainPayment.ShipmentId,
            Amount = c.Amount,
            Currency = c.Currency,
            AppliedFxRateToUsd = c.FxRateToUsd,
            AmountUsd = c.AmountUsd,
            Reference = mainPayment.Reference,
            Description = $"پرداخت کمیسیون — {c.Description}",
            RelatedExpenseTransactionId = expense.Id
        };
        _db.PaymentTransactions.Add(cashOut);
        await _db.SaveChangesAsync();

        var cashOutLedger = new LedgerEntry
        {
            EntryDate = mainPayment.PaymentDate,
            Side = LedgerSide.Debit,
            AmountUsd = c.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = c.Amount,
            SourceCurrencyCode = c.Currency,
            AppliedFxRateToUsd = c.FxRateToUsd,
            AppliedFxRateDate = c.EffectiveDate,
            AppliedFxRateSource = c.FxSource,
            Description = $"خروج نقدی کمیسیون — {c.Description}",
            SourceType = nameof(PaymentKind.CommissionPayment),
            SourceId = cashOut.Id,
            Reference = mainPayment.Reference,
            ContractId = cashOut.ContractId,
            ShipmentId = cashOut.ShipmentId
        };
        _db.LedgerEntries.Add(cashOutLedger);
        await _db.SaveChangesAsync();

        cashOut.LedgerEntryId = cashOutLedger.Id;
        mainPayment.RelatedExpenseTransactionId = expense.Id;
        await _db.SaveChangesAsync();

        // مرحله ۵ — Dual-write کمیسیون داخل همان Transaction قدیمی: اول مصرف (بدهی کمیسیون
        // را می‌سازد) و بعد خروج نقدی (همان بدهی را تسویه می‌کند). هر کدام Flag جدا دارند،
        // پس اگر فقط یکی روشن باشد، طرف دیگر Skip می‌شود و legacy دست‌نخورده می‌ماند.
        if (_expenseAccounting is not null)
        {
            await _expenseAccounting.TryPostExpenseAsync(expense);
        }

        if (_paymentAccounting is not null)
        {
            await _paymentAccounting.TryPostPaymentAsync(cashOut);
        }

        await _audit.LogAsync(
            nameof(ExpenseTransaction),
            expense.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Type", "Commission"),
                ("Amount", expense.Amount),
                ("Currency", expense.Currency),
                ("AmountUsd", expense.AmountUsd),
                ("RelatedPaymentTransactionId", expense.RelatedPaymentTransactionId)));
        await _audit.LogAsync(
            nameof(PaymentTransaction),
            cashOut.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("PaymentKind", cashOut.PaymentKind),
                ("Amount", cashOut.Amount),
                ("Currency", cashOut.Currency),
                ("AmountUsd", cashOut.AmountUsd),
                ("ExpenseTransactionId", cashOut.ExpenseTransactionId)));
        await _db.SaveChangesAsync();
    }

    // حذف امنِ رکوردهای کمیسیونِ نقد/بانکِ وابسته به یک پرداخت اصلی (برای ویرایش).
    private async Task RemoveCashCommissionAsync(PaymentTransaction mainPayment)
    {
        if (!mainPayment.RelatedExpenseTransactionId.HasValue)
        {
            return;
        }

        var expenseId = mainPayment.RelatedExpenseTransactionId.Value;

        var cashOut = await _db.PaymentTransactions
            .FirstOrDefaultAsync(p => p.ExpenseTransactionId == expenseId
                && p.PaymentKind == PaymentKind.CommissionPayment);
        if (cashOut is not null)
        {
            var cashOutLedgers = await _db.LedgerEntries
                .Where(l => l.SourceType == nameof(PaymentKind.CommissionPayment) && l.SourceId == cashOut.Id)
                .ToListAsync();
            cashOut.LedgerEntryId = null;
            await _db.SaveChangesAsync();
            _db.LedgerEntries.RemoveRange(cashOutLedgers);
            _db.PaymentTransactions.Remove(cashOut);
        }

        var expenseLedgers = await _db.LedgerEntries
            .Where(l => l.SourceType == "Expense" && l.SourceId == expenseId)
            .ToListAsync();
        _db.LedgerEntries.RemoveRange(expenseLedgers);

        var expense = await _db.ExpenseTransactions.FirstOrDefaultAsync(e => e.Id == expenseId);
        if (expense is not null)
        {
            _db.ExpenseTransactions.Remove(expense);
        }

        mainPayment.RelatedExpenseTransactionId = null;
        await _db.SaveChangesAsync();
    }

    // ViaSarraf: مصرف کمیسیون (P&L) + بدهی بستانکار صراف. صندوق دست نمی‌خورد.
    private async Task PostViaSarrafCommissionAsync(
        CommissionComputation c,
        int sarrafId,
        int? supplierId,
        int? contractId,
        DateTime date,
        string? reference,
        ExpenseType commissionType)
    {
        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = commissionType.Id,
            ContractId = contractId,
            ExpenseDate = date.Date,
            Amount = c.Amount,
            Currency = c.Currency,
            AppliedFxRateToUsd = c.FxRateToUsd,
            AmountUsd = c.AmountUsd,
            Description = $"کمیسیون صراف — {c.Description}"
        };
        _db.ExpenseTransactions.Add(expense);
        await _db.SaveChangesAsync();

        var expenseLedger = new LedgerEntry
        {
            EntryDate = date.Date,
            Side = LedgerSide.Debit,
            AmountUsd = c.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = c.Amount,
            SourceCurrencyCode = c.Currency,
            AppliedFxRateToUsd = c.FxRateToUsd,
            AppliedFxRateDate = c.EffectiveDate,
            AppliedFxRateSource = c.FxSource,
            Description = $"کمیسیون صراف — {c.Description}",
            SourceType = "Expense",
            SourceId = expense.Id,
            Reference = reference,
            // عمداً بدون SupplierId و بدون ContractId: مصرفِ کمیسیون بدهیِ تأمین‌کننده نیست و نباید
            // در صورت‌حساب او دیده شود. LedgerEntryOwnership.SupplierOwned سطر را با هر کدام از
            // این دو (SupplierId، یا قراردادِ خریدِ همان تأمین‌کننده) برمی‌داشت، پس هر دو خالی می‌مانند.
            // ردیابیِ قرارداد روی خودِ ExpenseTransaction محفوظ است و گزارش‌های مصرف آن را می‌بینند.
            ContractId = null,
            SupplierId = null
        };
        var sarrafPayableLedger = new LedgerEntry
        {
            EntryDate = date.Date,
            Side = LedgerSide.Credit,
            AmountUsd = c.AmountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = c.Amount,
            SourceCurrencyCode = c.Currency,
            AppliedFxRateToUsd = c.FxRateToUsd,
            AppliedFxRateDate = c.EffectiveDate,
            AppliedFxRateSource = c.FxSource,
            Description = $"بدهی شرکت به صراف بابت کمیسیون — {c.Description}",
            SourceType = ViaSarrafPayableLedgerSourceType,
            SourceId = sarrafId,
            Reference = reference,
            // بدهیِ کمیسیون به صراف است، نه پرداختِ قراردادِ تأمین‌کننده. با داشتنِ قرارداد و
            // نداشتنِ هیچ FK طرف، LedgerEntryOwnership.SupplierOwned آن را «مالِ تأمین‌کننده»
            // می‌شمرد و صورت‌حساب رسمیِ او را به‌اندازهٔ کمیسیون کم می‌کرد. مانده و صورت‌حساب
            // صراف از SourceId (شناسهٔ صراف) خوانده می‌شود و دست‌نخورده می‌ماند.
            ContractId = null
        };
        _db.LedgerEntries.AddRange(expenseLedger, sarrafPayableLedger);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            nameof(ExpenseTransaction),
            expense.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Type", "SarrafCommission"),
                ("Amount", expense.Amount),
                ("Currency", expense.Currency),
                ("AmountUsd", expense.AmountUsd),
                ("SarrafId", sarrafId)));
        await _db.SaveChangesAsync();
    }

    // ===================== تفاوت نرخ ارز حواله صراف (FX Difference) =====================
    // مفهومی جدا از کمیسیون: هیچ پول اضافه‌ای پرداخت نشده؛ فقط شرکت ارز را با نرخی خریده که
    // با نرخ قبولِ طرف مقابل فرق دارد.
    //   CompanyCostUsd      = مبلغ ارز ÷ نرخ خرید شرکت      → حساب صراف (SarrafChargedAmountUsd)
    //   SupplierAcceptedUsd = مبلغ ارز ÷ نرخ قبول تأمین‌کننده → حساب تأمین‌کننده (سطر خود Settlement)
    //   FxDifferenceUsd     = CompanyCostUsd − SupplierAcceptedUsd → مصرف (P&L)
    // موتور محاسبه همان SarrafSettlementService است؛ اینجا فقط سمتِ مصرفِ تفاوت ثبت می‌شود.
    // هیچ PaymentTransaction ساخته نمی‌شود، پس صندوق/بانک دست نمی‌خورد.

    public const string SarrafFxDifferenceExpenseCode = "FX_DIFFERENCE";

    private static string BuildSarrafFxDifferenceMarker(int settlementId)
        => $"[SS-{settlementId}]";

    // ExpenseTypeِ تفاوت نرخ را پیدا یا (یک‌بار) می‌سازد. بدون hardcode کردن Id — همان الگوی کمیسیون.
    private async Task<ExpenseType> EnsureFxDifferenceExpenseTypeAsync()
    {
        var existing = await _db.ExpenseTypes
            .FirstOrDefaultAsync(e => e.IsActive
                && (e.Category == "FxDifference" || e.Code == SarrafFxDifferenceExpenseCode));
        if (existing is not null)
        {
            return existing;
        }

        var created = new ExpenseType
        {
            Code = SarrafFxDifferenceExpenseCode,
            Name = "FX Difference",
            NamePersian = "تفاوت نرخ ارز",
            Category = "FxDifference",
            IsActive = true
        };
        _db.ExpenseTypes.Add(created);
        await _db.SaveChangesAsync();
        return created;
    }

    // سطر درآمدِ «سود تفاوت نرخ ارز». معادلِ درآمدی برای ExpenseTransactionِ سمت ضرر؛
    // چون سیستم موجودیتِ درآمد (IncomeTransaction) ندارد، سود با یک سطر بستانکارِ دفتر ثبت می‌شود.
    public const string SarrafFxGainLedgerSourceType = "SarrafFxGain";

    // نتیجهٔ تفاوت نرخ از دید شرکت. مبنا همان DifferenceAmountUsd سرویس است:
    //   DifferenceAmountUsd = CompanyCostUsd − SupplierAcceptedUsd
    //   مثبت → شرکت بیشتر خرج کرده از آنچه تأمین‌کننده قبول کرده → ضرر
    //   منفی → تأمین‌کننده بیشتر از هزینهٔ شرکت قبول کرده → سود
    private static bool IsSarrafFxLoss(SarrafSettlement settlement)
        => settlement.DifferenceType is SarrafSettlementDifferenceType.SupplierShortfall
            or SarrafSettlementDifferenceType.Loss;

    // تفاوت نرخ یک تسویهٔ صراف را با وضعیت فعلیِ همان تسویه «تطبیق» می‌دهد:
    // اول هر ثبت قبلیِ همین تسویه پاک می‌شود، بعد سمت درست ساخته می‌شود. بنابراین
    // فراخوانی دوباره رکورد تکراری نمی‌سازد و تغییر جهت (ضرر ↔ سود) هم امن است.
    // internal است تا تست بتواند هر دو رفتار را مستقیم بسنجد.
    internal async Task PostSarrafFxDifferenceAsync(SarrafSettlement settlement)
    {
        // idempotency و امنیتِ تغییر جهت: ثبت قبلیِ همین تسویه (هر سمتی که بوده) برداشته می‌شود.
        await RemoveSarrafFxDifferenceAsync(settlement.Id);

        var amountUsd = decimal.Round(Math.Abs(settlement.DifferenceAmountUsd), 4, MidpointRounding.AwayFromZero);
        if (amountUsd <= 0m || settlement.DifferenceType == SarrafSettlementDifferenceType.None)
        {
            // بدون تفاوت: نه مصرف، نه درآمد، نه سطر دفتر.
            return;
        }

        // تفاوتِ شناسایی‌شده به‌عنوان سود/زیان تبادله، سند مخصوص خودش را در SarrafSettlementService
        // می‌گیرد؛ ثبت دوبارهٔ آن اینجا یعنی دوباره‌شماری، پس فقط حالت AcceptedAmountOnly ثبت می‌شود.
        if (settlement.DifferenceTreatment != SarrafSettlementDifferenceTreatment.AcceptedAmountOnly)
        {
            return;
        }

        var marker = BuildSarrafFxDifferenceMarker(settlement.Id);
        var companyRate = settlement.SarrafRate;
        var counterpartyRate = settlement.SupplierRate ?? 0m;
        var isLoss = IsSarrafFxLoss(settlement);
        var headline = isLoss ? "ضرر تفاوت نرخ ارز حواله صراف" : "سود تفاوت نرخ ارز حواله صراف";
        var description = $"{headline} {marker} — نرخ شرکت {companyRate:0.####} در برابر نرخ طرف مقابل {counterpartyRate:0.####}";

        if (isLoss)
        {
            await PostSarrafFxLossAsync(settlement, amountUsd, description);
        }
        else
        {
            await PostSarrafFxGainAsync(settlement, amountUsd, description);
        }

        // برچسب دلیل تفاوت (فقط ثبت/نمایش؛ هیچ منطق مالی به آن وابسته نیست).
        var tracked = await _db.SarrafSettlements.FirstOrDefaultAsync(s => s.Id == settlement.Id);
        if (tracked is not null && tracked.DifferenceReason is null)
        {
            tracked.DifferenceReason = DifferenceReason.FxDifference;
        }
        await _db.SaveChangesAsync();
    }

    // ضرر: مصرف واقعی + سطر بدهکار. هیچ PaymentTransaction نقدی ساخته نمی‌شود.
    private async Task PostSarrafFxLossAsync(SarrafSettlement settlement, decimal amountUsd, string description)
    {
        var fxType = await EnsureFxDifferenceExpenseTypeAsync();

        var expense = new ExpenseTransaction
        {
            ExpenseTypeId = fxType.Id,
            // عمداً بدون قرارداد: گزارش سود و زیان مدیریتی تفاوتِ نرخ را از خودِ
            // SarrafSettlement.DifferenceType می‌خواند (SarrafSupplierShortfallUsd)، پس بستنِ
            // این مصرف به قرارداد باعث می‌شد همان ضرر بار دوم در GeneralExpenseCostUsd شمرده شود.
            // ردیابیِ تسویه در Description و Reference نگه داشته می‌شود.
            ContractId = null,
            ExpenseDate = settlement.SettlementDate,
            Amount = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AmountUsd = amountUsd,
            Description = description
        };
        _db.ExpenseTransactions.Add(expense);
        await _db.SaveChangesAsync();

        _db.LedgerEntries.Add(BuildSarrafFxDifferenceLedger(
            settlement,
            amountUsd,
            description,
            LedgerSide.Debit,
            sourceType: "Expense",
            sourceId: expense.Id));
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            nameof(ExpenseTransaction),
            expense.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Type", "SarrafFxLoss"),
                ("AmountUsd", expense.AmountUsd),
                ("SarrafSettlementId", settlement.Id),
                ("CompanyRate", settlement.SarrafRate),
                ("CounterpartyRate", settlement.SupplierRate)));
    }

    // سود: سطر بستانکارِ درآمد. نه مصرف ساخته می‌شود، نه مبلغ منفی، نه کاهش هزینه.
    private async Task PostSarrafFxGainAsync(SarrafSettlement settlement, decimal amountUsd, string description)
    {
        var gainLedger = BuildSarrafFxDifferenceLedger(
            settlement,
            amountUsd,
            description,
            LedgerSide.Credit,
            sourceType: SarrafFxGainLedgerSourceType,
            sourceId: settlement.Id);
        _db.LedgerEntries.Add(gainLedger);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            nameof(LedgerEntry),
            gainLedger.Id,
            AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("Type", "SarrafFxGain"),
                ("Side", gainLedger.Side),
                ("AmountUsd", gainLedger.AmountUsd),
                ("SarrafSettlementId", settlement.Id),
                ("CompanyRate", settlement.SarrafRate),
                ("CounterpartyRate", settlement.SupplierRate)));
    }

    // سطر دفترِ تفاوت نرخ — هر دو سمت یک شکل ساخته می‌شوند تا فقط Side و SourceType فرق کند.
    // نه SupplierId دارد نه ContractId: تفاوت نرخ بدهیِ تأمین‌کننده نیست و نباید مانده یا
    // صورت‌حساب او و مانده قرارداد را جابه‌جا کند (LedgerEntryOwnership.SupplierOwned هر دو را می‌خواند).
    private static LedgerEntry BuildSarrafFxDifferenceLedger(
        SarrafSettlement settlement,
        decimal amountUsd,
        string description,
        LedgerSide side,
        string sourceType,
        int sourceId)
        => new()
        {
            EntryDate = settlement.SettlementDate,
            Side = side,
            AmountUsd = amountUsd,
            Currency = SystemCurrency.BaseCurrencyCode,
            SourceAmount = amountUsd,
            SourceCurrencyCode = SystemCurrency.BaseCurrencyCode,
            AppliedFxRateToUsd = 1m,
            AppliedFxRateDate = settlement.SettlementDate,
            AppliedFxRateSource = "Sarraf settlement FX difference",
            Description = description,
            SourceType = sourceType,
            SourceId = sourceId,
            Reference = settlement.ReferenceNumber,
            ContractId = null,
            SupplierId = null
        };

    // ثبت‌های تفاوت نرخِ یک تسویه را برمی‌دارد (هر سمتی که باشد). برای ثبت دوباره و تغییر جهت.
    private async Task RemoveSarrafFxDifferenceAsync(int settlementId)
    {
        var marker = BuildSarrafFxDifferenceMarker(settlementId);

        var expenses = await _db.ExpenseTransactions
            .Where(e => e.Description != null && e.Description.Contains(marker))
            .ToListAsync();
        if (expenses.Count > 0)
        {
            var expenseIds = expenses.Select(e => e.Id).ToList();
            var expenseLedgers = await _db.LedgerEntries
                .Where(l => l.SourceType == "Expense" && expenseIds.Contains(l.SourceId))
                .ToListAsync();
            _db.LedgerEntries.RemoveRange(expenseLedgers);
            _db.ExpenseTransactions.RemoveRange(expenses);
        }

        var gainLedgers = await _db.LedgerEntries
            .Where(l => l.SourceType == SarrafFxGainLedgerSourceType && l.Description.Contains(marker))
            .ToListAsync();
        if (gainLedgers.Count > 0)
        {
            _db.LedgerEntries.RemoveRange(gainLedgers);
        }

        if (expenses.Count > 0 || gainLedgers.Count > 0)
        {
            await _db.SaveChangesAsync();
        }
    }

    // آیا کاربر نرخ خرید ارز توسط شرکت را جدا و متفاوت از نرخ قبول طرف مقابل وارد کرده است؟
    // فقط در این حالت مسیر دو‌نرخی (SarrafSettlementService) فعال می‌شود تا رفتار تک‌نرخیِ
    // موجود و داده‌های قبلی دست‌نخورده بمانند.
    private static bool HasDistinctSarrafCompanyRate(PaymentCreateViewModel model)
    {
        var currency = string.IsNullOrWhiteSpace(model.SarrafSupplierCurrency)
            ? "USD"
            : SystemCurrency.Normalize(model.SarrafSupplierCurrency);
        if (SystemCurrency.IsBaseCurrency(currency))
        {
            return false;
        }

        var companyRate = model.SarrafCompanyPerUsdRate ?? 0m;
        var counterpartyRate = model.SarrafSupplierPerUsdRate ?? 0m;
        return companyRate > 0m && counterpartyRate > 0m && companyRate != counterpartyRate;
    }

    // اعتبارسنجی حساب پرداخت کمیسیون (نقد/بانک). Id حساب نهایی را برمی‌گرداند.
    private async Task<int> ValidateCommissionCashAccountAsync(PaymentCreateViewModel model, int fallbackAccountId, string commissionCurrency)
    {
        var accountId = model.CommissionCashAccountId ?? fallbackAccountId;
        var account = await _db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is null || !account.IsActive)
        {
            ModelState.AddModelError(nameof(model.CommissionCashAccountId), "حساب پرداخت کمیسیون معتبر و فعال نیست.");
            return accountId;
        }
        if (account.AccountType != CashAccountType.Mixed
            && !string.Equals(account.Currency, commissionCurrency, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.CommissionCashAccountId), "ارز کمیسیون باید با ارز حساب پرداخت کمیسیون یکسان باشد.");
        }
        return accountId;
    }

    private static string BuildFxSource(string sourceCurrency, PriceLookupResult fxRate)
    {
        if (string.Equals(sourceCurrency, SystemCurrency.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return $"Identity {SystemCurrency.BaseCurrencyCode}/{SystemCurrency.BaseCurrencyCode}";
        }

        return fxRate.FallbackApplied
            ? $"DailyFxRate {sourceCurrency}/{SystemCurrency.BaseCurrencyCode} fallback from {fxRate.EffectiveDate.ToHtmlDateInput()}"
            : $"DailyFxRate {sourceCurrency}/{SystemCurrency.BaseCurrencyCode} on {fxRate.EffectiveDate.ToHtmlDateInput()}";
    }

    private static string GetSideName(LedgerSide side)
        => side == LedgerSide.Debit ? "بدهکار" : "بستانکار";

    private sealed record ResolvedPaymentContext(
        CashAccount CashAccount,
        int? CustomerId,
        int? SupplierId,
        int? ServiceProviderId,
        int? SarrafId,
        int? DriverId,
        int? EmployeeId,
        int? ContractId,
        int? ShipmentId,
        int? SalesTransactionId,
        int? ExpenseTransactionId,
        int? TruckDispatchId);

    private sealed record ViaSarrafSupplierContext(
        int SupplierId,
        int SarrafId,
        int? ContractId,
        string Currency,
        decimal AmountUsd,
        decimal FxRateToUsd,
        string FxRateSource);

    private sealed record PaymentIndexSummary(
        decimal TodayReceiptUsd,
        decimal TodayPaymentUsd,
        decimal CashAccountsBalanceUsd,
        string? LastDocumentReference,
        DateTime? LastDocumentDate,
        IReadOnlyList<CashAccountBalanceSummaryViewModel> CashAccountBalances);

    private sealed class PaymentListProjection
    {
        public int Id { get; init; }
        public DateTime PaymentDate { get; init; }
        public PaymentDirection Direction { get; init; }
        public PaymentKind PaymentKind { get; init; }
        public string CashAccountName { get; init; } = "";
        public string CashAccountCurrency { get; init; } = "USD";
        public int? CustomerId { get; init; }
        public string? CustomerName { get; init; }
        public int? SupplierId { get; init; }
        public string? SupplierName { get; init; }
        public int? ServiceProviderId { get; init; }
        public string? ServiceProviderName { get; init; }
        public int? SarrafId { get; init; }
        public string? SarrafName { get; init; }
        public int? EmployeeId { get; init; }
        public string? EmployeeName { get; init; }
        public int? DriverId { get; init; }
        public string? DriverName { get; init; }
        public int? ContractId { get; init; }
        public string? ContractNumber { get; init; }
        public int? ShipmentId { get; init; }
        public string? ShipmentCode { get; init; }
        public int? SalesTransactionId { get; init; }
        public string? SalesInvoiceNumber { get; init; }
        public int? ExpenseTransactionId { get; init; }
        public string? ExpenseDescription { get; init; }
        public int? TruckDispatchId { get; init; }
        public string? TruckPlateNumber { get; init; }
        public string? Description { get; init; }
        public int? CreatedByUserId { get; init; }
        public decimal Amount { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal AmountUsd { get; init; }
        public string? Reference { get; init; }
        public int? LedgerEntryId { get; init; }
        public decimal? CommissionAmount { get; init; }
        public string? CommissionCurrency { get; init; }
    }

    private static string NormalizeCurrency(string? currency)
        => SystemCurrency.Normalize(currency);
}
