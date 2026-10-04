using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Finance;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Controllers;

// تسویه بین طرف‌حساب‌ها: بدهکارِ شرکت مستقیماً به طلبکارِ شرکت پول می‌دهد.
// دو سطر دفتر کل (رسید از پرداخت‌کننده، برد به گیرنده) با همان قاعدهٔ CompanyFlowDirectionResolver
// و تسویه سه‌طرفه ساخته می‌شود. هیچ PaymentTransaction یا حساب صندوق/بانک لمس نمی‌شود.
[Authorize]
public class PartySettlementsController : Controller
{
    public const string LedgerSourceType = "PartySettlement";
    private const string LedgerTitle = "تسویه بین طرف‌حساب‌ها";

    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IAfghanistanBusinessClock _businessClock;
    private readonly IFormTokenGuard? _formTokens;
    private readonly IPartyBalanceReadService _balances;

    private ILedgerPostingService? _ledgerPosting;
    private ILedgerPostingService Ledger => _ledgerPosting ??= new LedgerPostingService(_db);

    public PartySettlementsController(
        ApplicationDbContext db,
        IAuditService audit,
        IAfghanistanBusinessClock businessClock,
        IFormTokenGuard? formTokens = null,
        IPartyBalanceReadService? balances = null)
    {
        _db = db;
        _audit = audit;
        _businessClock = businessClock;
        _formTokens = formTokens;
        _balances = balances ?? PartyBalanceReadService.CreateDefault(db);
    }

    public async Task<IActionResult> Index(int page = 1, [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        var pageSize = ListPageSize.Resolve(perPage, 20);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 20;

        var query = _db.PartySettlements.AsNoTracking();
        var totalCount = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        var items = await query
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var names = await LoadNamesAsync(items.SelectMany(s => new[]
        {
            (s.FromPartyType, s.FromPartyId),
            (s.ToPartyType, s.ToPartyId)
        }));

        return View(items.Select(s => new PartySettlementListRow(
            s.Id,
            s.SettlementDate,
            NameOf(names, s.FromPartyType, s.FromPartyId),
            PartySettlementParties.TypeLabel(s.FromPartyType),
            NameOf(names, s.ToPartyType, s.ToPartyId),
            PartySettlementParties.TypeLabel(s.ToPartyType),
            s.Amount,
            s.Currency,
            s.Status)).ToList());
    }

    /// <summary>
    /// گزارش تسویه بین طرف‌حساب‌ها — فقط‌خواندنی از همان رکوردهای PartySettlement؛
    /// هیچ LedgerEntry، مانده، صندوق یا تسویه‌ای ساخته یا تغییر داده نمی‌شود.
    /// جمع‌ها فقط از تسویه‌های فعال و همیشه به تفکیک ارز هستند (بدون تبدیل ارز).
    /// </summary>
    public async Task<IActionResult> Report(
        [FromQuery] PartySettlementReportFilter? filter,
        int page = 1,
        [FromQuery(Name = "pageSize")] int? perPage = null)
    {
        filter ??= new PartySettlementReportFilter();
        filter.Currency = string.IsNullOrWhiteSpace(filter.Currency) ? null : filter.Currency.Trim().ToUpperInvariant();
        filter.Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        var hasA = PartySettlementParties.TryParse(filter.PartyA, out var aType, out var aId);
        var hasB = PartySettlementParties.TryParse(filter.PartyB, out var bType, out var bId);
        if (!hasA) filter.PartyA = null;
        if (!hasB) filter.PartyB = null;
        if (hasA && hasB && aType == bType && aId == bId)
        {
            ModelState.AddModelError(nameof(filter.PartyB), "طرف‌حساب «ب» باید با «الف» فرق داشته باشد.");
            hasB = false;
            filter.PartyB = null;
        }
        if (filter.FromDate.HasValue && filter.ToDate.HasValue && filter.FromDate.Value.Date > filter.ToDate.Value.Date)
        {
            ModelState.AddModelError(nameof(filter.ToDate), "تاریخ پایان نباید قبل از تاریخ شروع باشد.");
            filter.ToDate = null;
        }

        var scope = _db.PartySettlements.AsNoTracking();
        if (filter.FromDate.HasValue)
        {
            var from = filter.FromDate.Value.Date;
            scope = scope.Where(s => s.SettlementDate >= from);
        }
        if (filter.ToDate.HasValue)
        {
            var toExclusive = filter.ToDate.Value.Date.AddDays(1);
            scope = scope.Where(s => s.SettlementDate < toExclusive);
        }
        if (filter.Currency is not null)
        {
            var currency = filter.Currency;
            scope = scope.Where(s => s.Currency == currency);
        }
        if (filter.Search is not null)
        {
            var search = filter.Search;
            var searchId = int.TryParse(search.Replace("PS-", "", StringComparison.OrdinalIgnoreCase), out var parsedId) ? parsedId : 0;
            scope = scope.Where(s => s.Id == searchId || (s.Description != null && s.Description.Contains(search)));
        }

        // «الف ← ب»: پرداخت‌کننده الف و گیرنده ب. طرف خالی با هر طرف‌حسابی جور می‌شود.
        IQueryable<PartySettlement> AToB(IQueryable<PartySettlement> q) => q.Where(s =>
            (!hasA || (s.FromPartyType == aType && s.FromPartyId == aId))
            && (!hasB || (s.ToPartyType == bType && s.ToPartyId == bId)));
        IQueryable<PartySettlement> BToA(IQueryable<PartySettlement> q) => q.Where(s =>
            (!hasB || (s.FromPartyType == bType && s.FromPartyId == bId))
            && (!hasA || (s.ToPartyType == aType && s.ToPartyId == aId)));

        var directional = filter.Direction switch
        {
            PartySettlementReportDirection.AToB => AToB(scope),
            PartySettlementReportDirection.BToA => BToA(scope),
            _ => scope.Where(s =>
                ((!hasA || (s.FromPartyType == aType && s.FromPartyId == aId))
                    && (!hasB || (s.ToPartyType == bType && s.ToPartyId == bId)))
                || ((!hasB || (s.FromPartyType == bType && s.FromPartyId == bId))
                    && (!hasA || (s.ToPartyType == aType && s.ToPartyId == aId))))
        };
        var listed = filter.Status switch
        {
            PartySettlementReportStatus.Cancelled => directional.Where(s => s.Status == PartySettlementStatus.Cancelled),
            PartySettlementReportStatus.All => directional,
            _ => directional.Where(s => s.Status == PartySettlementStatus.Posted)
        };
        var posted = listed.Where(s => s.Status == PartySettlementStatus.Posted);

        static async Task<IReadOnlyList<PartySettlementCurrencyTotal>> TotalsAsync(IQueryable<PartySettlement> q)
            => (await q.GroupBy(s => s.Currency)
                    .Select(g => new { Currency = g.Key, Amount = g.Sum(s => s.Amount), Count = g.Count() })
                    .ToListAsync())
                .OrderBy(t => t.Currency, StringComparer.Ordinal)
                .Select(t => new PartySettlementCurrencyTotal(t.Currency, t.Amount, t.Count))
                .ToList();

        var totalCount = await listed.CountAsync();
        var activeCount = await posted.CountAsync();
        var pageSize = ListPageSize.Resolve(perPage, 50);
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);
        ViewData["PageSize"] = pageSize;
        ViewData["DefaultPageSize"] = 50;
        ViewData["CurrentPage"] = page;
        ViewData["PageCount"] = pageCount;
        ViewData["TotalCount"] = totalCount;

        var items = await listed
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var nameKeys = items.SelectMany(s => new[] { (s.FromPartyType, s.FromPartyId), (s.ToPartyType, s.ToPartyId) }).ToList();
        if (hasA) nameKeys.Add((aType, aId));
        if (hasB) nameKeys.Add((bType, bId));
        var names = await LoadNamesAsync(nameKeys);

        var model = new PartySettlementReportViewModel
        {
            Filter = filter,
            PartyAName = hasA ? NameOf(names, aType, aId) : null,
            PartyBName = hasB ? NameOf(names, bType, bId) : null,
            TotalCount = totalCount,
            ActiveCount = activeCount,
            CancelledCount = totalCount - activeCount,
            AToBTotals = hasA || hasB ? await TotalsAsync(AToB(posted)) : [],
            BToATotals = hasA || hasB ? await TotalsAsync(BToA(posted)) : [],
            Totals = await TotalsAsync(posted),
            Rows = items.Select(s => new PartySettlementReportRow(
                s.Id,
                $"PS-{s.Id}",
                s.SettlementDate,
                NameOf(names, s.FromPartyType, s.FromPartyId),
                PartySettlementParties.TypeLabel(s.FromPartyType),
                NameOf(names, s.ToPartyType, s.ToPartyId),
                PartySettlementParties.TypeLabel(s.ToPartyType),
                s.Amount,
                s.Currency,
                s.CurrencyPerUsdRate,
                s.Description,
                s.CreatedByUserName,
                s.Status,
                s.CancelledAtUtc,
                s.CancelledByUserName,
                s.CancellationReason)).ToList()
        };

        await PopulateLookupsAsync(includeInactive: true);
        return View(model);
    }

    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.PartySettlements.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (item == null) return NotFound();
        var names = await LoadNamesAsync([(item.FromPartyType, item.FromPartyId), (item.ToPartyType, item.ToPartyId)]);
        ViewBag.FromName = NameOf(names, item.FromPartyType, item.FromPartyId);
        ViewBag.ToName = NameOf(names, item.ToPartyType, item.ToPartyId);
        return View(item);
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Create()
    {
        await PopulateLookupsAsync();
        return View(new PartySettlementFormModel { SettlementDate = _businessClock.Today });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        PartySettlementFormModel model,
        [FromForm(Name = FormTokenHtmlHelper.FieldName)] string? formToken = null)
    {
        var parsed = await ValidateFormAsync(model);
        if (parsed is not var (fromType, fromId, toType, toId))
        {
            await PopulateLookupsAsync();
            return View(model);
        }

        var settlement = new PartySettlement
        {
            Status = PartySettlementStatus.Posted,
            CreatedByUserName = User?.Identity?.Name
        };
        ApplyForm(settlement, model, fromType, fromId, toType, toId);

        var names = await LoadNamesAsync([(fromType, fromId), (toType, toId)]);
        var fromName = NameOf(names, fromType, fromId);
        var toName = NameOf(names, toType, toId);

        // همهٔ مراحل در یک تراکنش: سند، دو سطر دفتر و audit یا همه ثبت می‌شوند یا هیچ‌کدام.
        await using var transaction = await BeginTransactionIfRelationalAsync();

        _db.PartySettlements.Add(settlement);
        _formTokens?.Stamp(formToken, "PartySettlement.Create", nameof(PartySettlement));
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException duplicate) when (_formTokens?.IsDuplicate(duplicate) == true)
        {
            TempData["err"] = "این تسویه قبلاً ثبت شده است و دوباره ثبت نشد.";
            return RedirectToAction(nameof(Index));
        }

        var posted = Ledger.PostRange(
            BuildLedger(settlement, fromType, fromId, isPayer: true, $"{LedgerTitle}: پرداخت مستقیم به {toName}"),
            BuildLedger(settlement, toType, toId, isPayer: false, $"{LedgerTitle}: دریافت مستقیم از {fromName}"));
        await _db.SaveChangesAsync();

        settlement.FromLedgerEntryId = posted[0].Id;
        settlement.ToLedgerEntryId = posted[1].Id;
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(nameof(PartySettlement), settlement.Id, AuditAction.Insert,
            diff: AuditDiffFormatter.ForCreate(
                ("SettlementDate", settlement.SettlementDate.ToString("yyyy-MM-dd")),
                ("From", $"{PartySettlementParties.TypeLabel(fromType)} {fromName} (#{fromId})"),
                ("To", $"{PartySettlementParties.TypeLabel(toType)} {toName} (#{toId})"),
                ("Amount", settlement.Amount),
                ("Currency", settlement.Currency),
                ("AmountUsd", settlement.AmountUsd)));

        if (transaction is not null) await transaction.CommitAsync();

        TempData["ok"] = "تسویه ثبت شد.";
        return RedirectToAction(nameof(Details), new { id = settlement.Id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    public async Task<IActionResult> Edit(int id)
    {
        var item = await _db.PartySettlements.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (item == null) return NotFound();
        if (await EditBlockReasonAsync(item) is { } blocked)
        {
            TempData["err"] = blocked;
            return RedirectToAction(nameof(Details), new { id });
        }

        await PopulateLookupsAsync(keep: item);
        ViewBag.EditId = item.Id;
        return View("Create", new PartySettlementFormModel
        {
            SettlementDate = item.SettlementDate,
            FromParty = PartySettlementParties.Key(item.FromPartyType, item.FromPartyId),
            ToParty = PartySettlementParties.Key(item.ToPartyType, item.ToPartyId),
            Amount = item.Amount,
            Currency = item.Currency,
            CurrencyPerUsdRate = item.CurrencyPerUsdRate,
            Description = item.Description
        });
    }

    /// <summary>
    /// ویرایش تسویهٔ ثبت‌شده: همان دو سطر دفتر کل (پرداخت‌کننده/گیرنده) با <c>Ledger.Apply</c>
    /// در جا به‌روز می‌شوند — هم‌الگوی بقیهٔ مسیرهای «ویرایش سند» — تا هیچ سطر تکراری یا برگشتی
    /// ساخته نشود و شناسهٔ سطرها و پیوندشان با سند ثابت بماند. قاعدهٔ جهت (Debit/Credit) همان BuildLedger است.
    /// </summary>
    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, PartySettlementFormModel model)
    {
        var item = await _db.PartySettlements
            .Include(s => s.FromLedgerEntry)
            .Include(s => s.ToLedgerEntry)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (item == null) return NotFound();
        if (await EditBlockReasonAsync(item) is { } blocked)
        {
            TempData["err"] = blocked;
            return RedirectToAction(nameof(Details), new { id });
        }

        var parsed = await ValidateFormAsync(model, keepCurrency: item.Currency);
        if (parsed is not var (fromType, fromId, toType, toId))
        {
            await PopulateLookupsAsync(keep: item);
            ViewBag.EditId = item.Id;
            return View("Create", model);
        }

        var oldNames = await LoadNamesAsync([(item.FromPartyType, item.FromPartyId), (item.ToPartyType, item.ToPartyId)]);
        var before = (
            Date: item.SettlementDate.ToString("yyyy-MM-dd"),
            From: $"{PartySettlementParties.TypeLabel(item.FromPartyType)} {NameOf(oldNames, item.FromPartyType, item.FromPartyId)} (#{item.FromPartyId})",
            To: $"{PartySettlementParties.TypeLabel(item.ToPartyType)} {NameOf(oldNames, item.ToPartyType, item.ToPartyId)} (#{item.ToPartyId})",
            item.Amount, item.Currency, item.CurrencyPerUsdRate, item.AmountUsd, item.Description);

        ApplyForm(item, model, fromType, fromId, toType, toId);

        var names = await LoadNamesAsync([(fromType, fromId), (toType, toId)]);
        var fromName = NameOf(names, fromType, fromId);
        var toName = NameOf(names, toType, toId);

        await using var transaction = await BeginTransactionIfRelationalAsync();

        Ledger.Apply(item.FromLedgerEntry!,
            BuildLedger(item, fromType, fromId, isPayer: true, $"{LedgerTitle}: پرداخت مستقیم به {toName}"));
        Ledger.Apply(item.ToLedgerEntry!,
            BuildLedger(item, toType, toId, isPayer: false, $"{LedgerTitle}: دریافت مستقیم از {fromName}"));
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(nameof(PartySettlement), item.Id, AuditAction.Update,
            diff: AuditDiffFormatter.ForUpdate(
                ("SettlementDate", before.Date, item.SettlementDate.ToString("yyyy-MM-dd")),
                ("From", before.From, $"{PartySettlementParties.TypeLabel(fromType)} {fromName} (#{fromId})"),
                ("To", before.To, $"{PartySettlementParties.TypeLabel(toType)} {toName} (#{toId})"),
                ("Amount", before.Amount, item.Amount),
                ("Currency", before.Currency, item.Currency),
                ("CurrencyPerUsdRate", before.CurrencyPerUsdRate, item.CurrencyPerUsdRate),
                ("AmountUsd", before.AmountUsd, item.AmountUsd),
                ("Description", before.Description, item.Description)));

        if (transaction is not null) await transaction.CommitAsync();

        TempData["ok"] = "تسویه ویرایش شد و ماندهٔ هر دو طرف با مقادیر جدید هماهنگ شد.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = AuthPolicies.ManageData)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? cancellationReason)
    {
        var item = await _db.PartySettlements
            .Include(s => s.FromLedgerEntry)
            .Include(s => s.ToLedgerEntry)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (item == null) return NotFound();

        var reason = string.IsNullOrWhiteSpace(cancellationReason) ? null : cancellationReason.Trim();
        string? error = null;
        if (reason == null)
            error = "دلیل لغو الزامی است.";
        else if (item.Status != PartySettlementStatus.Posted)
            error = "این تسویه قبلاً لغو شده است.";
        else if (item.FromLedgerEntry == null || item.ToLedgerEntry == null)
            error = "سطرهای دفتر این تسویه کامل نیست و لغو امن ممکن نیست.";
        if (error != null)
        {
            TempData["err"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await using var transaction = await BeginTransactionIfRelationalAsync();

        var today = _businessClock.Today;
        var description = $"لغو {LedgerTitle}: {reason}";
        var fromReversal = await Ledger.ReverseAsync(item.FromLedgerEntry!, today, description, ReferenceOf(item.Id, isPayer: true));
        var toReversal = await Ledger.ReverseAsync(item.ToLedgerEntry!, today, description, ReferenceOf(item.Id, isPayer: false));
        if (fromReversal == null || toReversal == null)
        {
            // یکی از سطرها قبلاً برگشت خورده؛ تراکنش commit نمی‌شود تا ثبت ناقص نماند.
            TempData["err"] = "برای این تسویه قبلاً سند برگشت ساخته شده است.";
            return RedirectToAction(nameof(Details), new { id });
        }

        item.Status = PartySettlementStatus.Cancelled;
        item.CancelledAtUtc = DateTime.UtcNow;
        item.CancelledByUserName = User?.Identity?.Name;
        item.CancellationReason = reason;
        await _db.SaveChangesAsync();

        await _audit.LogAndSaveAsync(nameof(PartySettlement), item.Id, AuditAction.Reverse,
            diff: AuditDiffFormatter.ForUpdate(("Status", "Posted", "Cancelled"), ("CancellationReason", null, reason)));

        if (transaction is not null) await transaction.CommitAsync();

        TempData["ok"] = "تسویه لغو شد و ماندهٔ هر دو طرف به حالت قبل برگشت.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // فقط تسویهٔ فعال با هر دو سطر دفتر و بدون هیچ سطر برگشتی قابل ویرایش است.
    private async Task<string?> EditBlockReasonAsync(PartySettlement item)
    {
        if (item.Status != PartySettlementStatus.Posted)
            return "تسویهٔ لغوشده قابل ویرایش نیست.";
        if (item.FromLedgerEntryId == null || item.ToLedgerEntryId == null)
            return "سطرهای دفتر این تسویه کامل نیست و ویرایش امن ممکن نیست.";
        var reversalRefs = new[]
        {
            ReferenceOf(item.Id, isPayer: true) + CompanyFlowSourceTypes.ReversalReferenceSuffix,
            ReferenceOf(item.Id, isPayer: false) + CompanyFlowSourceTypes.ReversalReferenceSuffix
        };
        if (await _db.LedgerEntries.AnyAsync(l => l.SourceType == LedgerSourceType
                && l.SourceId == item.Id && reversalRefs.Contains(l.Reference)))
            return "برای این تسویه سند برگشت وجود دارد و ویرایش ممکن نیست.";
        return null;
    }

    // اعتبارسنجی مشترک ثبت و ویرایش؛ در صورت خطا null برمی‌گرداند و خطاها در ModelState است.
    private async Task<(AccountingPartyType FromType, int FromId, AccountingPartyType ToType, int ToId)?> ValidateFormAsync(
        PartySettlementFormModel model, string? keepCurrency = null)
    {
        model.Currency = string.IsNullOrWhiteSpace(model.Currency) ? "USD" : model.Currency.Trim().ToUpperInvariant();
        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();

        var hasFrom = PartySettlementParties.TryParse(model.FromParty, out var fromType, out var fromId);
        var hasTo = PartySettlementParties.TryParse(model.ToParty, out var toType, out var toId);
        if (!hasFrom)
            ModelState.AddModelError(nameof(model.FromParty), "طرف‌حساب پرداخت‌کننده را انتخاب کنید.");
        if (!hasTo)
            ModelState.AddModelError(nameof(model.ToParty), "طرف‌حساب گیرنده را انتخاب کنید.");
        if (hasFrom && hasTo && fromType == toType && fromId == toId)
            ModelState.AddModelError(nameof(model.ToParty), "پرداخت‌کننده و گیرنده نمی‌توانند یک طرف‌حساب باشند.");
        if (model.Amount <= 0m)
            ModelState.AddModelError(nameof(model.Amount), "مبلغ باید بزرگ‌تر از صفر باشد.");
        if (model.SettlementDate == default)
            ModelState.AddModelError(nameof(model.SettlementDate), "تاریخ را وارد کنید.");
        if (!await _db.Currencies.AnyAsync(c => c.Code == model.Currency && (c.IsActive || c.Code == keepCurrency)))
            ModelState.AddModelError(nameof(model.Currency), "ارز نامعتبر است.");

        if (model.Currency != "USD" && !(model.CurrencyPerUsdRate > 0m))
            ModelState.AddModelError(nameof(model.CurrencyPerUsdRate), "نرخ دالر به این ارز را وارد کنید.");

        if (hasFrom && !await PartyExistsAsync(fromType, fromId))
            ModelState.AddModelError(nameof(model.FromParty), "طرف‌حساب پرداخت‌کننده یافت نشد.");
        if (hasTo && !await PartyExistsAsync(toType, toId))
            ModelState.AddModelError(nameof(model.ToParty), "طرف‌حساب گیرنده یافت نشد.");

        return ModelState.IsValid ? (fromType, fromId, toType, toId) : null;
    }

    private static void ApplyForm(
        PartySettlement s, PartySettlementFormModel model,
        AccountingPartyType fromType, int fromId, AccountingPartyType toType, int toId)
    {
        var isUsd = model.Currency == "USD";
        var fxRateToUsd = isUsd ? 1m : 1m / model.CurrencyPerUsdRate!.Value;
        s.SettlementDate = model.SettlementDate.Date;
        s.FromPartyType = fromType;
        s.FromPartyId = fromId;
        s.ToPartyType = toType;
        s.ToPartyId = toId;
        s.Amount = Math.Round(model.Amount, 4, MidpointRounding.AwayFromZero);
        s.Currency = model.Currency;
        s.CurrencyPerUsdRate = isUsd ? null : model.CurrencyPerUsdRate;
        s.FxRateToUsd = fxRateToUsd;
        s.AmountUsd = Math.Round(model.Amount * fxRateToUsd, 2, MidpointRounding.AwayFromZero);
        s.Description = model.Description;
    }

    /// <summary>ماندهٔ فعلی یک طرف‌حساب با شرکت، به زبان ساده — برای نمایش زیر لیست فورم.</summary>
    [HttpGet]
    public async Task<IActionResult> PartyBalance(string? party, CancellationToken ct)
    {
        if (!PartySettlementParties.TryParse(party, out var type, out var id))
            return BadRequest();

        var statementType = PartyTypeMap.ToStatement(type);
        var rows = await _balances.GetBalancesAsync(new ManagementReportFilterViewModel(), ct, [statementType]);
        var row = rows.FirstOrDefault(r => r.PartyId == id);
        var balance = row?.ClosingBalanceUsd ?? 0m;
        var meaning = row?.BalanceMeaning
            ?? CompanyFlowText.BalanceMeaning(0m, CompanyFlowAccountKind.PartyAccount, isEnglish: false);
        var text = balance == 0m
            ? meaning
            : $"{meaning}: {NumberDisplay.Money(Math.Abs(balance), "USD")}";
        return Json(new { text });
    }

    // پرداخت‌کننده برای شرکت «رسید» است و گیرنده «برد». مشتری حساب دریافتنی است
    // (رسید = Debit) و بقیه پرداختنی‌اند (رسید = Credit) — همان قاعدهٔ تسویه سه‌طرفه.
    private static LedgerPostingRequest BuildLedger(
        PartySettlement s, AccountingPartyType type, int partyId, bool isPayer, string description)
    {
        var isReceivable = type == AccountingPartyType.Customer;
        return new LedgerPostingRequest
        {
            SourceType = LedgerSourceType,
            SourceId = s.Id,
            EntryDate = s.SettlementDate,
            Side = isPayer == isReceivable ? LedgerSide.Debit : LedgerSide.Credit,
            AmountUsd = s.AmountUsd,
            Currency = "USD",
            SourceAmount = s.Amount,
            SourceCurrencyCode = s.Currency,
            AppliedFxRateToUsd = s.FxRateToUsd,
            AppliedCurrencyPerUsdRate = s.CurrencyPerUsdRate,
            AppliedFxRateDate = s.SettlementDate,
            AppliedFxRateSource = s.CurrencyPerUsdRate.HasValue ? "Party settlement (manual)" : null,
            Description = string.IsNullOrWhiteSpace(s.Description) ? description : $"{description} — {s.Description}",
            Reference = ReferenceOf(s.Id, isPayer),
            CustomerId = type == AccountingPartyType.Customer ? partyId : null,
            SupplierId = type == AccountingPartyType.Supplier ? partyId : null,
            ServiceProviderId = type == AccountingPartyType.ServiceProvider ? partyId : null,
            DriverId = type == AccountingPartyType.Driver ? partyId : null
        };
    }

    // هر سطر مرجع جدا دارد: محافظ «دو بار برگشت» در ReverseAsync روی (SourceType, SourceId, Reference)
    // است؛ با مرجع مشترک، برگشتِ سطر اول سطر دوم را «قبلاً برگشت‌خورده» نشان می‌داد.
    private static string ReferenceOf(int id, bool isPayer) => $"PS-{id}-{(isPayer ? "P" : "R")}";

    private Task<bool> PartyExistsAsync(AccountingPartyType type, int id) => type switch
    {
        AccountingPartyType.Customer => _db.Customers.AnyAsync(c => c.Id == id),
        AccountingPartyType.Supplier => _db.Suppliers.AnyAsync(c => c.Id == id),
        AccountingPartyType.ServiceProvider => _db.ServiceProviders.AnyAsync(c => c.Id == id),
        AccountingPartyType.Driver => _db.Drivers.AnyAsync(c => c.Id == id),
        _ => Task.FromResult(false)
    };

    private async Task<Dictionary<(AccountingPartyType, int), string>> LoadNamesAsync(
        IEnumerable<(AccountingPartyType Type, int Id)> keys)
    {
        var list = keys.Distinct().ToList();
        int[] Ids(AccountingPartyType t) => list.Where(k => k.Type == t).Select(k => k.Id).ToArray();
        var result = new Dictionary<(AccountingPartyType, int), string>();

        var customerIds = Ids(AccountingPartyType.Customer);
        if (customerIds.Length > 0)
            foreach (var p in await _db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync())
                result[(AccountingPartyType.Customer, p.Id)] = p.Name;
        var supplierIds = Ids(AccountingPartyType.Supplier);
        if (supplierIds.Length > 0)
            foreach (var p in await _db.Suppliers.AsNoTracking().Where(c => supplierIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync())
                result[(AccountingPartyType.Supplier, p.Id)] = p.Name;
        var providerIds = Ids(AccountingPartyType.ServiceProvider);
        if (providerIds.Length > 0)
            foreach (var p in await _db.ServiceProviders.AsNoTracking().Where(c => providerIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync())
                result[(AccountingPartyType.ServiceProvider, p.Id)] = p.Name;
        var driverIds = Ids(AccountingPartyType.Driver);
        if (driverIds.Length > 0)
            foreach (var p in await _db.Drivers.AsNoTracking().Where(c => driverIds.Contains(c.Id)).Select(c => new { c.Id, c.FullName }).ToListAsync())
                result[(AccountingPartyType.Driver, p.Id)] = p.FullName;

        return result;
    }

    private static string NameOf(Dictionary<(AccountingPartyType, int), string> names, AccountingPartyType type, int id)
        => names.TryGetValue((type, id), out var name) ? name : $"#{id}";

    // گزارش، طرف‌حساب‌های غیرفعال را هم نشان می‌دهد چون تسویه‌های قدیمی آن‌ها هنوز گزارش می‌شوند.
    // ویرایش: طرف‌حساب‌ها و ارزِ فعلیِ سند حتی اگر غیرفعال شده باشند در لیست می‌مانند.
    private async Task PopulateLookupsAsync(bool includeInactive = false, PartySettlement? keep = null)
    {
        int[] Keep(AccountingPartyType t) => keep == null ? [] : new[]
        {
            keep.FromPartyType == t ? keep.FromPartyId : 0,
            keep.ToPartyType == t ? keep.ToPartyId : 0
        }.Where(i => i > 0).ToArray();
        var keepCustomers = Keep(AccountingPartyType.Customer);
        var keepSuppliers = Keep(AccountingPartyType.Supplier);
        var keepProviders = Keep(AccountingPartyType.ServiceProvider);
        var keepDrivers = Keep(AccountingPartyType.Driver);
        var keepCurrency = keep?.Currency;
        var groups = new List<(string Label, List<(string Key, string Name)> Items)>
        {
            (PartySettlementParties.GroupLabel(AccountingPartyType.Customer),
                (await _db.Customers.AsNoTracking().Where(c => includeInactive || c.IsActive || keepCustomers.Contains(c.Id)).OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync())
                    .Select(c => (PartySettlementParties.Key(AccountingPartyType.Customer, c.Id), c.Name)).ToList()),
            (PartySettlementParties.GroupLabel(AccountingPartyType.Supplier),
                (await _db.Suppliers.AsNoTracking().Where(c => includeInactive || c.IsActive || keepSuppliers.Contains(c.Id)).OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync())
                    .Select(c => (PartySettlementParties.Key(AccountingPartyType.Supplier, c.Id), c.Name)).ToList()),
            (PartySettlementParties.GroupLabel(AccountingPartyType.ServiceProvider),
                (await _db.ServiceProviders.AsNoTracking().Where(c => includeInactive || c.IsActive || keepProviders.Contains(c.Id)).OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync())
                    .Select(c => (PartySettlementParties.Key(AccountingPartyType.ServiceProvider, c.Id), c.Name)).ToList()),
            (PartySettlementParties.GroupLabel(AccountingPartyType.Driver),
                (await _db.Drivers.AsNoTracking().Where(c => includeInactive || c.IsActive || keepDrivers.Contains(c.Id)).OrderBy(c => c.FullName).Select(c => new { c.Id, c.FullName }).ToListAsync())
                    .Select(c => (PartySettlementParties.Key(AccountingPartyType.Driver, c.Id), c.FullName)).ToList())
        };
        ViewBag.PartyGroups = groups;
        ViewBag.Currencies = await _db.Currencies.AsNoTracking()
            .Where(c => c.IsActive || c.Code == keepCurrency)
            .OrderBy(c => c.Code)
            .Select(c => c.Code)
            .ToListAsync();
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync()
        => _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync()
            : null;
}
