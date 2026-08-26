using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>منبعِ هر ردیفِ صورت‌حساب شراکت — نه «محاسبهٔ مبهم».</summary>
public static class PartnershipStatementSources
{
    public const string Payment = "روزنامچه";
    public const string Sale = "فروش";
    public const string Loading = "بارگیری";
    public const string Expense = "مصرف";
    public const string Book = "دفتر قرارداد";
    public const string Reconciliation = "تطبیق";
    public const string Settlement = "تسویه بین شرکا";
}

/// <summary>
/// نوعِ رویدادِ یک ردیف صورت‌حساب، به زبانِ کاربر — نه اصطلاح حسابداری.
/// پروفایل شریک همین را در ستون «نوع رویداد» نشان می‌دهد.
/// </summary>
public enum PartnershipStatementLineKind
{
    PartnerFunding = 1,
    PartnerPurchase = 2,
    PartnerExpense = 3,
    SaleProceedsHeld = 4,
    ProfitShare = 5,
    PartnerSettlement = 6,
    Adjustment = 7
}

public sealed record PartnershipStatementLine(
    string Source,
    string Title,
    DateTime? Date,
    int? PartnerId,
    decimal AmountUsd,
    string? Reference,
    int? RecordId,
    PartnershipStatementLineKind Kind = PartnershipStatementLineKind.PartnerFunding,
    /// <summary>مقدار عملیاتی همین سند، فقط اگر خودِ سند آن را دارد (فروش). وگرنه null.</summary>
    decimal? QuantityMt = null,
    /// <summary>نرخ همان سند. هرگز حدس زده نمی‌شود؛ نبودش یعنی null.</summary>
    decimal? UnitPriceUsd = null);

public sealed record PartnershipPartnerTotals(
    int PartnerId,
    string PartnerName,
    decimal SharePercent,
    decimal FundingUsd,
    decimal ProceedsHeldUsd,
    decimal ProfitShareUsd,
    decimal SettlementsPaidUsd,
    decimal SettlementsReceivedUsd)
{
    /// <summary>
    /// مثبت = این شریک طلبکار است، منفی = این شریک بدهکار است.
    /// پرداخت/سرمایه‌ای که داده + سهم مفادش − عایدی که نزد خودش مانده
    /// + تسویه‌هایی که پرداخته − تسویه‌هایی که گرفته.
    /// </summary>
    public decimal NetPositionUsd => decimal.Round(
        FundingUsd + ProfitShareUsd - ProceedsHeldUsd + SettlementsPaidUsd - SettlementsReceivedUsd,
        2,
        MidpointRounding.AwayFromZero);
}

public sealed record PartnershipContractStatement(
    int ContractId,
    string ContractNumber,
    string ContractLabel,
    string Currency,
    decimal SalesUsd,
    decimal PurchaseCostUsd,
    decimal OperationalExpenseUsd,
    decimal TotalPartnerFundingUsd,
    decimal BookProfitUsd,
    decimal PaymentToBookDifferenceUsd,
    int? ProceedsHolderPartnerId,
    string? ProceedsHolderPartnerName,
    IReadOnlyList<PartnershipPartnerTotals> Partners,
    IReadOnlyList<PartnershipStatementLine> Lines)
{
    /// <summary>هزینهٔ دفتریِ قرارداد: خرید + مصارف. مبنای مفاد، همین است — نه پرداختِ شرکا.</summary>
    public decimal TotalCostUsd => decimal.Round(
        PurchaseCostUsd + OperationalExpenseUsd,
        2,
        MidpointRounding.AwayFromZero);

    /// <summary>
    /// باقیماندهٔ تطبیق‌نشدهٔ همین قرارداد = جمعِ مانده دو شریک.
    /// برابر است با «تفاوت تطبیق پرداخت با دفتر» به‌علاوهٔ گِردکردنِ سهم‌ها.
    /// عمداً صفر نمی‌شود و داخل مفاد پنهان نمی‌شود.
    /// </summary>
    public decimal UnreconciledResidualUsd => decimal.Round(
        Partners.Sum(p => p.NetPositionUsd),
        2,
        MidpointRounding.AwayFromZero);
}

public sealed record PartnershipSettlementRow(
    int Id,
    DateTime SettlementDate,
    int FromPartnerId,
    string FromPartnerName,
    int ToPartnerId,
    string ToPartnerName,
    int? ContractId,
    string? ContractLabel,
    decimal Amount,
    string Currency,
    decimal AmountUsd,
    string? Reference,
    string? Description,
    bool IsReversed,
    decimal RunningBalanceAfterUsd);

public sealed record PartnershipContractOption(int ContractId, string ContractLabel, bool IsSelected);

public sealed record PartnershipStatement(
    int PartnerAId,
    string PartnerAName,
    int PartnerBId,
    string PartnerBName,
    IReadOnlyList<PartnershipContractOption> ContractOptions,
    IReadOnlyList<PartnershipContractStatement> Contracts,
    IReadOnlyList<PartnershipPartnerTotals> Totals,
    IReadOnlyList<PartnershipSettlementRow> Settlements,
    int? DebtorPartnerId,
    string? DebtorPartnerName,
    int? CreditorPartnerId,
    string? CreditorPartnerName,
    decimal AmountDueUsd,
    decimal CreditorClaimUsd,
    decimal UnreconciledResidualUsd);

public sealed record PartnershipPairOption(
    int PartnerAId,
    string PartnerAName,
    int PartnerBId,
    string PartnerBName,
    int ContractCount);

/// <summary>جهتِ مانده یک شریک — همان چیزی که پروفایل با جمله نشان می‌دهد.</summary>
public enum PartnerBalanceDirection
{
    Settled = 0,
    Creditor = 1,
    Debtor = 2
}

public sealed record PartnerCoPartner(int PartnerId, string PartnerName);

/// <summary>وضعیت همین شریک در یک قرارداد شراکتی.</summary>
public sealed record PartnerContractPosition(
    int ContractId,
    string ContractNumber,
    string ContractLabel,
    string Currency,
    decimal SharePercent,
    decimal FundingUsd,
    decimal ProceedsHeldUsd,
    decimal ProfitShareUsd,
    decimal SettlementsPaidUsd,
    decimal SettlementsReceivedUsd,
    decimal NetPositionUsd,
    IReadOnlyList<PartnerCoPartner> CoPartners);

/// <summary>
/// یک رویداد در گردش حساب شریک. <paramref name="EffectUsd"/> اثرِ علامت‌دار روی مانده است
/// (مثبت = به نفع شریک) و <paramref name="RunningBalanceUsd"/> جمعِ تجمعی همان اثرهاست،
/// پس آخرین ردیف دقیقاً همان «مانده فعلی» خلاصه حساب می‌شود. این دو، علامتِ داخلیِ سیستم‌اند؛
/// ستون‌های بدهکار/بستانکار و «مانده»ِ نمایشی از نگاشتِ پایین می‌آیند و قرینهٔ آن‌اند.
/// </summary>
public sealed record PartnerAccountEntry(
    DateTime? Date,
    int? ContractId,
    string? ContractLabel,
    string Description,
    PartnershipStatementLineKind Kind,
    decimal AmountUsd,
    decimal EffectUsd,
    decimal RunningBalanceUsd,
    string? Reference,
    string SourceType,
    int? SourceId,
    /// <summary>شریکِ طرفِ همین ردیف — برای تسویه‌ها. ردیف‌های دیگر طرف مقابل ندارند.</summary>
    int? CounterpartyPartnerId = null,
    /// <summary>مقدار MT همان سند، اگر سند دارد. پرداخت و تسویه ندارند و null می‌مانند.</summary>
    decimal? QuantityMt = null,
    /// <summary>نرخ همان سند، اگر سند دارد.</summary>
    decimal? UnitPriceUsd = null)
{
    // ── نگاشتِ نمایشیِ دفترِ حسابدار ──
    // علامتِ داخلی (EffectUsd/NetPositionUsd) دست‌نخورده می‌ماند؛ فقط ستون‌های صورت‌حساب
    // به قاعدهٔ همان دفترِ کاغذی نوشته می‌شوند: آنچه شریک آورده (پرداخت، مصرف، سهم مفاد)
    // بدهکار است و آنچه به شریک رسیده (تسویهٔ پرداخت‌شده، عاید فروش نزد شریک) بستانکار.
    // مانده = بستانکار − بدهکار، پس دقیقاً معکوسِ اثرِ داخلی است.

    /// <summary>ستون بدهکار: آنچه شریک به حساب گذاشته. هر ردیف فقط یکی از دو ستون را پر می‌کند.</summary>
    public decimal DebitUsd => EffectUsd > 0m ? EffectUsd : 0m;

    /// <summary>ستون بستانکار: آنچه به شریک رسیده، به‌صورت مثبت نمایش داده می‌شود.</summary>
    public decimal CreditUsd => EffectUsd < 0m ? -EffectUsd : 0m;

    /// <summary>مانده تجمعیِ صورت‌حساب به قاعدهٔ بستانکار − بدهکار — همان ستون «مانده».</summary>
    public decimal AccountantBalanceUsd => -RunningBalanceUsd;
}

/// <summary>
/// صورت‌حساب یک شریک در همهٔ قراردادهای شراکتی‌اش — همان فرمول و همان علامتِ
/// صورت‌حساب شراکت، فقط از دید یک شریک.
/// </summary>
public sealed record PartnerAccountStatement(
    int PartnerId,
    string PartnerName,
    decimal FundingUsd,
    decimal ProceedsHeldUsd,
    decimal ProfitShareUsd,
    decimal SettlementsPaidUsd,
    decimal SettlementsReceivedUsd,
    decimal NetPositionUsd,
    PartnerBalanceDirection Direction,
    decimal AmountUsd,
    DateTime? LastActivityDate,
    IReadOnlyList<PartnershipContractOption> ContractOptions,
    IReadOnlyList<PartnerContractPosition> Contracts,
    IReadOnlyList<PartnerCoPartner> CoPartners,
    IReadOnlyList<PartnerAccountEntry> Entries)
{
    /// <summary>جمع بستانکار — از همان اثرهای ردیف‌ها، نه فرمول تازه.</summary>
    public decimal TotalCreditUsd => decimal.Round(
        Entries.Sum(e => e.CreditUsd), 2, MidpointRounding.AwayFromZero);

    /// <summary>جمع بدهکار — از همان اثرهای ردیف‌ها. بستانکار منهای بدهکار = مانده.</summary>
    public decimal TotalDebitUsd => decimal.Round(
        Entries.Sum(e => e.DebitUsd), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// ماندهٔ نهاییِ صورت‌حساب = بستانکار − بدهکار. همان عددِ آخرین ردیفِ گردش حساب و
    /// قرینهٔ <see cref="NetPositionUsd"/> است، چون نگاشتِ بدهکار/بستانکار قاعدهٔ دفترِ
    /// حسابدار را دنبال می‌کند و علامتِ داخلی دست‌نخورده می‌ماند.
    /// </summary>
    public decimal AccountantBalanceUsd => decimal.Round(
        TotalCreditUsd - TotalDebitUsd, 2, MidpointRounding.AwayFromZero);
}

public interface IPartnershipStatementService
{
    Task<IReadOnlyList<PartnershipPairOption>> ListPairsAsync(CancellationToken ct = default);

    Task<PartnershipStatement?> BuildAsync(
        int partnerAId,
        int partnerBId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// وضعیت یک شریک در همهٔ قراردادهای شراکتی‌اش. تنها منبعِ ارقامِ پروفایل شریک.
    /// </summary>
    Task<PartnerAccountStatement?> BuildForPartnerAsync(
        int partnerId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default);
}

/// <summary>
/// صورت‌حساب شراکت بین دو شریک.
///
/// هیچ Revenue و هیچ Expense تازه‌ای نمی‌سازد: فروش از <see cref="SalesTransaction"/> واقعی،
/// خرید از <see cref="IPurchaseAggregationService"/>، مصارف از <see cref="ExpenseTransaction"/>
/// و «کدام شریک واقعاً پرداخت کرد» از <see cref="PartnerFundingReader"/> می‌آید — همان منابعی
/// که پروفایل شریک و صورت‌حساب رسمی هم می‌خوانند.
///
/// مفادِ قرارداد فقط و فقط از دادهٔ عملیاتی می‌آید: «فروش − خرید − مصارف». پرداختِ شرکا
/// (<see cref="PaymentTransaction"/>) هرگز جای هزینه را در فرمول مفاد نمی‌گیرد؛ یک مفهوم
/// جداست و فقط می‌گوید هر شریک چقدر تأمین مالی کرده.
///
/// چون «جمع پرداخت شرکا» و «خرید + مصارف دفتری» لزوماً برابر نیستند، جمع مانده دو شریک
/// دقیقاً صفر نمی‌شود. آن باقیمانده به‌زور صفر نمی‌شود و داخل مفاد پنهان نمی‌شود؛ به‌صورت
/// <see cref="PartnershipContractStatement.PaymentToBookDifferenceUsd"/> و
/// <see cref="PartnershipContractStatement.UnreconciledResidualUsd"/> صریح گزارش می‌شود.
/// </summary>
public sealed class PartnershipStatementService : IPartnershipStatementService
{
    private readonly ApplicationDbContext _db;
    private readonly IPurchaseAggregationService _purchaseAggregation;

    public PartnershipStatementService(
        ApplicationDbContext db,
        IPurchaseAggregationService? purchaseAggregation = null)
    {
        _db = db;
        _purchaseAggregation = purchaseAggregation ?? new PurchaseAggregationService(db);
    }

    private static decimal Round(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public async Task<IReadOnlyList<PartnershipPairOption>> ListPairsAsync(CancellationToken ct = default)
    {
        var links = await _db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.Contract != null && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            .Select(cp => new
            {
                cp.ContractId,
                cp.PartnerId,
                PartnerName = cp.Partner != null ? cp.Partner.Name : string.Empty
            })
            .ToListAsync(ct);

        var pairs = new Dictionary<(int A, int B), (string AName, string BName, HashSet<int> Contracts)>();
        foreach (var group in links.GroupBy(l => l.ContractId))
        {
            var members = group.DistinctBy(m => m.PartnerId).OrderBy(m => m.PartnerId).ToList();
            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    var key = (members[i].PartnerId, members[j].PartnerId);
                    if (!pairs.TryGetValue(key, out var entry))
                    {
                        entry = (members[i].PartnerName, members[j].PartnerName, []);
                        pairs[key] = entry;
                    }

                    entry.Contracts.Add(group.Key);
                }
            }
        }

        return pairs
            .Select(p => new PartnershipPairOption(
                p.Key.A,
                p.Value.AName,
                p.Key.B,
                p.Value.BName,
                p.Value.Contracts.Count))
            .OrderByDescending(p => p.ContractCount)
            .ThenBy(p => p.PartnerAId)
            .ToList();
    }

    public async Task<PartnershipStatement?> BuildAsync(
        int partnerAId,
        int partnerBId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default)
    {
        if (partnerAId <= 0 || partnerBId <= 0 || partnerAId == partnerBId)
        {
            return null;
        }

        var partners = await _db.Partners
            .AsNoTracking()
            .Where(p => p.Id == partnerAId || p.Id == partnerBId)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        if (partners.Count != 2)
        {
            return null;
        }

        var nameById = partners.ToDictionary(p => p.Id, p => p.Name);

        // قراردادهای شراکتیِ مشترکِ همین دو شریک — هیچ شناسه‌ای هاردکد نیست.
        var sharedLinks = await LoadMemberLinksAsync(
            cp => cp.PartnerId == partnerAId || cp.PartnerId == partnerBId,
            ct);

        var sharedContracts = sharedLinks
            .GroupBy(l => l.ContractId)
            .Where(g => g.Any(x => x.PartnerId == partnerAId) && g.Any(x => x.PartnerId == partnerBId))
            .OrderBy(g => g.First().ContractNumber, StringComparer.Ordinal)
            .ToList();

        var allContractIds = sharedContracts.Select(g => g.Key).ToList();
        var selectedIds = contractIds is { Count: > 0 }
            ? allContractIds.Where(contractIds.Contains).ToList()
            : allContractIds;
        if (selectedIds.Count == 0)
        {
            selectedIds = allContractIds;
        }

        var options = sharedContracts
            .Select(g => new PartnershipContractOption(
                g.Key,
                Contract.BuildDisplayLabel(g.First().ContractName, g.First().ContractNumber),
                selectedIds.Contains(g.Key)))
            .ToList();

        var selected = sharedContracts.Where(g => selectedIds.Contains(g.Key)).ToList();

        var contractStatements = await BuildContractStatementsAsync(selected, nameById, ct);

        var settlements = await LoadSettlementsAsync(
            partnerAId, partnerBId, selectedIds, allContractIds, nameById, ct);

        var totals = new[] { partnerAId, partnerBId }
            .Select(pid =>
            {
                var rows = contractStatements
                    .SelectMany(c => c.Partners)
                    .Where(p => p.PartnerId == pid)
                    .ToList();
                var paid = Round(settlements
                    .Where(s => !s.IsReversed && s.FromPartnerId == pid)
                    .Sum(s => s.AmountUsd));
                var received = Round(settlements
                    .Where(s => !s.IsReversed && s.ToPartnerId == pid)
                    .Sum(s => s.AmountUsd));

                return new PartnershipPartnerTotals(
                    PartnerId: pid,
                    PartnerName: nameById.GetValueOrDefault(pid) ?? string.Empty,
                    SharePercent: rows.Count == 0 ? 0m : rows.Average(r => r.SharePercent),
                    FundingUsd: Round(rows.Sum(r => r.FundingUsd)),
                    ProceedsHeldUsd: Round(rows.Sum(r => r.ProceedsHeldUsd)),
                    ProfitShareUsd: Round(rows.Sum(r => r.ProfitShareUsd)),
                    SettlementsPaidUsd: paid,
                    SettlementsReceivedUsd: received);
            })
            .ToList();

        var debtor = totals.OrderBy(t => t.NetPositionUsd).First();
        var creditor = totals.OrderByDescending(t => t.NetPositionUsd).First();

        // مبلغِ قابلِ پرداخت = بدهیِ واقعیِ شریکِ بدهکار. طلبِ شریکِ طلبکار می‌تواند به اندازهٔ
        // باقیماندهٔ تطبیق‌نشده فرق کند؛ آن عدد جداگانه گزارش می‌شود و اینجا صاف نمی‌شود.
        var amountDue = Round(Math.Abs(debtor.NetPositionUsd));
        var creditorClaim = Round(creditor.NetPositionUsd);
        var unreconciledResidual = Round(totals.Sum(t => t.NetPositionUsd));
        var hasDirection = amountDue > 0m
            && debtor.PartnerId != creditor.PartnerId
            && debtor.NetPositionUsd < 0m;

        return new PartnershipStatement(
            PartnerAId: partnerAId,
            PartnerAName: nameById.GetValueOrDefault(partnerAId) ?? string.Empty,
            PartnerBId: partnerBId,
            PartnerBName: nameById.GetValueOrDefault(partnerBId) ?? string.Empty,
            ContractOptions: options,
            Contracts: contractStatements,
            Totals: totals,
            Settlements: settlements,
            DebtorPartnerId: hasDirection ? debtor.PartnerId : null,
            DebtorPartnerName: hasDirection ? debtor.PartnerName : null,
            CreditorPartnerId: hasDirection ? creditor.PartnerId : null,
            CreditorPartnerName: hasDirection ? creditor.PartnerName : null,
            AmountDueUsd: hasDirection ? amountDue : 0m,
            CreditorClaimUsd: hasDirection ? creditorClaim : 0m,
            UnreconciledResidualUsd: unreconciledResidual);
    }


    /// <summary>
    /// وضعیت یک شریک در همهٔ قراردادهای شراکتی‌اش.
    ///
    /// هیچ فرمول تازه‌ای اینجا نیست: قرارداد به قرارداد از همان
    /// <see cref="BuildContractStatementsAsync"/> می‌خواند و مانده را با همان تعریفِ
    /// <see cref="PartnershipPartnerTotals.NetPositionUsd"/> می‌سازد. برای همین عددِ پروفایل
    /// و عددِ صورت‌حساب شراکت هرگز از هم جدا نمی‌شوند.
    /// </summary>
    public async Task<PartnerAccountStatement?> BuildForPartnerAsync(
        int partnerId,
        IReadOnlyCollection<int>? contractIds = null,
        CancellationToken ct = default)
    {
        if (partnerId <= 0)
        {
            return null;
        }

        var partner = await _db.Partners
            .AsNoTracking()
            .Where(p => p.Id == partnerId)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(ct);
        if (partner is null)
        {
            return null;
        }

        // قراردادهای شراکتیِ همین شریک، و بعد همهٔ اعضای همان قراردادها (برای نام شریک مقابل).
        var partnerContractIds = await _db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.PartnerId == partnerId
                && cp.Contract != null
                && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            .Select(cp => cp.ContractId)
            .Distinct()
            .ToListAsync(ct);

        var links = partnerContractIds.Count == 0
            ? []
            : await LoadMemberLinksAsync(cp => partnerContractIds.Contains(cp.ContractId), ct);

        var memberIds = links.Select(l => l.PartnerId).Distinct().ToList();
        var nameById = memberIds.Count == 0
            ? new Dictionary<int, string>()
            : (await _db.Partners
                .AsNoTracking()
                .Where(p => memberIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(ct))
                .ToDictionary(p => p.Id, p => p.Name);

        var contractGroups = links
            .GroupBy(l => l.ContractId)
            .OrderBy(g => g.First().ContractNumber, StringComparer.Ordinal)
            .ToList();

        var allContractIds = contractGroups.Select(g => g.Key).ToList();
        var selectedIds = contractIds is { Count: > 0 }
            ? allContractIds.Where(contractIds.Contains).ToList()
            : allContractIds;
        if (selectedIds.Count == 0)
        {
            selectedIds = allContractIds;
        }

        var options = contractGroups
            .Select(g => new PartnershipContractOption(
                g.Key,
                Contract.BuildDisplayLabel(g.First().ContractName, g.First().ContractNumber),
                selectedIds.Contains(g.Key)))
            .ToList();

        var selected = contractGroups.Where(g => selectedIds.Contains(g.Key)).ToList();
        var contractStatements = await BuildContractStatementsAsync(selected, nameById, ct);
        var settlements = await LoadPartnerSettlementsAsync(partnerId, selectedIds, allContractIds, ct);

        var coPartners = links
            .Where(l => selectedIds.Contains(l.ContractId) && l.PartnerId != partnerId)
            .Select(l => l.PartnerId)
            .Distinct()
            .Select(id => new PartnerCoPartner(id, nameById.GetValueOrDefault(id) ?? string.Empty))
            .OrderBy(c => c.PartnerName, StringComparer.Ordinal)
            .ToList();

        var positions = new List<PartnerContractPosition>();
        foreach (var contract in contractStatements)
        {
            var own = contract.Partners.FirstOrDefault(x => x.PartnerId == partnerId);
            if (own is null)
            {
                continue;
            }

            var paid = Round(settlements
                .Where(x => !x.IsReversed && x.ContractId == contract.ContractId && x.FromPartnerId == partnerId)
                .Sum(x => x.AmountUsd));
            var received = Round(settlements
                .Where(x => !x.IsReversed && x.ContractId == contract.ContractId && x.ToPartnerId == partnerId)
                .Sum(x => x.AmountUsd));
            var withSettlements = own with { SettlementsPaidUsd = paid, SettlementsReceivedUsd = received };

            positions.Add(new PartnerContractPosition(
                ContractId: contract.ContractId,
                ContractNumber: contract.ContractNumber,
                ContractLabel: contract.ContractLabel,
                Currency: contract.Currency,
                SharePercent: own.SharePercent,
                FundingUsd: own.FundingUsd,
                ProceedsHeldUsd: own.ProceedsHeldUsd,
                ProfitShareUsd: own.ProfitShareUsd,
                SettlementsPaidUsd: paid,
                SettlementsReceivedUsd: received,
                NetPositionUsd: withSettlements.NetPositionUsd,
                CoPartners: contract.Partners
                    .Where(x => x.PartnerId != partnerId)
                    .Select(x => new PartnerCoPartner(x.PartnerId, x.PartnerName))
                    .ToList()));
        }

        var settlementsPaidUsd = Round(settlements
            .Where(x => !x.IsReversed && x.FromPartnerId == partnerId)
            .Sum(x => x.AmountUsd));
        var settlementsReceivedUsd = Round(settlements
            .Where(x => !x.IsReversed && x.ToPartnerId == partnerId)
            .Sum(x => x.AmountUsd));

        var totals = new PartnershipPartnerTotals(
            PartnerId: partnerId,
            PartnerName: partner.Name,
            SharePercent: positions.Count == 0 ? 0m : positions.Average(p => p.SharePercent),
            FundingUsd: Round(positions.Sum(p => p.FundingUsd)),
            ProceedsHeldUsd: Round(positions.Sum(p => p.ProceedsHeldUsd)),
            ProfitShareUsd: Round(positions.Sum(p => p.ProfitShareUsd)),
            SettlementsPaidUsd: settlementsPaidUsd,
            SettlementsReceivedUsd: settlementsReceivedUsd);

        var entries = BuildPartnerEntries(partnerId, contractStatements, positions, settlements);

        var net = totals.NetPositionUsd;
        var direction = net > 0m
            ? PartnerBalanceDirection.Creditor
            : net < 0m
                ? PartnerBalanceDirection.Debtor
                : PartnerBalanceDirection.Settled;

        return new PartnerAccountStatement(
            PartnerId: partnerId,
            PartnerName: partner.Name,
            FundingUsd: totals.FundingUsd,
            ProceedsHeldUsd: totals.ProceedsHeldUsd,
            ProfitShareUsd: totals.ProfitShareUsd,
            SettlementsPaidUsd: totals.SettlementsPaidUsd,
            SettlementsReceivedUsd: totals.SettlementsReceivedUsd,
            NetPositionUsd: net,
            Direction: direction,
            AmountUsd: Round(Math.Abs(net)),
            LastActivityDate: entries.Where(e => e.Date.HasValue).Select(e => e.Date).DefaultIfEmpty(null).Max(),
            ContractOptions: options,
            Contracts: positions,
            CoPartners: coPartners,
            Entries: entries);
    }

    /// <summary>
    /// گردش حساب شریک. اثرِ هر ردیف دقیقاً همان جزءِ فرمول مانده است، بنابراین ماندهٔ تجمعیِ
    /// آخرین ردیف با «مانده فعلی» خلاصه حساب یکی می‌شود.
    /// </summary>
    private static List<PartnerAccountEntry> BuildPartnerEntries(
        int partnerId,
        IReadOnlyList<PartnershipContractStatement> contracts,
        IReadOnlyList<PartnerContractPosition> positions,
        IReadOnlyList<PartnerSettlementRecord> settlements)
    {
        var draft = new List<(DateTime? Date, int? ContractId, string? Label, string Description,
            PartnershipStatementLineKind Kind, decimal Amount, decimal Effect, string SourceType, int? SourceId,
            string? Reference, int? CounterpartyPartnerId, decimal? QuantityMt, decimal? UnitPriceUsd)>();

        foreach (var contract in contracts)
        {
            var partnerLines = contract.Lines.Where(l => l.PartnerId == partnerId).ToList();
            foreach (var line in partnerLines.Where(l => l.Kind != PartnershipStatementLineKind.SaleProceedsHeld))
            {
                draft.Add((
                    line.Date,
                    contract.ContractId,
                    contract.ContractLabel,
                    line.Title,
                    line.Kind,
                    Math.Abs(line.AmountUsd),
                    line.AmountUsd,
                    line.Source == PartnershipStatementSources.Sale ? "Sale" : "Payment",
                    line.RecordId,
                    line.Reference,
                    null,
                    line.QuantityMt,
                    line.UnitPriceUsd));
            }

            AppendPartnerSaleProceedsDraft(
                draft,
                contract.ContractId,
                contract.ContractLabel,
                partnerLines.Where(l => l.Kind == PartnershipStatementLineKind.SaleProceedsHeld).ToList());

            var position = positions.FirstOrDefault(p => p.ContractId == contract.ContractId);
            if (position is null || position.ProfitShareUsd == 0m)
            {
                continue;
            }

            // سهم مفاد در تاریخ آخرین فروشِ همان قرارداد دیده می‌شود؛ اگر فروشی نیست، بی‌تاریخ می‌ماند.
            var profitDate = contract.Lines
                .Where(l => l.Kind == PartnershipStatementLineKind.SaleProceedsHeld && l.Date.HasValue)
                .Select(l => l.Date)
                .DefaultIfEmpty(null)
                .Max();

            draft.Add((
                profitDate,
                contract.ContractId,
                contract.ContractLabel,
                $"سهم مفاد {position.SharePercent:0.##}٪ از مفاد قرارداد",
                PartnershipStatementLineKind.ProfitShare,
                Math.Abs(position.ProfitShareUsd),
                position.ProfitShareUsd,
                "Contract",
                contract.ContractId,
                null,
                null,
                null,
                null));
        }

        foreach (var settlement in settlements)
        {
            var isPayer = settlement.FromPartnerId == partnerId;
            var effect = settlement.IsReversed
                ? 0m
                : isPayer ? settlement.AmountUsd : -settlement.AmountUsd;
            var counterparty = isPayer ? settlement.ToPartnerName : settlement.FromPartnerName;
            var description = string.IsNullOrWhiteSpace(settlement.Description)
                ? isPayer ? $"تسویه پرداخت‌شده به {counterparty}" : $"تسویه دریافت‌شده از {counterparty}"
                : settlement.Description!;

            draft.Add((
                settlement.SettlementDate,
                settlement.ContractId,
                settlement.ContractLabel,
                settlement.IsReversed ? $"{description} — برگشت‌خورده" : description,
                settlement.IsReversed
                    ? PartnershipStatementLineKind.Adjustment
                    : PartnershipStatementLineKind.PartnerSettlement,
                settlement.AmountUsd,
                effect,
                "Settlement",
                settlement.Id,
                settlement.Reference,
                isPayer ? settlement.ToPartnerId : settlement.FromPartnerId,
                null,
                null));
        }

        var ordered = draft
            .OrderBy(d => d.Date.HasValue ? 0 : 1)
            .ThenBy(d => d.Date ?? DateTime.MaxValue)
            .ThenBy(d => d.ContractId ?? int.MaxValue)
            .ThenBy(d => d.SourceId ?? int.MaxValue)
            .ToList();

        var entries = new List<PartnerAccountEntry>(ordered.Count);
        var running = 0m;
        foreach (var row in ordered)
        {
            running += row.Effect;
            entries.Add(new PartnerAccountEntry(
                Date: row.Date,
                ContractId: row.ContractId,
                ContractLabel: row.Label,
                Description: row.Description,
                Kind: row.Kind,
                AmountUsd: decimal.Round(row.Amount, 2, MidpointRounding.AwayFromZero),
                EffectUsd: decimal.Round(row.Effect, 2, MidpointRounding.AwayFromZero),
                RunningBalanceUsd: decimal.Round(running, 2, MidpointRounding.AwayFromZero),
                Reference: row.Reference,
                SourceType: row.SourceType,
                SourceId: row.SourceId,
                CounterpartyPartnerId: row.CounterpartyPartnerId,
                QuantityMt: row.QuantityMt,
                UnitPriceUsd: row.UnitPriceUsd));
        }

        return entries;
    }

    /// <summary>
    /// عاید فروش در گردش حساب شریک به‌صورت خلاصهٔ هر قرارداد دیده می‌شود؛ جزئیات هر فاکتور
    /// همان‌جا که بود در صورت‌حساب شراکت/قرارداد باقی می‌ماند. جمع‌ها همان فرمول مانده‌اند.
    /// </summary>
    private static void AppendPartnerSaleProceedsDraft(
        List<(DateTime? Date, int? ContractId, string? Label, string Description,
            PartnershipStatementLineKind Kind, decimal Amount, decimal Effect, string SourceType, int? SourceId,
            string? Reference, int? CounterpartyPartnerId, decimal? QuantityMt, decimal? UnitPriceUsd)> draft,
        int contractId,
        string contractLabel,
        IReadOnlyList<PartnershipStatementLine> saleLines)
    {
        if (saleLines.Count == 0)
        {
            return;
        }

        if (saleLines.Count == 1)
        {
            var line = saleLines[0];
            var amount = Math.Abs(line.AmountUsd);
            draft.Add((
                line.Date,
                contractId,
                contractLabel,
                line.Title,
                PartnershipStatementLineKind.SaleProceedsHeld,
                amount,
                -amount,
                "Sale",
                line.RecordId,
                line.Reference,
                null,
                line.QuantityMt,
                line.UnitPriceUsd));
            return;
        }

        var totalAmount = Round(saleLines.Sum(l => Math.Abs(l.AmountUsd)));
        var totalQuantityMt = saleLines.Sum(l => l.QuantityMt ?? 0m);
        var lastSaleDate = saleLines
            .Where(l => l.Date.HasValue)
            .Select(l => l.Date)
            .DefaultIfEmpty(null)
            .Max();

        draft.Add((
            lastSaleDate,
            contractId,
            contractLabel,
            $"جمع عاید فروش ({saleLines.Count:N0} فروش)",
            PartnershipStatementLineKind.SaleProceedsHeld,
            totalAmount,
            -totalAmount,
            "Contract",
            contractId,
            null,
            null,
            totalQuantityMt == 0m ? null : Round(totalQuantityMt),
            totalQuantityMt == 0m ? null : Round(totalAmount / totalQuantityMt)));
    }

    private sealed record PartnerSettlementRecord(
        int Id,
        DateTime SettlementDate,
        int FromPartnerId,
        string FromPartnerName,
        int ToPartnerId,
        string ToPartnerName,
        int? ContractId,
        string? ContractLabel,
        decimal AmountUsd,
        string? Reference,
        string? Description,
        bool IsReversed);

    private async Task<List<PartnerSettlementRecord>> LoadPartnerSettlementsAsync(
        int partnerId,
        IReadOnlyCollection<int> selectedContractIds,
        IReadOnlyCollection<int> allContractIds,
        CancellationToken ct)
    {
        var selected = selectedContractIds.ToHashSet();
        var showsEveryContract = selected.SetEquals(allContractIds);

        var rows = await _db.PartnerSettlements
            .AsNoTracking()
            .Include(s => s.Contract)
            .Include(s => s.FromPartner)
            .Include(s => s.ToPartner)
            .Where(s => s.FromPartnerId == partnerId || s.ToPartnerId == partnerId)
            .OrderBy(s => s.SettlementDate)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        // تسویهٔ بدون قرارداد، تسویهٔ کلیِ حساب است و فقط در نمای «همهٔ قراردادها» شمرده می‌شود.
        return rows
            .Where(s => s.ContractId.HasValue
                ? selected.Contains(s.ContractId.Value)
                : showsEveryContract)
            .Select(s => new PartnerSettlementRecord(
                s.Id,
                s.SettlementDate,
                s.FromPartnerId,
                s.FromPartner?.Name ?? string.Empty,
                s.ToPartnerId,
                s.ToPartner?.Name ?? string.Empty,
                s.ContractId,
                s.Contract?.DisplayLabel,
                s.AmountUsd,
                s.Reference,
                s.Description,
                s.IsReversed))
            .ToList();
    }

    /// <summary>
    /// محاسبهٔ هر قرارداد شراکتی — تنها جای این فرمول در سیستم. هم صورت‌حساب بین دو شریک
    /// و هم پروفایل یک شریک از همین‌جا می‌خوانند، بنابراین دو عددِ متفاوت ساخته نمی‌شود.
    /// </summary>
    private async Task<List<PartnershipContractStatement>> BuildContractStatementsAsync(
        IReadOnlyList<IGrouping<int, ContractMemberLink>> selected,
        IReadOnlyDictionary<int, string> nameById,
        CancellationToken ct)
    {
        var selectedIds = selected.Select(g => g.Key).ToList();
        var saleByContract = await LoadSalesAsync(selectedIds, ct);
        var fundingRows = await PartnerFundingReader.LoadPartnerFundedPaymentsAsync(
            _db, selectedIds, partnerId: null, toDate: null, ct);
        var (purchaseByContract, loadingExpenseByContract) = await LoadPurchaseAsync(selectedIds, ct);
        var expenseByContract = await LoadExpensesAsync(selectedIds, ct);

        var contractStatements = new List<PartnershipContractStatement>();
        foreach (var group in selected)
        {
            var contractId = group.Key;
            var head = group.First();

            var sales = saleByContract.GetValueOrDefault(contractId) ?? [];
            var salesUsd = Round(sales.Sum(s => s.TotalUsd));
            var purchaseCostUsd = Round(purchaseByContract.GetValueOrDefault(contractId));
            var operationalExpenseUsd = Round(
                expenseByContract.GetValueOrDefault(contractId)
                + loadingExpenseByContract.GetValueOrDefault(contractId));

            var contractFunding = fundingRows.Where(f => f.ContractId == contractId).ToList();
            var fundingByPartner = contractFunding
                .GroupBy(f => f.PartnerId)
                .ToDictionary(
                    g => g.Key,
                    g => Round(g.Sum(f => f.Direction == PaymentDirection.Out ? f.AmountUsd : -f.AmountUsd)));
            var totalFundingUsd = Round(fundingByPartner.Values.Sum());

            // تنها مبنای مفاد: دادهٔ عملیاتی دفتر. پرداختِ شرکا اینجا هیچ نقشی ندارد.
            var bookProfitUsd = Round(salesUsd - purchaseCostUsd - operationalExpenseUsd);
            // مفهوم جدا: پولِ شرکا چقدر با هزینهٔ ثبت‌شدهٔ دفتر فرق دارد. صفر نمی‌شود.
            var paymentToBookDifferenceUsd = Round(totalFundingUsd - purchaseCostUsd - operationalExpenseUsd);

            var holderId = head.SaleProceedsHolderPartnerId;
            var partnerTotals = group
                .OrderByDescending(x => x.SharePercent)
                .ThenBy(x => x.PartnerId)
                .Select(x => new PartnershipPartnerTotals(
                    PartnerId: x.PartnerId,
                    PartnerName: nameById.GetValueOrDefault(x.PartnerId) ?? string.Empty,
                    SharePercent: x.SharePercent,
                    FundingUsd: fundingByPartner.GetValueOrDefault(x.PartnerId),
                    ProceedsHeldUsd: holderId == x.PartnerId ? salesUsd : 0m,
                    ProfitShareUsd: Round(bookProfitUsd * x.SharePercent / 100m),
                    SettlementsPaidUsd: 0m,
                    SettlementsReceivedUsd: 0m))
                .ToList();

            var lines = new List<PartnershipStatementLine>();
            lines.AddRange(contractFunding.Select(f => new PartnershipStatementLine(
                PartnershipStatementSources.Payment,
                DescribeFunding(f),
                f.PaymentDate,
                f.PartnerId,
                f.Direction == PaymentDirection.Out ? f.AmountUsd : -f.AmountUsd,
                f.Reference,
                f.PaymentId,
                ResolveFundingKind(f))));
            lines.AddRange(sales.Select(s => new PartnershipStatementLine(
                PartnershipStatementSources.Sale,
                string.IsNullOrWhiteSpace(s.InvoiceNumber) ? "فروش" : $"فروش {s.InvoiceNumber}",
                s.SaleDate,
                holderId,
                s.TotalUsd,
                s.InvoiceNumber,
                s.SaleId,
                PartnershipStatementLineKind.SaleProceedsHeld,
                // مقدار و نرخ از خودِ سند فروش می‌آید. نرخ فقط وقتی معنا دارد که مقدار صفر نباشد.
                QuantityMt: s.QuantityMt == 0m ? null : s.QuantityMt,
                UnitPriceUsd: s.QuantityMt == 0m ? null : Round(s.TotalUsd / s.QuantityMt))));

            contractStatements.Add(new PartnershipContractStatement(
                ContractId: contractId,
                ContractNumber: head.ContractNumber,
                ContractLabel: Contract.BuildDisplayLabel(head.ContractName, head.ContractNumber),
                Currency: head.Currency,
                SalesUsd: salesUsd,
                PurchaseCostUsd: purchaseCostUsd,
                OperationalExpenseUsd: operationalExpenseUsd,
                TotalPartnerFundingUsd: totalFundingUsd,
                BookProfitUsd: bookProfitUsd,
                PaymentToBookDifferenceUsd: paymentToBookDifferenceUsd,
                ProceedsHolderPartnerId: holderId,
                ProceedsHolderPartnerName: holderId.HasValue
                    ? nameById.GetValueOrDefault(holderId.Value)
                    : null,
                Partners: partnerTotals,
                Lines: lines.OrderBy(l => l.Date).ThenBy(l => l.RecordId).ToList()));
        }

        return contractStatements;
    }

    /// <summary>
    /// «خرید» یا «مصرف» — همان تفکیکی که کاربر در گردش حساب می‌بیند. پرداختِ برگشتی
    /// (جهت ورودی) اصلاح است، نه پرداخت تازه.
    /// </summary>
    private static PartnershipStatementLineKind ResolveFundingKind(PartnerFundingPaymentRow row)
        => row.Direction == PaymentDirection.In
            ? PartnershipStatementLineKind.Adjustment
            : row.PaymentKind == PaymentKind.SupplierPayment
                ? PartnershipStatementLineKind.PartnerPurchase
                : PartnershipStatementLineKind.PartnerExpense;

    /// <summary>عضویتِ شرکا در قراردادهای شراکتی، با همان اطلاعات سرقراردادی که محاسبه لازم دارد.</summary>
    private async Task<List<ContractMemberLink>> LoadMemberLinksAsync(
        System.Linq.Expressions.Expression<Func<ContractPartner, bool>> predicate,
        CancellationToken ct)
        => await _db.ContractPartners
            .AsNoTracking()
            .Where(cp => cp.Contract != null && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            .Where(predicate)
            .Select(cp => new ContractMemberLink(
                cp.ContractId,
                cp.PartnerId,
                cp.SharePercent,
                cp.Contract!.ContractNumber,
                cp.Contract!.ContractName,
                cp.Contract!.Currency,
                cp.Contract!.SaleProceedsHolderPartnerId))
            .ToListAsync(ct);

    private sealed record ContractMemberLink(
        int ContractId,
        int PartnerId,
        decimal SharePercent,
        string ContractNumber,
        string ContractName,
        string Currency,
        int? SaleProceedsHolderPartnerId);

    private static string DescribeFunding(PartnerFundingPaymentRow row)
        => string.IsNullOrWhiteSpace(row.Description)
            ? PaymentKindLabels.ToPersian(row.PaymentKind)
            : row.Description!;

    private sealed record SaleRow(
        int SaleId,
        DateTime SaleDate,
        string? InvoiceNumber,
        decimal QuantityMt,
        decimal TotalUsd);

    /// <summary>
    /// فروشِ یک قرارداد شراکتی در دیتابیس واقعی به سه شکل به قرارداد وصل است:
    /// <c>SalesTransaction.ContractId</c>، <c>SalesTransaction.SourcePurchaseContractId</c>
    /// و <c>LedgerEntry(SourceType="Sale").ContractId</c>. اینجا هر فروش دقیقاً به یک قرارداد
    /// نسبت داده می‌شود تا هیچ درآمدی دوبار شمرده نشود.
    /// </summary>
    private async Task<Dictionary<int, List<SaleRow>>> LoadSalesAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct)
    {
        var result = new Dictionary<int, List<SaleRow>>();
        if (contractIds.Count == 0)
        {
            return result;
        }

        var ids = contractIds.Distinct().ToArray();
        var direct = await _db.SalesTransactions
            .AsNoTracking()
            .Where(s => !s.IsCancelled
                && ((s.ContractId != null && ids.Contains(s.ContractId!.Value))
                    || (s.SourcePurchaseContractId != null && ids.Contains(s.SourcePurchaseContractId!.Value))))
            .Select(s => new
            {
                s.Id,
                s.ContractId,
                s.SourcePurchaseContractId,
                s.SaleDate,
                s.InvoiceNumber,
                s.QuantityMt,
                s.TotalUsd
            })
            .ToListAsync(ct);

        var contractBySale = new Dictionary<int, int>();
        var rowsById = new Dictionary<int, SaleRow>();
        foreach (var row in direct)
        {
            contractBySale[row.Id] = row.ContractId is int c && ids.Contains(c)
                ? c
                : row.SourcePurchaseContractId!.Value;
            rowsById[row.Id] = new SaleRow(row.Id, row.SaleDate, row.InvoiceNumber, row.QuantityMt, row.TotalUsd);
        }

        var ledgerSales = await _db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.SourceType == "Sale" && l.ContractId != null && ids.Contains(l.ContractId!.Value))
            .Select(l => new { SaleId = l.SourceId, ContractId = l.ContractId!.Value })
            .Distinct()
            .ToListAsync(ct);

        var missingSaleIds = ledgerSales
            .Where(l => !contractBySale.ContainsKey(l.SaleId))
            .Select(l => l.SaleId)
            .Distinct()
            .ToArray();

        var extra = missingSaleIds.Length == 0
            ? []
            : await _db.SalesTransactions
                .AsNoTracking()
                .Where(s => !s.IsCancelled && missingSaleIds.Contains(s.Id))
                .Select(s => new { s.Id, s.SaleDate, s.InvoiceNumber, s.QuantityMt, s.TotalUsd })
                .ToListAsync(ct);

        foreach (var row in extra)
        {
            rowsById[row.Id] = new SaleRow(row.Id, row.SaleDate, row.InvoiceNumber, row.QuantityMt, row.TotalUsd);
            contractBySale[row.Id] = ledgerSales.First(l => l.SaleId == row.Id).ContractId;
        }

        foreach (var pair in contractBySale)
        {
            if (!rowsById.TryGetValue(pair.Key, out var sale))
            {
                continue;
            }

            if (!result.TryGetValue(pair.Value, out var list))
            {
                list = [];
                result[pair.Value] = list;
            }

            list.Add(sale);
        }

        return result;
    }

    private async Task<(Dictionary<int, decimal> PurchaseCost, Dictionary<int, decimal> LoadingExpense)> LoadPurchaseAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct)
    {
        var purchase = new Dictionary<int, decimal>();
        var loading = new Dictionary<int, decimal>();
        if (contractIds.Count == 0)
        {
            return (purchase, loading);
        }

        var ids = contractIds.Distinct().ToArray();
        var contracts = await _db.Contracts
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.ContractType == ContractType.Purchase)
            .ToListAsync(ct);
        if (contracts.Count == 0)
        {
            return (purchase, loading);
        }

        var finalPriceByContract = contracts.ToDictionary(
            c => c.Id,
            ContractPricingAdapter.GetCanonicalFinalPrice);
        var snapshots = await _purchaseAggregation.AggregateForContractsAsync(
            contracts.Select(c => c.Id).ToList(),
            finalPriceByContract,
            ct);

        foreach (var snapshot in snapshots)
        {
            purchase[snapshot.Key] = snapshot.Value.TraceablePurchaseCostUsd;
            loading[snapshot.Key] = snapshot.Value.LoadingTransportExpenseUsd
                + snapshot.Value.LoadingWarehouseExpenseUsd
                + snapshot.Value.LoadingOtherExpenseUsd
                + snapshot.Value.LoadingRailwayExpenseUsd;
        }

        return (purchase, loading);
    }

    private async Task<Dictionary<int, decimal>> LoadExpensesAsync(
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct)
    {
        if (contractIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var ids = contractIds.Distinct().ToArray();
        var rows = await _db.ExpenseTransactions
            .AsNoTracking()
            .Where(e => !e.IsCancelled && e.ContractId != null && ids.Contains(e.ContractId!.Value))
            .GroupBy(e => e.ContractId!.Value)
            .Select(g => new { ContractId = g.Key, AmountUsd = g.Sum(e => e.AmountUsd) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ContractId, r => r.AmountUsd);
    }

    private async Task<List<PartnershipSettlementRow>> LoadSettlementsAsync(
        int partnerAId,
        int partnerBId,
        IReadOnlyCollection<int> selectedContractIds,
        IReadOnlyCollection<int> allContractIds,
        IReadOnlyDictionary<int, string> nameById,
        CancellationToken ct)
    {
        var selected = selectedContractIds.ToHashSet();
        var showsEveryContract = selected.SetEquals(allContractIds);

        var rows = await _db.PartnerSettlements
            .AsNoTracking()
            .Include(s => s.Contract)
            .Where(s => (s.FromPartnerId == partnerAId && s.ToPartnerId == partnerBId)
                || (s.FromPartnerId == partnerBId && s.ToPartnerId == partnerAId))
            .OrderBy(s => s.SettlementDate)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        // تسویهٔ بدون قرارداد، تسویهٔ کلیِ حساب شراکت است و فقط در نمای «همهٔ قراردادها» شمرده می‌شود.
        var filtered = rows
            .Where(s => s.ContractId.HasValue
                ? selected.Contains(s.ContractId.Value)
                : showsEveryContract)
            .ToList();

        var result = new List<PartnershipSettlementRow>(filtered.Count);
        var running = 0m;
        foreach (var s in filtered)
        {
            if (!s.IsReversed)
            {
                running += s.FromPartnerId == partnerAId ? s.AmountUsd : -s.AmountUsd;
            }

            result.Add(new PartnershipSettlementRow(
                Id: s.Id,
                SettlementDate: s.SettlementDate,
                FromPartnerId: s.FromPartnerId,
                FromPartnerName: nameById.GetValueOrDefault(s.FromPartnerId) ?? string.Empty,
                ToPartnerId: s.ToPartnerId,
                ToPartnerName: nameById.GetValueOrDefault(s.ToPartnerId) ?? string.Empty,
                ContractId: s.ContractId,
                ContractLabel: s.Contract?.DisplayLabel,
                Amount: s.Amount,
                Currency: s.Currency,
                AmountUsd: s.AmountUsd,
                Reference: s.Reference,
                Description: s.Description,
                IsReversed: s.IsReversed,
                RunningBalanceAfterUsd: Round(running)));
        }

        return result;
    }
}
