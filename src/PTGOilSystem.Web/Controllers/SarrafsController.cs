using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sarrafs;
using PTGOilSystem.Web.Models.Shared;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.PartyStatements;
using PTGOilSystem.Web.Models.Reports;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class SarrafsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPartyStatementReadService _partyStatements;
    private readonly IPartyBalanceReadService _partyBalances;

    public SarrafsController(
        ApplicationDbContext db,
        IPartyStatementReadService? partyStatements = null,
        IPartyBalanceReadService? partyBalances = null)
    {
        _db = db;
        _partyStatements = partyStatements ?? PartyStatementReadService.CreateDefault(db);
        _partyBalances = partyBalances ?? PartyBalanceReadService.CreateDefault(db);
    }

    public async Task<IActionResult> Index(string? search = null)
    {
        var query = _db.Sarrafs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(s =>
                s.Name.Contains(search)
                || (s.PhoneNumber != null && s.PhoneNumber.Contains(search))
                || (s.Address != null && s.Address.Contains(search)));
        }

        var sarrafs = await query
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.PhoneNumber, s.IsActive })
            .ToListAsync();

        var sarrafIds = sarrafs.Select(s => s.Id).ToList();
        var settlements = await _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => sarrafIds.Contains(s.SarrafId) && s.Status == SarrafSettlementStatus.Posted)
            .GroupBy(s => s.SarrafId)
            .Select(g => new
            {
                SarrafId = g.Key,
                Count = g.Count(),
                // بدهی خالص: تسویه‌های Out بدهی را زیاد و In (دریافت از طریق صراف) بدهی را کم می‌کند.
                ChargedUsd = g.Where(s => s.Direction == SarrafSettlementDirection.Out).Sum(s => s.SarrafChargedAmountUsd)
                    - g.Where(s => s.Direction == SarrafSettlementDirection.In).Sum(s => s.SarrafChargedAmountUsd),
                LastDate = g.Max(s => (DateTime?)s.SettlementDate)
            })
            .ToDictionaryAsync(s => s.SarrafId);

        var viaSarrafPayables = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType
                && sarrafIds.Contains(l.SourceId))
            .GroupBy(l => l.SourceId)
            .Select(g => new
            {
                SarrafId = g.Key,
                ChargedUsd = g.Sum(l => l.AmountUsd),
                LastDate = g.Max(l => (DateTime?)l.EntryDate)
            })
            .ToDictionaryAsync(s => s.SarrafId);

        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.SarrafId.HasValue && sarrafIds.Contains(p.SarrafId.Value))
            .GroupBy(p => p.SarrafId!.Value)
            .Select(g => new
            {
                SarrafId = g.Key,
                PaidUsd = g.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd)
                    - g.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd)
            })
            .ToDictionaryAsync(p => p.SarrafId);

        var items = sarrafs.Select(s =>
        {
            settlements.TryGetValue(s.Id, out var settlement);
            viaSarrafPayables.TryGetValue(s.Id, out var viaSarrafPayable);
            payments.TryGetValue(s.Id, out var payment);
            return new SarrafIndexItemViewModel
            {
                Id = s.Id,
                Name = s.Name,
                PhoneNumber = s.PhoneNumber,
                IsActive = s.IsActive,
                SettlementCount = settlement?.Count ?? 0,
                ChargedUsd = (settlement?.ChargedUsd ?? 0m) + (viaSarrafPayable?.ChargedUsd ?? 0m),
                PaidUsd = payment?.PaidUsd ?? 0m,
                LastSettlementDate = LatestDate(settlement?.LastDate, viaSarrafPayable?.LastDate)
            };
        }).ToList();

        // ماندهٔ هر صراف از موتورِ رسمی (همان صورت‌حساب صراف): مثبت = طلب شرکت، منفی = بدهی شرکت.
        // «سپرده‌شده» و «پرداخت‌شده» فقط شاخصِ عملیاتی‌اند و مانده از آن‌ها ساخته نمی‌شود.
        var officialBalances = (await _partyBalances.GetBalancesAsync(
                new ManagementReportFilterViewModel(),
                HttpContext?.RequestAborted ?? CancellationToken.None,
                [PartyStatementPartyType.Sarraf]))
            .Where(row => row.PartyType == PartyStatementPartyType.Sarraf)
            .ToDictionary(row => row.PartyId, row => row.ClosingBalanceUsd);
        items = items
            .Select(item => item with { OfficialBalanceUsd = officialBalances.GetValueOrDefault(item.Id) })
            .ToList();

        return View(new SarrafIndexViewModel
        {
            Search = search,
            Items = items,
            ActiveCount = items.Count(i => i.IsActive),
            TotalChargedUsd = items.Sum(i => i.ChargedUsd),
            TotalPaidUsd = items.Sum(i => i.PaidUsd),
            TotalPayableUsd = items.Sum(i => i.PayableUsd),
            TotalOfficialBalanceUsd = items.Sum(i => i.OfficialBalanceUsd)
        });
    }

    public IActionResult Create()
        => View(new SarrafFormViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SarrafFormViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var sarraf = new Sarraf
        {
            Name = model.Name.Trim(),
            PhoneNumber = Clean(model.PhoneNumber),
            Address = Clean(model.Address),
            Notes = Clean(model.Notes),
            IsActive = model.IsActive
        };

        _db.Sarrafs.Add(sarraf);
        await _db.SaveChangesAsync();
        TempData["ok"] = "صراف با موفقیت ثبت شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Details), new { id = sarraf.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var sarraf = await _db.Sarrafs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (sarraf is null)
        {
            return NotFound();
        }

        return View(new SarrafFormViewModel
        {
            Id = sarraf.Id,
            Name = sarraf.Name,
            PhoneNumber = sarraf.PhoneNumber,
            Address = sarraf.Address,
            Notes = sarraf.Notes,
            IsActive = sarraf.IsActive
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl = null)
    {
        var item = await _db.Sarrafs.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound();

        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();

        TempData["ok"] = item.IsActive ? "رکورد فعال شد." : "رکورد غیرفعال شد.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, SarrafFormViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var sarraf = await _db.Sarrafs.FirstOrDefaultAsync(s => s.Id == id);
        if (sarraf is null)
        {
            return NotFound();
        }

        sarraf.Name = model.Name.Trim();
        sarraf.PhoneNumber = Clean(model.PhoneNumber);
        sarraf.Address = Clean(model.Address);
        sarraf.Notes = Clean(model.Notes);
        sarraf.IsActive = model.IsActive;
        await _db.SaveChangesAsync();

        TempData["ok"] = "اطلاعات صراف ذخیره شد.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var sarraf = await _db.Sarrafs.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (sarraf is null)
        {
            return NotFound();
        }

        // Projected to SarrafSettlementListItemViewModel below; EF ignores Include
        // on a projected query, so the nav names are read straight in the Select.
        var settlements = await _db.SarrafSettlements
            .AsNoTracking()
            .Where(s => s.SarrafId == id)
            .OrderByDescending(s => s.SettlementDate)
            .ThenByDescending(s => s.Id)
            .Select(s => new SarrafSettlementListItemViewModel
            {
                Id = s.Id,
                SettlementDate = s.SettlementDate,
                SarrafName = sarraf.Name,
                Direction = s.Direction,
                CounterpartyType = s.CounterpartyType,
                SupplierId = s.SupplierId,
                SupplierName = s.Supplier != null ? s.Supplier.Name : null,
                CustomerId = s.CustomerId,
                CustomerName = s.Customer != null ? s.Customer.Name : null,
                ServiceProviderId = s.ServiceProviderId,
                ServiceProviderName = s.ServiceProvider != null ? s.ServiceProvider.Name : null,
                DriverId = s.DriverId,
                DriverName = s.Driver != null ? s.Driver.FullName : null,
                EmployeeId = s.EmployeeId,
                EmployeeName = s.Employee != null ? s.Employee.FullName : null,
                ContractId = s.ContractId,
                ContractNumber = s.Contract != null ? s.Contract.ContractNumber : null,
                ReferenceNumber = s.ReferenceNumber,
                RequestedAmount = s.RequestedAmount,
                RequestedCurrency = s.RequestedCurrency,
                RequestedFxRateToUsd = s.RequestedFxRateToUsd,
                RequestedAmountUsd = s.RequestedAmountUsd,
                SarrafChargedAmount = s.SarrafChargedAmount,
                SarrafCurrency = s.SarrafCurrency,
                SarrafFxRateToUsd = s.SarrafFxRateToUsd,
                SarrafChargedAmountUsd = s.SarrafChargedAmountUsd,
                SupplierAcceptedAmount = s.SupplierAcceptedAmount,
                SupplierAcceptedCurrency = s.SupplierAcceptedCurrency,
                SupplierAcceptedFxRateToUsd = s.SupplierAcceptedFxRateToUsd,
                SupplierAcceptedAmountUsd = s.SupplierAcceptedAmountUsd,
                DifferenceAmountUsd = s.DifferenceAmountUsd,
                DifferenceType = s.DifferenceType,
                DifferenceTreatment = s.DifferenceTreatment,
                DifferenceReason = s.DifferenceReason,
                Status = s.Status,
                LedgerEntryId = s.LedgerEntryId,
                ExchangeDifferenceLedgerEntryId = s.ExchangeDifferenceLedgerEntryId
            })
            .ToListAsync();

        var payments = await _db.PaymentTransactions
            .AsNoTracking()
            .Include(p => p.CashAccount)
            .Where(p => p.SarrafId == id)
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Select(p => new SarrafPaymentStatementItemViewModel
            {
                PaymentId = p.Id,
                PaymentDate = p.PaymentDate,
                Direction = p.Direction,
                CashAccountName = p.CashAccount != null ? p.CashAccount.Name : "",
                Amount = p.Amount,
                Currency = p.Currency,
                AppliedFxRateToUsd = p.AppliedFxRateToUsd ?? 0m,
                AmountUsd = p.AmountUsd,
                Reference = p.Reference,
                Description = p.Description,
                LedgerEntryId = p.LedgerEntryId
            })
            .ToListAsync();

        var viaSarrafPayables = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == PaymentsController.ViaSarrafPayableLedgerSourceType
                && l.SourceId == id)
            .OrderByDescending(l => l.EntryDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new SarrafViaPaymentStatementItem
            {
                LedgerEntryId = l.Id,
                EntryDate = l.EntryDate,
                Amount = l.SourceAmount ?? l.AmountUsd,
                Currency = l.SourceCurrencyCode ?? l.Currency,
                AmountUsd = l.AmountUsd,
                Reference = l.Reference,
                Description = l.Description,
                ContractNumber = l.Contract != null ? l.Contract.ContractNumber : null
            })
            .ToListAsync();

        // فاز A — trace-only: حواله‌های مشتری که این صراف فقط واسطه آن بوده است.
        // هیچ اثری روی مانده صراف ندارد؛ مانده فقط از SarrafSettlements و PaymentTransactions بالا محاسبه می‌شود.
        var customerHawalas = await _db.ThreeWaySettlements
            .AsNoTracking()
            .Include(t => t.Customer)
            .Include(t => t.Supplier)
            .Where(t => t.SarrafId == id && t.PayeeType == ThreeWayPayeeType.Sarraf)
            .OrderByDescending(t => t.SettlementDate)
            .ThenByDescending(t => t.Id)
            .Select(t => new SarrafCustomerHawalaTraceItemViewModel
            {
                Id = t.Id,
                SettlementDate = t.SettlementDate,
                CustomerName = t.Customer != null ? t.Customer.Name : null,
                SupplierName = t.Supplier != null ? t.Supplier.Name : null,
                HawalaReference = t.HawalaReference,
                CustomerPaidUsd = t.CustomerPaidUsd,
                SupplierAcceptedUsd = t.SupplierAcceptedUsd,
                Currency = t.Currency,
                Status = t.Status
            })
            .ToListAsync();

        var posted = settlements.Where(s => s.Status == SarrafSettlementStatus.Posted).ToList();
        // KPI «بدهی ما به صراف»: تسویه‌های Out (پرداخت شرکت از طریق صراف) بدهی را زیاد و
        // تسویه‌های In (دریافت از طریق صراف) بدهی را کم می‌کنند — هم‌سو با مانده صورت‌حساب خطی.
        var postedOut = posted.Where(s => s.Direction == SarrafSettlementDirection.Out).ToList();
        var postedIn = posted.Where(s => s.Direction == SarrafSettlementDirection.In).ToList();
        var model = new SarrafDetailsViewModel
        {
            Id = sarraf.Id,
            Name = sarraf.Name,
            PhoneNumber = sarraf.PhoneNumber,
            Address = sarraf.Address,
            Notes = sarraf.Notes,
            IsActive = sarraf.IsActive,
            CreatedAtUtc = sarraf.CreatedAtUtc,
            RequestedUsd = postedOut.Sum(s => s.RequestedAmountUsd),
            ChargedUsd = postedOut.Sum(s => s.SarrafChargedAmountUsd)
                - postedIn.Sum(s => s.SarrafChargedAmountUsd)
                + viaSarrafPayables.Sum(l => l.AmountUsd),
            AcceptedUsd = postedOut.Sum(s => s.SupplierAcceptedAmountUsd),
            PaidUsd = payments.Where(p => p.Direction == PaymentDirection.Out).Sum(p => p.AmountUsd)
                - payments.Where(p => p.Direction == PaymentDirection.In).Sum(p => p.AmountUsd),
            SupplierShortfallUsd = posted
                .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.SupplierShortfall)
                .Sum(s => Math.Abs(s.DifferenceAmountUsd)),
            ExchangeGainUsd = posted
                .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Gain)
                .Sum(s => Math.Abs(s.DifferenceAmountUsd)),
            ExchangeLossUsd = posted
                .Where(s => s.DifferenceType == SarrafSettlementDifferenceType.Loss)
                .Sum(s => Math.Abs(s.DifferenceAmountUsd)),
            Settlements = settlements,
            Payments = payments,
            CustomerHawalas = customerHawalas,
            StatementRows = BuildSarrafStatementRows(settlements, payments, viaSarrafPayables)
        };
        var statement = await _partyStatements.GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Sarraf, id),
            new PartyStatementFilter { IncludeOperationalColumns = false },
            HttpContext?.RequestAborted ?? CancellationToken.None);
        ViewData["PartyStatementSummary"] = statement.Summary;
        ViewData["PartyStatementRecentRows"] = statement.Rows.Where(r => !r.IsOpeningBalance).Reverse().Take(5).ToList();
        return View(model);
    }

    // صورت‌حساب خطی صراف (فقط نمایش/خواندنی). مانده فقط از تسویه‌های Posted و پرداخت‌ها ساخته می‌شود.
    // قاعدهٔ یک‌دست «داده/گرفته»: پرداخت ما به صراف = داده‌شده (Credit)،
    //   پرداخت صراف از طرف ما/حواله = گرفته‌شده (Debit). مانده = Σ(داده − گرفته).
    // اختلاف نرخ (DifferenceAmountUsd) جداگانه به مانده اضافه نمی‌شود تا double-count نشود.
    private static IReadOnlyList<PartyStatementRowViewModel> BuildSarrafStatementRows(
        IReadOnlyList<SarrafSettlementListItemViewModel> settlements,
        IReadOnlyList<SarrafPaymentStatementItemViewModel> payments,
        IReadOnlyList<SarrafViaPaymentStatementItem> viaSarrafPayables)
    {
        var events = new List<StatementEvent>();

        foreach (var s in settlements.Where(s => s.Status == SarrafSettlementStatus.Posted))
        {
            // جهت تعیین می‌کند تسویه «گرفته» است یا «داده»:
            //   Out (پرداخت برای تأمین‌کننده/شرکت خدماتی): صراف از طرف ما پرداخت کرد → ما بدهکار صراف → «گرفته» (CreditUsd).
            //   In  (دریافت از مشتری): صراف برای ما پول گرفت → صراف بدهکار ما → «داده» (DebitUsd).
            var isIn = s.Direction == SarrafSettlementDirection.In;
            events.Add(new StatementEvent
            {
                Date = s.SettlementDate,
                Order = 0,
                SourceRouteId = s.Id,
                Type = isIn ? "دریافت از طریق صراف" : "پرداخت از طریق صراف",
                Reference = s.ReferenceNumber,
                ContractNumber = s.ContractNumber,
                Description = isIn
                    ? $"صراف از {s.CounterpartyName} برای شرکت دریافت کرد"
                    : $"پرداخت صراف برای {s.CounterpartyName}",
                SourceController = "SarrafSettlements",
                SourceAction = "Details",
                LedgerEntryId = s.LedgerEntryId ?? 0,
                SourceAmount = s.SarrafChargedAmount,
                Currency = s.SarrafCurrency,
                FxRateUsed = s.SarrafFxRateToUsd == 0m ? null : s.SarrafFxRateToUsd,
                CreditUsd = isIn ? null : s.SarrafChargedAmountUsd,
                DebitUsd = isIn ? s.SarrafChargedAmountUsd : null
            });
        }

        foreach (var l in viaSarrafPayables)
        {
            events.Add(new StatementEvent
            {
                Date = l.EntryDate,
                Order = 0,
                SourceRouteId = l.LedgerEntryId,
                Type = "پرداخت تأمین‌کننده از طریق صراف",
                Reference = l.Reference,
                ContractNumber = l.ContractNumber,
                Description = string.IsNullOrWhiteSpace(l.Description)
                    ? "صراف از طرف شرکت برای تأمین‌کننده پرداخت کرد"
                    : l.Description,
                SourceController = "Ledger",
                SourceAction = "Details",
                LedgerEntryId = l.LedgerEntryId,
                SourceAmount = l.Amount,
                Currency = l.Currency,
                FxRateUsed = null,
                CreditUsd = l.AmountUsd,
                DebitUsd = null
            });
        }

        foreach (var p in payments)
        {
            var isOut = p.Direction == PaymentDirection.Out;
            events.Add(new StatementEvent
            {
                Date = p.PaymentDate,
                Order = 1,
                SourceRouteId = p.PaymentId,
                Type = isOut ? "پرداخت به صراف" : "برگشت از صراف",
                Reference = p.Reference,
                ContractNumber = null,
                Description = isOut ? "پرداخت ما به صراف" : "برگشت پول از صراف",
                SourceController = "Payments",
                SourceAction = "Details",
                LedgerEntryId = p.LedgerEntryId ?? 0,
                SourceAmount = p.Amount,
                Currency = p.Currency,
                FxRateUsed = p.AppliedFxRateToUsd == 0m ? null : p.AppliedFxRateToUsd,
                CreditUsd = isOut ? null : p.AmountUsd,
                DebitUsd = isOut ? p.AmountUsd : null
            });
        }

        var rows = new List<PartyStatementRowViewModel>(events.Count);
        var balance = 0m;
        foreach (var e in events
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Order)
            .ThenBy(e => e.SourceRouteId))
        {
            // قاعدهٔ یک‌دست «داده/گرفته»:
            //   پرداخت ما به صراف (DebitUsd قبلی) = داده‌شده (Credit)،
            //   پرداخت صراف از طرف ما/کمیشن/حواله (CreditUsd قبلی) = گرفته‌شده (Debit).
            var givenUsd = e.DebitUsd;   // داده‌شده
            var takenUsd = e.CreditUsd;  // گرفته‌شده
            var hasGiven = givenUsd.HasValue && givenUsd.Value != 0m;
            var hasTaken = takenUsd.HasValue && takenUsd.Value != 0m;
            balance += (givenUsd ?? 0m) - (takenUsd ?? 0m);
            var effectUsd = hasGiven ? givenUsd : hasTaken ? takenUsd : (decimal?)null;
            rows.Add(new PartyStatementRowViewModel
            {
                LedgerEntryId = e.LedgerEntryId,
                Date = e.Date,
                Type = e.Type,
                Reference = e.Reference,
                SourceDetailsController = e.SourceController,
                SourceDetailsAction = e.SourceAction,
                SourceDetailsRouteId = e.SourceRouteId,
                ContractNumber = e.ContractNumber,
                Description = e.Description,
                SourceAmount = e.SourceAmount,
                Currency = e.Currency,
                FxRateUsed = e.FxRateUsed,
                FxRateDisplayPerUsd = e.FxRateUsed.HasValue && e.FxRateUsed.Value > 0m && !string.Equals(e.Currency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? decimal.Round(1m / e.FxRateUsed.Value, 4, MidpointRounding.AwayFromZero)
                    : (decimal?)null,
                StatementCreditUsd = givenUsd,
                StatementDebitUsd = takenUsd,
                EffectLabel = hasGiven ? "پرداخت ما / داده شد" : hasTaken ? "پرداخت صراف / گرفته شد" : "بدون اثر",
                EffectClass = hasGiven ? "is-increase" : hasTaken ? "is-decrease" : "is-neutral",
                SignedUsd = effectUsd.HasValue ? $"{(hasGiven ? "+" : "-")}{effectUsd.Value:N2}" : "-",
                RunningBalanceUsd = balance,
                BalanceLabel = balance > 0 ? "طلب ما از صراف" : balance < 0 ? "قابل پرداخت به صراف" : "تسویه",
                BalanceClass = balance > 0 ? "finance-positive" : balance < 0 ? "finance-negative" : ""
            });
        }

        return rows;
    }

    // ساختار داخلی فقط برای ساخت صورت‌حساب (قبل از محاسبهٔ مانده تجمعی).
    private sealed class StatementEvent
    {
        public DateTime Date { get; init; }
        public int Order { get; init; }
        public int SourceRouteId { get; init; }
        public string Type { get; init; } = string.Empty;
        public string? Reference { get; init; }
        public string? ContractNumber { get; init; }
        public string Description { get; init; } = string.Empty;
        public string SourceController { get; init; } = string.Empty;
        public string SourceAction { get; init; } = string.Empty;
        public int LedgerEntryId { get; init; }
        public decimal? SourceAmount { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal? FxRateUsed { get; init; }
        public decimal? CreditUsd { get; init; }
        public decimal? DebitUsd { get; init; }
    }

    private sealed class SarrafViaPaymentStatementItem
    {
        public int LedgerEntryId { get; init; }
        public DateTime EntryDate { get; init; }
        public decimal Amount { get; init; }
        public string Currency { get; init; } = "USD";
        public decimal AmountUsd { get; init; }
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public string? ContractNumber { get; init; }
    }

    private static DateTime? LatestDate(DateTime? first, DateTime? second)
        => first.HasValue && second.HasValue
            ? (first.Value >= second.Value ? first : second)
            : first ?? second;

    [HttpGet]
    public async Task<IActionResult> GetCloneData(int id)
    {
        var item = await _db.Sarrafs.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.Name, s.PhoneNumber, s.IsActive })
            .FirstOrDefaultAsync();
        if (item == null) return NotFound();
        return Json(item);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
