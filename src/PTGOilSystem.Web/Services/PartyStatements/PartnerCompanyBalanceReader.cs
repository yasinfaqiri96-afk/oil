using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Reporting;

namespace PTGOilSystem.Web.Services.PartyStatements;

/// <param name="BalanceUsd">
/// همان علامتِ حساب شریک: مثبت = شرکت به شریک بدهکار است، منفی = شریک به شرکت بدهکار است.
/// </param>
/// <param name="IsBookOwner">
/// شریکِ مالکِ دفترِ یکی از شرکت‌ها (<see cref="Company.OwnerPartnerId"/>، همان «سهم شرکت» در فورم
/// قرارداد). ماندهٔ او سرمایهٔ مالک است، نه طلب یا بدهیِ شرکت از یک طرفِ بیرونی.
/// </param>
public sealed record PartnerCompanyBalance(int PartnerId, string PartnerName, decimal BalanceUsd, bool IsBookOwner);

/// <summary>
/// ماندهٔ واقعیِ هر شریک با شرکت تا یک تاریخ — مرجعِ ردیف شرکا در «بیلانس کلی شرکت».
///
/// پرداخت و تسویه از سند اصلی خوانده می‌شوند؛ مفاد از اقتصاد قرارداد و فیصدی تاریخ هر فروش.
/// فقط رویدادهای مالی تاریخ‌دار و تا همان تاریخ شمرده می‌شوند:
/// پرداختِ شریک (سرمایه، خرید، مصرف) و برگشتِ آن، عایدِ فروشِ نزد شریک، سهم مفادِ محققِ فروش‌ها و
/// تسویه‌های بین شرکا.
///
/// دو ردیفِ گردش حساب رویداد مالی نیستند و اینجا نمی‌آیند، ولی در صورت‌حساب شراکت و پروفایل شریک
/// دست‌نخورده می‌مانند:
/// <list type="bullet">
/// <item>«سهم از هزینهٔ کالای هنوز فروخته‌نشده» (<see cref="PartnershipStatementLineKind.UnsoldCostShare"/>):
/// تخصیصِ برابرسازیِ هزینه بین شرکاست. هزینهٔ واقعی همان‌جا که هست (بدهی فروشنده، صندوق، پرداخت شریک)
/// شمرده شده است.</item>
/// <item>سهم مفادِ بی‌تاریخ: قرارداد هنوز فروشی ندارد، پس مفادی محقق نشده است.</item>
/// </list>
/// درصدِ سهم قراردادی به‌تنهایی هیچ مانده‌ای نمی‌سازد.
/// </summary>
public sealed class PartnerCompanyBalanceReader(ApplicationDbContext db)
{
    // Keep existing call sites compatible while internal statement arithmetic remains independent.
    public PartnerCompanyBalanceReader(ApplicationDbContext db, IPartnershipStatementService partnerships) : this(db)
        => ArgumentNullException.ThrowIfNull(partnerships);
    public async Task<IReadOnlyList<PartnerCompanyBalance>> ReadAsync(DateTime asOfDate, CancellationToken ct = default)
    {
        var events = await ReadEventsAsync(asOfDate, ct: ct);
        var partnerIds = await db.ContractPartners.AsNoTracking()
            .Where(cp => cp.Contract != null && cp.Contract.OwnershipType == ContractOwnershipType.Partnership)
            .Select(cp => cp.PartnerId)
            .Distinct()
            .ToListAsync(ct);
        if (partnerIds.Count == 0)
        {
            return [];
        }

        var bookOwnerIds = (await db.Companies.AsNoTracking()
                .Where(c => c.OwnerPartnerId != null)
                .Select(c => c.OwnerPartnerId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        var names = await db.Partners.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return partnerIds.Select(id => new PartnerCompanyBalance(id, names.GetValueOrDefault(id, "-"),
                events.Where(e => e.PartnerId == id).Sum(e => e.EffectUsd), bookOwnerIds.Contains(id)))
            .OrderBy(balance => balance.PartnerId)
            .ToList();
    }

    public async Task<HashSet<int>> BookOwnerIdsAsync(CancellationToken ct = default)
        => (await db.Companies.AsNoTracking().Where(c => c.OwnerPartnerId != null)
            .Select(c => c.OwnerPartnerId!.Value).ToListAsync(ct)).ToHashSet();

    /// <summary>رویدادهای واقعی شرکت؛ تخصیص داخلی هزینه و کل فروش وصول‌نشده کنار می‌مانند.</summary>
    public async Task<IReadOnlyList<PartnerCompanyEvent>> ReadEventsAsync(
        DateTime asOfDate, int? contractId = null, CancellationToken ct = default)
    {
        var before = DateTime.SpecifyKind(asOfDate.Date.AddDays(1), DateTimeKind.Utc);
        var contracts = await db.Contracts.AsNoTracking()
            .Where(c => c.OwnershipType == ContractOwnershipType.Partnership
                && (!contractId.HasValue || c.Id == contractId.Value))
            .Select(c => c.Id).ToListAsync(ct);
        if (contracts.Count == 0) return [];
        var members = await db.ContractPartners.AsNoTracking().Where(cp => contracts.Contains(cp.ContractId))
            .Select(cp => new { cp.ContractId, cp.PartnerId }).Distinct().ToListAsync(ct);
        var memberKeys = members.Select(m => (m.ContractId, m.PartnerId)).ToHashSet();
        var result = new List<PartnerCompanyEvent>();
        void Add(int partnerId, int? cid, DateTime date, decimal effect, PartnershipStatementLineKind kind)
        {
            if (effect != 0m) result.Add(new(partnerId, cid, date.Date, effect, kind));
        }

        var funding = await PartnerFundingReader.LoadPartnerFundedPaymentsAsync(db, contracts, toDate: asOfDate, ct: ct);
        // Customer receipts/refunds are custody, never personal capital funding.
        foreach (var f in funding.Where(f => f.PaymentKind is not PaymentKind.CustomerReceipt and not PaymentKind.CustomerPayment))
            Add(f.PartnerId, f.ContractId, f.PaymentDate,
                f.Direction == PaymentDirection.Out ? f.AmountUsd : -f.AmountUsd,
                PartnershipStatementLineKind.PartnerPurchase);

        var settlements = await db.PartnerSettlements.AsNoTracking()
            .Where(s => s.SettlementDate < before
                && (!s.IsReversed || s.ReversedAtUtc.HasValue)
                && (!contractId.HasValue || s.ContractId == contractId))
            .ToListAsync(ct);
        var partnerIds = members.Select(m => m.PartnerId).ToHashSet();
        foreach (var s in settlements)
        {
            if (partnerIds.Contains(s.FromPartnerId)) Add(s.FromPartnerId, s.ContractId, s.SettlementDate, s.AmountUsd, PartnershipStatementLineKind.PartnerSettlement);
            if (partnerIds.Contains(s.ToPartnerId)) Add(s.ToPartnerId, s.ContractId, s.SettlementDate, -s.AmountUsd, PartnershipStatementLineKind.PartnerSettlement);
            if (s.IsReversed && s.ReversedAtUtc < before)
            {
                if (partnerIds.Contains(s.FromPartnerId)) Add(s.FromPartnerId, s.ContractId, s.ReversedAtUtc.Value, -s.AmountUsd, PartnershipStatementLineKind.Adjustment);
                if (partnerIds.Contains(s.ToPartnerId)) Add(s.ToPartnerId, s.ContractId, s.ReversedAtUtc.Value, s.AmountUsd, PartnershipStatementLineKind.Adjustment);
            }
        }

        var economics = await new ProfitAndLossService(db, reportAsOfDate: asOfDate)
            .BuildContractEconomicsAsync(contracts, ct);
        var history = await ContractPartnerShareHistory.LoadAsync(db, contracts, ct);
        var salesById = economics.Values.SelectMany(e => e.Sales.Select(s => new { e.ContractId, Sale = s }))
            .GroupBy(s => s.Sale.SalesTransactionId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var e in economics.Values)
        {
            if (e.Sales.Count == 0) continue;
            var costs = e.RealizedCostOfGoodsSoldUsd + e.RealizedOperationalCostUsd;
            var saleProfits = PartnerProfitAllocationPolicy.Settle(e.Sales.ToDictionary(s => s.SalesTransactionId, s =>
                s.AmountUsd - (e.SoldQuantityMt != 0m ? costs * s.QuantityMt / e.SoldQuantityMt : 0m)
                + (e.RevenueUsd != 0m ? e.RealizedFxNetUsd * s.AmountUsd / e.RevenueUsd : 0m)));
            foreach (var sale in e.Sales)
            {
                var shares = PartnerProfitAllocationPolicy.Settle(history.SharesOn(e.ContractId, sale.SaleDate)
                    .ToDictionary(s => s.PartnerId, s => saleProfits[sale.SalesTransactionId] * s.SharePercent / 100m));
                foreach (var (partnerId, amount) in shares)
                    Add(partnerId, e.ContractId, sale.SaleDate, amount, PartnershipStatementLineKind.ProfitShare);
            }
        }

        // Explicit customer receipt/refund, identified partner, money outside company cash.
        // Cash applications allocate a payment; they never create another cash receipt.
        var receipts = await db.PaymentTransactions.AsNoTracking()
            .Where(p => p.PaymentDate < before && p.CustomerId.HasValue
                && p.FundingSource == PaymentFundingSource.Partner && p.PaidByPartnerId.HasValue
                && !p.CashAccountId.HasValue
                && ((p.PaymentKind == PaymentKind.CustomerReceipt && p.Direction == PaymentDirection.In)
                    || (p.PaymentKind == PaymentKind.CustomerPayment && p.Direction == PaymentDirection.Out)))
            .ToListAsync(ct);
        var receiptIds = receipts.Select(p => p.Id).ToList();
        var applications = await db.CustomerPaymentAllocationApplications.AsNoTracking()
            .Where(a => receiptIds.Contains(a.PaymentTransactionId) && a.AppliedAt < before
                && (a.Status == CustomerPaymentAllocationApplicationStatus.Active
                    || (a.ReversedAtUtc.HasValue && a.ReversedAtUtc.Value >= before)))
            .Select(a => new { a.PaymentTransactionId, a.SalesTransactionId, a.AppliedAmountUsd }).ToListAsync(ct);
        var saleIds = salesById.Keys.ToList();
        var totalsBySale = await db.SalesTransactions.AsNoTracking()
            .Where(s => saleIds.Contains(s.Id)).Select(s => new { s.Id, s.TotalUsd })
            .ToDictionaryAsync(s => s.Id, s => s.TotalUsd, ct);
        foreach (var p in receipts)
        {
            var effect = p.Direction == PaymentDirection.In ? -p.AmountUsd : p.AmountUsd;
            var partnerId = p.PaidByPartnerId!.Value;
            // Reversing an invoice application does not return the actual cash.
            if (!contractId.HasValue && partnerIds.Contains(partnerId))
            {
                Add(partnerId, p.ContractId, p.PaymentDate, effect, PartnershipStatementLineKind.SaleProceedsHeld);
                continue;
            }
            if (p.ContractId.HasValue && memberKeys.Contains((p.ContractId.Value, partnerId)))
            {
                Add(partnerId, p.ContractId, p.PaymentDate, effect, PartnershipStatementLineKind.SaleProceedsHeld);
                continue;
            }
            var allocations = p.SalesTransactionId.HasValue
                ? new[] { (SaleId: p.SalesTransactionId.Value, Amount: p.AmountUsd) }
                : applications.Where(a => a.PaymentTransactionId == p.Id)
                    .Select(a => (SaleId: a.SalesTransactionId, Amount: a.AppliedAmountUsd)).ToArray();
            foreach (var a in allocations)
            {
                if (!salesById.TryGetValue(a.SaleId, out var sales) || totalsBySale.GetValueOrDefault(a.SaleId) == 0m) continue;
                foreach (var sale in sales.Where(s => memberKeys.Contains((s.ContractId, partnerId))))
                    Add(partnerId, sale.ContractId, p.PaymentDate,
                        (p.Direction == PaymentDirection.In ? -1m : 1m) * a.Amount * sale.Sale.AmountUsd / totalsBySale[a.SaleId],
                        PartnershipStatementLineKind.SaleProceedsHeld);
            }
        }
        return result;
    }

    /// <summary>ردیفِ گردش حساب که رویداد مالیِ واقعی تا <paramref name="asOfDate"/> است.</summary>
    public static bool IsFinancialEvent(PartnerAccountEntry entry, DateTime asOfDate)
        => entry.Kind != PartnershipStatementLineKind.UnsoldCostShare
            && entry.Date.HasValue
            && entry.Date.Value.Date <= asOfDate.Date;
}

public sealed record PartnerCompanyEvent(int PartnerId, int? ContractId, DateTime Date, decimal EffectUsd, PartnershipStatementLineKind Kind);
