using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services.PartyStatements;

namespace PTGOilSystem.Web.Services.Reporting;

public interface IReceivablesPayablesReader
{
    /// <summary>
    /// ردیف‌های «طلبات و بدهی‌ها» — همان ردیف‌هایی که گزارش وب و کارت‌های داشبورد می‌خوانند.
    /// </summary>
    /// <param name="includeEmployees">
    /// ماندهٔ کارمند همان ماندهٔ معاش است؛ کاربری که معاش نمی‌بیند این ردیف‌ها را هم نمی‌بیند.
    /// </param>
    Task<IReadOnlyList<ReceivablePayableRowViewModel>> ReadRowsAsync(
        ManagementReportFilterViewModel filter,
        bool includeEmployees,
        IReadOnlyCollection<PartyStatementPartyType>? partyTypes = null,
        CancellationToken ct = default);
}

/// <summary>
/// مرجع واحد ردیف‌های «طلبات و بدهی‌ها»: ماندهٔ رسمی طرف‌حساب از <see cref="IPartyBalanceReadService"/>
/// به‌اضافهٔ اصلاح فقط‌خواندنیِ تفاوت نرخِ ثبت‌نشدهٔ تأمین‌کننده. هیچ سندی ثبت یا تغییر نمی‌شود.
/// </summary>
public sealed class ReceivablesPayablesReader(
    ApplicationDbContext db,
    IPartyBalanceReadService partyBalances,
    ISupplierFxSettlementService supplierFxSettlements) : IReceivablesPayablesReader
{
    public async Task<IReadOnlyList<ReceivablePayableRowViewModel>> ReadRowsAsync(
        ManagementReportFilterViewModel filter,
        bool includeEmployees,
        IReadOnlyCollection<PartyStatementPartyType>? partyTypes = null,
        CancellationToken ct = default)
    {
        // «طلبات و بدهی‌ها» فقط طرف‌حساب بیرونی را می‌شمارد؛ حساب جاریِ خودِ جوازها
        // طرف معامله نیست. رجوع: PartyBalanceSnapshotFilters.ExternalPartiesOnly.
        var balanceRows = (await partyBalances.GetBalancesAsync(filter, ct, partyTypes))
            .ExternalPartiesOnly();
        if (!includeEmployees)
        {
            balanceRows = balanceRows.Where(row => row.PartyType != PartyStatementPartyType.Employee).ToList();
        }
        var supplierIds = balanceRows
            .Where(row => row.PartyType == PartyStatementPartyType.Supplier)
            .Select(row => row.PartyId)
            .Distinct()
            .ToArray();
        var fxSettlements = await supplierFxSettlements.GetManyAsync(
            supplierIds,
            filter.ContractId,
            filter.ToDate,
            ct);

        // در نمای همهٔ قراردادها، تعدیل ثبت‌شدهٔ SupplierFxDifference از قبل داخل ماندهٔ
        // رسمی است؛ فقط بخشِ ثبت‌نشده را به‌صورت read-only اصلاح می‌کنیم تا دوباره‌شماری نشود.
        // در فیلتر یک قرارداد، سطر شناسایی عمداً ContractId ندارد و در ماندهٔ خام هم نیست؛
        // بنابراین کل تفاوت همان قرارداد باید در ستون تعدیل گزارش دیده شود.
        var recognizedFxEffects = filter.ContractId.HasValue
            ? new Dictionary<int, decimal>()
            : await LoadRecognizedSupplierFxEffectsAsync(supplierIds, filter.ToDate, ct);
        var policies = new PartyStatementPolicyResolver();
        return balanceRows.Select(balance => new ReceivablePayableRowViewModel
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

        var query = db.LedgerEntries.AsNoTracking()
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
}
