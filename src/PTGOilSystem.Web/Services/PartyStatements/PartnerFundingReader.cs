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
    int? LedgerEntryId);

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
        var rows = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.LedgerEntryId != null
                && ((p.ContractId != null && ids.Contains(p.ContractId!.Value))
                    || (p.SalesTransaction != null
                        && p.SalesTransaction.ContractId != null
                        && ids.Contains(p.SalesTransaction.ContractId!.Value))))
            .Select(p => new
            {
                LedgerEntryId = p.LedgerEntryId!.Value,
                p.FundingSource,
                p.PaidByPartnerId
            })
            .ToListAsync(ct);

        var all = rows.Select(r => r.LedgerEntryId).ToHashSet();
        var byPartner = rows
            .Where(r => r.FundingSource == PaymentFundingSource.Partner && r.PaidByPartnerId != null)
            .ToDictionary(r => r.LedgerEntryId, r => r.PaidByPartnerId!.Value);

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
        // دامنه دقیقاً همان چیزی است که پروفایل شریک نشان می‌دهد: پرداختِ مستقیمِ قرارداد و
        // پرداختی که از راه یک فروشِ همان قرارداد ثبت شده. پیش‌تر فقط ContractId خوانده می‌شد
        // و پرداخت شریک روی فروش، در جدول دیده می‌شد ولی در «پرداخت واقعی» شمرده نمی‌شد.
        var query = db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.FundingSource == PaymentFundingSource.Partner
                && p.PaidByPartnerId != null
                && ((p.ContractId != null && ids.Contains(p.ContractId!.Value))
                    || (p.SalesTransaction != null
                        && p.SalesTransaction.ContractId != null
                        && ids.Contains(p.SalesTransaction.ContractId!.Value))));

        if (partnerId.HasValue)
        {
            query = query.Where(p => p.PaidByPartnerId == partnerId.Value);
        }

        if (toDate.HasValue)
        {
            var exclusiveEnd = toDate.Value.Date.AddDays(1);
            query = query.Where(p => p.PaymentDate < exclusiveEnd);
        }

        return await query
            .OrderBy(p => p.PaymentDate)
            .ThenBy(p => p.Id)
            .Select(p => new PartnerFundingPaymentRow(
                p.Id,
                p.PaidByPartnerId!.Value,
                p.ContractId != null ? p.ContractId!.Value : p.SalesTransaction!.ContractId!.Value,
                p.PaymentDate,
                p.Direction,
                p.PaymentKind,
                p.Amount,
                p.Currency,
                p.AmountUsd,
                p.Reference,
                p.Description,
                p.LedgerEntryId))
            .ToListAsync(ct);
    }
}
