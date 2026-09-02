using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.AccountStatements;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public partial class AccountStatementsController : Controller
{
    private const string BaseCurrency = "USD";
    private const int LookupLimit = 200;

    private readonly ApplicationDbContext _db;

    // PTG-P1-03 — تنها مسیرِ ساختنِ سطر دفتر کل.
    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);
    private readonly ICurrencyConversionService _currencyConversion;
    private readonly IAuditService _audit;
    private readonly ICompanyFlowDirectionResolver _flowResolver;

    [ActivatorUtilitiesConstructor]
    public AccountStatementsController(
        ApplicationDbContext db,
        ICurrencyConversionService currencyConversion,
        IAuditService audit,
        ICompanyFlowDirectionResolver flowResolver)
    {
        _db = db;
        _currencyConversion = currencyConversion;
        _audit = audit;
        _flowResolver = flowResolver;
    }

    public AccountStatementsController(
        ApplicationDbContext db,
        IPricingService pricing,
        IAuditService audit)
        : this(
            db,
            new CurrencyConversionService(pricing),
            audit,
            new CompanyFlowDirectionResolver())
    {
    }

    public async Task<IActionResult> Index([FromQuery] AccountStatementFilterViewModel? filter = null, int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new AccountStatementFilterViewModel();
        NormalizeFilter(filter);
        await PopulateLookupsAsync(filter: filter);

        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var statementRows = await BuildStatementRowsAsync(filter, page, pageSize);
        return View(new AccountStatementIndexViewModel
        {
            Filter = filter,
            OpeningBalanceUsd = statementRows.OpeningBalanceUsd,
            ClosingBalanceUsd = statementRows.ClosingBalanceUsd,
            Items = statementRows.Items,
            CurrentPage = statementRows.CurrentPage,
            PageCount = statementRows.PageCount,
            TotalCount = statementRows.TotalCount
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var entry = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new
            {
                l.Id,
                l.EntryDate,
                l.Side,
                l.SourceAmount,
                l.SourceCurrencyCode,
                l.AppliedFxRateToUsd,
                l.AppliedFxRateDate,
                l.AppliedFxRateSource,
                l.AmountUsd,
                l.Currency,
                l.SourceType,
                l.SourceId,
                l.Reference,
                l.Description,
                l.ContractId,
                l.CustomerId,
                l.SupplierId,
                l.ServiceProviderId,
                l.DriverId,
                l.EmployeeId,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null,
                CustomerName = l.Customer != null ? l.Customer.Name : null,
                SupplierName = l.Supplier != null ? l.Supplier.Name : null
            })
            .FirstOrDefaultAsync(l => l.Id == id);

        if (entry is null)
        {
            return NotFound();
        }

        var runningBalance = await CalculateRunningBalanceAtAsync(
            entry.EntryDate,
            entry.Id,
            entry.ContractId,
            entry.CustomerId,
            entry.SupplierId,
            entry.ServiceProviderId,
            entry.DriverId,
            entry.EmployeeId);

        return View(new AccountStatementDetailsViewModel
        {
            Id = entry.Id,
            EntryDate = entry.EntryDate,
            Side = entry.Side,
            SideName = GetSideName(entry.Side),
            SourceAmount = entry.SourceAmount ?? entry.AmountUsd,
            SourceCurrencyCode = entry.SourceCurrencyCode ?? entry.Currency,
            AppliedFxRateToUsd = entry.AppliedFxRateToUsd
                ?? (string.Equals(entry.SourceCurrencyCode ?? entry.Currency, BaseCurrency, StringComparison.OrdinalIgnoreCase) ? 1m : 0m),
            AppliedFxRateDate = entry.AppliedFxRateDate,
            AppliedFxRateSource = entry.AppliedFxRateSource,
            AmountUsd = entry.AmountUsd,
            RunningBalanceUsd = runningBalance,
            SourceType = entry.SourceType,
            SourceId = entry.SourceId,
            Reference = entry.Reference,
            Description = entry.Description,
            ContractNumber = entry.ContractNumber,
            CustomerName = entry.CustomerName,
            SupplierName = entry.SupplierName
        });
    }

    public async Task<IActionResult> Contract(int contractId)
    {
        var model = await BuildContractAccountStatementAsync(contractId);
        return model is null ? NotFound() : View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        var model = new AccountStatementCreateViewModel
        {
            EntryDate = AfghanistanBusinessClock.SystemToday,
            SourceCurrencyCode = BaseCurrency
        };

        await PopulateLookupsAsync(createModel: model);
        return View(model);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AccountStatementCreateViewModel model)
    {
        NormalizeCreateModel(model);
        await ValidateRelationsAsync(model);

        if (!ModelState.IsValid)
        {
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        CurrencyConversionResult conversion;
        try
        {
            conversion = await _currencyConversion.ResolveToBaseAsync(
                model.SourceCurrencyCode,
                model.EntryDate.Date,
                model.AppliedFxRateToUsd);
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError(nameof(model.AppliedFxRateToUsd), ex.Message);
            await PopulateLookupsAsync(createModel: model);
            return View(model);
        }

        var sourceType = model.EntryKind == AccountStatementEntryKind.OpeningBalance
            ? "OpeningBalance"
            : "ManualAdjustment";
        var amountUsd = conversion.ConvertToBase(model.SourceAmount);

        // PTG-P1-03 — این سطر مبدأی بیرون از خودش ندارد: SourceId پس از ذخیره با Id خودش پر می‌شود.
        var ledgerRequest = new LedgerPostingRequest
        {
            EntryDate = model.EntryDate.Date,
            Side = model.Side,
            AmountUsd = amountUsd,
            Currency = BaseCurrency,
            SourceAmount = model.SourceAmount,
            SourceCurrencyCode = conversion.SourceCurrencyCode,
            AppliedFxRateToUsd = conversion.AppliedRateToBase,
            AppliedFxRateDate = conversion.EffectiveDate.Date,
            AppliedFxRateSource = conversion.SourceDescription,
            Description = model.Description,
            SourceType = sourceType,
            SourceId = 0,
            AllowDeferredSourceId = true,
            Reference = model.Reference,
            ContractId = model.ContractId,
            CustomerId = model.CustomerId,
            SupplierId = model.SupplierId
        };

        LedgerEntry ledgerEntry;

        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync();
        }

        try
        {
            ledgerEntry = Ledger.Post(ledgerRequest);
            await _db.SaveChangesAsync();

            ledgerEntry.SourceId = ledgerEntry.Id;
            await _db.SaveChangesAsync();

            await _audit.LogAndSaveAsync(
                nameof(LedgerEntry),
                ledgerEntry.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("SourceType", ledgerEntry.SourceType),
                    ("EntryDate", ledgerEntry.EntryDate),
                    ("Side", ledgerEntry.Side),
                    ("SourceAmount", ledgerEntry.SourceAmount),
                    ("SourceCurrencyCode", ledgerEntry.SourceCurrencyCode),
                    ("AppliedFxRateToUsd", ledgerEntry.AppliedFxRateToUsd),
                    ("AppliedFxRateDate", ledgerEntry.AppliedFxRateDate),
                    ("AmountUsd", ledgerEntry.AmountUsd),
                    ("Reference", ledgerEntry.Reference)));

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            throw;
        }

        TempData["ok"] = "ثبت statement با موفقیت انجام شد.";
        return RedirectToAction(nameof(Details), new { id = ledgerEntry.Id });
    }

    private sealed record AccountStatementRowsResult(
        decimal OpeningBalanceUsd,
        decimal ClosingBalanceUsd,
        IReadOnlyList<AccountStatementListItemViewModel> Items,
        int CurrentPage,
        int PageCount,
        int TotalCount);

    private async Task<AccountStatementRowsResult> BuildStatementRowsAsync(
        AccountStatementFilterViewModel filter,
        int page = 1,
        int pageSize = 20)
    {

        var query = BuildFilteredLedgerQuery(filter, applyDates: false);
        var openingBalance = 0m;

        if (filter.ToDate.HasValue)
        {
            query = query.Where(l => l.EntryDate <= filter.ToDate.Value.Date);
        }

        if (filter.FromDate.HasValue)
        {
            var fromDate = filter.FromDate.Value.Date;
            var openingQuery = query.Where(l => l.EntryDate < fromDate);
            openingBalance = await SumSignedAmountAsync(openingQuery);
            query = query.Where(l => l.EntryDate >= fromDate);
        }

        var orderedQuery = query
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.Id);

        var totalCount = await orderedQuery.CountAsync();
        var pageCount = page <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = page <= 0 ? 1 : Math.Clamp(page, 1, pageCount);
        var skip = page <= 0 ? 0 : (currentPage - 1) * pageSize;

        var closingBalance = openingBalance + await SumSignedAmountAsync(orderedQuery);
        var balanceBeforePage = openingBalance;
        if (skip > 0)
        {
            balanceBeforePage += await SumSignedAmountAsync(orderedQuery.Take(skip));
        }

        var entries = await (page <= 0
                ? orderedQuery
                : orderedQuery.Skip(skip).Take(pageSize))
            .ToListAsync();

        var rows = new List<AccountStatementListItemViewModel>();
        var balance = balanceBeforePage;

        foreach (var entry in entries)
        {
            balance += SignedAmount(entry);

            rows.Add(new AccountStatementListItemViewModel
            {
                Id = entry.Id,
                EntryDate = entry.EntryDate,
                Side = entry.Side,
                SideName = GetSideName(entry.Side),
                SourceAmount = GetSourceAmount(entry),
                SourceCurrencyCode = GetSourceCurrency(entry),
                AppliedFxRateToUsd = GetAppliedRate(entry),
                AppliedFxRateDate = entry.AppliedFxRateDate,
                AmountUsd = entry.AmountUsd,
                RunningBalanceUsd = balance,
                SourceType = entry.SourceType,
                SourceId = entry.SourceId,
                Reference = entry.Reference,
                Description = entry.Description,
                ContractNumber = entry.Contract?.ContractNumber,
                CustomerName = entry.Customer?.Name,
                SupplierName = entry.Supplier?.Name
            });
        }

        return new AccountStatementRowsResult(openingBalance, closingBalance, rows, currentPage, pageCount, totalCount);
    }

    private async Task<ContractAccountStatementViewModel?> BuildContractAccountStatementAsync(int contractId)
    {
        var contract = await _db.Contracts
            .AsNoTracking()
            .Where(c => c.Id == contractId)
            .Select(c => new
            {
                c.Id,
                c.ContractName,
                c.ContractNumber,
                c.ContractType,
                ProductName = c.Product != null ? c.Product.Name : null,
                SupplierName = c.Supplier != null ? c.Supplier.Name : null,
                CustomerName = c.Customer != null ? c.Customer.Name : null,
                c.Currency,
                c.QuantityMt
            })
            .FirstOrDefaultAsync(c => c.Id == contractId);

        if (contract is null)
        {
            return null;
        }

        var drafts = new List<ContractAccountStatementDraftRow>();

        // نقش طرف‌حسابِ قرارداد — مبنای تعیین جهت برای سطرهایی که SourceType قطعی ندارند.
        var flowRole = contract.ContractType == ContractType.Purchase
            ? CompanyFlowPartyRole.Supplier
            : CompanyFlowPartyRole.Customer;

        var ledgerEntries = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            // «بدهی شرکت به صراف» پایِ دومِ یک پرداختِ از طریق صراف است و طرفش صراف است، نه
            // این قرارداد. تا وقتی هر دو پا اینجا نشان داده می‌شدند، پرداختِ تأمین‌کننده با
            // بدهیِ صراف خنثی می‌شد و بیلانس قرارداد صفر می‌ماند. حساب صراف آن را می‌بیند.
            .Where(l => l.SourceType != LedgerEntryOwnership.ViaSarrafPayableSourceType)
            .OrderBy(l => l.EntryDate)
            .ThenBy(l => l.Id)
            .ToListAsync();

        var transferIds = ledgerEntries
            .Where(l => l.SourceType == ContractBalanceTransferService.LedgerSourceType && l.SourceId > 0)
            .Select(l => l.SourceId)
            .Distinct()
            .ToArray();
        var transferLookup = transferIds.Length == 0
            ? new Dictionary<int, ContractBalanceTransferLookup>()
            : await _db.ContractBalanceTransfers
                .AsNoTracking()
                .Where(t => transferIds.Contains(t.Id))
                .Select(t => new ContractBalanceTransferLookup(
                    t.Id,
                    t.FromContractId,
                    t.ToContractId,
                    t.Notes,
                    t.FromContract != null ? t.FromContract.ContractNumber : null,
                    t.ToContract != null ? t.ToContract.ContractNumber : null))
                .ToDictionaryAsync(t => t.Id);

        foreach (var entry in ledgerEntries)
        {
            var sourceCurrency = GetSourceCurrency(entry);
            var sourceAmount = GetSourceAmount(entry);
            var fxRate = GetNullableAppliedRate(entry, sourceCurrency);
            var hasMissingFx = !string.Equals(sourceCurrency, BaseCurrency, StringComparison.OrdinalIgnoreCase)
                && (!fxRate.HasValue || fxRate <= 0m);
            var description = entry.Description;
            string? relatedContractNumber = null;
            var notes = "Posted in Ledger";

            if (entry.SourceType == ContractBalanceTransferService.LedgerSourceType
                && transferLookup.TryGetValue(entry.SourceId, out var transfer))
            {
                if (entry.ContractId == transfer.FromContractId)
                {
                    relatedContractNumber = transfer.ToContractNumber;
                    description = $"Transfer to contract {relatedContractNumber ?? transfer.ToContractId.ToString()}";
                }
                else if (entry.ContractId == transfer.ToContractId)
                {
                    relatedContractNumber = transfer.FromContractNumber;
                    description = $"Transfer from contract {relatedContractNumber ?? transfer.FromContractId.ToString()}";
                }

                if (!string.IsNullOrWhiteSpace(transfer.Notes))
                {
                    notes = $"Posted in Ledger. {transfer.Notes}";
                }
            }

            // جهت تجاری از لایهٔ مرکزی می‌آید تا همین رویداد در صورت‌حساب طرف‌حساب و اینجا
            // دقیقاً یکسان دیده شود. پیش‌تر اینجا خامِ Debit/Credit نمایش داده می‌شد و مثلاً
            // «بارگیری» در دفتر قرارداد بستانکار و در صورت‌حساب تأمین‌کننده بدهکار بود.
            // مرجع هم خوانده می‌شود: سطرِ برگشتِ بارگیری/فروش/مصرف SourceType سند اصلی را نگه
            // می‌دارد و تنها با پسوندِ مرجع علامت می‌خورد.
            var lifecycle = CompanyFlowSourceTypes.IsReversal(entry.SourceType, entry.Reference)
                ? CompanyFlowLifecycle.Reversal
                : CompanyFlowLifecycle.Original;
            var direction = _flowResolver.Resolve(
                new CompanyFlowEvent(entry.SourceType, entry.Side, flowRole, lifecycle));
            var isReceipt = direction == CompanyFlowDirection.Receipt;

            drafts.Add(new ContractAccountStatementDraftRow
            {
                Date = entry.EntryDate.Date,
                SortGroup = 10,
                SourceType = entry.SourceType,
                SourceId = entry.SourceId,
                Reference = entry.Reference,
                Description = description,
                SourceCurrency = sourceCurrency,
                ReceiptOriginal = isReceipt ? sourceAmount : null,
                OutflowOriginal = isReceipt ? null : sourceAmount,
                FxRateToUsd = fxRate,
                ReceiptUsd = isReceipt ? entry.AmountUsd : null,
                OutflowUsd = isReceipt ? null : entry.AmountUsd,
                RelatedContractNumber = relatedContractNumber,
                Notes = notes,
                WarningBadge = hasMissingFx ? "Missing FX" : null,
                IsFinancial = true,
                IsReversalRow = lifecycle == CompanyFlowLifecycle.Reversal,
                SortId = entry.Id
            });
        }

        // بارگیری‌هایی که سند مالی دارند نباید دوباره به‌عنوان ردیف عملیاتی تکرار شوند.
        var postedLoadingIds = ledgerEntries
            .Where(l => l.SourceType == SupplierLoadingLedger.SourceType)
            .Select(l => l.SourceId)
            .ToHashSet();

        await AddPaymentWarningRowsAsync(contractId, drafts);
        await AddExpenseWarningRowsAsync(contractId, drafts);
        await AddOperationalLoadingRowsAsync(contractId, drafts, postedLoadingIds);
        await AddAllocatedSaleRowsAsync(contractId, flowRole, drafts, ledgerEntries);

        var rows = BuildContractAccountRows(drafts);
        var totals = new ContractAccountStatementTotalsViewModel
        {
            TotalReceiptUsd = rows.Sum(r => r.ReceiptUsd ?? 0m),
            TotalOutflowUsd = rows.Sum(r => r.OutflowUsd ?? 0m),
            BalanceUsd = rows.LastOrDefault()?.BalanceUsd ?? 0m,
            BalancesByCurrency = BuildCurrencyBalances(rows)
        };

        return new ContractAccountStatementViewModel
        {
            ContractId = contract.Id,
            ContractName = contract.ContractName,
            ContractNumber = contract.ContractNumber,
            ProductName = contract.ProductName ?? "-",
            ContractType = contract.ContractType.ToString(),
            CounterpartyName = contract.ContractType == ContractType.Purchase
                ? contract.SupplierName ?? "-"
                : contract.CustomerName ?? "-",
            ContractCurrency = contract.Currency,
            QuantityMt = contract.QuantityMt,
            Rows = rows,
            Totals = totals
        };
    }

    private async Task AddPaymentWarningRowsAsync(int contractId, List<ContractAccountStatementDraftRow> drafts)
    {
        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.ContractId == contractId)
            .OrderBy(p => p.PaymentDate)
            .ThenBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.PaymentDate,
                p.PaymentKind,
                p.Currency,
                p.AppliedFxRateToUsd,
                p.Reference,
                p.Description,
                p.LedgerEntryId,
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : null
            })
            .ToListAsync();

        if (payments.Count == 0)
        {
            return;
        }

        var paymentIds = payments.Select(p => p.Id).ToArray();
        var linkedLedgerIds = payments
            .Where(p => p.LedgerEntryId.HasValue)
            .Select(p => p.LedgerEntryId!.Value)
            .ToArray();
        var paymentSourceTypes = Enum.GetNames<PaymentKind>();

        var postedPaymentSourceIds = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => paymentIds.Contains(l.SourceId) && paymentSourceTypes.Contains(l.SourceType))
            .Select(l => l.SourceId)
            .ToListAsync();
        var postedLedgerIds = linkedLedgerIds.Length == 0
            ? new List<int>()
            : await _db.LedgerEntries
                .AsNoTracking()
                .Where(l => linkedLedgerIds.Contains(l.Id))
                .Select(l => l.Id)
                .ToListAsync();

        var postedPaymentSourceIdSet = postedPaymentSourceIds.ToHashSet();
        var postedLedgerIdSet = postedLedgerIds.ToHashSet();

        foreach (var payment in payments)
        {
            if (postedPaymentSourceIdSet.Contains(payment.Id)
                || (payment.LedgerEntryId.HasValue && postedLedgerIdSet.Contains(payment.LedgerEntryId.Value)))
            {
                continue;
            }

            drafts.Add(new ContractAccountStatementDraftRow
            {
                Date = payment.PaymentDate.Date,
                SortGroup = 20,
                SourceType = payment.PaymentKind.ToString(),
                SourceId = payment.Id,
                Reference = payment.Reference,
                Description = payment.Description ?? payment.PaymentKind.ToString(),
                SourceCurrency = payment.Currency,
                FxRateToUsd = payment.AppliedFxRateToUsd,
                Notes = $"Payment exists in {payment.CashAccountName ?? "cash account"} but has no LedgerEntry; it is not included in balance.",
                WarningBadge = "Payment without Ledger",
                SortId = payment.Id
            });
        }
    }

    private async Task AddExpenseWarningRowsAsync(int contractId, List<ContractAccountStatementDraftRow> drafts)
    {
        var expenses = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => e.ContractId == contractId)
            .OrderBy(e => e.ExpenseDate)
            .ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.ExpenseDate,
                e.Currency,
                e.AppliedFxRateToUsd,
                e.Description,
                ExpenseTypeCode = e.ExpenseType != null ? e.ExpenseType.Code : null,
                ExpenseTypeName = e.ExpenseType != null ? e.ExpenseType.Name : null
            })
            .ToListAsync();

        if (expenses.Count == 0)
        {
            return;
        }

        var expenseIds = expenses.Select(e => e.Id).ToArray();
        var postedExpenseIds = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Expense" && expenseIds.Contains(l.SourceId))
            .Select(l => l.SourceId)
            .ToListAsync();
        var postedExpenseIdSet = postedExpenseIds.ToHashSet();

        foreach (var expense in expenses)
        {
            if (postedExpenseIdSet.Contains(expense.Id))
            {
                continue;
            }

            drafts.Add(new ContractAccountStatementDraftRow
            {
                Date = expense.ExpenseDate.Date,
                SortGroup = 30,
                SourceType = "Expense",
                SourceId = expense.Id,
                Reference = expense.ExpenseTypeCode,
                Description = expense.Description ?? expense.ExpenseTypeName ?? "Expense",
                SourceCurrency = expense.Currency,
                FxRateToUsd = expense.AppliedFxRateToUsd,
                Notes = "Expense exists but has no LedgerEntry; it is not included in balance.",
                WarningBadge = "Expense without Ledger",
                SortId = expense.Id
            });
        }
    }

    /// <summary>
    /// ردیف‌های عملیاتی (بدون سند مالی). بارگیری‌هایی که سند مالی دارند از این فهرست کنار
    /// می‌روند تا یک بارگیری هم‌زمان به‌صورت مالی و عملیاتی تکرار نشود.
    /// </summary>
    private async Task AddOperationalLoadingRowsAsync(
        int contractId,
        List<ContractAccountStatementDraftRow> drafts,
        IReadOnlySet<int> postedLoadingIds)
    {
        var loadings = await _db.LoadingRegisters
            .AsNoTracking()
            .Where(l => l.ContractId == contractId)
            .OrderBy(l => l.LoadingDate)
            .ThenBy(l => l.Id)
            .ToListAsync();

        var loadingIds = loadings.Select(l => l.Id).ToArray();
        var loadingPriceById = loadings.ToDictionary(l => l.Id, l => l.LoadingPriceUsd);

        foreach (var loading in loadings.Where(l => !postedLoadingIds.Contains(l.Id)))
        {
            drafts.Add(new ContractAccountStatementDraftRow
            {
                Date = loading.LoadingDate.Date,
                SortGroup = 40,
                SourceType = nameof(LoadingRegister),
                SourceId = loading.Id,
                Reference = FirstNonEmpty(loading.BillOfLadingNumber, loading.RwbNo, loading.WagonNumber),
                Description = "Loading / cargo registered",
                QuantityMt = loading.LoadedQuantityMt,
                UnitPrice = loading.LoadingPriceUsd,
                SourceCurrency = loading.LoadingPriceUsd.HasValue ? BaseCurrency : null,
                Notes = BuildOperationalLoadingNote(loading.LoadedQuantityMt, loading.LoadingPriceUsd),
                WarningBadge = "Operational only",
                IsOperationalOnly = true,
                SortId = loading.Id
            });
        }

        if (loadingIds.Length == 0)
        {
            return;
        }

        var receipts = await _db.LoadingReceipts
            .AsNoTracking()
            .Where(r => loadingIds.Contains(r.LoadingRegisterId) && !r.IsCancelled)
            .OrderBy(r => r.ReceiptDate)
            .ThenBy(r => r.Id)
            .ToListAsync();

        foreach (var receipt in receipts)
        {
            loadingPriceById.TryGetValue(receipt.LoadingRegisterId, out var unitPrice);
            drafts.Add(new ContractAccountStatementDraftRow
            {
                Date = receipt.ReceiptDate.Date,
                SortGroup = 50,
                SourceType = nameof(LoadingReceipt),
                SourceId = receipt.Id,
                Reference = receipt.ReferenceDocument,
                Description = "Loading receipt / received cargo",
                QuantityMt = receipt.ReceivedQuantityMt,
                UnitPrice = unitPrice,
                SourceCurrency = unitPrice.HasValue ? BaseCurrency : null,
                Notes = BuildOperationalLoadingNote(receipt.ReceivedQuantityMt, unitPrice),
                WarningBadge = "Operational only",
                IsOperationalOnly = true,
                SortId = receipt.Id
            });
        }

        var allocations = await _db.LoadingReceiptAllocations
            .AsNoTracking()
            .Where(a => a.SourcePurchaseContractId == contractId
                || (a.LoadingReceipt != null && loadingIds.Contains(a.LoadingReceipt.LoadingRegisterId)))
            .OrderBy(a => a.LoadingReceipt != null ? a.LoadingReceipt.ReceiptDate : DateTime.MinValue)
            .ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Id,
                a.ReferenceDocument,
                a.DestinationReference,
                a.Destination,
                a.QuantityMt,
                a.Status,
                LoadingReceiptDate = a.LoadingReceipt != null ? a.LoadingReceipt.ReceiptDate : (DateTime?)null,
                LoadingRegisterId = a.LoadingReceipt != null ? (int?)a.LoadingReceipt.LoadingRegisterId : null,
                LoadingPriceUsd = a.LoadingReceipt != null && a.LoadingReceipt.LoadingRegister != null
                    ? a.LoadingReceipt.LoadingRegister.LoadingPriceUsd
                    : null,
                SourcePurchaseContractNumber = a.SourcePurchaseContract != null ? a.SourcePurchaseContract.ContractNumber : null
            })
            .ToListAsync();

        foreach (var allocation in allocations)
        {
            var unitPrice = allocation.LoadingRegisterId.HasValue && loadingPriceById.TryGetValue(allocation.LoadingRegisterId.Value, out var price)
                ? price
                : allocation.LoadingPriceUsd;

            drafts.Add(new ContractAccountStatementDraftRow
            {
                Date = allocation.LoadingReceiptDate?.Date ?? DateTime.MinValue,
                SortGroup = 60,
                SourceType = nameof(LoadingReceiptAllocation),
                SourceId = allocation.Id,
                Reference = FirstNonEmpty(allocation.ReferenceDocument, allocation.DestinationReference),
                Description = $"Receipt allocation: {allocation.Destination}",
                QuantityMt = allocation.QuantityMt,
                UnitPrice = unitPrice,
                SourceCurrency = unitPrice.HasValue ? BaseCurrency : null,
                RelatedContractNumber = allocation.SourcePurchaseContractNumber,
                Notes = $"Status: {allocation.Status}. {BuildOperationalLoadingNote(allocation.QuantityMt, unitPrice)}",
                WarningBadge = "Operational only",
                IsOperationalOnly = true,
                SortId = allocation.Id
            });
        }
    }

    /// <summary>
    /// سهم اثبات‌شدهٔ این قرارداد خرید از فروش‌های چند-قراردادی.
    /// <para>
    /// فروشِ چند-قراردادی عمداً <c>LedgerEntry.ContractId</c> ندارد (AUD-06: هیچ قراردادی
    /// حدس زده نمی‌شود)، پس در حلقهٔ دفترکل بالا اصلاً دیده نمی‌شود و عایدش از صورت‌حسابِ
    /// هر دو قرارداد بیرون می‌ماند. اینجا فقط سهم واقعی همان قرارداد از
    /// <c>SalesTransactionSourceAllocations</c> — همان منبعی که ContractPnl می‌خواند —
    /// اضافه می‌شود. فروشی که سطر دفترکلش پیش‌تر روی همین قرارداد نشسته کنار گذاشته
    /// می‌شود تا یک فروش دو بار شمرده نشود.
    /// </para>
    /// </summary>
    private async Task AddAllocatedSaleRowsAsync(
        int contractId,
        CompanyFlowPartyRole flowRole,
        List<ContractAccountStatementDraftRow> drafts,
        IReadOnlyCollection<LedgerEntry> ledgerEntries)
    {
        var postedSaleIds = ledgerEntries
            .Where(l => l.SourceType == CompanyFlowSourceTypes.Sale)
            .Select(l => l.SourceId)
            .ToHashSet();

        var shares = await _db.SalesTransactionSourceAllocations
            .AsNoTracking()
            .Where(a => a.SourcePurchaseContractId == contractId
                && a.AmountUsd != 0m
                && a.SalesTransaction != null
                && !a.SalesTransaction.IsCancelled)
            .Select(a => new
            {
                a.SalesTransactionId,
                a.QuantityMt,
                a.AmountUsd,
                SaleDate = a.SalesTransaction!.SaleDate,
                a.SalesTransaction.InvoiceNumber
            })
            .ToListAsync();

        var pending = shares
            .Where(s => !postedSaleIds.Contains(s.SalesTransactionId))
            .GroupBy(s => s.SalesTransactionId)
            .Select(g => new
            {
                SaleId = g.Key,
                QuantityMt = g.Sum(s => s.QuantityMt),
                AmountUsd = g.Sum(s => s.AmountUsd),
                g.First().SaleDate,
                g.First().InvoiceNumber
            })
            .OrderBy(s => s.SaleDate)
            .ThenBy(s => s.SaleId)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var direction = _flowResolver.Resolve(
            new CompanyFlowEvent(
                CompanyFlowSourceTypes.Sale,
                LedgerSide.Credit,
                flowRole,
                CompanyFlowLifecycle.Original));
        var isReceipt = direction == CompanyFlowDirection.Receipt;

        foreach (var share in pending)
        {
            drafts.Add(new ContractAccountStatementDraftRow
            {
                Date = share.SaleDate.Date,
                SortGroup = 10,
                SourceType = CompanyFlowSourceTypes.Sale,
                SourceId = share.SaleId,
                Reference = share.InvoiceNumber,
                Description = $"سهم این قرارداد از فروش فاکتور {share.InvoiceNumber}",
                QuantityMt = share.QuantityMt,
                SourceCurrency = BaseCurrency,
                ReceiptOriginal = isReceipt ? share.AmountUsd : null,
                OutflowOriginal = isReceipt ? null : share.AmountUsd,
                ReceiptUsd = isReceipt ? share.AmountUsd : null,
                OutflowUsd = isReceipt ? null : share.AmountUsd,
                Notes = "Multi-contract sale; only this contract's allocated share is shown.",
                WarningBadge = "Allocated share",
                IsFinancial = true,
                SortId = share.SaleId
            });
        }
    }

    private static IReadOnlyList<ContractAccountStatementRowViewModel> BuildContractAccountRows(
        IEnumerable<ContractAccountStatementDraftRow> drafts)
    {
        var rows = new List<ContractAccountStatementRowViewModel>();
        var balanceUsd = 0m;
        var balancesByCurrency = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var draft in drafts
            .OrderBy(r => r.Date)
            .ThenBy(r => r.SortGroup)
            .ThenBy(r => r.SortId))
        {
            string? originalBalanceDisplay = null;

            if (draft.IsFinancial)
            {
                // بیلانس قرارداد = Σبرد − Σرسید، دقیقاً مثل صورت‌حساب طرف‌حساب.
                balanceUsd += (draft.OutflowUsd ?? 0m) - (draft.ReceiptUsd ?? 0m);

                if (!string.IsNullOrWhiteSpace(draft.SourceCurrency)
                    && (draft.OutflowOriginal.HasValue || draft.ReceiptOriginal.HasValue))
                {
                    var currency = draft.SourceCurrency.Trim().ToUpperInvariant();
                    balancesByCurrency.TryGetValue(currency, out var currentBalance);
                    currentBalance += (draft.OutflowOriginal ?? 0m) - (draft.ReceiptOriginal ?? 0m);
                    balancesByCurrency[currency] = currentBalance;
                    originalBalanceDisplay = $"{currentBalance:N2} {currency}";
                }
            }

            rows.Add(new ContractAccountStatementRowViewModel
            {
                Date = draft.Date,
                SourceType = draft.SourceType,
                SourceId = draft.SourceId,
                Reference = draft.Reference,
                Description = draft.Description,
                QuantityMt = draft.QuantityMt,
                UnitPrice = draft.UnitPrice,
                SourceCurrency = draft.SourceCurrency,
                ReceiptOriginal = draft.ReceiptOriginal,
                OutflowOriginal = draft.OutflowOriginal,
                BalanceOriginalByCurrency = originalBalanceDisplay,
                FxRateToUsd = draft.FxRateToUsd,
                ReceiptUsd = draft.ReceiptUsd,
                OutflowUsd = draft.OutflowUsd,
                BalanceUsd = balanceUsd,
                RelatedContractNumber = draft.RelatedContractNumber,
                Notes = draft.Notes,
                WarningBadge = draft.WarningBadge,
                IsFinancial = draft.IsFinancial,
                IsOperationalOnly = draft.IsOperationalOnly,
                IsReversalRow = draft.IsReversalRow
            });
        }

        return rows;
    }

    private static IReadOnlyList<ContractAccountCurrencyBalanceViewModel> BuildCurrencyBalances(
        IReadOnlyList<ContractAccountStatementRowViewModel> rows)
    {
        return rows
            .Where(r => r.IsFinancial && !string.IsNullOrWhiteSpace(r.SourceCurrency))
            .GroupBy(r => r.SourceCurrency!.Trim().ToUpperInvariant())
            .Select(g => new ContractAccountCurrencyBalanceViewModel
            {
                Currency = g.Key,
                BalanceOriginal = g.Sum(r => (r.OutflowOriginal ?? 0m) - (r.ReceiptOriginal ?? 0m))
            })
            .OrderBy(r => r.Currency)
            .ToList();
    }

    private IQueryable<LedgerEntry> BuildFilteredLedgerQuery(AccountStatementFilterViewModel filter, bool applyDates)
    {
        var query = _db.LedgerEntries
            .Include(l => l.Contract)
            .Include(l => l.Customer)
            .Include(l => l.Supplier)
            .AsNoTracking()
            .AsQueryable();

        if (applyDates && filter.FromDate.HasValue)
        {
            query = query.Where(l => l.EntryDate >= filter.FromDate.Value.Date);
        }

        if (applyDates && filter.ToDate.HasValue)
        {
            query = query.Where(l => l.EntryDate <= filter.ToDate.Value.Date);
        }

        if (filter.ContractId.HasValue)
        {
            query = query.Where(l => l.ContractId == filter.ContractId.Value);
        }

        if (filter.CustomerId.HasValue)
        {
            query = query.Where(l => l.CustomerId == filter.CustomerId.Value);
        }

        if (filter.SupplierId.HasValue)
        {
            query = query.Where(l => l.SupplierId == filter.SupplierId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceCurrencyCode))
        {
            query = query.Where(l =>
                (l.SourceCurrencyCode != null && l.SourceCurrencyCode == filter.SourceCurrencyCode)
                || (l.SourceCurrencyCode == null && l.Currency == filter.SourceCurrencyCode));
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            query = query.Where(l => l.Reference != null && l.Reference.Contains(filter.Reference));
        }

        return query;
    }

    private async Task<decimal> CalculateRunningBalanceAtAsync(
        DateTime entryDate,
        int entryId,
        int? contractId,
        int? customerId,
        int? supplierId,
        int? serviceProviderId,
        int? driverId,
        int? employeeId)
    {
        var query = _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.EntryDate < entryDate || (l.EntryDate == entryDate && l.Id <= entryId));

        query = customerId.HasValue ? query.Where(l => l.CustomerId == customerId.Value)
            : supplierId.HasValue ? query.Where(l => l.SupplierId == supplierId.Value)
            : serviceProviderId.HasValue ? query.Where(l => l.ServiceProviderId == serviceProviderId.Value)
            : driverId.HasValue ? query.Where(l => l.DriverId == driverId.Value)
            : employeeId.HasValue ? query.Where(l => l.EmployeeId == employeeId.Value)
            : contractId.HasValue ? query.Where(l => l.ContractId == contractId.Value)
            : query.Where(l => l.Id == entryId);

        return await SumSignedAmountAsync(query);
    }

    private static async Task<decimal> SumSignedAmountAsync(IQueryable<LedgerEntry> query)
        => await query.SumAsync(l => (decimal?)(l.Side == LedgerSide.Credit ? l.AmountUsd : -l.AmountUsd)) ?? 0m;

    private async Task PopulateLookupsAsync(
        AccountStatementCreateViewModel? createModel = null,
        AccountStatementFilterViewModel? filter = null)
    {
        var selectedContractId = createModel?.ContractId ?? filter?.ContractId;
        var contracts = await _db.Contracts
            .AsNoTracking()
            .OrderBy(c => selectedContractId.HasValue && c.Id == selectedContractId.Value ? 0 : 1)
            .ThenByDescending(c => c.ContractDate)
            .ThenBy(c => c.ContractNumber)
            .Take(LookupLimit)
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
            contracts
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
            selectedContractId);

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

        ViewBag.Suppliers = new SelectList(
            await _db.Suppliers
                .AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(),
            "Id",
            "Name",
            createModel?.SupplierId ?? filter?.SupplierId);

        ViewBag.Currencies = new SelectList(
            await _db.Currencies
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code })
                .ToListAsync(),
            "Code",
            "Code",
            createModel?.SourceCurrencyCode ?? filter?.SourceCurrencyCode);
    }

    private async Task ValidateRelationsAsync(AccountStatementCreateViewModel model)
    {
        if (model.ContractId.HasValue && !await _db.Contracts.AsNoTracking().AnyAsync(c => c.Id == model.ContractId.Value))
        {
            ModelState.AddModelError(nameof(model.ContractId), "قرارداد انتخاب‌شده معتبر نیست.");
        }

        if (model.CustomerId.HasValue && !await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == model.CustomerId.Value && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.CustomerId), "مشتری انتخاب‌شده معتبر نیست.");
        }

        if (model.SupplierId.HasValue && !await _db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == model.SupplierId.Value && s.IsActive))
        {
            ModelState.AddModelError(nameof(model.SupplierId), "تأمین‌کننده انتخاب‌شده معتبر نیست.");
        }

        var hasActiveCurrencies = await _db.Currencies.AsNoTracking().AnyAsync(c => c.IsActive);
        if (hasActiveCurrencies
            && !await _db.Currencies.AsNoTracking().AnyAsync(c => c.Code == model.SourceCurrencyCode && c.IsActive))
        {
            ModelState.AddModelError(nameof(model.SourceCurrencyCode), "ارز انتخاب‌شده معتبر نیست.");
        }
    }

    private static void NormalizeFilter(AccountStatementFilterViewModel filter)
    {
        filter.SourceCurrencyCode = NormalizeCurrency(filter.SourceCurrencyCode);
        filter.Reference = string.IsNullOrWhiteSpace(filter.Reference) ? null : filter.Reference.Trim();
    }

    private static void NormalizeCreateModel(AccountStatementCreateViewModel model)
    {
        model.EntryDate = model.EntryDate.Date;
        model.SourceCurrencyCode = NormalizeCurrency(model.SourceCurrencyCode) ?? BaseCurrency;
        model.Reference = model.Reference?.Trim() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;
    }

    private static string? NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? null : SystemCurrency.Normalize(currency);

    private static decimal SignedAmount(LedgerEntry entry)
        => entry.Side == LedgerSide.Credit ? entry.AmountUsd : -entry.AmountUsd;

    private static decimal GetSourceAmount(LedgerEntry entry)
        => entry.SourceAmount ?? entry.AmountUsd;

    private static string GetSourceCurrency(LedgerEntry entry)
        => entry.SourceCurrencyCode ?? entry.Currency;

    private static decimal GetAppliedRate(LedgerEntry entry)
        => entry.AppliedFxRateToUsd ?? (string.Equals(GetSourceCurrency(entry), BaseCurrency, StringComparison.OrdinalIgnoreCase) ? 1m : 0m);

    private static decimal? GetNullableAppliedRate(LedgerEntry entry, string sourceCurrency)
        => entry.AppliedFxRateToUsd
            ?? (string.Equals(sourceCurrency, BaseCurrency, StringComparison.OrdinalIgnoreCase) ? 1m : null);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string BuildOperationalLoadingNote(decimal quantityMt, decimal? unitPriceUsd)
    {
        if (!unitPriceUsd.HasValue || unitPriceUsd <= 0m)
        {
            return "Operational only / not posted to Ledger.";
        }

        var estimatedValueUsd = Math.Round(quantityMt * unitPriceUsd.Value, 4, MidpointRounding.AwayFromZero);
        return $"Operational only / not posted to Ledger. Estimated cargo value: {estimatedValueUsd:N2} USD.";
    }

    private sealed class ContractAccountStatementDraftRow
    {
        public DateTime Date { get; init; }
        public int SortGroup { get; init; }
        public int SortId { get; init; }
        public string SourceType { get; init; } = string.Empty;
        public int SourceId { get; init; }
        public string? Reference { get; init; }
        public string Description { get; init; } = string.Empty;
        public decimal? QuantityMt { get; init; }
        public decimal? UnitPrice { get; init; }
        public string? SourceCurrency { get; init; }
        public decimal? ReceiptOriginal { get; init; }
        public decimal? OutflowOriginal { get; init; }
        public decimal? FxRateToUsd { get; init; }
        public decimal? ReceiptUsd { get; init; }
        public decimal? OutflowUsd { get; init; }
        public string? RelatedContractNumber { get; init; }
        public string? Notes { get; init; }
        public string? WarningBadge { get; init; }
        public bool IsFinancial { get; init; }
        public bool IsOperationalOnly { get; init; }
        public bool IsReversalRow { get; init; }
    }

    private sealed record ContractBalanceTransferLookup(
        int Id,
        int FromContractId,
        int ToContractId,
        string? Notes,
        string? FromContractNumber,
        string? ToContractNumber);

    private string GetSideName(LedgerSide side) => UiText.LedgerSideName(HttpContext, side);
}
