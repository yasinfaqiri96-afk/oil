using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <summary>
/// یک پرداختِ واقعیِ شریک، همان‌طور که در روزنامچه ثبت شده است.
/// </summary>
public sealed record PartnerFundingPaymentRow(
    int PaymentId,
    int PartnerId,
    int ContractId,
    DateTime PaymentDate,
    PaymentDirection Direction,
    PaymentKind PaymentKind,
    decimal Amount,
    string Currency,
    decimal AmountUsd,
    string? Reference,
    string? Description,
    int? LedgerEntryId,
    /// <summary>
    /// پول از صندوقِ شرکتی خارج شده که دفترش دفترِ شخصیِ همین شریک است
    /// (<see cref="Company.OwnerPartnerId"/>)، نه از جیبِ شخصیِ او.
    /// فقط برای برچسب و ردیابی است؛ ریاضیِ سرمایه‌گذاری با پرداختِ مستقیم یکی است.
    /// </summary>
    bool ViaOwnedCompany = false);

/// <summary>
/// نگاشتِ سطرهای لجرِ برخاسته از روزنامچه، برای قراردادهای موردنظر.
/// </summary>
/// <param name="PaymentLedgerEntryIds">
/// شناسهٔ همهٔ LedgerEntryهایی که سندشان یک PaymentTransaction است. صورت‌حساب شریک این سطرها را
/// دیگر کورکورانه بر SharePercent تقسیم نمی‌کند.
/// </param>
/// <param name="PartnerByPaymentLedgerEntryId">
/// از میان همان سطرها، آن‌هایی که شریک واقعاً پرداختشان کرده، به شناسهٔ همان شریک.
/// </param>
public sealed record PartnerFundingLedgerMap(
    IReadOnlySet<int> PaymentLedgerEntryIds,
    IReadOnlyDictionary<int, int> PartnerByPaymentLedgerEntryId)
{
    public static PartnerFundingLedgerMap Empty { get; } =
        new(new HashSet<int>(), new Dictionary<int, int>());
}

/// <summary>
/// تنها منبعِ «کدام شریک واقعاً این پرداخت را داد».
///
/// چرا اینجا و نه در هر سرویس جداگانه: سه محاسبهٔ مانده شریک وجود دارد (صورت‌حساب تفصیلی،
/// بیلانس مدیریتی، پروفایل شریک) و هر سه باید دقیقاً یک تعریف از «پرداخت واقعی شریک» داشته
/// باشند. این کلاس فقط می‌خواند؛ هیچ سندی نمی‌سازد و هیچ جهت/علامتی تعریف نمی‌کند —
/// جهت همچنان از <see cref="CompanyFlow.ICompanyFlowDirectionResolver"/> می‌آید.
///
/// دو راهِ قطعی برای «پولِ شریک» شناخته می‌شود و هیچ راهِ سومی (نام، حدس، تطبیقِ متنی) وجود ندارد:
///
///   ۱) <see cref="PaymentFundingSource.Partner"/> با <see cref="PaymentTransaction.PaidByPartnerId"/> —
///      شریک از جیبِ خودش داده و صندوقِ شرکت اصلاً حرکت نکرده است.
///
///   ۲) <see cref="PaymentFundingSource.Company"/> از شرکتی که
///      <see cref="Company.OwnerPartnerId"/> دارد و همان شریک عضوِ همین قراردادِ شراکتی است —
///      مالکِ شرکت پول را از دفترِ خودش داده، پس سرمایه‌گذاریِ اوست. هر سه شرط لازم است:
///      پرداخت به همین قرارداد بچسبد، قرارداد
///      <see cref="ContractOwnershipType.Partnership"/> باشد، و شریکِ مالک واقعاً در
///      <see cref="ContractPartner"/> همان قرارداد باشد. پرداختِ عمومیِ شرکت، سربار، یا
///      پرداختِ قراردادِ دیگر هیچ‌وقت از این در رد نمی‌شود.
/// </summary>
public static class PartnerFundingReader
{
    /// <summary>
    /// سطرهای لجرِ روزنامچهٔ قراردادهای داده‌شده را برمی‌گرداند تا صورت‌حساب شریک بتواند
    /// «پرداخت شرکت» را کنار بگذارد و «پرداخت شریک» را کامل به خودِ همان شریک بدهد.
    /// </summary>
    public static async Task<PartnerFundingLedgerMap> LoadLedgerMapAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(contractIds);

        if (contractIds.Count == 0)
        {
            return PartnerFundingLedgerMap.Empty;
        }

        var ids = contractIds.Distinct().ToArray();
        var rows = await LoadScopedPaymentsAsync(db, ids, requireLedgerEntry: true, toDate: null, ct);
        if (rows.Count == 0)
        {
            return PartnerFundingLedgerMap.Empty;
        }

        var context = await FundingContext.LoadAsync(db, ids, rows, ct);

        var all = rows.Select(r => r.LedgerEntryId!.Value).ToHashSet();
        var byPartner = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            var partnerId = context.ResolveFundingPartnerId(row);
            if (partnerId.HasValue)
            {
                byPartner[row.LedgerEntryId!.Value] = partnerId.Value;
            }
        }

        return new PartnerFundingLedgerMap(all, byPartner);
    }

    /// <summary>
    /// پرداخت‌های واقعیِ شریک روی قراردادهای داده‌شده. با <paramref name="partnerId"/> فقط همان شریک.
    /// </summary>
    public static async Task<IReadOnlyList<PartnerFundingPaymentRow>> LoadPartnerFundedPaymentsAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<int> contractIds,
        int? partnerId = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(contractIds);

        if (contractIds.Count == 0)
        {
            return [];
        }

        var ids = contractIds.Distinct().ToArray();
        var rows = await LoadScopedPaymentsAsync(db, ids, requireLedgerEntry: false, toDate, ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var context = await FundingContext.LoadAsync(db, ids, rows, ct);

        var result = new List<PartnerFundingPaymentRow>();
        foreach (var row in rows)
        {
            var payerId = context.ResolveFundingPartnerId(row);
            if (!payerId.HasValue || (partnerId.HasValue && payerId.Value != partnerId.Value))
            {
                continue;
            }

            result.Add(new PartnerFundingPaymentRow(
                row.PaymentId,
                payerId.Value,
                row.ContractId,
                row.PaymentDate,
                row.Direction,
                row.PaymentKind,
                row.Amount,
                row.Currency,
                row.AmountUsd,
                row.Reference,
                row.Description,
                row.LedgerEntryId,
                ViaOwnedCompany: row.FundingSource != PaymentFundingSource.Partner));
        }

        return result
            .OrderBy(r => r.PaymentDate)
            .ThenBy(r => r.PaymentId)
            .ToList();
    }

    /// <summary>
    /// دامنه دقیقاً همان چیزی است که پروفایل شریک نشان می‌دهد: پرداختِ مستقیمِ قرارداد و
    /// پرداختی که از راه یک فروشِ همان قرارداد ثبت شده.
    /// </summary>
    private static async Task<List<ScopedPaymentRow>> LoadScopedPaymentsAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<int> ids,
        bool requireLedgerEntry,
        DateTime? toDate,
        CancellationToken ct)
    {
        var query = db.PaymentTransactions
            .AsNoTracking()
            .Where(p => (p.ContractId != null && ids.Contains(p.ContractId!.Value))
                || (p.SalesTransaction != null
                    && p.SalesTransaction.ContractId != null
                    && ids.Contains(p.SalesTransaction.ContractId!.Value)));

        if (requireLedgerEntry)
        {
            query = query.Where(p => p.LedgerEntryId != null);
        }

        if (toDate.HasValue)
        {
            var exclusiveEnd = toDate.Value.Date.AddDays(1);
            query = query.Where(p => p.PaymentDate < exclusiveEnd);
        }

        return await query
            .OrderBy(p => p.PaymentDate)
            .ThenBy(p => p.Id)
            .Select(p => new ScopedPaymentRow(
                p.Id,
                p.LedgerEntryId,
                p.ContractId != null ? p.ContractId!.Value : p.SalesTransaction!.ContractId!.Value,
                p.FundingSource,
                p.PaidByPartnerId,
                p.CompanyId,
                p.PaymentDate,
                p.Direction,
                p.PaymentKind,
                p.Amount,
                p.Currency,
                p.AmountUsd,
                p.Reference,
                p.Description))
            .ToListAsync(ct);
    }

    private sealed record ScopedPaymentRow(
        int PaymentId,
        int? LedgerEntryId,
        int ContractId,
        PaymentFundingSource FundingSource,
        int? PaidByPartnerId,
        int? PayerCompanyId,
        DateTime PaymentDate,
        PaymentDirection Direction,
        PaymentKind PaymentKind,
        decimal Amount,
        string Currency,
        decimal AmountUsd,
        string? Reference,
        string? Description);

    /// <summary>
    /// حقایقی که برای تصمیمِ «این پول مالِ کدام شریک است» لازم‌اند. همه از دیتابیس خوانده
    /// می‌شوند و هیچ‌کدام از روی نام یا ترتیب حدس زده نمی‌شوند.
    /// </summary>
    private sealed class FundingContext
    {
        private readonly HashSet<int> _partnershipContractIds = [];
        private readonly Dictionary<int, int> _contractCompanyId = [];
        private readonly Dictionary<int, int> _companyOwnerPartnerId = [];
        private readonly HashSet<(int ContractId, int PartnerId)> _members = [];

        public static async Task<FundingContext> LoadAsync(
            ApplicationDbContext db,
            IReadOnlyCollection<int> contractIds,
            IReadOnlyCollection<ScopedPaymentRow> rows,
            CancellationToken ct)
        {
            var context = new FundingContext();

            var contracts = await db.Contracts
                .AsNoTracking()
                .Where(c => contractIds.Contains(c.Id))
                .Select(c => new { c.Id, c.CompanyId, c.OwnershipType })
                .ToListAsync(ct);
            foreach (var contract in contracts)
            {
                context._contractCompanyId[contract.Id] = contract.CompanyId;
                if (contract.OwnershipType == ContractOwnershipType.Partnership)
                {
                    context._partnershipContractIds.Add(contract.Id);
                }
            }

            var members = await db.ContractPartners
                .AsNoTracking()
                .Where(cp => contractIds.Contains(cp.ContractId))
                .Select(cp => new { cp.ContractId, cp.PartnerId })
                .Distinct()
                .ToListAsync(ct);
            foreach (var member in members)
            {
                context._members.Add((member.ContractId, member.PartnerId));
            }

            // شرکتِ پرداخت‌کننده: یا خودِ سند گفته کدام شرکت، یا شرکتِ همان قرارداد.
            var companyIds = rows
                .Select(r => r.PayerCompanyId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Concat(contracts.Select(c => c.CompanyId))
                .Distinct()
                .ToArray();
            if (companyIds.Length > 0)
            {
                var owners = await db.Companies
                    .AsNoTracking()
                    .Where(c => companyIds.Contains(c.Id) && c.OwnerPartnerId != null)
                    .Select(c => new { c.Id, OwnerPartnerId = c.OwnerPartnerId!.Value })
                    .ToListAsync(ct);
                foreach (var owner in owners)
                {
                    context._companyOwnerPartnerId[owner.Id] = owner.OwnerPartnerId;
                }
            }

            return context;
        }

        /// <summary>
        /// شریکی که این پرداخت سرمایه‌گذاریِ اوست، یا null اگر پولِ هیچ شریکی نیست.
        /// </summary>
        public int? ResolveFundingPartnerId(ScopedPaymentRow row)
        {
            if (row.FundingSource == PaymentFundingSource.Partner)
            {
                // پرداختِ مستقیمِ شریک. عضویت شرط است تا پرداختِ یک شریک روی قراردادی که
                // عضوش نیست، به حساب شراکت همان قرارداد ننشیند.
                return row.PaidByPartnerId.HasValue
                    && _members.Contains((row.ContractId, row.PaidByPartnerId.Value))
                        ? row.PaidByPartnerId
                        : null;
            }

            // پرداختِ شرکت. فقط اگر قرارداد شراکتی باشد و شرکتِ پرداخت‌کننده مالکِ شریکی
            // داشته باشد که خودش عضوِ همین قرارداد است.
            if (!_partnershipContractIds.Contains(row.ContractId))
            {
                return null;
            }

            var companyId = row.PayerCompanyId
                ?? (_contractCompanyId.TryGetValue(row.ContractId, out var contractCompanyId)
                    ? contractCompanyId
                    : (int?)null);
            if (!companyId.HasValue
                || !_companyOwnerPartnerId.TryGetValue(companyId.Value, out var ownerPartnerId))
            {
                return null;
            }

            return _members.Contains((row.ContractId, ownerPartnerId)) ? ownerPartnerId : null;
        }
    }
}
