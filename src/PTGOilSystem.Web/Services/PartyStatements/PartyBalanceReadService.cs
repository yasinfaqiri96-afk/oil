using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Parties;

namespace PTGOilSystem.Web.Services.PartyStatements;

public interface IPartyBalanceReadService
{
    /// <param name="partyTypes">
    /// اگر داده شود، فقط ماندهٔ همین نوع‌های طرف‌حساب ساخته می‌شود و منابعی که هیچ‌کدام از
    /// این نوع‌ها را نمی‌سازند اصلاً از دیتابیس خوانده نمی‌شوند. این فقط «کمتر خواندن» است،
    /// نه «جور دیگر حساب کردن»: هر منبع فقط نوعِ خودش را تولید می‌کند (صراف → صراف،
    /// کارمند → کارمند، شریک → شریک)، پس حذفِ منبعی که در فهرست نیست روی عددِ هیچ ردیفِ
    /// باقی‌مانده اثر ندارد. null یعنی همان رفتار قبلی: همهٔ نوع‌ها.
    /// </param>
    Task<IReadOnlyList<PartyBalanceSnapshot>> GetBalancesAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default,
        IReadOnlyCollection<PartyStatementPartyType>? partyTypes = null);

    /// <summary>
    /// ماندهٔ رسمیِ هر طرف‌حساب در دامنهٔ هر قرارداد (همان موتور، همان قواعدِ جهت و انتساب).
    /// «ماندهٔ قرارداد» جمعِ همین ردیف‌هاست. همهٔ صفحه‌ها، بستن قرارداد و گزارش‌ها از همین
    /// می‌خوانند. علامت: مثبت = طلب شرکت، منفی = بدهی شرکت.
    /// </summary>
    /// <param name="asOfDate">اگر داده شود، مانده تا پایانِ همین روز (اسنادِ بعد از آن شمرده نمی‌شوند).</param>
    /// <param name="resolveNames">نامِ طرف‌حساب فقط وقتی خوانده می‌شود که مصرف‌کننده لازمش دارد.</param>
    Task<IReadOnlyDictionary<int, ContractBalanceSummary>> GetContractBalancesAsync(
        IReadOnlyCollection<int> contractIds,
        DateTime? asOfDate = null,
        CancellationToken ct = default,
        bool resolveNames = true);
}

public sealed record ContractPartyBalance(
    int ContractId,
    PartyStatementPartyType PartyType,
    int PartyId,
    string PartyName,
    decimal ClosingBalanceUsd,
    decimal TotalOutflowUsd = 0m,
    decimal TotalReceiptUsd = 0m);

/// <summary>ماندهٔ یک قرارداد به تفکیکِ طرف‌حساب. علامت: مثبت = طلب شرکت، منفی = بدهی شرکت.</summary>
public sealed record ContractBalanceSummary(int ContractId, IReadOnlyList<ContractPartyBalance> Parties)
{
    public decimal NetBalanceUsd => Parties.Sum(p => p.ClosingBalanceUsd);
    public decimal ReceivableUsd => Parties.Where(p => p.ClosingBalanceUsd > 0m).Sum(p => p.ClosingBalanceUsd);
    public decimal PayableUsd => -Parties.Where(p => p.ClosingBalanceUsd < 0m).Sum(p => p.ClosingBalanceUsd);
    /// <summary>جمعِ «برد» (داده‌شده) و «رسید» (گرفته‌شده)؛ مانده = برد − رسید.</summary>
    public decimal TotalOutflowUsd => Parties.Sum(p => p.TotalOutflowUsd);
    public decimal TotalReceiptUsd => Parties.Sum(p => p.TotalReceiptUsd);
}

public sealed record PartyBalanceSnapshot(
    PartyStatementPartyType PartyType,
    int PartyId,
    string PartyName,
    decimal OpeningBalanceUsd,
    decimal TotalReceiptUsd,
    decimal TotalOutflowUsd,
    decimal PeriodMovementUsd,
    decimal ClosingBalanceUsd,
    DateTime? LastEntryDate,
    string BalanceMeaning,
    string? DetailsController);

/// <summary>
/// «طرف‌حساب بیرونی» یعنی کسی که واقعاً می‌تواند به شرکت بدهکار یا از شرکت طلبکار باشد:
/// مشتری، تأمین‌کننده، شرکت خدماتی، صراف، راننده، کارمند و شریک.
///
/// ردیف‌های <see cref="PartyStatementPartyType.Company"/> بیرون می‌مانند. آن ردیف‌ها حساب
/// جاریِ خودِ جوازهای شرکت‌اند (از جمله شرکت مالک سیستم) و از روی CompanyId هر سند دفتر
/// ساخته می‌شوند، نه از یک طرف معامله. شرکت نمی‌تواند به خودش بدهکار یا از خودش طلبکار
/// باشد، پس آوردنِ آن در «طلبات و بدهی‌ها» عدد را باد می‌کند و مدیر را گمراه می‌کند.
///
/// خودِ صورت‌حساب شرکت جای خودش محفوظ است: صفحهٔ Companies/Details آن را از
/// <see cref="IPartyStatementReadService"/> می‌خواند و این فیلتر به آن کاری ندارد.
/// </summary>
public static class PartyBalanceSnapshotFilters
{
    public static IReadOnlyList<PartyBalanceSnapshot> ExternalPartiesOnly(
        this IEnumerable<PartyBalanceSnapshot> rows)
        => rows.Where(row => row.PartyType != PartyStatementPartyType.Company).ToList();
}

/// <summary>
/// Bulk balance reader used by management balance reports. It shares the same
/// direction resolver, party policies and closing formula as the official party
/// statement, while keeping query count bounded instead of opening one statement
/// per party.
/// </summary>
public sealed class PartyBalanceReadService : IPartyBalanceReadService
{
    private readonly ApplicationDbContext _db;
    private readonly IPartyStatementPolicyResolver _policies;
    private readonly ICompanyFlowDirectionResolver _directions;
    private readonly ICompanyFlowBalanceService _balances;
    private readonly IPartyDirectory _parties;

    /// <summary>تنها منبعِ ماندهٔ شریک. این گزارش فرمول جداگانه‌ای برای شریک ندارد.</summary>
    private readonly IPartnershipStatementService _partnerships;

    /// <summary>همان سرویسِ ثبت‌شده در DI با قواعدِ پیش‌فرض، برای جایی که سرویس تزریق نشده است.</summary>
    public static PartyBalanceReadService CreateDefault(ApplicationDbContext db)
        => new(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            new PartyDirectory(db));

    public PartyBalanceReadService(
        ApplicationDbContext db,
        IPartyStatementPolicyResolver policies,
        ICompanyFlowDirectionResolver directions,
        ICompanyFlowBalanceService balances,
        IPartyDirectory parties,
        IPartnershipStatementService? partnerships = null)
    {
        _db = db;
        _policies = policies;
        _directions = directions;
        _balances = balances;
        _parties = parties;
        _partnerships = partnerships ?? new PartnershipStatementService(db);
    }

    public async Task<IReadOnlyList<PartyBalanceSnapshot>> GetBalancesAsync(
        ManagementReportFilterViewModel filter,
        CancellationToken ct = default,
        IReadOnlyCollection<PartyStatementPartyType>? partyTypes = null)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // فهرست خالی با null یکی نیست: null یعنی «همه»، خالی یعنی «هیچ‌کدام».
        bool Wanted(PartyStatementPartyType type) => partyTypes is null || partyTypes.Contains(type);

        var events = new List<BalanceEvent>();

        // مسیر دفتر تنها منبعِ مشتری/تأمین‌کننده/شرکت خدماتی/راننده/جواز است؛ اگر هیچ‌کدام
        // خواسته نشده باشند خواندنش هم لازم نیست.
        if (Wanted(PartyStatementPartyType.Customer)
            || Wanted(PartyStatementPartyType.Supplier)
            || Wanted(PartyStatementPartyType.ServiceProvider)
            || Wanted(PartyStatementPartyType.Driver)
            || Wanted(PartyStatementPartyType.Company))
        {
            await AddLedgerEventsAsync(events, filter, ct);
        }

        if (!filter.CustomerId.HasValue && !filter.SupplierId.HasValue)
        {
            if (Wanted(PartyStatementPartyType.Sarraf)) await AddSarrafEventsAsync(events, filter, ct);
            if (Wanted(PartyStatementPartyType.Employee)) await AddEmployeeEventsAsync(events, filter, ct);
            if (Wanted(PartyStatementPartyType.Partner)) await AddPartnerEventsAsync(events, filter, ct);
        }

        if (partyTypes is not null)
        {
            events.RemoveAll(e => !partyTypes.Contains(e.PartyType));
        }

        var names = await _parties.GetNamesAsync(
            events.Select(e => new PartyKey(e.PartyType, e.PartyId)).Distinct().ToArray(),
            ct);
        var from = filter.FromDate?.Date;

        return events
            .GroupBy(e => new { e.PartyType, e.PartyId })
            .Select(group =>
            {
                var openingRows = from.HasValue
                    ? group.Where(e => e.Date < from.Value)
                    : [];
                var periodRows = from.HasValue
                    ? group.Where(e => e.Date >= from.Value)
                    : group;

                var opening = openingRows.Sum(e => _balances.SignedEffect(
                    e.Direction,
                    e.AmountUsd,
                    CompanyFlowAccountKind.PartyAccount));
                var summary = _balances.Summarize(
                    opening,
                    periodRows.Select(e => new CompanyFlowAmount(e.Direction, e.AmountUsd)),
                    CompanyFlowAccountKind.PartyAccount);
                var policy = _policies.Resolve(group.Key.PartyType);

                return new PartyBalanceSnapshot(
                    group.Key.PartyType,
                    group.Key.PartyId,
                    names.GetValueOrDefault(new PartyKey(group.Key.PartyType, group.Key.PartyId), "-"),
                    summary.OpeningBalance,
                    summary.TotalReceipt,
                    summary.TotalOutflow,
                    summary.NetMovement,
                    summary.ClosingBalance,
                    // فقط ردیف‌های دارای تاریخ واقعی: تاریخِ جایگزینِ ردیفِ بی‌تاریخ نباید
                    // «آخرین حرکت» شود. اگر هیچ ردیفِ تاریخ‌داری نباشد، نتیجه null است
                    // یعنی «بدون حرکت» — نه ۰۰۰۱/۰۱/۰۱.
                    group.Where(e => e.IsDateKnown).Max(e => (DateTime?)e.Date),
                    policy.BalanceMeaning(summary.ClosingBalance, isEnglish: false),
                    _parties.DetailsController(group.Key.PartyType));
            })
            .Where(row => row.OpeningBalanceUsd != 0m
                || row.TotalReceiptUsd != 0m
                || row.TotalOutflowUsd != 0m)
            .OrderByDescending(row => Math.Abs(row.ClosingBalanceUsd))
            .ThenBy(row => row.PartyName)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<int, ContractBalanceSummary>> GetContractBalancesAsync(
        IReadOnlyCollection<int> contractIds,
        DateTime? asOfDate = null,
        CancellationToken ct = default,
        bool resolveNames = true)
    {
        ArgumentNullException.ThrowIfNull(contractIds);
        var ids = contractIds.Distinct().ToArray();
        var events = new List<BalanceEvent>();
        if (ids.Length > 0)
        {
            await AddContractLedgerEventsAsync(events, ids, asOfDate, ct);
            await AddSarrafEventsAsync(events, new ManagementReportFilterViewModel { ToDate = asOfDate }, ct, ids);
        }

        IReadOnlyDictionary<PartyKey, string> names = resolveNames
            ? await _parties.GetNamesAsync(
                events.Select(e => new PartyKey(e.PartyType, e.PartyId)).Distinct().ToArray(),
                ct)
            : new Dictionary<PartyKey, string>();

        var result = ids.ToDictionary(
            id => id,
            id => new ContractBalanceSummary(id, []));
        foreach (var contract in events.Where(e => e.ContractId.HasValue).GroupBy(e => e.ContractId!.Value))
        {
            var parties = contract
                .GroupBy(e => new { e.PartyType, e.PartyId })
                .Select(party =>
                {
                    var summary = _balances.Summarize(
                        0m,
                        party.Select(e => new CompanyFlowAmount(e.Direction, e.AmountUsd)),
                        CompanyFlowAccountKind.PartyAccount);
                    return new ContractPartyBalance(
                        contract.Key,
                        party.Key.PartyType,
                        party.Key.PartyId,
                        names.GetValueOrDefault(new PartyKey(party.Key.PartyType, party.Key.PartyId), "-"),
                        summary.ClosingBalance,
                        summary.TotalOutflow,
                        summary.TotalReceipt);
                })
                .OrderByDescending(p => Math.Abs(p.ClosingBalanceUsd))
                .ThenBy(p => p.PartyName)
                .ToList();
            result[contract.Key] = new ContractBalanceSummary(contract.Key, parties);
        }

        return result;
    }

    private async Task AddLedgerEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var query = _db.LedgerEntries.AsNoTracking()
            .WhereEffectiveSarrafSettlementLegs(_db);
        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < end);
        }
        if (filter.ContractId.HasValue)
        {
            var contractId = filter.ContractId.Value;
            query = query.Where(l => l.ContractId == contractId
                || (l.SourceType == CompanyFlowSourceTypes.Sale
                    && _db.SalesTransactions.Any(s => s.Id == l.SourceId && s.ContractId == contractId)));
        }

        // مانده فقط جمعِ هر ترکیب است، نه تک‌تکِ سطرها. پس جمع در PostgreSQL انجام می‌شود و
        // فقط ترکیب‌های متمایز خوانده می‌شوند. کلیدِ گروه هر چیزی را دارد که روی جهتِ سطر
        // اثر می‌گذارد (طرف‌حساب، SourceType، Side، نشانهٔ برگشت) به‌علاوهٔ اینکه سطر پیش از
        // شروع دوره است یا داخل آن. چون مبلغِ هر سطر با Math.Abs وارد می‌شد، اینجا هم
        // sum(abs(...)) گرفته می‌شود تا عدد دقیقاً همان بماند.
        var from = filter.FromDate?.Date;
        var projected = ProjectLedgerRows(query, from);

        var rows = await projected
            .GroupBy(r => new
            {
                r.EffectiveCustomerId,
                r.EffectiveSupplierId,
                r.ServiceProviderId,
                r.DriverId,
                r.EffectiveCompanyId,
                r.Side,
                r.SourceType,
                r.HasReversalReference,
                r.IsOpening
            })
            .Select(g => new
            {
                g.Key.EffectiveCustomerId,
                g.Key.EffectiveSupplierId,
                g.Key.ServiceProviderId,
                g.Key.DriverId,
                g.Key.EffectiveCompanyId,
                g.Key.Side,
                g.Key.SourceType,
                g.Key.HasReversalReference,
                // تاریخِ نمایندهٔ گروه. چون گروه‌ها با IsOpening جدا شده‌اند، بزرگ‌ترین تاریخِ
                // گروهِ «پیش از دوره» هم هنوز پیش از دوره است و تقسیمِ اول‌دوره/داخل‌دوره
                // دست‌نخورده می‌ماند. «آخرین حرکت» هم بیشینهٔ همین بیشینه‌هاست.
                EntryDate = g.Max(r => r.EntryDate),
                AmountUsd = g.Sum(r => Math.Abs(r.AmountUsd))
            })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            // مرجعِ نماینده: فقط باید همان پاسخِ IsReversalReference را بدهد که سطرهای
            // این گروه می‌دادند. متنِ اصلی در محاسبهٔ مانده نقشی ندارد.
            var reference = row.HasReversalReference
                ? CompanyFlowSourceTypes.ReversalReferenceSuffix
                : null;

            if (row.EffectiveCustomerId.HasValue
                && (!filter.CustomerId.HasValue || row.EffectiveCustomerId == filter.CustomerId.Value)
                && !filter.SupplierId.HasValue)
            {
                AddLedgerEvent(target, row.EffectiveCustomerId.Value, PartyStatementPartyType.Customer,
                    CompanyFlowPartyRole.Customer, row.EntryDate, row.Side, row.AmountUsd, row.SourceType, reference);
            }

            if (row.EffectiveSupplierId.HasValue
                && (!filter.SupplierId.HasValue || row.EffectiveSupplierId == filter.SupplierId.Value)
                && !filter.CustomerId.HasValue)
            {
                AddLedgerEvent(target, row.EffectiveSupplierId.Value, PartyStatementPartyType.Supplier,
                    CompanyFlowPartyRole.Supplier, row.EntryDate, row.Side, row.AmountUsd, row.SourceType, reference);
            }

            if (!filter.CustomerId.HasValue && !filter.SupplierId.HasValue)
            {
                if (row.ServiceProviderId.HasValue)
                {
                    AddLedgerEvent(target, row.ServiceProviderId.Value, PartyStatementPartyType.ServiceProvider,
                        CompanyFlowPartyRole.ServiceProvider, row.EntryDate, row.Side, row.AmountUsd, row.SourceType, reference);
                }
                if (row.DriverId.HasValue)
                {
                    AddLedgerEvent(target, row.DriverId.Value, PartyStatementPartyType.Driver,
                        CompanyFlowPartyRole.Driver, row.EntryDate, row.Side, row.AmountUsd, row.SourceType, reference);
                }
                if (row.EffectiveCompanyId.HasValue)
                {
                    AddLedgerEvent(target, row.EffectiveCompanyId.Value, PartyStatementPartyType.Company,
                        CompanyFlowPartyRole.Company, row.EntryDate, row.Side, row.AmountUsd, row.SourceType, reference);
                }
            }
        }
    }

    /// <summary>
    /// انتسابِ هر سطرِ دفتر به طرف‌حساب — تنها تعریفِ «این سطر مالِ کیست». ماندهٔ طرف‌حساب
    /// (<see cref="GetBalancesAsync"/>) و ماندهٔ قرارداد (<see cref="GetContractBalancesAsync"/>)
    /// هر دو از همین نگاشت می‌خوانند تا دو فرمولِ جدا ساخته نشود. عضوی که در کلیدِ گروهِ
    /// مصرف‌کننده نیاید، اصلاً به SQL ترجمه نمی‌شود.
    /// </summary>
    private IQueryable<LedgerBalanceRow> ProjectLedgerRows(IQueryable<LedgerEntry> query, DateTime? from)
        => query.Select(l => new LedgerBalanceRow
            {
                EntryDate = l.EntryDate,
                Side = l.Side,
                AmountUsd = l.AmountUsd,
                SourceType = l.SourceType,
                // فقط «مرجع نشانهٔ برگشت دارد یا نه» لازم است، نه خودِ متنِ مرجع. قاعدهٔ
                // تشخیص برگشت یک‌جا در CompanyFlowSourceTypes می‌ماند و اینجا تکرار نمی‌شود.
                HasReversalReference = l.Reference != null
                    && l.Reference.EndsWith(CompanyFlowSourceTypes.ReversalReferenceSuffix),
                IsOpening = from.HasValue && l.EntryDate < from.Value,
                SourceId = l.SourceId,
                CustomerId = l.CustomerId,
                // انتساب مشتری قرینهٔ تأمین‌کننده است: هزینه و اسناد نقدیِ بدون طرف‌حساب از راهِ
                // قرارداد به مشتری نمی‌چسبند. رجوع: LedgerEntryOwnership.CustomerOwnedByContract.
                EffectiveCustomerId = l.CustomerId
                    ?? (l.SupplierId == null
                        && l.ServiceProviderId == null
                        && l.DriverId == null
                        && l.EmployeeId == null
                        && l.SourceType != LedgerEntryOwnership.ExpenseSourceType
                        && !LedgerEntryOwnership.CashSourceTypesWithoutContractParty.Contains(l.SourceType)
                        && l.Contract != null
                        && l.Contract.ContractType == ContractType.Sale
                            ? l.Contract.CustomerId
                            : null)
                    ?? (l.SourceType == CompanyFlowSourceTypes.Sale
                        ? _db.SalesTransactions
                            .Where(s => s.Id == l.SourceId)
                            .Select(s => (int?)s.CustomerId)
                            .FirstOrDefault()
                        : null),
                EffectiveSupplierId = l.SourceType == LedgerEntryOwnership.ViaSarrafPayableSourceType
                    ? null
                    : l.SupplierId
                        // AUD-04: هزینه بدون SupplierId صریح از راهِ قرارداد به تأمین‌کننده نمی‌چسبد.
                        ?? (l.SupplierId == null
                            && l.SourceType != LedgerEntryOwnership.ExpenseSourceType
                            // فروش مالِ مشتریِ سند فروش است، نه تأمین‌کنندهٔ قراردادِ منبع.
                            && l.SourceType != LedgerEntryOwnership.SaleSourceType
                            // AUD-05: پرداختِ نقدیِ بدون طرف‌حساب هم از راهِ قرارداد به
                            // تأمین‌کننده نمی‌چسبد (کرایه موتر، پرداخت هزینه، کمیسیون …).
                            && !LedgerEntryOwnership.CashSourceTypesWithoutContractParty.Contains(l.SourceType)
                            && l.ServiceProviderId == null
                            && l.DriverId == null
                            && l.CustomerId == null
                            && l.EmployeeId == null
                            && l.Contract != null
                            && l.Contract.ContractType == ContractType.Purchase
                                ? l.Contract.SupplierId
                                : null),
                ServiceProviderId = l.ServiceProviderId,
                DriverId = l.DriverId,
                // حساب جاریِ جواز. پرداختی که شریک از جیب خودش داده صندوق شرکت را حرکت
                // نداده، پس خروجِ پولِ شرکت نیست و اینجا شمرده نمی‌شود — وگرنه همان خرج
                // یک بار به‌عنوان مصرف و یک بار به‌عنوان پرداختِ شرکت دو بار می‌آید.
                // رجوع: PartyStatementReadService، شاخهٔ Company.
                EffectiveCompanyId = _db.PaymentTransactions.Any(p =>
                        p.LedgerEntryId == l.Id && p.FundingSource == PaymentFundingSource.Partner)
                    ? null
                    : l.Contract != null
                        ? (int?)l.Contract.CompanyId
                        : l.SourceType == CompanyFlowSourceTypes.Sale
                            ? _db.SalesTransactions
                                .Where(s => s.Id == l.SourceId)
                                .Select(s => s.CompanyId)
                                .FirstOrDefault()
                            : null,
                EmployeeId = l.EmployeeId,
                PartnerId = l.PartnerId,
                ContractId = l.ContractId,
                // فروشی که قراردادِ سندِ دفترش خالی یا قراردادِ دیگری است، از راهِ خودِ فروش به
                // قراردادِ فروش می‌رسد (همان قاعدهٔ فیلترِ قرارداد در GetBalancesAsync).
                SaleContractId = l.SourceType == CompanyFlowSourceTypes.Sale
                    ? _db.SalesTransactions
                        .Where(s => s.Id == l.SourceId)
                        .Select(s => s.ContractId)
                        .FirstOrDefault()
                    : null
            });

    private async Task AddContractLedgerEventsAsync(
        List<BalanceEvent> target,
        int[] contractIds,
        DateTime? asOfDate,
        CancellationToken ct)
    {
        var query = _db.LedgerEntries.AsNoTracking()
            .WhereEffectiveSarrafSettlementLegs(_db)
            .Where(l => (l.ContractId != null && contractIds.Contains(l.ContractId.Value))
                || (l.SourceType == CompanyFlowSourceTypes.Sale
                    && _db.SalesTransactions.Any(s => s.Id == l.SourceId
                        && s.ContractId != null
                        && contractIds.Contains(s.ContractId.Value))));
        if (asOfDate.HasValue)
        {
            var end = asOfDate.Value.Date.AddDays(1);
            query = query.Where(l => l.EntryDate < end);
        }

        var rows = await ProjectLedgerRows(query, from: null)
            .GroupBy(r => new
            {
                r.ContractId,
                r.SaleContractId,
                r.EffectiveCustomerId,
                r.EffectiveSupplierId,
                r.ServiceProviderId,
                r.DriverId,
                r.EmployeeId,
                r.PartnerId,
                r.Side,
                r.SourceType,
                r.HasReversalReference
            })
            .Select(g => new
            {
                g.Key.ContractId,
                g.Key.SaleContractId,
                g.Key.EffectiveCustomerId,
                g.Key.EffectiveSupplierId,
                g.Key.ServiceProviderId,
                g.Key.DriverId,
                g.Key.EmployeeId,
                g.Key.PartnerId,
                g.Key.Side,
                g.Key.SourceType,
                g.Key.HasReversalReference,
                EntryDate = g.Max(r => r.EntryDate),
                AmountUsd = g.Sum(r => Math.Abs(r.AmountUsd))
            })
            .ToListAsync(ct);

        var requested = contractIds.ToHashSet();
        foreach (var row in rows)
        {
            var reference = row.HasReversalReference
                ? CompanyFlowSourceTypes.ReversalReferenceSuffix
                : null;
            // یک سطر ممکن است هم با ContractId خودش و هم از راهِ فروش به قراردادِ دیگری برسد؛
            // در هر قرارداد دقیقاً یک بار شمرده می‌شود (همان نتیجهٔ فیلترِ تک‌قرارداد).
            var keys = new List<int>(2);
            if (row.ContractId is int direct && requested.Contains(direct))
            {
                keys.Add(direct);
            }
            if (row.SaleContractId is int viaSale && requested.Contains(viaSale) && row.ContractId != viaSale)
            {
                keys.Add(viaSale);
            }

            foreach (var contractId in keys)
            {
                void Add(int? partyId, PartyStatementPartyType type, CompanyFlowPartyRole role)
                {
                    if (partyId.HasValue)
                    {
                        AddLedgerEvent(target, partyId.Value, type, role, row.EntryDate, row.Side,
                            row.AmountUsd, row.SourceType, reference, contractId);
                    }
                }

                Add(row.EffectiveCustomerId, PartyStatementPartyType.Customer, CompanyFlowPartyRole.Customer);
                Add(row.EffectiveSupplierId, PartyStatementPartyType.Supplier, CompanyFlowPartyRole.Supplier);
                Add(row.ServiceProviderId, PartyStatementPartyType.ServiceProvider, CompanyFlowPartyRole.ServiceProvider);
                Add(row.DriverId, PartyStatementPartyType.Driver, CompanyFlowPartyRole.Driver);
                Add(row.EmployeeId, PartyStatementPartyType.Employee, CompanyFlowPartyRole.Employee);
                Add(row.PartnerId, PartyStatementPartyType.Partner, CompanyFlowPartyRole.Partner);
            }
        }
    }

    private sealed class LedgerBalanceRow
    {
        public DateTime EntryDate { get; init; }
        public LedgerSide Side { get; init; }
        public decimal AmountUsd { get; init; }
        public string SourceType { get; init; } = "";
        public bool HasReversalReference { get; init; }
        public bool IsOpening { get; init; }
        public int SourceId { get; init; }
        public int? CustomerId { get; init; }
        public int? EffectiveCustomerId { get; init; }
        public int? EffectiveSupplierId { get; init; }
        public int? ServiceProviderId { get; init; }
        public int? DriverId { get; init; }
        public int? EmployeeId { get; init; }
        public int? PartnerId { get; init; }
        public int? EffectiveCompanyId { get; init; }
        public int? ContractId { get; init; }
        public int? SaleContractId { get; init; }
    }

    private void AddLedgerEvent(
        List<BalanceEvent> target,
        int partyId,
        PartyStatementPartyType partyType,
        CompanyFlowPartyRole role,
        DateTime date,
        LedgerSide side,
        decimal amountUsd,
        string sourceType,
        string? reference = null,
        int? contractId = null)
    {
        // مرجع هم خوانده می‌شود: برگشتِ بارگیری/فروش/مصرف SourceType اصلی را نگه می‌دارد و
        // تنها با پسوندِ مرجع علامت می‌خورد. رجوع: CompanyFlowSourceTypes.ReversalReferenceSuffix.
        var lifecycle = CompanyFlowSourceTypes.IsReversal(sourceType, reference)
            ? CompanyFlowLifecycle.Reversal
            : CompanyFlowLifecycle.Original;
        var direction = _directions.Resolve(new CompanyFlowEvent(sourceType, side, role, lifecycle));
        target.Add(new BalanceEvent(partyType, partyId, date, direction, Math.Abs(amountUsd), ContractId: contractId));
    }

    private async Task AddSarrafEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct,
        int[]? contractIds = null)
    {
        var payments = _db.PaymentTransactions.AsNoTracking().Where(p => p.SarrafId.HasValue);
        var settlements = _db.SarrafSettlements.AsNoTracking()
            .Where(s => s.Status == SarrafSettlementStatus.Posted);
        var via = _db.LedgerEntries.AsNoTracking()
            .Where(l => l.SourceType == LedgerEntryOwnership.ViaSarrafPayableSourceType);

        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            payments = payments.Where(p => p.PaymentDate < end);
            settlements = settlements.Where(s => s.SettlementDate < end);
            via = via.Where(l => l.EntryDate < end);
        }
        if (filter.ContractId.HasValue)
        {
            payments = payments.Where(p => p.ContractId == filter.ContractId.Value);
            settlements = settlements.Where(s => s.ContractId == filter.ContractId.Value);
            via = via.Where(l => l.ContractId == filter.ContractId.Value);
        }
        if (contractIds is not null)
        {
            payments = payments.Where(p => p.ContractId != null && contractIds.Contains(p.ContractId.Value));
            settlements = settlements.Where(s => s.ContractId != null && contractIds.Contains(s.ContractId.Value));
            via = via.Where(l => l.ContractId != null && contractIds.Contains(l.ContractId.Value));
        }

        // همان قاعدهٔ تجمیعِ مسیر دفتر: جمع در PostgreSQL، کلیدِ گروه هر چیزی که روی جهت
        // اثر دارد، به‌علاوهٔ «پیش از شروع دوره یا داخل آن».
        var from = filter.FromDate?.Date;

        var paymentRows = await payments
            .GroupBy(p => new
            {
                SarrafId = p.SarrafId!.Value,
                p.Direction,
                p.ContractId,
                IsOpening = from.HasValue && p.PaymentDate < from.Value
            })
            .Select(g => new
            {
                g.Key.SarrafId,
                g.Key.Direction,
                g.Key.ContractId,
                PaymentDate = g.Max(p => p.PaymentDate),
                AmountUsd = g.Sum(p => Math.Abs(p.AmountUsd))
            })
            .ToListAsync(ct);
        target.AddRange(paymentRows.Select(p => new BalanceEvent(
            PartyStatementPartyType.Sarraf,
            p.SarrafId,
            p.PaymentDate,
            p.Direction == PaymentDirection.Out
                ? CompanyFlowDirection.Outflow
                : CompanyFlowDirection.Receipt,
            Math.Abs(p.AmountUsd),
            ContractId: p.ContractId)));

        var settlementRows = await settlements
            .GroupBy(s => new
            {
                s.SarrafId,
                s.Direction,
                s.ContractId,
                IsOpening = from.HasValue && s.SettlementDate < from.Value
            })
            .Select(g => new
            {
                g.Key.SarrafId,
                g.Key.Direction,
                g.Key.ContractId,
                SettlementDate = g.Max(s => s.SettlementDate),
                SarrafChargedAmountUsd = g.Sum(s => Math.Abs(s.SarrafChargedAmountUsd))
            })
            .ToListAsync(ct);
        target.AddRange(settlementRows.Select(s => new BalanceEvent(
            PartyStatementPartyType.Sarraf,
            s.SarrafId,
            s.SettlementDate,
            s.Direction == SarrafSettlementDirection.Out
                ? CompanyFlowDirection.Receipt
                : CompanyFlowDirection.Outflow,
            Math.Abs(s.SarrafChargedAmountUsd),
            ContractId: s.ContractId)));

        var viaRows = await via
            .GroupBy(l => new
            {
                SarrafId = l.SourceId,
                l.ContractId,
                l.Side,
                l.SourceType,
                HasReversalReference = l.Reference != null
                    && l.Reference.EndsWith(CompanyFlowSourceTypes.ReversalReferenceSuffix),
                IsOpening = from.HasValue && l.EntryDate < from.Value
            })
            .Select(g => new
            {
                g.Key.SarrafId,
                g.Key.ContractId,
                g.Key.Side,
                g.Key.SourceType,
                g.Key.HasReversalReference,
                EntryDate = g.Max(l => l.EntryDate),
                AmountUsd = g.Sum(l => Math.Abs(l.AmountUsd))
            })
            .ToListAsync(ct);
        foreach (var row in viaRows)
        {
            var reference = row.HasReversalReference
                ? CompanyFlowSourceTypes.ReversalReferenceSuffix
                : null;
            AddLedgerEvent(target, row.SarrafId, PartyStatementPartyType.Sarraf,
                CompanyFlowPartyRole.Sarraf, row.EntryDate, row.Side, row.AmountUsd, row.SourceType, reference,
                row.ContractId);
        }
    }

    private async Task AddEmployeeEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        var query = _db.EmployeeSalaryTransactions.AsNoTracking().Where(t => !t.IsCancelled);
        if (filter.ToDate.HasValue)
        {
            var end = filter.ToDate.Value.Date.AddDays(1);
            query = query.Where(t => t.TransactionDate < end);
        }

        // علامتِ مبلغ خودش Side را تعیین می‌کند، پس وارد کلید گروه می‌شود تا جمعِ هر گروه
        // دقیقاً همان جمعِ سطرهای هم‌جهت باشد.
        var from = filter.FromDate?.Date;
        var rows = await query
            .GroupBy(t => new
            {
                t.EmployeeId,
                t.TransactionType,
                IsCredit = t.AmountUsd >= 0m,
                IsOpening = from.HasValue && t.TransactionDate < from.Value
            })
            .Select(g => new
            {
                g.Key.EmployeeId,
                g.Key.TransactionType,
                g.Key.IsCredit,
                TransactionDate = g.Max(t => t.TransactionDate),
                AmountUsd = g.Sum(t => Math.Abs(t.AmountUsd))
            })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var side = row.IsCredit ? LedgerSide.Credit : LedgerSide.Debit;
            var direction = _directions.Resolve(new CompanyFlowEvent(
                row.TransactionType.ToString(),
                side,
                CompanyFlowPartyRole.Employee));
            target.Add(new BalanceEvent(
                PartyStatementPartyType.Employee,
                row.EmployeeId,
                row.TransactionDate,
                direction,
                Math.Abs(row.AmountUsd)));
        }
    }

    /// <summary>
    /// ماندهٔ شریک — از همان <see cref="IPartnershipStatementService"/> که پروفایل شریک و
    /// صورت‌حساب شراکت می‌خوانند. این گزارش فرمول جداگانه‌ای برای شریک ندارد.
    ///
    /// پیش از این همین متد سهمِ درصدیِ هر سطرِ لجرِ قرارداد را می‌گرفت. آن محاسبه نه
    /// <see cref="Contract.SaleProceedsHolderPartnerId"/> را می‌شناخت و نه
    /// <see cref="PartnerSettlement"/> را، پس ماندهٔ همین شریک اینجا با پروفایل او
    /// دقیقاً به اندازهٔ کلِ عایدِ فروشِ نزد یک شریک فرق می‌کرد.
    ///
    /// اثرِ هر ردیف (<see cref="PartnerAccountEntry.EffectUsd"/>) خودش جزءِ فرمول مانده است:
    /// مثبت = شریک ارزشی آورده (برد)، منفی = ارزشی به او رسیده (رسید). بیلانسِ حسابِ
    /// طرف‌حساب «اول دوره + Σبرد − Σرسید» است، پس ماندهٔ نهایی همان
    /// <see cref="PartnerAccountStatement.NetPositionUsd"/> در می‌آید:
    /// مثبت = شریک طلبکار، منفی = شریک بدهکار.
    /// </summary>
    private async Task AddPartnerEventsAsync(
        List<BalanceEvent> target,
        ManagementReportFilterViewModel filter,
        CancellationToken ct)
    {
        // فقط قراردادهایی که واقعاً شراکتی‌اند. دامنه با صورت‌حساب شراکت یکی است.
        var membershipQuery = _db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.Contract != null
                && cp.Contract.OwnershipType == ContractOwnershipType.Partnership);
        if (filter.ContractId.HasValue)
        {
            membershipQuery = membershipQuery.Where(cp => cp.ContractId == filter.ContractId.Value);
        }

        var partnerIds = await membershipQuery
            .Select(cp => cp.PartnerId)
            .Distinct()
            .ToListAsync(ct);
        if (partnerIds.Count == 0)
        {
            return;
        }

        int[]? contractIds = filter.ContractId.HasValue ? [filter.ContractId.Value] : null;
        var toDate = filter.ToDate?.Date;
        // ردیفِ بی‌تاریخ فقط سهمِ مفادِ قراردادی است که هنوز فروشی ندارد. به ابتدای
        // دوره نسبت داده می‌شود تا هیچ‌وقت خاموش از جمع حذف نشود.
        var undatedFallback = filter.FromDate?.Date ?? DateTime.MinValue;

        // یک بار برای همهٔ شرکا. پیش از این به ازای هر شریک یک صورت‌حساب کامل ساخته می‌شد و
        // چون دامنهٔ قراردادها یکی بود، همان کوئری‌ها عیناً تکرار می‌شدند. فرمول عوض نشده:
        // همان IPartnershipStatementService و همان Entries، فقط یک بار خوانده می‌شود.
        var statements = await _partnerships.BuildForPartnersAsync(partnerIds, contractIds, ct);

        foreach (var partnerId in partnerIds)
        {
            if (!statements.TryGetValue(partnerId, out var statement) || statement is null)
            {
                continue;
            }

            foreach (var entry in statement.Entries)
            {
                if (toDate.HasValue && entry.Date.HasValue && entry.Date.Value.Date > toDate.Value)
                {
                    continue;
                }

                if (entry.EffectUsd == 0m)
                {
                    continue;
                }

                target.Add(new BalanceEvent(
                    PartyStatementPartyType.Partner,
                    partnerId,
                    entry.Date?.Date ?? undatedFallback,
                    entry.EffectUsd < 0m
                        ? CompanyFlowDirection.Receipt
                        : CompanyFlowDirection.Outflow,
                    Math.Abs(entry.EffectUsd),
                    IsDateKnown: entry.Date.HasValue));
            }
        }
    }

    /// <param name="IsDateKnown">
    /// نادرست فقط برای ردیفِ بی‌تاریخ (سهمِ مفادِ قراردادی که هنوز فروشی ندارد). تاریخِ
    /// جایگزین برای جای‌دادنِ مبلغ در دوره لازم است، ولی «آخرین حرکت» نباید از آن ساخته شود.
    /// </param>
    private sealed record BalanceEvent(
        PartyStatementPartyType PartyType,
        int PartyId,
        DateTime Date,
        CompanyFlowDirection Direction,
        decimal AmountUsd,
        bool IsDateKnown = true,
        int? ContractId = null);
}
